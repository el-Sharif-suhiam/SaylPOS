using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

[CollectionDefinition("database")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture> { }

/// <summary>
/// قاعدة بيانات تكامل حقيقية على LocalDB لكل رَن اختبارات:
/// تُبنى من SaylDB.sql (مع استبدال اسم القاعدة وحذف سطر UnitCost
/// كي يُنشئه سكربت Upgrade_11 نفسه — فيُختبر الترحيل أيضًا)،
/// ثم يُطبَّق Upgrade_11_BatchUnitCost.sql.
///
/// السلامة:
/// - اسم القاعدة عشوائي لكل رَن (SaylCogsTest_{hex}) فلا يتصادم رَنان متوازيان
///   ولا يُسقط أحدهما قاعدة الآخر.
/// - أي DROP يمر حصريًا عبر DropDatabase التي ترفض أي اسم لا يطابق نمط
///   قواعد الاختبار الحصري (SaylCogsTest أو SaylCogsTest_{32hex})، وتتحقق من
///   وجود القاعدة بالاسم المطلوب (باراميتر، لا حقن) قبل أي أمر إتلاف.
/// - كنسخ اليتامى (drop-by-prefix) يُنفَّذ فقط للأسماء المطابقة للنمط الحصري،
///   وبعمر يتجاوز 24 ساعة — كي لا يمس رَنًا موازيًا بدأ قبل قليل.
/// - بعد البناء يُتحقق من هوية القاعدة المتصلة (DB_NAME()) — إن تعذّر
///   التحقق يفشل الإقلاع قبل كتابة أي بيانات.
/// </summary>
public sealed partial class DatabaseFixture : IAsyncLifetime
{
    /// <summary>بادئة حصرية لقواعد بيانات الاختبارات — لا يُسقط أي اسم خارجها.</summary>
    public const string TestDbPrefix = "SaylCogsTest";

    /// <summary>الاسم الثابت القديم (قبل التوزيع العشوائي) — يُنظَّف كيتيم فقط.</summary>
    public const string LegacyDbName = "SaylCogsTest";

    /// <summary>اسم فريد لكل رَن — رَنان متوازيان لا يتشاركان قاعدة أبدًا.</summary>
    public static string DbName { get; } = $"{TestDbPrefix}_{Guid.NewGuid():N}";

    public static string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    public Task InitializeAsync()
    {
        try
        {
            // تنظيف اليتامى (أسماء قديمة تخص رَن سابق) ثم البناء من الصفر
            DropOrphanedTestDatabases();
            DropDatabase(DbName);
            // السكربت ينشئ القاعدة بنفسه (CREATE DATABASE + USE) — يتصل بـmaster أولًا
            ExecuteBatches(LoadSaylDbScriptWithoutUnitCost(), master: true);
            // مسار الإقلاع الفعلي: EnsureSchema يضيف ما تأخر عن SaylDB.sql (مثل IsQuarantined)
            ExecuteBatches(clsUsersService.EnsureSchemaSql);
            ExecuteBatches(LoadUpgrade11Script());
            // مسار الإقلاع الفعلي: أعمدة الجرد الجديدة (CountedAt / BookQuantityAtCount)
            ExecuteBatches(LoadUpgrade12Script());
            // مسار الإقلاع الفعلي: عمود idempotency (PostedMovementId) + فهرس الحركات المرتبطة
            ExecuteBatches(LoadUpgrade13Script());
            // تدقيق الرفض: SystemLogs.UserId قابل للـ NULL (محاولات دخول بلا هوية)
            ExecuteBatches(LoadUpgrade14Script());
            // بوابة فشل-آمن: الهوية يجب أن تُتحقق قبل أن يلمس أي اختبار البيانات
            VerifyTestDatabaseIdentity();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fixture] init failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public Task DisposeAsync()
    {
        // إغلاق أي جلسة جرد مفتوحة تركها اختبار فاشل — يمنع تسرب الجلسة إلى الاختبارات التالية.
        try { CloseOpenSessionIfAny(); } catch { /* ignore cleanup errors */ }
        // إسقاط قاعدة هذا الرَن فقط — أي شيء آخر خارج النطاق تمامًا
        DropDatabase(DbName);
        return Task.CompletedTask;
    }

    private void CloseOpenSessionIfAny()
    {
        // Minimal close: count all unposted lines at their current book quantities, post, ignore errors.
        try
        {
            using var db = CreateContext();
            if (!db.StockCounts.Any(sc => !sc.IsPosted)) return;
            // find a test user with the Inventory permission (posting is gated in the service)
            int? uid = db.Users.AsNoTracking()
                .Where(u => u.IsActive && (u.Role.RoleValue & Common.Utils.AppPermissions.Inventory) != 0)
                .OrderByDescending(u => u.Role.RoleValue)
                .Select(u => (int?)u.UserId).FirstOrDefault();
            if (!uid.HasValue) return;
            var svc = new Sayl.Core.Services.clsStockCountService(db);
            foreach (var l in db.StockCounts.Where(sc => !sc.IsPosted).ToList())
            {
                if (!l.CountedQuantity.HasValue)
                {
                    var inv = db.Inventories.Find(l.ProductId);
                    l.CountedQuantity = inv?.QuantityAvailable ?? 0;
                    l.BookQuantityAtCount = inv?.QuantityAvailable ?? 0;
                    l.CountedAt = DateTime.UtcNow;
                }
            }
            db.SaveChanges();
            svc.PostSessionAsync(uid.Value).GetAwaiter().GetResult();
        }
        catch { /* best-effort cleanup */ }
    }

    public SaylDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SaylDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new SaylDbContext(options);
    }

    // ─────────────────────────────────────────────────

    private static string RepoRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");

    private static string LoadSaylDbScriptWithoutUnitCost()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "SaylDB.sql"));
        // قاعدة اختبار باسمها — لا تلمس SaylDB الحقيقية أبدًا
        script = script.Replace("SaylDB", DbName);
        // حذف العمود الجديد ليضيفه سكربت الترقية (اختبار الترحيل الفعلي)
        script = Regex_UnitCostColumn().Replace(script, "");
        return script;
    }

    private static string LoadUpgrade11Script() =>
        File.ReadAllText(Path.Combine(RepoRoot, "Upgrade_11_BatchUnitCost.sql"));

    private static string LoadUpgrade12Script() =>
        File.ReadAllText(Path.Combine(RepoRoot, "Upgrade_12_StockCountCountedAt.sql"));

    private static string LoadUpgrade13Script() =>
        File.ReadAllText(Path.Combine(RepoRoot, "Upgrade_13_StockCountPostedMovementId.sql"));

    private static string LoadUpgrade14Script() =>
        File.ReadAllText(Path.Combine(RepoRoot, "Upgrade_14_SystemLogsUserIdNullable.sql"));

    /// <summary>
    /// إسقاط قاعدة اختبار بالاسم المطلوب — بعد حاجزين إلزاميين:
    /// 1) الاسم يطابق نمط قواعد الاختبار الحصري (وإلا رفض صريح — فشل-آمن).
    /// 2) وجود القاعدة بهذا الاسم بالضبط يُتحقق باراميتر قبل أي أمر إتلاف.
    /// </summary>
    private static void DropDatabase(string dbName)
    {
        if (!IsSafeTestDbName(dbName))
            throw new InvalidOperationException(
                $"[fixture] refusing to drop '{dbName}': name does not match the test-only " +
                $"'{TestDbPrefix}' pattern. Destructive SQL is limited to databases created by this fixture.");

        var csb = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();

        // التحقق من الهوية: القاعدة موجودة بالاسم المطلوب بالضبط (باراميتر — لا حقن ولا wildcard)
        using (var check = cn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sys.databases WHERE name = @name";
            check.Parameters.AddWithValue("@name", dbName);
            if (check.ExecuteScalar() is null)
                return; // لا شيء يُسقط
        }

        // الاسم مُتحقَّق منه بالـregex (حروف/أرقام/شرطة سفلية فقط) فالاقتباس آمن
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}];";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// تنظيف قواعد يتيمة من رَن سابق انهار قبل Dispose: فقط الأسماء المطابقة
    /// للنمط الحصري، والعتيقة (> 24 ساعة) — كي لا يلمس هذا الرَن قاعدة
    /// رَن متوازٍ بدأ قبل قليل. الاسم الثابت القديم (SaylCogsTest) يُنظَّف دائمًا:
    /// لم يُنشئه سوى النسخة القديمة من هذا الفيكسر.
    /// </summary>
    private static void DropOrphanedTestDatabases()
    {
        var csb = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        // [ _ ] داخل LIKE يطابق حرف الشرطة السفلية حرفيًا (لا wildcard)
        cmd.CommandText = @"
SELECT name, create_date FROM sys.databases
WHERE name = @legacy OR name LIKE @prefixPattern";
        cmd.Parameters.AddWithValue("@legacy", LegacyDbName);
        cmd.Parameters.AddWithValue("@prefixPattern", TestDbPrefix + "[_%]");

        var orphans = new List<(string Name, DateTime CreatedUtc)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(0);
                // sys.databases.create_date قيمة UTC (Kind=Unspecified من الدرايفر)
                orphans.Add((name, reader.GetDateTime(1)));
            }
        }

        foreach (var (name, createdUtc) in orphans)
        {
            // فشل-آمن: أي اسم غير مطابق للنمط الحصري يُتجاهل (ولا يُسقط أبدًا)
            if (!IsSafeTestDbName(name)) continue;
            bool legacy = string.Equals(name, LegacyDbName, StringComparison.OrdinalIgnoreCase);
            bool stale = createdUtc <= DateTime.UtcNow.AddHours(-24);
            if (legacy || stale)
                DropDatabase(name);
        }
    }

    /// <summary>نمط أسماء قواعد الاختبار الحصري: SaylCogsTest أو SaylCogsTest_{32hex}.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^SaylCogsTest(_[0-9a-fA-F]{32})?$")]
    private static partial System.Text.RegularExpressions.Regex Regex_SafeTestDbName();

    /// <summary>بوابة كل عملية إتلاف: true فقط لأسماء قواعد الاختبار الحصرية.</summary>
    internal static bool IsSafeTestDbName(string? name) =>
        !string.IsNullOrEmpty(name) && Regex_SafeTestDbName().IsMatch(name);

    /// <summary>
    /// تحقق هوية بعد البناء: الاتصال يجب أن يكون على قاعدة هذا الرَن تحديدًا.
    /// إن تعذّر التحقق — فشل صريح قبل أن يكتب أي اختبار بيانات.
    /// </summary>
    private static void VerifyTestDatabaseIdentity()
    {
        using var cn = new SqlConnection(ConnectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT DB_NAME()";
        var actual = cmd.ExecuteScalar() as string;
        if (!string.Equals(actual, DbName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"[fixture] test database identity could not be verified: connected to '{actual}', expected '{DbName}'. Aborting before any data is written.");
    }

    /// <summary>تنفيذ سكربت SQL مقسومًا على GO — SqlClient يشغل بـ QUOTED_IDENTIFIER/ANSI_NULLS ON افتراضيًا.</summary>
    internal static void ExecuteBatches(string script, bool master = false)
    {
        string cs = master
            ? new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString
            : ConnectionString;
        using var cn = new SqlConnection(cs);
        cn.Open();

        foreach (var batch in SplitOnGo(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;
            using var cmd = cn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 120;
            cmd.ExecuteNonQuery();
        }
    }

    internal static IEnumerable<string> SplitOnGo(string script)
    {
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                batches.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }
        batches.Add(current.ToString());
        return batches;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*UnitCost DECIMAL\(10,2\) NULL.*$\r?\n?", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex Regex_UnitCostColumn();
}
