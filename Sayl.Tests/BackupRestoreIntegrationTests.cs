using Common.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// دورة نسخ/استعادة ناجحة حقيقية (BACKUP + RESTORE فعليان على SQL Server):
/// الاختبارات الأخرى للنسخ الاحتياطي تغطي التصريح ومسارات الفشل فقط — هذا
/// الاختبار هو الإثبات الآلي الوحيد أن مسار الاسترجاع الفعلي يعمل: نسخة
/// تُنشأ، بيانات تُتلف، استعادة تُنفَّذ، والبيانات الأصلية تُسترجع.
///
/// ⚠️ SAFETY:
/// - الاستعادة تُنفَّذ حصريًا على قاعدة اختبار مخصصة معزولة
///   (SaylCogsTest_{32hex} — نفس النمط الحصري للفيكسر) تُبنى وتُسقط داخل
///   الاختبار نفسه. لا تُلمس SaylDB الإنتاجية ولا قاعدة الفيكسر المشتركة
///   (الاستعادة على قاعدة الفيكسر كانت ستمحو بذر الاختبارات الأخرى).
/// - هوية قاعدة الاختبار تُتحقق (DB_NAME()) قبل أي أمر تدميري، والإسقاط
///   يمر بنفس بوابة النمط الحصري + تحقق الوجود بالاسم باراميتر.
/// - CLEANUP في finally: إسقاط القاعدة المخصصة وحذف مجلد النسخ المؤقت حتى
///   عند فشل أي تأكيد.
/// </summary>
[Collection("database")]
public class BackupRestoreIntegrationTests
{
    private readonly DatabaseFixture _fx;
    public BackupRestoreIntegrationTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>سياق على القاعدة المخصصة — Pooling=false كي لا تسمّم الـpool
    /// باتصالات ماتت عند SINGLE_USER WITH ROLLBACK IMMEDIATE أثناء الاستعادة.</summary>
    private SaylDbContext CreateDedicatedContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<SaylDbContext>()
            .UseSqlServer($@"Server=(localdb)\MSSQLLocalDB;Database={dbName};Trusted_Connection=True;TrustServerCertificate=True;Pooling=false")
            .Options;
        return new SaylDbContext(options);
    }

    /// <summary>بناء مخطط القاعدة المخصصة من SaylDB.sql + EnsureSchema — نفس
    /// مسار الفيكسر (استبدال الاسم)، بلا أي إشارة لاسم قاعدة آخر.</summary>
    private void BuildDedicatedDatabase(string dbName)
    {
        string repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
        string script = File.ReadAllText(Path.Combine(repoRoot, "SaylDB.sql"));
        script = script.Replace("SaylDB", dbName);
        DatabaseFixture.ExecuteBatches(script, master: true);
        DatabaseFixture.ExecuteBatches(clsUsersService.EnsureSchemaSql);
    }

    /// <summary>إسقاط القاعدة المخصصة — بنفس بوابة الفيكسر: النمط الحصري ثم
    /// تحقق الوجود بالاسم باراميتر قبل أي أمر إتلاف.</summary>
    private static void DropDedicatedDatabase(string dbName)
    {
        if (!DatabaseFixture.IsSafeTestDbName(dbName))
            throw new InvalidOperationException($"refusing to drop '{dbName}': outside the test-only pattern.");
        if (string.Equals(dbName, DatabaseFixture.DbName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to drop '{dbName}': this is the shared fixture database, not this test's dedicated database.");

        var csb = new SqlConnectionStringBuilder(DatabaseFixture.ConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();
        using (var check = cn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sys.databases WHERE name = @name";
            check.Parameters.AddWithValue("@name", dbName);
            if (check.ExecuteScalar() is null) return;
        }
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}];";
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Restore_SuccessfulCycle_RecoversData_OnlyInDedicatedDatabase()
    {
        string dbName = $"{DatabaseFixture.TestDbPrefix}_{Guid.NewGuid():N}";
        string backupFolder = Path.Combine(Path.GetTempPath(), $"sayl-restore-{Guid.NewGuid():N}");
        string marker = $"bri-{Guid.NewGuid():N}"[..12];

        try
        {
            // ── 0) هوية القاعدة المخصصة قبل أي شيء (بوابة السلامة) ──
            BuildDedicatedDatabase(dbName);
            await using (var identity = CreateDedicatedContext(dbName))
            {
                await identity.Database.OpenConnectionAsync();
                string actual = await identity.Database.SqlQueryRaw<string>("SELECT DB_NAME() AS [Value]").SingleAsync();
                Assert.Equal(dbName, actual); // إن فشل هذا فلا يُنفَّذ أي أمر تدميري
                await identity.Database.CloseConnectionAsync();
            }

            // علامة في قاعدة الفيكسر المشتركة — بقاؤها بعد الاستعادة يثبت أن
            // الاستعادة أصابت القاعدة المخصصة فقط.
            string fixtureMarker = $"bri-fx-{Guid.NewGuid():N}"[..12];
            await using (var db = _fx.CreateContext())
            {
                db.Products.Add(new Product
                {
                    Name = $"restore-guard-{fixtureMarker}",
                    Type = "stock", BasePrice = 1m, CostPrice = 1m, IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // ── 1) بذر بيانات معروفة في القاعدة المخصصة (قبل النسخة) ──
            int userId;
            await using (var db = CreateDedicatedContext(dbName))
            {
                var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
                var user = new User { Username = $"restore-actor-{marker}", PasswordHash = "x", Role = role, IsActive = true };
                db.Users.Add(user);
                db.Products.Add(new Product
                {
                    Name = $"restore-data-{marker}",          // البيانات الثمينة — يجب أن تُسترجع
                    Type = "stock", BasePrice = 42m, CostPrice = 21m, IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                userId = user.UserId;
            }

            // ── 2) نسخة احتياطية فعلية (BACKUP DATABASE حقيقي) ──
            string backupPath;
            await using (var db = CreateDedicatedContext(dbName))
            {
                var svc = new clsBackupService(db);
                var backup = await svc.BackupAsync(backupFolder, keepLast: 5);
                Assert.True(backup.IsSuccess, $"BACKUP should succeed: {backup.Message}");
                backupPath = backup.Data!;
                Assert.True(File.Exists(backupPath), "ملف النسخة يجب أن يوجد فعليًا");
                Assert.EndsWith(".bak", backupPath, StringComparison.OrdinalIgnoreCase);
            }

            // ── 3) إتلاف البيانات بعد النسخة: حذف الثمينة + إضافة لاحقة ──
            await using (var db = CreateDedicatedContext(dbName))
            {
                var precious = await db.Products.SingleAsync(p => p.Name == $"restore-data-{marker}");
                db.Products.Remove(precious);
                db.Products.Add(new Product
                {
                    Name = $"post-backup-{marker}",           // بيانات بعد النسخة — يجب أن تزول بالاستعادة
                    Type = "stock", BasePrice = 1m, CostPrice = 1m, IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // ── 4) استعادة فعلية (RESTORE DATABASE حقيقي) بفاعل مصرّح ──
            await using (var db = CreateDedicatedContext(dbName))
            {
                var svc = new clsBackupService(db);
                var restore = await svc.RestoreAsync(backupPath, userId);
                Assert.True(restore.IsSuccess, $"RESTORE should succeed: {restore.Message}");
            }

            // ── 5) البيانات الأصلية استُرجعت، وما بعد النسخة زال ──
            await using (var after = CreateDedicatedContext(dbName))
            {
                bool preciousBack = await after.Products.AnyAsync(p => p.Name == $"restore-data-{marker}");
                bool postBackupGone = !await after.Products.AnyAsync(p => p.Name == $"post-backup-{marker}");
                Assert.True(preciousBack, "البيانات الموجودة عند النسخة يجب أن تُسترجع");
                Assert.True(postBackupGone, "البيانات المضافة بعد النسخة يجب أن تزول بالاستعادة");
                Assert.Equal(dbName, after.Database.GetDbConnection().Database);
            }

            // ── 6) نسخة pre-restore أُنشئت فعلًا قبل الاستعادة (VULN-23) ──
            string[] preRestoreFiles = Directory.GetFiles(backupFolder, "*_pre-restore.bak");
            Assert.NotEmpty(preRestoreFiles);

            // ── 7) الاستعادة أصابت القاعدة المخصصة فقط: قاعدة الفيكسر سليمة ──
            await using (var fxCheck = _fx.CreateContext())
            {
                bool fixtureUntouched = await fxCheck.Products.AnyAsync(p => p.Name == $"restore-guard-{fixtureMarker}");
                Assert.True(fixtureUntouched, "قاعدة الفيكسر المشتركة يجب ألا تُلمس بالاستعادة");
                Assert.Equal(DatabaseFixture.DbName, fxCheck.Database.GetDbConnection().Database);
            }
        }
        finally
        {
            try { DropDedicatedDatabase(dbName); }
            catch (Exception ex) { Console.Error.WriteLine($"[bri] drop failed: {ex.GetType().Name}: {ex.Message}"); }
            try { if (Directory.Exists(backupFolder)) Directory.Delete(backupFolder, recursive: true); }
            catch (Exception ex) { Console.Error.WriteLine($"[bri] folder cleanup failed: {ex.Message}"); }
        }
    }
}
