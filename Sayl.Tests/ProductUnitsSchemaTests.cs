using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using System.Runtime.CompilerServices;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// المرحلة 3 — مخطط وحدات المنتج الخامل (Upgrade_20 + Upgrade_21):
/// تكافؤ الترقية (SaylDB.sql التركيب الجديد == القاعدة المُرقَّاة على
/// كائنات المخطط الجديدة)، ترحيل صفوف الأساس (تكوين فقط)، أعمدة اللقطات
/// NULL للصفوف الإرثية، وحواجز المحرك: تفرّد الاسم، CHECK عامل الأساس،
/// والفهرس الفريد المُرشَّح لأحادية الأساس.
/// </summary>
[Collection("database")]
public class ProductUnitsSchemaTests
{
    private readonly DatabaseFixture _fx;
    public ProductUnitsSchemaTests(DatabaseFixture fx) => _fx = fx;

    // ═══ 1) تكافؤ الترقية: تركيب جديد (SaylDB.sql خام) == مُرقَّى (سكربتا 20/21) ═══

    /// <summary>قاعدة تركيب جديد مؤقتة من SaylDB.sql كاملةً (بلا أي سكربت
    /// ترقية) — تُبنى على نفس LocalDB وتُسقط في finally عبر حاجز الاسم الحصري.</summary>
    private static string BuildFreshInstallDatabase()
    {
        string dbName = $"{DatabaseFixture.TestDbPrefix}_{Guid.NewGuid():N}";
        string script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "SaylDB.sql"));
        script = script.Replace("SaylDB", dbName);
        DatabaseFixture.ExecuteBatches(script, master: true);
        return dbName;
    }

    private static string ConnectionStringFor(string dbName) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={dbName};Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>لقطة كتالوج طبيعية (Kind|Name|Detail) لكل كائنات المخطط
    /// الجديدة: أعمدة/فهارس/قيود الجدول الجديد + أعمدة اللقطات والفهرس
    /// والمفتاح الأجنبي على InvoiceItems — الترتيب حتمي فيُقارَن مباشرة.</summary>
    private static async Task<List<string>> CatalogSnapshotAsync(string dbName)
    {
        await using var cn = new SqlConnection(ConnectionStringFor(dbName));
        await cn.OpenAsync();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
SELECT Kind + N'|' + Name + N'|' + Detail AS Value
FROM (
    SELECT N'PU.Column' AS Kind, c.name COLLATE DATABASE_DEFAULT AS Name,
           CONCAT(c.max_length, N'|', c.precision, N'|', c.scale, N'|', c.is_nullable) AS Detail
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.ProductUnits')
    UNION ALL
    SELECT N'PU.Index', i.name COLLATE DATABASE_DEFAULT, CONCAT(i.is_unique, N'|', i.has_filter, N'|', i.is_primary_key, N'|', i.is_unique_constraint)
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID(N'dbo.ProductUnits') AND i.name IS NOT NULL
    UNION ALL
    SELECT N'PU.Constraint', o.name COLLATE DATABASE_DEFAULT, o.type_desc COLLATE DATABASE_DEFAULT
    FROM sys.objects o
    WHERE o.parent_object_id = OBJECT_ID(N'dbo.ProductUnits')
      AND o.type IN ('PK','UQ','C','F','D')
    UNION ALL
    SELECT N'PU.Default', dc.name COLLATE DATABASE_DEFAULT, dc.definition COLLATE DATABASE_DEFAULT
    FROM sys.default_constraints dc
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.ProductUnits')
    UNION ALL
    SELECT N'II.Column', c.name COLLATE DATABASE_DEFAULT, CONCAT(c.max_length, N'|', c.precision, N'|', c.scale, N'|', c.is_nullable)
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.InvoiceItems')
      AND c.name IN (N'UnitName', N'UnitFactorToBase', N'EnteredQuantity', N'ProductUnitId')
    UNION ALL
    SELECT N'II.Index', i.name COLLATE DATABASE_DEFAULT, CONCAT(i.is_unique, N'|', i.has_filter)
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID(N'dbo.InvoiceItems') AND i.name = N'IX_InvoiceItems_ProductUnitId'
    UNION ALL
    SELECT N'II.FK', fk.name COLLATE DATABASE_DEFAULT,
           CONCAT(OBJECT_NAME(fk.referenced_object_id) COLLATE DATABASE_DEFAULT, N'|', fk.delete_referential_action)
    FROM sys.foreign_keys fk
    WHERE fk.name = N'FK_ProductUnits_ProductUnitId'
) t
ORDER BY Kind, Name;";
        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>تكافؤ الترقية على كائنات المرحلة 3: قاعدة تركيب جديد من
    /// SaylDB.sql كاملةً مقابل قاعدة الـfixture (المبنية بلا UnitCost ثم
    /// رُقِّيت بسكربتات 11..21) — الكتالوج الطبيعي للكائنات الجديدة متطابق.
    /// ويُثبت كذلك أن سكربتا 20/21 يمرّان فوق المخطط الكامل بلا فشل (noop).</summary>
    [Fact]
    public async Task UpgradeParity_FreshInstallSchema_Matches_UpgradedSchema_ForNewObjects()
    {
        string scratch = BuildFreshInstallDatabase();
        try
        {
            var fresh = await CatalogSnapshotAsync(scratch);
            var upgraded = await CatalogSnapshotAsync(DatabaseFixture.DbName);

            // تقرير تشخيصي عند الفشل: أي كائن اختلف
            var missing = fresh.Except(upgraded).ToList();
            var extra = upgraded.Except(fresh).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                "Parity mismatch.\nMissing in upgraded: " + string.Join("; ", missing) +
                "\nExtra in upgraded: " + string.Join("; ", extra));
            Assert.NotEmpty(fresh); // اللقطة ليست فارغة — الفحص يعمل فعلًا

            // سكربتا الترقية فوق التركيب الجديد الكامل: noop حقيقي بلا أخطاء
            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_20_ProductUnits.sql")));
            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_21_TransactionUnitSnapshots.sql")));
        }
        finally
        {
            DropScratchDatabase(scratch);
        }
    }

    /// <summary>إسقاط قاعدة التركيب المؤقتة — بنفس حاجزَي الـfixture: نمط
    /// الاسم الحصري ثم التحقق بالوجود قبل أي أمر إتلاف.</summary>
    private static void DropScratchDatabase(string dbName)
    {
        if (!DatabaseFixture.IsSafeTestDbName(dbName))
            throw new InvalidOperationException($"refusing to drop '{dbName}': outside the test-only pattern.");

        var csb = new SqlConnectionStringBuilder(DatabaseFixture.ConnectionString) { InitialCatalog = "master" };
        using var cn = new SqlConnection(csb.ConnectionString);
        cn.Open();
        using (var check = cn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sys.databases WHERE name = @name";
            check.Parameters.AddWithValue("@name", dbName);
            if (check.ExecuteScalar() is null)
                return;
        }
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}];";
        cmd.ExecuteNonQuery();
    }

    // ═══ 2) ترحيل صفوف الأساس: تكوين فقط — صف واحد بعامل 1 لكل منتج ═══

    /// <summary>كل منتج (الترحيل + بذر SaylDB.sql + TestSeed) له صف أساس
    /// واحد بالضبط: FactorToBase = 1، نشط، والاسم مرآة ISNULL(Unit,'وحدة').
    /// ثم Idempotency: إعادة التشغيل (ensure + السكربت) لا تكرر ولا تغيّر.</summary>
    [Fact]
    public async Task BaseRowBackfill_EveryProduct_HasExactlyOneBaseRow_Factor1_MirrorsUnit()
    {
        await using var db = _fx.CreateContext();
        var svc = new clsProductService(db);

        // مسار الإقلاع أولًا: منتجات بُذرت مباشرة (بلا خدمة الإضافة) تكتسب
        // صفوف أساسها من الترحيل — نفس ما يحدث لأي قاعدة قائمة عند الإقلاع.
        await svc.EnsureProductUnitsSchemaAsync();

        const string violationsSql = @"
SELECT
    (SELECT COUNT(*) FROM dbo.Products p
     WHERE (SELECT COUNT(*) FROM dbo.ProductUnits pu
            WHERE pu.ProductId = p.ProductId AND pu.IsBase = 1) <> 1)
  + (SELECT COUNT(*) FROM dbo.ProductUnits WHERE IsBase = 1 AND (FactorToBase <> 1 OR IsActive = 0))
  + (SELECT COUNT(*) FROM dbo.Products p
     JOIN dbo.ProductUnits pu ON pu.ProductId = p.ProductId AND pu.IsBase = 1
     WHERE pu.Name <> ISNULL(NULLIF(LTRIM(RTRIM(p.Unit)), N''), N'وحدة')) AS Value";
        int violationsBefore = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(violationsSql)).ToListAsync()).Single();
        Assert.Equal(0, violationsBefore);

        // المنتج التجريبي المبذور من SaylDB.sql: أساسه 'قطعة' (مرآة Unit)
        string demoBase = (await db.Database.SqlQuery<string>($@"
            SELECT pu.Name AS Value
            FROM dbo.ProductUnits pu
            JOIN dbo.Products p ON p.ProductId = pu.ProductId
            WHERE p.Barcode = N'6281002123450' AND pu.IsBase = 1").ToListAsync()).Single();
        Assert.Equal("قطعة", demoBase);

        int baseRowsBefore = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(
            "SELECT COUNT(*) AS Value FROM dbo.ProductUnits WHERE IsBase = 1")).ToListAsync()).Single();
        int totalRowsBefore = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(
            "SELECT COUNT(*) AS Value FROM dbo.ProductUnits")).ToListAsync()).Single();

        // إعادة تشغيل مسار الإقلاع الفعلي + السكربت: لا تكرار ولا اختراع
        await svc.EnsureProductUnitsSchemaAsync();
        await svc.EnsureProductUnitsSchemaAsync();
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_20_ProductUnits.sql")));

        int baseRowsAfter = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(
            "SELECT COUNT(*) AS Value FROM dbo.ProductUnits WHERE IsBase = 1")).ToListAsync()).Single();
        int totalRowsAfter = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(
            "SELECT COUNT(*) AS Value FROM dbo.ProductUnits")).ToListAsync()).Single();
        Assert.Equal(baseRowsBefore, baseRowsAfter);
        Assert.Equal(totalRowsBefore, totalRowsAfter); // لا صفوف بديلة تُخترع أبدًا

        int violationsAfter = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(violationsSql)).ToListAsync()).Single();
        Assert.Equal(0, violationsAfter);
    }

    // ═══ 3) أعمدة اللقطات: NULL للصفوف الإرثية — لا تُختلق تاريخ ═══

    /// <summary>المرحلة 3 لا كاتب للقطات بعد (المرحلة 4 تُدخلها): كل صف
    /// فواتير قائم/جديد يجب أن تبقى أعمدته الأربعة NULL — لا افتراضيات
    /// ولا ترحيل يختلق وحدة أو عاملًا لتاريخ لا يملكها.</summary>
    [Fact]
    public async Task SnapshotColumns_AreNull_ForLegacyRows_AndHaveNoDefaults()
    {
        await using var db = _fx.CreateContext();
        string marker = $"pu21-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        var invoice = new Invoice
        {
            Type = Common.Enums.enInvoicesType.sale,
            Status = Common.Enums.enInvoicesStatus.completed,
            PaymentStatus = Common.Enums.enPaymentStatus.paid,
            PartyType = 'C',
            PartyId = seed.CustomerId,
            UserId = seed.UserId,
            TotalAmount = 20m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 20m,
            InvoiceNumber = TestSeed.NewInvoiceNumber(),
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        db.InvoiceItems.Add(new InvoiceItem
        {
            InvoiceId = invoice.InvoiceId,
            ProductId = seed.ProductId,
            Quantity = 2,
            ItemPrice = 10m
        });
        await db.SaveChangesAsync();

        var snapshot = (await db.Database.SqlQuery<LegacySnapshotRow>($@"
            SELECT UnitName AS UnitName,
                   UnitFactorToBase AS UnitFactorToBase,
                   EnteredQuantity AS EnteredQuantity,
                   ProductUnitId AS ProductUnitId
            FROM dbo.InvoiceItems
            WHERE InvoiceItemId = {db.InvoiceItems.Local.Single(i => i.InvoiceId == invoice.InvoiceId).InvoiceItemId}")
            .ToListAsync()).Single();
        Assert.Null(snapshot.UnitName);
        Assert.Null(snapshot.UnitFactorToBase);
        Assert.Null(snapshot.EnteredQuantity);
        Assert.Null(snapshot.ProductUnitId);

        // القاعدة كلها: لا افتراضيات على أعمدة اللقطات — NULL بلا استثناء
        int defaultCount = (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value
            FROM sys.default_constraints dc
            JOIN sys.columns c ON c.column_id = dc.parent_column_id
                 AND c.object_id = dc.parent_object_id
            WHERE dc.parent_object_id = OBJECT_ID(N'dbo.InvoiceItems')
              AND c.name IN (N'UnitName', N'UnitFactorToBase', N'EnteredQuantity', N'ProductUnitId')")
            .ToListAsync()).Single();
        Assert.Equal(0, defaultCount);
    }

    // ═══ 4..6) حواجز المحرك على ProductUnits ═══

    /// <summary>UQ_ProductUnits_Product_Name: نفس (المنتج، الاسم) يُرفض
    /// — حتى بين صفوف غير أساسية (مرحلة لاحقة).</summary>
    [Fact]
    public async Task UniqueNameConstraint_DuplicateNameForSameProduct_IsRejected()
    {
        await using var db = _fx.CreateContext();
        string marker = $"pu-nm-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);
        string unitName = $"كرتون-{marker}";

        await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
VALUES ({seed.ProductId}, N'{unitName}', 12, 0, 1)");

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
VALUES ({seed.ProductId}, N'{unitName}', 24, 0, 1)"));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($@"
DELETE FROM dbo.ProductUnits WHERE ProductId = {seed.ProductId} AND Name = N'{unitName}'");
        }
    }

    /// <summary>CK_ProductUnits_BaseFactor: صف أساس بعامل <> 1 مرفوض
    /// — الأساس بتعريفه هو نفسه (عامل 1).</summary>
    [Fact]
    public async Task BaseFactorCheck_BaseRowWithFactorNotOne_IsRejected()
    {
        await using var db = _fx.CreateContext();
        string marker = $"pu-bf-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        // حاجز الاسم مُلبّى (اسم فريد) — الرفض من CHECK لا من التفرد
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
VALUES ({seed.ProductId}, N'كرتون-{marker}', 12, 1, 1)"));
    }

    /// <summary>UQ_ProductUnits_Base (فهرس فريد مُرشَّح): أساس ثانٍ لنفس
    /// المنتج مرفوض على مستوى المحرك — أحادية الأساس حاجز قاعدي لا فحص تطبيقي.</summary>
    [Fact]
    public async Task FilteredUniqueBaseIndex_SecondBaseRow_IsRejected()
    {
        await using var db = _fx.CreateContext();
        string marker = $"pu-bi-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        // مسار الإقلاع: منتج البذر المباشر يكتسب صف أساسه من الترحيل
        await new clsProductService(db).EnsureProductUnitsSchemaAsync();

        // صف الأساس موجود للمنتج (بذر/ترحيل) — أساس ثانٍ بعامل 1 يُرفض بـ2601
        int baseRows = (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM dbo.ProductUnits
            WHERE ProductId = {seed.ProductId} AND IsBase = 1").ToListAsync()).Single();
        Assert.Equal(1, baseRows);

        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
VALUES ({seed.ProductId}, N'حبة-{marker}', 1, 1, 1)"));
    }

    // ═══ 7) مسار الإقلاع: اللقطة على مخطط كامل noop بلا أخطاء ═══

    /// <summary>الترتيب الحقيقي للإقلاع (Program.cs → EnsureOwnerSetup):
    /// EnsureProductUnitsSchemaAsync ثم EnsureInvoiceItemSnapshotSchemaAsync
    /// على مخطط مكتمل — لا-op حقيقي بلا فشل (idempotency عبر المسارات).</summary>
    [Fact]
    public async Task EnsureMethods_OnCompleteSchema_AreNoop_AndIdempotent()
    {
        await using var db = _fx.CreateContext();
        var svc = new clsProductService(db);

        await svc.EnsureProductUnitsSchemaAsync();
        await svc.EnsureInvoiceItemSnapshotSchemaAsync();
        await svc.EnsureProductUnitsSchemaAsync(); // الإقلاع الثاني
        await svc.EnsureInvoiceItemSnapshotSchemaAsync();

        int tables = (await db.Database.SqlQuery<int>(FormattableStringFactory.Create(
            "SELECT COUNT(*) AS Value FROM sys.tables WHERE name = N'ProductUnits'")).ToListAsync()).Single();
        Assert.Equal(1, tables);
        int cols = (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems')
              AND name IN (N'UnitName', N'UnitFactorToBase', N'EnteredQuantity', N'ProductUnitId')").ToListAsync()).Single();
        Assert.Equal(4, cols);
    }

    private sealed class LegacySnapshotRow
    {
        public string? UnitName { get; set; }
        public decimal? UnitFactorToBase { get; set; }
        public decimal? EnteredQuantity { get; set; }
        public int? ProductUnitId { get; set; }
    }
}
