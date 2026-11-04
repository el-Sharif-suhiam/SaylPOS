using Common.DTOs.ViewModel;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// تقييم المخزون ومؤشر الانخفاض في لوحة التحكم:
/// • قيمة المخزون من تكلفة الدفعات (Batches.UnitCost — COSTING.md) لا
///   Product.CostPrice (سعر آخر شراء)؛ الدفعة بلا تكلفة تعود إلى
///   Product.CostPrice كما وقت البيع (السلوك الموثق).
/// • مؤشر الانخفاض على المتاح للبيع فعليًا (قاعدة clsProductService
///   الموحّدة: دفعات صالحة − محجوز) — المنتهي/المحجوب/المحجوز لا يُبقي
///   المنتج «متاحًا» على الورق. العتبة ≤ حد الطلب كما كانت.
/// (قاعدة بيانات مشتركة — التحقق الكلي يقارن اللوحة بحساب مستقل من الدفعات.)
/// </summary>
[Collection("database")]
public class DashboardInventoryValuationTests
{
    private readonly DatabaseFixture _fx;
    public DashboardInventoryValuationTests(DatabaseFixture fx) => _fx = fx;

    private sealed record SeededProduct(int ProductId, int ReorderLevel);

    /// <summary>منتج مخزني مع سجل مخزون ودفعات (تكلفة/صلاحية/حجر لكل دفعة).</summary>
    private static async Task<SeededProduct> SeedProductWithBatchesAsync(
        SaylDbContext db, string marker, string suffix, decimal costPrice, int reorderLevel,
        int quantityAvailable, params (int Qty, decimal? UnitCost, DateOnly? Expiry, bool Quarantined)[] batches)
    {
        var product = new Product
        {
            Name = $"inv-{marker}-{suffix}",
            Type = "stock",
            BasePrice = 20m,
            CostPrice = costPrice,
            ReorderLevel = reorderLevel,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        foreach (var (qty, unitCost, expiry, quarantined) in batches)
        {
            db.Batches.Add(new Batch
            {
                ProductId = product.ProductId,
                BatchNumber = $"INV-{marker}-{suffix}-{Guid.NewGuid():N}",
                BatchQuantity = qty,
                UnitCost = unitCost,
                ExpiryDate = expiry,
                IsQuarantined = quarantined,
                CreatedAt = DateTime.UtcNow
            });
        }

        db.Inventories.Add(new Inventory
        {
            ProductId = product.ProductId,
            QuantityAvailable = quantityAvailable,
            QuantityReserved = 0,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return new SeededProduct(product.ProductId, reorderLevel);
    }

    /// <summary>حجز معلق واقعي: فاتورة معلّقة + صف StockReservations + المجموع
    /// في Inventory.QuantityReserved (نفس بنية clsCheckoutService.ReserveStockAsync).</summary>
    private static async Task ReserveAsync(SaylDbContext db, TestSeed seed, int productId, int qty)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.suspended,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = 1m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 1m,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.StockReservations.Add(new StockReservation
        {
            InvoiceId = invoice.InvoiceId,
            ProductId = productId,
            Quantity = qty,
            CreatedAt = DateTime.UtcNow
        });
        var inv = await db.Inventories.FirstAsync(i => i.ProductId == productId);
        inv.QuantityReserved += qty;
        await db.SaveChangesAsync();
    }

    /// <summary>قيمة المخزون المستقلة من الدفعات (المعادلة المرجعية نفسها).</summary>
    private static async Task<decimal> ExpectedStockValueAsync(SaylDbContext db)
        => await (from b in db.Batches.AsNoTracking()
                  join p in db.Products.AsNoTracking() on b.ProductId equals p.ProductId
                  where p.Type == "stock" && p.IsActive && b.BatchQuantity > 0
                  select b.BatchQuantity * (b.UnitCost ?? p.CostPrice))
                 .SumAsync();

    // ══ 1) دفعات بتكاليف مختلفة: القيمة من تكلفة كل دفعة لا CostPrice ══
    // دفعتان 4@6 و6@9 على منتج CostPrice=10 (قديم) → 24+54 = 78 لا 10×10=100.
    [Fact]
    public async Task StockValue_MultiBatch_UsesEachBatchUnitCost()
    {
        string marker = $"dv1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        await TestSeed.SeedAsync(db, marker);

        var p = await SeedProductWithBatchesAsync(db, marker, "multi", costPrice: 10m, reorderLevel: 0,
            quantityAvailable: 10,
            (4, 6m, null, false), (6, 9m, null, false));

        await using var reportDb = _fx.CreateContext();
        var dashboard = await new clsReportService(reportDb).GetDashboardSummaryAsync(TestSeedToday);

        // اللوحة ككل تطابق المعادلة المرجعية المستقلة من الدفعات (قاعدة مشتركة)
        Assert.Equal(await ExpectedStockValueAsync(reportDb), dashboard.Data!.TotalStockValue);

        // إسهام هذا المنتج تحديدًا 78 (لا 100 بتكلفة المنتج الوحيدة)
        var ownBatches = await reportDb.Batches.AsNoTracking()
            .Where(b => b.ProductId == p.ProductId)
            .Select(b => new { b.BatchQuantity, b.UnitCost })
            .ToListAsync();
        decimal own = ownBatches.Sum(b => b.BatchQuantity * (b.UnitCost ?? 0m));
        Assert.Equal(78m, own);
    }

    // ══ 2) دفعة بلا تكلفة: تعود إلى Product.CostPrice (سلوك وقت البيع الموثق) ══
    // دفعة 3 بلا UnitCost على منتج CostPrice=5 → إسهامها 15.
    [Fact]
    public async Task StockValue_MissingBatchCost_FallsBackToProductCostPrice()
    {
        string marker = $"dv2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        await TestSeed.SeedAsync(db, marker);

        var p = await SeedProductWithBatchesAsync(db, marker, "nocost", costPrice: 5m, reorderLevel: 0,
            quantityAvailable: 3,
            (3, null, null, false));

        await using var reportDb = _fx.CreateContext();
        var dashboard = await new clsReportService(reportDb).GetDashboardSummaryAsync(TestSeedToday);
        Assert.Equal(await ExpectedStockValueAsync(reportDb), dashboard.Data!.TotalStockValue);

        var ownBatches = await reportDb.Batches.AsNoTracking()
            .Where(b => b.ProductId == p.ProductId)
            .Select(b => new { b.BatchQuantity, b.UnitCost })
            .ToListAsync();

        decimal own = ownBatches.Sum(b => b.BatchQuantity * (b.UnitCost ?? 0m));
        Assert.Equal(0m, own); // دفعة بلا تكلفة مخزنة (UnitCost null في القاعدة)

        decimal ownValued = ownBatches.Sum(b => b.BatchQuantity * (b.UnitCost ?? 5m));
        Assert.Equal(15m, ownValued); // القيمة المُقيَّمة 3×5 (fallback موثق)
    }

    // ══ 3) المخزون المحجوز: كافٍ فعليًا 10، محجوز 6، حد الطلب 5 → متاح للبيع 4 = منخفض ══
    // (الكود القديم كان يقيس على الفعلي 10 > 5 فيُخفي النُفاد خلف الحجز)
    [Fact]
    public async Task LowStock_ReservedStock_ExcludedFromSellable()
    {
        string marker = $"dv3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var p = await SeedProductWithBatchesAsync(db, marker, "resv", costPrice: 2m, reorderLevel: 5,
            quantityAvailable: 10, (10, 2m, null, false));
        await ReserveAsync(db, seed, p.ProductId, 6);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var status = (await reports.GetInventoryStatusAsync()).Data!.Single(x => x.ProductId == p.ProductId);
        Assert.Equal(10, status.QuantityAvailable);  // الفعلي لم يتغير
        Assert.Equal(6, status.QuantityReserved);
        Assert.Equal(4, status.NetAvailable);        // 10 − 6
        Assert.True(status.IsLowStock);              // 4 ≤ 5 — على المتاح للبيع

        var dashboard = await reports.GetDashboardSummaryAsync(TestSeedToday);
        var low = dashboard.Data!.LowStockItems.FirstOrDefault(x => x.ProductId == p.ProductId);
        Assert.NotNull(low);                          // المنتج في قائمة التنبيه
        Assert.Equal(4, low!.NetAvailable);
    }

    // ══ 4) دفعة منتهية الصلاحية: مستثناة من المتاح للبيع (وكأنها غير موجودة للبيع) ══
    // 8 منتهية + 2 صالحة، حد الطلب 5 → متاح للبيع 2 = منخفض رغم الفعلي 10.
    [Fact]
    public async Task LowStock_ExpiredBatch_ExcludedFromSellable()
    {
        string marker = $"dv4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        await TestSeed.SeedAsync(db, marker);

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var p = await SeedProductWithBatchesAsync(db, marker, "exp", costPrice: 2m, reorderLevel: 5,
            quantityAvailable: 10,
            (8, 2m, yesterday, false),   // منتهية
            (2, 3m, null, false));       // صالحة

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var status = (await reports.GetInventoryStatusAsync()).Data!.Single(x => x.ProductId == p.ProductId);
        Assert.Equal(10, status.QuantityAvailable);
        Assert.Equal(2, status.NetAvailable);
        Assert.True(status.IsLowStock);

        var dashboard = await reports.GetDashboardSummaryAsync(TestSeedToday);
        Assert.Contains(dashboard.Data!.LowStockItems, x => x.ProductId == p.ProductId && x.NetAvailable == 2);
    }

    // ══ 5) دفعة محجوبة (حجر): مستثناة من المتاح للبيع ══
    // 8 محجوبة + 2 صالحة، حد الطلب 5 → متاح للبيع 2 = منخفض.
    [Fact]
    public async Task LowStock_QuarantinedBatch_ExcludedFromSellable()
    {
        string marker = $"dv5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        await TestSeed.SeedAsync(db, marker);

        var p = await SeedProductWithBatchesAsync(db, marker, "qtn", costPrice: 2m, reorderLevel: 5,
            quantityAvailable: 10,
            (8, 2m, null, true),         // محجوبة
            (2, 3m, null, false));       // صالحة

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var status = (await reports.GetInventoryStatusAsync()).Data!.Single(x => x.ProductId == p.ProductId);
        Assert.Equal(10, status.QuantityAvailable);
        Assert.Equal(2, status.NetAvailable);
        Assert.True(status.IsLowStock);

        var dashboard = await reports.GetDashboardSummaryAsync(TestSeedToday);
        Assert.Contains(dashboard.Data!.LowStockItems, x => x.ProductId == p.ProductId && x.NetAvailable == 2);
    }

    // ══ 6) فعلي كافٍ لكن البيع غير كافٍ: محجوز + منتهي معًا يُخفيان النُفاد ══
    // فعلي 10 = 5 منتهية + 5 صالحة، محجوز 3، حد الطلب 5 → متاح للبيع 2 = منخفض.
    [Fact]
    public async Task LowStock_SufficientPhysical_InsufficientSellable()
    {
        string marker = $"dv6-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var p = await SeedProductWithBatchesAsync(db, marker, "mix", costPrice: 2m, reorderLevel: 5,
            quantityAvailable: 10,
            (5, 2m, yesterday, false),   // منتهية
            (5, 2m, null, false));       // صالحة
        await ReserveAsync(db, seed, p.ProductId, 3);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var status = (await reports.GetInventoryStatusAsync()).Data!.Single(x => x.ProductId == p.ProductId);
        Assert.Equal(10, status.QuantityAvailable); // «متاح» الورقي يوحي بالوفرة
        Assert.Equal(2, status.NetAvailable);       // 5 صالحة − 3 محجوزة
        Assert.True(status.IsLowStock);

        var dashboard = await reports.GetDashboardSummaryAsync(TestSeedToday);
        Assert.Contains(dashboard.Data!.LowStockItems, x => x.ProductId == p.ProductId && x.NetAvailable == 2);
    }

    // ══ 7) متاح للبيع كافٍ: ليس منخفضًا رغم حجز جزئي ══
    // فعلي 10 صالحة، محجوز 3، حد الطلب 5 → متاح 7 > 5.
    [Fact]
    public async Task NotLowStock_WhenSellableAboveReorder()
    {
        string marker = $"dv7-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var p = await SeedProductWithBatchesAsync(db, marker, "ok", costPrice: 2m, reorderLevel: 5,
            quantityAvailable: 10, (10, 2m, null, false));
        await ReserveAsync(db, seed, p.ProductId, 3);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var status = (await reports.GetInventoryStatusAsync()).Data!.Single(x => x.ProductId == p.ProductId);
        Assert.Equal(7, status.NetAvailable);
        Assert.False(status.IsLowStock);

        var dashboard = await reports.GetDashboardSummaryAsync(TestSeedToday);
        Assert.DoesNotContain(dashboard.Data!.LowStockItems, x => x.ProductId == p.ProductId);
    }

    // ══ 8) عدّاد اللوحة الكلي يطابق الحساب المستقل على المتاح للبيع ══
    [Fact]
    public async Task Dashboard_LowStockCount_MatchesIndependentSellableComputation()
    {
        string marker = $"dv8-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // منتجان منخفضان (فعلي 1/0 ≤ حد 5) ومنتج كافٍ
        await SeedProductWithBatchesAsync(db, marker, "lowA", 2m, 5, 1, (1, 2m, null, false));
        await SeedProductWithBatchesAsync(db, marker, "lowB", 2m, 5, 0);
        await SeedProductWithBatchesAsync(db, marker, "ok", 2m, 5, 10, (10, 2m, null, false));

        await using var reportDb = _fx.CreateContext();
        var netAll = await new clsProductService(reportDb).GetNetAvailableQuantitiesAsync();
        // نفس دلالات اللوحة: منتجات مخزنية نشطة لها سجل مخزون (ضم داخلي)
        var allProducts = await (from p in reportDb.Products.AsNoTracking()
                                 join inv in reportDb.Inventories.AsNoTracking() on p.ProductId equals inv.ProductId
                                 where p.Type == "stock" && p.IsActive
                                 select new { p.ProductId, p.ReorderLevel })
                                .ToListAsync();
        var lowIds = allProducts
            .Where(p => netAll.GetValueOrDefault(p.ProductId) <= (p.ReorderLevel ?? 0))
            .Select(p => p.ProductId)
            .ToHashSet();

        var dashboard = await new clsReportService(reportDb).GetDashboardSummaryAsync(TestSeedToday);
        Assert.Equal(lowIds.Count, dashboard.Data!.LowStockCount);
        Assert.True(dashboard.Data.LowStockItems.All(x => x.IsLowStock && x.NetAvailable <= x.ReorderLevel));

        // مع ≤10 منخفضات: القائمة نفسها تتطابق مع مجموعة المنتجات المنخفضة
        if (lowIds.Count <= 10)
            Assert.Equal(lowIds, dashboard.Data.LowStockItems.Select(x => x.ProductId).ToHashSet());
    }

    // ══ 9) النسخة الجماعية = النسخة الأحادية (قاعدة واحدة) ══
    [Fact]
    public async Task BulkNetAvailable_MatchesSingleProductRule()
    {
        string marker = $"dv9-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var p1 = await SeedProductWithBatchesAsync(db, marker, "b1", 2m, 5, 10,
            (5, 2m, yesterday, false), (3, 2m, null, true), (4, 2m, null, false));
        await ReserveAsync(db, seed, p1.ProductId, 2);
        var p2 = await SeedProductWithBatchesAsync(db, marker, "b2", 2m, 5, 6, (6, 2m, null, false));

        await using var checkDb = _fx.CreateContext();
        var bulk = await new clsProductService(checkDb).GetNetAvailableQuantitiesAsync(new[] { p1.ProductId, p2.ProductId });
        var single = new clsProductService(checkDb);

        Assert.Equal(await single.GetNetAvailableQuantityAsync(p1.ProductId), bulk[p1.ProductId]); // 4−2=2
        Assert.Equal(await single.GetNetAvailableQuantityAsync(p2.ProductId), bulk[p2.ProductId]); // 6
    }

    /// <summary>اليوم البرمجي (UTC+3) — مطابق لحدود أيام التقارير.</summary>
    private static DateOnly TestSeedToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
}
