using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ترقية Upgrade_11: عمود Batches.UnitCost يُنشأ، والدفعات القائمة تُرحَّل
/// إلى تكلفة أحدث بند شراء مرتبط بها — بيانات تاريخية تُحفظ لا تُمس.
/// (الـfixture نفسه يبني القاعدة بلا العمود ثم يشغل السكربت — فالاختبار
/// يثبت أنه أنشأه وترحّل على حالة ترقية حقيقية.)
/// </summary>
[Collection("database")]
public class BatchUnitCostUpgradeTests
{
    private readonly DatabaseFixture _fx;
    public BatchUnitCostUpgradeTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task ColumnExists_AfterUpgrade()
    {
        await using var db = _fx.CreateContext();
        var columns = await db.Database
            .SqlQuery<string>($@"SELECT name AS Value FROM sys.columns
                                 WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'UnitCost'")
            .ToListAsync();
        Assert.NotEmpty(columns); // الـfixture شغل Upgrade_11 على قاعدة بُنيت بلا العمود
    }

    [Fact]
    public async Task Backfill_LegacyBatch_FromLatestPurchaseItem()
    {
        await using var db = _fx.CreateContext();
        string marker = $"up-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        // حالة قديمة: دفعة بلا تكلفة مخزنة + بند شراء يحمل تكلفة
        var batch = new Batch { ProductId = seed.ProductId, BatchNumber = "B-LEGACY", BatchQuantity = 7, CreatedAt = DateTime.UtcNow };
        db.Batches.Add(batch);

        var product = await db.Products.FirstAsync(p => p.ProductId == seed.ProductId);
        product.CostPrice = 10m;

        var invoice = new Invoice
        {
            Type = enInvoicesType.purchase,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.paid,
            PartyType = 'S',
            PartyId = seed.SupplierId,
            UserId = seed.UserId,
            TotalAmount = 50m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 50m,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.InvoiceItems.Add(new InvoiceItem
        {
            InvoiceId = invoice.InvoiceId,
            ProductId = seed.ProductId,
            Quantity = 5,
            BatchId = batch.BatchId,
            ItemPrice = 10m,
            ItemCost = 10m
        });
        await db.SaveChangesAsync();

        // إعادة تشغيل سكربت الترقية (idempotent) — الترحيل يملأ الدفة القديمة
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_11_BatchUnitCost.sql")));

        await db.Entry(batch).ReloadAsync();
        Assert.Equal(10.00m, batch.UnitCost);
    }

    /// <summary>المسار الفعلي عند إقلاع التطبيق: EnsureSchemaUpgradesAsync على
    /// قاعدة قديمة بلا عمود UnitCost — يجب أن يُنشئه ويرحّل التكلفة التاريخية.</summary>
    [Fact]
    public async Task EnsureSchemaUpgradesAsync_AddsUnitCost_AndBackfills_OnLegacyDatabase()
    {
        await using var db = _fx.CreateContext();

        // محاكاة قاعدة قديمة: إسقاط العمود (البيانات المرجعية تُستعاد في finally)
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.Batches DROP COLUMN UnitCost;");
        try
        {
            string marker = $"rt-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);

            // الزرع عبر SQL مباشرة — قاعدة قديمة لا يعرفها نموذج EF (بلا عمود UnitCost)
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.Batches (ProductId, BatchNumber, BatchQuantity, CreatedAt)
VALUES ({seed.ProductId}, N'B-LEGACY-RT', 3, GETUTCDATE());
DECLARE @bid INT = SCOPE_IDENTITY();
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt)
VALUES (N'purchase', N'completed', N'paid', N'S', {seed.SupplierId}, {seed.UserId}, 30, 0, 0, 30, GETUTCDATE());
DECLARE @iid INT = SCOPE_IDENTITY();
INSERT INTO dbo.InvoiceItems (InvoiceId, ProductId, Quantity, BatchId, ItemPrice, ItemCost)
VALUES (@iid, {seed.ProductId}, 3, @bid, 10, 10);");

            // مسار الإقلاع الفعلي (Program.cs → EnsureOwnerSetup): لا سكربت خارجي هنا
            var svc = new clsUsersService(db);
            await svc.EnsureSchemaUpgradesAsync();

            var columns = await db.Database
                .SqlQuery<string>($@"SELECT name AS Value FROM sys.columns
                                     WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'UnitCost'")
                .ToListAsync();
            Assert.NotEmpty(columns);

            decimal? backfilled = (await db.Database.SqlQuery<decimal?>($@"
                SELECT b.UnitCost AS Value FROM dbo.Batches b
                WHERE b.ProductId = {seed.ProductId} AND b.BatchNumber = N'B-LEGACY-RT'")
                .ToListAsync()).Single();
            Assert.Equal(10.00m, backfilled);
        }
        finally
        {
            // إعادة الحالة المشتركة للـfixture مهما كانت النتيجة — بقية الاختبارات لا تتأثر
            await db.Database.ExecuteSqlRawAsync(
                "IF COL_LENGTH(N'dbo.Batches', N'UnitCost') IS NULL ALTER TABLE dbo.Batches ADD UnitCost DECIMAL(10,2) NULL;");
        }
    }

    /// <summary>إعادة التشغيل على قاعدة بها العمود بالفعل (تثبيت جديد أو ترقية سابقة):
    /// بلا أخطاء، والتكلفة المخزنة أثمن من أي بند شراء — لا تُستبدل ولا تُخترع قيمة.</summary>
    [Fact]
    public async Task Upgrade11_Rerun_WhenColumnAlreadyExists_KeepsExistingCosts()
    {
        await using var db = _fx.CreateContext();
        string marker = $"idem-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        var batch = new Batch { ProductId = seed.ProductId, BatchNumber = "B-IDEM", BatchQuantity = 4, UnitCost = 7.50m, CreatedAt = DateTime.UtcNow };
        db.Batches.Add(batch);

        var invoice = new Invoice
        {
            Type = enInvoicesType.purchase,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.paid,
            PartyType = 'S',
            PartyId = seed.SupplierId,
            UserId = seed.UserId,
            TotalAmount = 40m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 40m,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.InvoiceItems.Add(new InvoiceItem
        {
            InvoiceId = invoice.InvoiceId,
            ProductId = seed.ProductId,
            Quantity = 4,
            BatchId = batch.BatchId,
            ItemPrice = 10m,
            ItemCost = 10m
        });
        await db.SaveChangesAsync();

        // إعادة تشغيل سكربت الترقية ثم مسار الإقلاع — كلاهما يجب أن يمرا بلا خطأ
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_11_BatchUnitCost.sql")));
        await new clsUsersService(db).EnsureSchemaUpgradesAsync();

        await db.Entry(batch).ReloadAsync();
        Assert.Equal(7.50m, batch.UnitCost);
        Assert.Equal(4, batch.BatchQuantity); // لا مساس بالكميات
    }

    /// <summary>التثبيت الجديد: SaylDB.sql ينشئ العمود ضمن Batches، وEnsureSchemaSql
    /// يمر فوقه بلا محاولة إضافة مكررة (نفس مخطط الـfixture بعد الإضافة).</summary>
    [Fact]
    public async Task FreshInstallSchema_ContainsUnitCost_AndStartupUpgradeIsNoop()
    {
        string script = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SaylDB.sql"));
        Assert.Contains("UnitCost DECIMAL(10,2) NULL", script);

        await using var db = _fx.CreateContext();
        await new clsUsersService(db).EnsureSchemaUpgradesAsync(); // عمود موجود مسبقًا — يجب ألا يفشل

        var columns = await db.Database
            .SqlQuery<string>($@"SELECT name AS Value FROM sys.columns
                                 WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'UnitCost'")
            .ToListAsync();
        Assert.NotEmpty(columns);
    }

    /// <summary>التغليف: سكربت Upgrade_11 يجب أن يُنسخ إلى حزمة المثبّت
    /// (قائمة المصادر في build-installer.ps1 وقسم [Files] في setup.iss).</summary>
    [Fact]
    public async Task Installer_Includes_Upgrade11_Migration()
    {
        string repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
        string ps1 = await File.ReadAllTextAsync(Path.Combine(repoRoot, "installer", "build-installer.ps1"));
        string iss = await File.ReadAllTextAsync(Path.Combine(repoRoot, "installer", "setup.iss"));

        Assert.Contains("Upgrade_11_BatchUnitCost.sql", ps1);
        Assert.Contains("Upgrade_11_BatchUnitCost.sql", iss);
    }
}
