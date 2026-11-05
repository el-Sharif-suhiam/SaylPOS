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
/// </summary>
public sealed partial class DatabaseFixture : IAsyncLifetime
{
    public const string DbName = "SaylCogsTest";
    public static string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    public Task InitializeAsync()
    {
        try
        {
            // إسقاط بقايا رَن سابق ثم البناء من الصفر
            DropDatabase();
            // السكربت ينشئ القاعدة بنفسه (CREATE DATABASE + USE) — يتصل بـmaster أولًا
            ExecuteBatches(LoadSaylDbScriptWithoutUnitCost(), master: true);
            // مسار الإقلاع الفعلي: EnsureSchema يضيف ما تأخر عن SaylDB.sql (مثل IsQuarantined)
            ExecuteBatches(clsUsersService.EnsureSchemaSql);
            ExecuteBatches(LoadUpgrade11Script());
            // مسار الإقلاع الفعلي: أعمدة الجرد الجديدة (CountedAt / BookQuantityAtCount)
            ExecuteBatches(LoadUpgrade12Script());
            // مسار الإقلاع الفعلي: عمود idempotency (PostedMovementId) + فهرس الحركات المرتبطة
            ExecuteBatches(LoadUpgrade13Script());
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
        DropDatabase();
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

    private static void DropDatabase()
    {
        var csb = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = $@"
IF DB_ID(N'{DbName}') IS NOT NULL
BEGIN
    ALTER DATABASE {DbName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {DbName};
END";
            cmd.ExecuteNonQuery();
        }
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
