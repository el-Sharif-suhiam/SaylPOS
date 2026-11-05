using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Database restore authorization at the trusted execution boundary (the service).
/// Restore replaces the ENTIRE database — the most destructive operation in the
/// system — so a direct call to clsBackupService.RestoreAsync must never bypass
/// the required permission, regardless of what the WinForms UI shows or hides.
///
/// Policy (VULN-27, smallest change consistent with existing UI behavior):
/// the restore button lives on the Settings screen, which MainForm already gates
/// with AppPermissions.Settings (2048). The service therefore requires the same
/// Settings bit via the established mechanism (clsPermissions.GetUserRoleValueAsync):
///   - userId ≤ 0 (missing identity) → denied
///   - non-existent user → denied
///   - inactive user or inactive role → denied
///   - role without the Settings bit (accountant 508, cashier 0, custom) → denied
///
/// ⚠️ SAFETY: no test here ever executes a successful RESTORE. The fixture DB
/// (SaylCogsTest on LocalDB) is isolated, and the "failure path" test feeds a
/// garbage file so the RESTORE command itself fails; every assertion afterwards
/// proves the database was left intact. Authorization denials happen BEFORE any
/// destructive SQL by construction, and are verified to leave the data untouched.
/// </summary>
[Collection("database")]
public class BackupRestoreAuthorizationTests
{
    private readonly DatabaseFixture _fx;
    public BackupRestoreAuthorizationTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── helpers (fresh context per step) ─────────────────

    /// <summary>مستخدم بدور محدد (قيمة بت) — نشط افتراضيًا.</summary>
    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue, bool isActive = true)
    {
        var role = new Role { Name = $"role-{marker}", RoleValue = roleValue, IsActive = true };
        db.Roles.Add(role);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>ملف .bak غير صالح (محتوى عشوائي) — يمر فحص الوجود ويفشل RESTORE نفسه.</summary>
    private static string CreateGarbageBackupFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sayl-test-{Guid.NewGuid():N}.bak");
        File.WriteAllBytes(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });
        return path;
    }

    /// <summary>علامة حية داخل القاعدة — بقاؤها بعد الرفض يثبت أن القاعدة لم تُلمس.</summary>
    private static async Task SeedMarkerAsync(SaylDbContext db, string marker)
    {
        db.Products.Add(new Product
        {
            Name = $"restore-guard-{marker}",
            Type = "stock",
            BasePrice = 1m,
            CostPrice = 1m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<bool> MarkerExistsAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await db.Products.AnyAsync(p => p.Name == $"restore-guard-{marker}");
    }

    // ══ 1) هوية مفقودة (0 / سالبة) → رفض قبل أي أمر SQL ══
    [Fact]
    public async Task Restore_MissingActor_Rejected()
    {
        string marker = $"br-miss-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            await using (var db = _fx.CreateContext())
                await SeedMarkerAsync(db, marker);

            var svc = new clsBackupService(_fx.CreateContext());
            var r0 = await svc.RestoreAsync(file, actorUserId: 0);
            Assert.False(r0.IsSuccess);
            Assert.Contains("مرفوضة", r0.Message);

            var svc2 = new clsBackupService(_fx.CreateContext());
            var rNeg = await svc2.RestoreAsync(file, actorUserId: -5);
            Assert.False(rNeg.IsSuccess);
            Assert.Contains("مرفوضة", rNeg.Message);

            Assert.True(await MarkerExistsAsync(marker)); // القاعدة لم تُلمس
        }
        finally { File.Delete(file); }
    }

    // ══ 2) مستخدم غير موجود → رفض ══
    [Fact]
    public async Task Restore_NonExistentActor_Rejected()
    {
        string marker = $"br-nousr-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            await using (var db = _fx.CreateContext())
                await SeedMarkerAsync(db, marker);

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, actorUserId: 99_999_999);
            Assert.False(r.IsSuccess);
            Assert.Contains("مرفوضة", r.Message);

            Assert.True(await MarkerExistsAsync(marker));
        }
        finally { File.Delete(file); }
    }

    // ══ 3) مستخدم غير مفعّل → رفض ══
    [Fact]
    public async Task Restore_InactiveUser_Rejected()
    {
        string marker = $"br-inact-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
            {
                await SeedMarkerAsync(db, marker);
                userId = await SeedUserAsync(db, marker, AppPermissions.AdminRoleValue, isActive: false);
            }

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, userId);
            Assert.False(r.IsSuccess);
            Assert.Contains("مرفوضة", r.Message);

            Assert.True(await MarkerExistsAsync(marker));
        }
        finally { File.Delete(file); }
    }

    // ══ 4) استدعاء مباشر للخدمة (تجاوز الواجهة) بدور بلا صلاحية الإعدادات → رفض ══
    // الملف موجود وصالح الوجود عمدًا: يثبت أن فحص التصريح يسبق أي فحص ملف
    // وأي أمر SQL — والرسالة هي رسالة التصريح لا رسالة الملف.
    [Fact]
    public async Task Restore_DirectServiceCall_WithoutSettingsPermission_Rejected()
    {
        string marker = $"br-direct-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile(); // موجود فعلًا
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
            {
                await SeedMarkerAsync(db, marker);
                userId = await SeedUserAsync(db, marker, AppPermissions.SalesInvoices); // بلا Settings
            }

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, userId);
            Assert.False(r.IsSuccess);
            Assert.Contains("صلاحية شاشة الإعدادات", r.Message);

            Assert.True(await MarkerExistsAsync(marker)); // لم تُنشأ حتى نسخة pre-restore
        }
        finally { File.Delete(file); }
    }

    // ══ 5) دور المحاسب الثابت (508) — مرفوض بنيويًا رغم نشاطه ══
    [Fact]
    public async Task Restore_AccountantRole_Rejected()
    {
        string marker = $"br-acct-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
                userId = await SeedUserAsync(db, marker, AppPermissions.AccountantRoleValue);

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, userId);
            Assert.False(r.IsSuccess);
            Assert.Contains("صلاحية شاشة الإعدادات", r.Message);
        }
        finally { File.Delete(file); }
    }

    // ══ 6) دور مصرّح (يمتلك بت الإعدادات) + ملف مفقود → يعتبر فحص التصريح ناجحًا ══
    // رسالة «الملف غير موجود» (لا رسالة التصريح): إثبات أن البوابة تفتح
    // لأصحاب الصلاحية فقط، والاستعادة نفسها لم تُنفَّذ (لا ملف أصلًا).
    [Theory]
    [InlineData(AppPermissions.Settings)]          // دور مخصص بصلاحية الإعدادات
    [InlineData(AppPermissions.AdminRoleValue)]    // مدير النظام
    public async Task Restore_AuthorizedRole_MissingFile_FailsOnFileValidation(int roleValue)
    {
        string marker = $"br-ok-{roleValue}-{Guid.NewGuid():N}"[..12];
        string missing = Path.Combine(Path.GetTempPath(), $"sayl-missing-{Guid.NewGuid():N}.bak");
        if (File.Exists(missing)) File.Delete(missing);

        int userId;
        await using (var db = _fx.CreateContext())
            userId = await SeedUserAsync(db, marker, roleValue);

        var svc = new clsBackupService(_fx.CreateContext());
        var r = await svc.RestoreAsync(missing, userId);
        Assert.False(r.IsSuccess);
        Assert.Contains("ملف النسخة الاحتياطية غير موجود", r.Message); // تجاوز البوابة، فشل لاحق ملف
    }

    // ══ 7) مسار الفشل الآمن: مصرّح + ملف تالف → RESTORE يفشل والقاعدة سليمة ══
    // تُنفَّذ نسخة pre-restore حقيقية (BACKUP آمن لقاعدة الاختبار المعزولة)،
    // ثم يفشل RESTORE على الملف التالف — يُثبت أن القاعدة بقيت intact
    // ولم تُعلق في وضع SINGLE_USER (استعلام لاحق ناجح).
    [Fact]
    public async Task Restore_FailurePath_DatabaseUntouchedAndAccessible()
    {
        // تحقق هوية قاعدة الاختبار قبل أي أمر قد يكون تدميريًا (شرط السلامة)
        await using (var identityCheck = _fx.CreateContext())
        {
            Assert.Equal(DatabaseFixture.DbName, identityCheck.Database.GetDbConnection().Database);
        }

        string marker = $"br-fail-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
            {
                await SeedMarkerAsync(db, marker);
                userId = await SeedUserAsync(db, marker, AppPermissions.AdminRoleValue);
            }

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, userId);
            Assert.False(r.IsSuccess); // فشل (نسخة وقائية أو RESTORE نفسه) — لا نجاح أبدًا

            // القاعدة intact: العلامة باقية، والاتصال يعمل (لم تُعلق SINGLE_USER)
            Assert.True(await MarkerExistsAsync(marker));
            await using (var after = _fx.CreateContext())
            {
                int productCount = await after.Products.CountAsync();
                Assert.True(productCount > 0);
            }
        }
        finally { File.Delete(file); }
    }

    // ══ 8) تدقيق بدء الاستعادة: من بدأ ومتى — يُسجَّل بعد التصريح وفحص الملف ══
    [Fact]
    public async Task Restore_AuthorizedAttempt_AuditsInitiationWithActorAndTime()
    {
        string marker = $"br-audit-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
                userId = await SeedUserAsync(db, marker, AppPermissions.AdminRoleValue);

            DateTime before = DateTime.UtcNow.AddSeconds(-1);
            var svc = new clsBackupService(_fx.CreateContext());
            await svc.RestoreAsync(file, userId); // سيفشل لاحقًا (ملف تالف) — البدء يجب أن يكون مسجلًا

            await using (var db = _fx.CreateContext())
            {
                var log = await db.SystemLogs
                    .Where(l => l.UserId == userId && l.Action == "RestoreDatabase")
                    .OrderByDescending(l => l.CreatedAt)
                    .FirstOrDefaultAsync();

                Assert.NotNull(log);
                Assert.Contains(Path.GetFileName(file), log!.Details);
                Assert.True(log.CreatedAt >= before, "طابع زمن التدقيق يجب أن يكون وقت المحاولة الفعلية");
            }
        }
        finally { File.Delete(file); }
    }

    // ══ 9) محاولة غير مصرّح بها لا تُولّد سجل تدقيق بدء (الفحص قبل التدقيق) ══
    [Fact]
    public async Task Restore_UnauthorizedAttempt_NoInitiationAuditWritten()
    {
        string marker = $"br-noaudit-{Guid.NewGuid():N}"[..12];
        string file = CreateGarbageBackupFile();
        try
        {
            int userId;
            await using (var db = _fx.CreateContext())
                userId = await SeedUserAsync(db, marker, AppPermissions.Reports); // بلا Settings

            var svc = new clsBackupService(_fx.CreateContext());
            var r = await svc.RestoreAsync(file, userId);
            Assert.False(r.IsSuccess);

            await using (var db = _fx.CreateContext())
            {
                bool logged = await db.SystemLogs
                    .AnyAsync(l => l.UserId == userId && l.Action == "RestoreDatabase");
                Assert.False(logged);
            }
        }
        finally { File.Delete(file); }
    }
}
