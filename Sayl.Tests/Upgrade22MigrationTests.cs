using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Upgrade_22 — المسار الحقيقي لترحيل الكميات: قاعدة إرثية مؤقتة تُبنى
/// بنمط الـfixture نفسه (SaylDB.sql باسم اختباري + Upgrade_11..21) ثم
/// يُرجَع الزمن: الأعمدة العشرة تُحوَّل إلى INT ويُسقَط
/// CK_StockReservations_Quantity — ثم يُشغَّل سكربت الترقية من مستودع
/// الشيفرة. يُثبت: النوع DECIMAL(10,3)، القيم المعروفة محفوظة (5 → 5.000)،
/// القيد يعود ويمنع 0، الافتراضيات سليمة، وإعادة التشغيل بلا أثر.
/// السلامة: اسم القاعدة عشوائي بنمط SaylCogsTest_{32hex} الحصري، والإسقاط
/// عبر حاجز النمط + التحقق باراميتر — نمط DatabaseFixture دون لمس قواعده.
/// </summary>
public partial class Upgrade22MigrationTests
{
    private static readonly string ScratchDbName = $"SaylCogsTest_{Guid.NewGuid():N}";

    private static string ScratchConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={ScratchDbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static string RepoRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");

    private static string LoadScript(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot, fileName));

    /// <summary>نمط أسماء قواعد الاختبار الحصري — نفس بوابة الـfixture.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^SaylCogsTest(_[0-9a-fA-F]{32})?$")]
    private static partial System.Text.RegularExpressions.Regex Regex_SafeTestDbName();

    /// <summary>تنفيذ سكربت مقسّمًا على GO — نفس نمط DatabaseFixture.ExecuteBatches.</summary>
    private static void ExecuteBatches(string script, bool master = false)
    {
        string cs = master
            ? new SqlConnectionStringBuilder(ScratchConnectionString) { InitialCatalog = "master" }.ConnectionString
            : ScratchConnectionString;
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

    private static IEnumerable<string> SplitOnGo(string script)
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

    /// <summary>إسقاط القاعدة المؤقتة — حاجزا الـfixture نفسهما: نمط الاسم
    /// الحصري أولًا ثم التحقق من الوجود باراميتر قبل أي أمر إتلاف.</summary>
    private static void SafeDropScratchDatabase()
    {
        if (!Regex_SafeTestDbName().IsMatch(ScratchDbName))
            throw new InvalidOperationException($"[test] refusing to drop '{ScratchDbName}': outside the test-only name pattern.");

        var csb = new SqlConnectionStringBuilder(ScratchConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();

        using (var check = cn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sys.databases WHERE name = @name";
            check.Parameters.AddWithValue("@name", ScratchDbName);
            if (check.ExecuteScalar() is null)
                return; // لا شيء يُسقط
        }

        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = $"ALTER DATABASE [{ScratchDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDbName}];";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>بناء قاعدة إرثية: مخطط كامل حتى Upgrade_21 (قبل الكسري) —
    /// بنمط الـfixture: SaylDB.sql باسم اختباري وبلا عمود UnitCost ثم
    /// EnsureSchema ثم سكربتات الترقية من مستودع الشيفرة.</summary>
    private static TestSeed BuildLegacyDatabase()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "SaylDB.sql"));
        script = script.Replace("SaylDB", ScratchDbName);
        script = System.Text.RegularExpressions.Regex.Replace(
            script, @"^\s*UnitCost DECIMAL\(10,2\) NULL.*$\r?\n?",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
        ExecuteBatches(script, master: true);
        ExecuteBatches(clsUsersService.EnsureSchemaSql);
        ExecuteBatches(LoadScript("Upgrade_11_BatchUnitCost.sql"));
        ExecuteBatches(LoadScript("Upgrade_12_StockCountCountedAt.sql"));
        ExecuteBatches(LoadScript("Upgrade_13_StockCountPostedMovementId.sql"));
        ExecuteBatches(LoadScript("Upgrade_14_SystemLogsUserIdNullable.sql"));
        ExecuteBatches(LoadScript("Upgrade_15_UtcDefaults.sql"));
        ExecuteBatches(LoadScript("Upgrade_16_DefaultCreditLimit.sql"));
        ExecuteBatches(LoadScript("Upgrade_17_CreditApprovals.sql"));
        ExecuteBatches(LoadScript("Upgrade_18_CreditApprovalGrantPolicy.sql"));
        ExecuteBatches(LoadScript("Upgrade_19_AnnualInvoiceNumbering.sql"));
        ExecuteBatches(LoadScript("Upgrade_20_ProductUnits.sql"));
        ExecuteBatches(LoadScript("Upgrade_21_TransactionUnitSnapshots.sql"));

        // بذر مرجعي عبر النموذج (حالة ما قبل التحويل — أعمدة لا تزال DECIMAL)
        var options = new DbContextOptionsBuilder<SaylDbContext>()
            .UseSqlServer(ScratchConnectionString)
            .Options;
        using var db = new SaylDbContext(options);
        return TestSeed.SeedAsync(db, $"up22-{Guid.NewGuid():N}"[..12]).GetAwaiter().GetResult();
    }

    /// <summary>إرجاع الزمن: القيد يُسقَط والأعمدة العشرة تُحوَّل إلى INT —
    /// حالة قاعدة ما قبل Upgrade_22 حرفيًا: الافتراضيات باقية على أعمدة
    /// INT (وهي التي يصطدم بها ALTER COLUMN على المسار الحقيقي) — فتُسقَط
    /// حول التحويل وتُعاد بنفس تعريفها كي يختبر السكربت الصعوبة الحقيقية.</summary>
    private static void RevertQuantityColumnsToInt()
    {
        ExecuteBatches(@"
IF OBJECT_ID(N'dbo.CK_StockReservations_Quantity', N'C') IS NOT NULL
    ALTER TABLE dbo.StockReservations DROP CONSTRAINT CK_StockReservations_Quantity;

DECLARE @df sysname, @dfdef nvarchar(max), @tbl sysname, @col sysname;
DECLARE df_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT dc.name, dc.definition, OBJECT_NAME(dc.parent_object_id), COL_NAME(dc.parent_object_id, dc.parent_column_id)
    FROM sys.default_constraints dc
    WHERE dc.parent_object_id IN (OBJECT_ID(N'dbo.Products'), OBJECT_ID(N'dbo.Inventory'),
                                  OBJECT_ID(N'dbo.InvoiceItems'), OBJECT_ID(N'dbo.StockReservations'))
      AND COL_NAME(dc.parent_object_id, dc.parent_column_id) IN
          (N'ReorderLevel', N'QuantityAvailable', N'QuantityReserved', N'Quantity');
OPEN df_cur;
FETCH NEXT FROM df_cur INTO @df, @dfdef, @tbl, @col;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC(N'ALTER TABLE dbo.' + @tbl + N' DROP CONSTRAINT ' + @df + N';');
    FETCH NEXT FROM df_cur INTO @df, @dfdef, @tbl, @col;
END
CLOSE df_cur; DEALLOCATE df_cur;

-- فهرس الكمية المتاحة يمنع ALTER COLUMN على عموده — يُسقَط حول التحويل
-- ويعود على أعمدة INT (الحالة الإرثية كانت تحمل الفهرس نفسه)
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Inventory_QuantityAvailable'
           AND object_id = OBJECT_ID(N'dbo.Inventory'))
    DROP INDEX IX_Inventory_QuantityAvailable ON dbo.Inventory;

ALTER TABLE dbo.Products ALTER COLUMN ReorderLevel INT NULL;
ALTER TABLE dbo.Batches ALTER COLUMN BatchQuantity INT NOT NULL;
ALTER TABLE dbo.Inventory ALTER COLUMN QuantityAvailable INT NULL;
ALTER TABLE dbo.Inventory ALTER COLUMN QuantityReserved INT NULL;
ALTER TABLE dbo.InvoiceItems ALTER COLUMN Quantity INT NOT NULL;
ALTER TABLE dbo.StockReservations ALTER COLUMN Quantity INT NOT NULL;
ALTER TABLE dbo.StockMovements ALTER COLUMN Quantity INT NOT NULL;
ALTER TABLE dbo.StockCounts ALTER COLUMN BookQuantity INT NOT NULL;
ALTER TABLE dbo.StockCounts ALTER COLUMN CountedQuantity INT NULL;
ALTER TABLE dbo.StockCounts ALTER COLUMN BookQuantityAtCount INT NULL;

-- الافتراضيات القديمة تعود كما كانت (أعمدة INT مع افتراضيات — الحالة الإرثية)
ALTER TABLE dbo.Products ADD CONSTRAINT DF_Products_ReorderLevel DEFAULT (0) FOR ReorderLevel;
ALTER TABLE dbo.Inventory ADD CONSTRAINT DF_Inventory_QuantityAvailable DEFAULT (0) FOR QuantityAvailable;
ALTER TABLE dbo.Inventory ADD CONSTRAINT DF_Inventory_QuantityReserved DEFAULT (0) FOR QuantityReserved;
ALTER TABLE dbo.InvoiceItems ADD CONSTRAINT DF_InvoiceItems_Quantity DEFAULT (1) FOR Quantity;
ALTER TABLE dbo.StockReservations ADD CONSTRAINT DF_StockReservations_Quantity DEFAULT (1) FOR Quantity;

CREATE INDEX IX_Inventory_QuantityAvailable ON dbo.Inventory(QuantityAvailable);");
    }

    /// <summary>صفوف معروفة بأعداد صحيحة على كل عمود من العشرة — تحفظ قيمتها
    /// بعد التحويل (5 → 5.000 دلاليًا).</summary>
    private static void InsertKnownLegacyRows(TestSeed seed)
    {
        ExecuteBatches($@"
UPDATE dbo.Products SET ReorderLevel = 5 WHERE ProductId = {seed.ProductId};
IF NOT EXISTS (SELECT 1 FROM dbo.Inventory WHERE ProductId = {seed.ProductId})
    INSERT INTO dbo.Inventory (ProductId, QuantityAvailable, QuantityReserved) VALUES ({seed.ProductId}, 0, 0);
UPDATE dbo.Inventory SET QuantityAvailable = 12, QuantityReserved = 2 WHERE ProductId = {seed.ProductId};

INSERT INTO dbo.Batches (ProductId, BatchNumber, BatchQuantity, CreatedAt)
VALUES ({seed.ProductId}, N'B-UP22', 7, GETUTCDATE());
DECLARE @bid INT = SCOPE_IDENTITY();

INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, InvoiceNumber, CreatedAt)
VALUES (N'purchase', N'completed', N'paid', N'S', {seed.SupplierId}, {seed.UserId}, 50, 0, 0, 50, N'{TestSeed.NewInvoiceNumber()}', GETUTCDATE());
DECLARE @iid INT = SCOPE_IDENTITY();

INSERT INTO dbo.InvoiceItems (InvoiceId, ProductId, Quantity, BatchId, ItemPrice, ItemCost)
VALUES (@iid, {seed.ProductId}, 5, @bid, 10, 10);
INSERT INTO dbo.StockReservations (InvoiceId, ProductId, Quantity)
VALUES (@iid, {seed.ProductId}, 5);
INSERT INTO dbo.StockMovements (ProductId, MovementType, Quantity, SourceType)
VALUES ({seed.ProductId}, N'purchase', -3, N'ManualAdjustment');
INSERT INTO dbo.StockCounts (CountSessionId, ProductId, ProductName, BookQuantity, CountedQuantity, CountedBy, BookQuantityAtCount)
VALUES (999001, {seed.ProductId}, N'UP22', 10, 9, {seed.UserId}, 10);");
    }

    /// <summary>عدد أعمدة الكميات العشرة التي هي DECIMAL(10,3) فعلًا —
    /// دقة 10 وسلّم 3 من sys.columns.</summary>
    private static async Task<int> DecimalColumnCountAsync()
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sys.columns c " +
            "JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE t.name = N'decimal' AND c.precision = 10 AND c.scale = 3 AND (" +
            string.Join(" OR ", KnownColumns.Select(k =>
                $"(c.object_id = OBJECT_ID(N'dbo.{k.Table}') AND c.name = N'{k.Column}')")) + ")";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // بذرة معروفة لأعمدة العشرة (جدول.عمود) — للفحص النوعي والقيمي
    private static readonly (string Table, string Column, decimal Expected)[] KnownColumns =
    {
        ("Products", "ReorderLevel", 5m),
        ("Batches", "BatchQuantity", 7m),
        ("Inventory", "QuantityAvailable", 12m),
        ("Inventory", "QuantityReserved", 2m),
        ("InvoiceItems", "Quantity", 5m),
        ("StockReservations", "Quantity", 5m),
        ("StockMovements", "Quantity", -3m),
        ("StockCounts", "BookQuantity", 10m),
        ("StockCounts", "CountedQuantity", 9m),
        ("StockCounts", "BookQuantityAtCount", 10m),
    };

    private static async Task<decimal> ReadScalarAsync(string sql)
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        object? raw = await cmd.ExecuteScalarAsync();
        Assert.NotNull(raw);
        return Convert.ToDecimal(raw);
    }

    private static async Task<int> ReadCountAsync(string sql)
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        object? raw = await cmd.ExecuteScalarAsync();
        Assert.NotNull(raw);
        return Convert.ToInt32(raw);
    }

    private static async Task<string> ReadStringAsync(string sql)
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        object? raw = await cmd.ExecuteScalarAsync();
        Assert.NotNull(raw);
        return Convert.ToString(raw)!;
    }

    [Fact]
    public async Task Upgrade22_OnLegacyIntDatabase_ConvertsColumns_PreservesValues_RestoresConstraint()
    {
        SafeDropScratchDatabase();
        try
        {
            // ── الحالة الإرثية: أعمدة INT بلا قيد الحجز ثم صفوف معروفة ──
            TestSeed seed = BuildLegacyDatabase();
            RevertQuantityColumnsToInt();
            InsertKnownLegacyRows(seed);

            // تحقق شرطي: التحويل العكسي واقع فعلًا (StockReservations.Quantity كان INT)
            Assert.Equal("int", await ReadStringAsync(
                "SELECT TYPE_NAME(system_type_id) AS Value FROM sys.columns " +
                "WHERE object_id = OBJECT_ID(N'dbo.StockReservations') AND name = N'Quantity'"));

            // ── المسار الحقيقي: سكربت الترقية من مستودع الشيفرة ──
            string upgrade22 = LoadScript("Upgrade_22_QuantityColumnsDecimal.sql");
            ExecuteBatches(upgrade22);

            // 1) الأعمدة العشرة كلها DECIMAL(10,3) — دقة 10 وسلّم 3
            Assert.Equal(10, await DecimalColumnCountAsync());

            // 2) القيم المعروفة محفوظة حرفيًا (5 → 5.000)
            foreach (var (table, column, expected) in KnownColumns)
            {
                decimal actual = await ReadScalarAsync(
                    $"SELECT CAST({column} AS DECIMAL(10,3)) AS Value FROM dbo.{table} " +
                    $"WHERE {RowFilter(table, seed)}");
                Assert.Equal(expected, actual);
            }

            // 3) القيد عاد (بالاسم) ويمنع الصفر وظيفيًا (إدراج 0 → رفض)
            Assert.Equal(1, await ReadCountAsync(
                "SELECT COUNT(*) AS Value FROM sys.check_constraints " +
                "WHERE object_id = OBJECT_ID(N'dbo.CK_StockReservations_Quantity', N'C')"));
            await Assert.ThrowsAnyAsync<SqlException>(() => InsertReservationWithQuantityAsync(seed, 0m));

            // 4) الافتراضيات سليمة: إدراج بلا كمية → 1.000 (الافتراضي باقٍ بعد ALTER COLUMN)
            await InsertReservationDefaultQuantityAsync(seed);
            Assert.Equal(1.000m, await ReadScalarAsync(
                "SELECT CAST(Quantity AS DECIMAL(10,3)) AS Value FROM dbo.StockReservations " +
                "WHERE ReservationId = (SELECT MAX(ReservationId) FROM dbo.StockReservations)"));
            // وقيود الافتراضيات الخمسة كلها موجودة بأعمدتها بعد التحويل
            Assert.Equal(5, await ReadCountAsync(
                "SELECT COUNT(*) AS Value FROM sys.default_constraints dc " +
                "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
                "WHERE (dc.parent_object_id = OBJECT_ID(N'dbo.Products') AND c.name = N'ReorderLevel') " +
                "OR (dc.parent_object_id = OBJECT_ID(N'dbo.Inventory') AND c.name IN (N'QuantityAvailable', N'QuantityReserved')) " +
                "OR (dc.parent_object_id = OBJECT_ID(N'dbo.InvoiceItems') AND c.name = N'Quantity') " +
                "OR (dc.parent_object_id = OBJECT_ID(N'dbo.StockReservations') AND c.name = N'Quantity')"));

            // ── إعادة التشغيل: بلا أخطاء وبلا أثر (idempotent) ──
            ExecuteBatches(upgrade22);
            Assert.Equal(10, await DecimalColumnCountAsync());
            Assert.Equal(5.000m, await ReadScalarAsync(
                $"SELECT CAST(Quantity AS DECIMAL(10,3)) AS Value FROM dbo.StockReservations WHERE {RowFilter("StockReservations", seed)}"));
        }
        finally
        {
            // تنظيف القاعدة المؤقتة بنمط السلامة مهما كانت النتيجة
            SafeDropScratchDatabase();
        }
    }

    /// <summary>مرشّح الصف المزروع المعروف لكل جدول — قيم seed أعداد صحيحة
    /// (معرّفات) فالاقتباس الآمن.</summary>
    private static string RowFilter(string table, TestSeed seed) => table switch
    {
        "Products" => $"ProductId = {seed.ProductId}",
        "Batches" => "BatchNumber = N'B-UP22'",
        "Inventory" => $"ProductId = {seed.ProductId}",
        "InvoiceItems" => "InvoiceId = (SELECT MIN(InvoiceId) FROM dbo.Invoices WHERE Type = N'purchase')",
        "StockReservations" => "InvoiceId = (SELECT MIN(InvoiceId) FROM dbo.Invoices WHERE Type = N'purchase') " +
                               $"AND ProductId = {seed.ProductId}",
        "StockMovements" => $"ProductId = {seed.ProductId} AND MovementType = N'purchase' AND SourceType = N'ManualAdjustment'",
        "StockCounts" => "CountSessionId = 999001",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    /// <summary>إدراج حجز بكمية محددة على فاتورة شراء جديدة — لعزل رفض القيد
    /// عن فهرس التفرّد (فاتورة جديدة = لا صف حجز سابق لها).</summary>
    private static async Task InsertReservationWithQuantityAsync(TestSeed seed, decimal quantity)
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, InvoiceNumber, CreatedAt)
VALUES (N'purchase', N'completed', N'paid', N'S', {seed.SupplierId}, {seed.UserId}, 0, 0, 0, 0, N'{TestSeed.NewInvoiceNumber()}', GETUTCDATE());
DECLARE @iid INT = SCOPE_IDENTITY();
INSERT INTO dbo.StockReservations (InvoiceId, ProductId, Quantity)
VALUES (@iid, {seed.ProductId}, {quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>إدراج حجز بلا كمية إطلاقًا — الافتراضي (1) يجب أن يسري.</summary>
    private static async Task InsertReservationDefaultQuantityAsync(TestSeed seed)
    {
        await using var cn = new SqlConnection(ScratchConnectionString);
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, InvoiceNumber, CreatedAt)
VALUES (N'purchase', N'completed', N'paid', N'S', {seed.SupplierId}, {seed.UserId}, 0, 0, 0, 0, N'{TestSeed.NewInvoiceNumber()}', GETUTCDATE());
DECLARE @iid INT = SCOPE_IDENTITY();
INSERT INTO dbo.StockReservations (InvoiceId, ProductId) VALUES (@iid, {seed.ProductId});";
        await cmd.ExecuteNonQueryAsync();
    }
}
