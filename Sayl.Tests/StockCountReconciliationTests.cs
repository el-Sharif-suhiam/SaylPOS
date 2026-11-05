using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// مصالحة الجرد الفعلي مع حركات ما بعد العد:
/// الفرق يُحسب مقابل الكمية الدفترية لحظة العد الفعلي (BookQuantityAtCount) —
/// لا لحظة بدء الجلسة. أي حركة (بيع/استلام/تسوية) حصلت بعد عد المنتج
/// تُحفظ في الرصيد النهائي: final = counted + (حركات ما بعد العد).
/// (سياق جديد لكل عملية — كما في SessionAttributionTests.)
/// </summary>
[Collection("database")]
public class StockCountReconciliationTests
{
    private readonly DatabaseFixture _fx;
    public StockCountReconciliationTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة (سياق جديد لكل عملية) ─────────────────

    private async Task<TestSeed> SeedAsync(string m)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, m);
    }

    private async Task<int> SeedProductAsync(string m, string name)
    {
        await using var db = _fx.CreateContext();
        var product = new Product
        {
            Name = $"{name}-{m}",
            Type = "stock",
            BasePrice = 25m,
            CostPrice = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    private async Task ReceiveAsync(TestSeed seed, int productId, int qty, string batch)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = batch, Quantity = qty, UnitCost = 10m } }
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    private async Task SaleAsync(TestSeed seed, int productId, int qty)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            SessionId = null,
            Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = qty, UnitPrice = 25m } },
            Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    private async Task AdjustAsync(TestSeed seed, int productId, int qty, bool increase)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).AdjustStockAsync(new DtoStockAdjustmentRequest
        {
            ProductId = productId,
            Quantity = qty,
            IsIncrease = increase,
            Reason = $"test-adj-{seed.UserId}",
            UserId = seed.UserId
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    /// <summary>حركة خام (كما تكتبها الخدمات): تعديل Inventory + سجل StockMovement بتاريخ محدد.</summary>
    private async Task RawMovementAsync(int productId, int delta, DateTime createdAt)
    {
        await using var db = _fx.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {delta} WHERE ProductId = {productId}");
        db.StockMovements.Add(new StockMovement
        {
            ProductId = productId,
            MovementType = delta >= 0 ? enStockMovementType.adjustment_in : enStockMovementType.sale,
            Quantity = delta,
            SourceType = "Test",
            CreatedAt = createdAt
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> AvailableAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
        return inv.QuantityAvailable ?? 0;
    }

    private async Task<StockCount?> LineAsync(int countId)
    {
        await using var db = _fx.CreateContext();
        return await db.StockCounts.AsNoTracking().SingleOrDefaultAsync(c => c.CountId == countId);
    }

    /// <summary>فتح جلسة وعدّ كل السطور: قيم مخصصة للمنتجات المستهدفة، وقيمة الكتيب الحي للبقية (فرق صفر).</summary>
    private async Task StartAndCountAsync(int userId, Dictionary<int, int> countedByProduct)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        var start = await svc.StartSessionAsync(userId);
        Assert.True(start.IsSuccess, start.Message);

        foreach (var line in await svc.GetOpenSessionAsync())
        {
            int counted = countedByProduct.TryGetValue(line.ProductId, out int v) ? v : line.BookQuantity;
            var r = await svc.SetCountedAsync(line.CountId, counted, userId);
            Assert.True(r.IsSuccess, r.Message);
        }
    }

    private async Task<(List<StockCountVM> lines, int sessionId)> StartSessionAsync(int userId)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        var start = await svc.StartSessionAsync(userId);
        Assert.True(start.IsSuccess, start.Message);
        return (await svc.GetOpenSessionAsync(), start.Data);
    }

    private async Task SetCountedAsync(int countId, int? counted, int actorUserId)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockCountService(db).SetCountedAsync(countId, counted, actorUserId);
        Assert.True(r.IsSuccess, r.Message);
    }

    private async Task<Result> TryPostAsync(int userId)
    {
        await using var db = _fx.CreateContext();
        return await new clsStockCountService(db).PostSessionAsync(userId);
    }

    private async Task PostAsync(int userId)
    {
        var r = await TryPostAsync(userId);
        Assert.True(r.IsSuccess, r.Message);
    }

    // ══ 1) لا حركات بعد العد — الفرق يُرحَّل كما هو ══
    [Fact]
    public async Task NoMovementsAfterCount_PostsExactCount()
    {
        string m = $"sc1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "a");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 9 });
        await PostAsync(seed.UserId);

        Assert.Equal(9, await AvailableAsync(pid)); // 9 — لا حركات لاحقة
    }

    // ══ 2) بيع بعد العد قبل الترحيل — يُحفظ في النهائي (مثال المشكلة الأصلي) ══
    [Fact]
    public async Task SaleAfterCount_PreservedInFinal()
    {
        string m = $"sc2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "b");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 10 });
        await SaleAsync(seed, pid, 2);                // بيع −2 بعد العد → رصيد 8

        await PostAsync(seed.UserId);
        Assert.Equal(8, await AvailableAsync(pid));  // 10 − 2 — البيعة محفوظة (لا +2 زائفة)
    }

    // ══ 3) المثال المرجعي: عجز بالعد + بيع بعد العد → 9 − 2 = 7 ══
    [Fact]
    public async Task ShortageCountWithSaleAfterCount_YieldsSeven()
    {
        string m = $"sc3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "c");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 9 });
        await SaleAsync(seed, pid, 2);                // بيع −2 بعد العد → رصيد 8

        await PostAsync(seed.UserId);
        Assert.Equal(7, await AvailableAsync(pid));  // 9 − 2 = 7 — السلوك المطلوب حرفيًا
    }

    // ══ 4) استلام مخزون بعد العد — يُحفظ ══
    [Fact]
    public async Task ReceiptAfterCount_PreservedInFinal()
    {
        string m = $"sc4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "d");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 10 });
        await ReceiveAsync(seed, pid, 5, $"B2-{m}");  // استلام +5 بعد العد → رصيد 15

        await PostAsync(seed.UserId);
        Assert.Equal(15, await AvailableAsync(pid)); // 10 + 5 — الاستلام محفوظ
    }

    // ══ 5) تسوية مخزون بعد العد — تُحفظ ══
    [Fact]
    public async Task AdjustmentAfterCount_PreservedInFinal()
    {
        string m = $"sc5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "e");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 10 });
        await AdjustAsync(seed, pid, 3, increase: false); // تسوية −3 بعد العد → رصيد 7

        await PostAsync(seed.UserId);
        Assert.Equal(7, await AvailableAsync(pid)); // 10 − 3 — التسوية محفوظة
    }

    // ══ 6) منتجان بعدّ في وقتين مختلفين — لقطة كل سطر مستقلة ══
    [Fact]
    public async Task DifferentProductsCountedAtDifferentTimes_EachUsesOwnSnapshot()
    {
        string m = $"sc6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int p1 = await SeedProductAsync(m, "f");
        int p2 = await SeedProductAsync(m, "g");
        await ReceiveAsync(seed, p1, 10, $"B1-{m}"); // دفتري 10
        await ReceiveAsync(seed, p2, 20, $"B2-{m}"); // دفتري 20

        var (lines, _) = await StartSessionAsync(seed.UserId);
        int l1 = lines.Single(l => l.ProductId == p1).CountId;
        int l2 = lines.Single(l => l.ProductId == p2).CountId;

        // عدّ P1 = 10 عند T1 (دفتري لحظتها 10)
        await SetCountedAsync(l1, 10, seed.UserId);
        DateTime t1 = (await LineAsync(l1))!.CountedAt!.Value;

        // حركتان بعد عدّ P1 وقبل عدّ P2: بيع P1 −2، استلام P2 +5
        await SaleAsync(seed, p1, 2);
        await ReceiveAsync(seed, p2, 5, $"B3-{m}");  // رصيد P2 الآن 25

        // عدّ P2 = 24 عند T2 (دفتري لحظتها 25 — بعد الاستلام)
        await SetCountedAsync(l2, 24, seed.UserId);
        DateTime t2 = (await LineAsync(l2))!.CountedAt!.Value;
        Assert.True(t2 > t1, "لحظة عدّ P2 يجب أن تكون بعد لحظة عدّ P1");
        Assert.Equal(25, (await LineAsync(l2))!.BookQuantityAtCount);

        // عدّ بقية سطور الجلسة (منتجات اختبارات أخرى) بقيمة الكتيب — فرق صفر
        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);
            foreach (var line in await svc.GetOpenSessionAsync())
                if (line.ProductId != p1 && line.ProductId != p2)
                    Assert.True((await svc.SetCountedAsync(line.CountId, line.BookQuantity, seed.UserId)).IsSuccess);
        }

        await PostAsync(seed.UserId);

        // P1: دفتري عند عدّه = 10، معدود 10 → فرق 0 → البيعة اللاحقة محفوظة: 8
        Assert.Equal(8, await AvailableAsync(p1));
        // P2: دفتري عند عدّه = 25 (بعد الاستلام)، معدود 24 → فرق −1 → 24
        Assert.Equal(24, await AvailableAsync(p2));
    }

    // ══ 7) إعادة العد قبل الترحيل — تُعتمد آخر عدّة وآخر لحظة ══
    [Fact]
    public async Task Recount_UsesLatestCountAndTimestamp()
    {
        string m = $"sc7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "h");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        var (lines, _) = await StartSessionAsync(seed.UserId);
        int lineId = lines.Single(l => l.ProductId == pid).CountId;

        // عدّ أول: 9 عند T1
        await SetCountedAsync(lineId, 9, seed.UserId);
        var first = (await LineAsync(lineId))!;
        Assert.Equal(10, first.BookQuantityAtCount);

        // بيع −2 ثم إعادة عدّ: 12 عند T2 (الرصيد الآن 8)
        await SaleAsync(seed, pid, 2);
        await SetCountedAsync(lineId, 12, seed.UserId);
        var recounted = (await LineAsync(lineId))!;
        Assert.True(recounted.CountedAt!.Value > first.CountedAt!.Value, "لحظة إعادة العد يجب أن تُحدَّث");
        Assert.Equal(8, recounted.BookQuantityAtCount!.Value); // لقطة جديدة عند إعادة العد

        // عدّ بقية السطور
        await StartAndCountRestAsync(seed.UserId, lineId);

        await PostAsync(seed.UserId);
        // آخر عدّة (12) على دفتري لحظتها (8) → فرق +4 → 8 + 4 = 12
        Assert.Equal(12, await AvailableAsync(pid));
    }

    /// <summary>عدّ بقية سطور الجلسة المفتوحة (ما عدا سطرًا مستثنى) بقيمة الكتيب — فرق صفر.</summary>
    private async Task StartAndCountRestAsync(int userId, int exceptLineId)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        foreach (var line in await svc.GetOpenSessionAsync())
            if (line.CountId != exceptLineId)
                Assert.True((await svc.SetCountedAsync(line.CountId, line.BookQuantity, userId)).IsSuccess);
    }

    // ══ 8) حركات متعددة بعد العد — كلها تُحفظ ══
    [Fact]
    public async Task MultipleMovementsAfterCount_AllPreserved()
    {
        string m = $"sc8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "i");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 10 });
        await SaleAsync(seed, pid, 2);                   // −2
        await ReceiveAsync(seed, pid, 5, $"B2-{m}");     // +5
        await AdjustAsync(seed, pid, 1, increase: true); // +1 → رصيد 14

        await PostAsync(seed.UserId);
        Assert.Equal(14, await AvailableAsync(pid)); // 10 − 2 + 5 + 1
    }

    // ══ 9) حركات بتواريخ قريبة/أسبق من لحظة العد — الرياضيات لا تتأثر (snapshot لا timestamp) ══
    [Fact]
    public async Task MovementsNearCountTimestamp_SnapshotMathHolds()
    {
        string m = $"sc9-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "j");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 10 });
        DateTime countedAt = (await LineAsync(
            (await StartSessionLinesAsync(pid)).CountId))!.CountedAt!.Value;

        // حركة «مؤرَّخة زورًا» قبل لحظة العد بملي ثانية لكنها حصلت فعليًا بعده
        await RawMovementAsync(pid, -2, countedAt.AddMilliseconds(-1));
        // وحركة مؤرَّخة بعد لحظة العد مباشرة
        await RawMovementAsync(pid, +5, countedAt.AddMilliseconds(1));

        await PostAsync(seed.UserId);
        // لا اعتماد على مقارنة التواريخ: 10 − 2 + 5 = 13
        Assert.Equal(13, await AvailableAsync(pid));
    }

    private async Task<StockCountVM> StartSessionLinesAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await new clsStockCountService(db).GetOpenSessionAsync())
            .Single(l => l.ProductId == productId);
    }

    // ══ 10) ترحيل مزدوج — المحاولة الثانية تُرفض والرصيد لا يتغير ══
    [Fact]
    public async Task DuplicatePost_SecondAttemptRejected()
    {
        string m = $"sc10-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "k");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}"); // دفتري 10

        await StartAndCountAsync(seed.UserId, new() { [pid] = 9 });
        await PostAsync(seed.UserId);
        Assert.Equal(9, await AvailableAsync(pid));

        // محاولة ترحيل ثانية — لا جلسة مفتوحة
        var again = await TryPostAsync(seed.UserId);
        Assert.False(again.IsSuccess);
        Assert.Equal(9, await AvailableAsync(pid)); // بلا تسوية إضافية

        // تعديل سطر مرحّل مرفوض أيضًا
        await using var db = _fx.CreateContext();
        int postedLine = await db.StockCounts.AsNoTracking()
            .Where(c => c.ProductId == pid && c.IsPosted).Select(c => c.CountId).SingleAsync();
        var editPosted = await new clsStockCountService(db).SetCountedAsync(postedLine, 50, seed.UserId);
        Assert.False(editPosted.IsSuccess);
        Assert.Equal(9, await AvailableAsync(pid));
    }

    // ══ 11) لحظة العد والدفتري-عند-العد يُسجَّلان فعلًا ══
    [Fact]
    public async Task SetCounted_RecordsTimestampAndBookSnapshot()
    {
        string m = $"sc11-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "l");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}");

        var (lines, _) = await StartSessionAsync(seed.UserId);
        int lineId = lines.Single(l => l.ProductId == pid).CountId;

        await SetCountedAsync(lineId, 7, seed.UserId);
        var line = (await LineAsync(lineId))!;
        Assert.NotNull(line.CountedAt);
        Assert.Equal(10, line.BookQuantityAtCount);
        Assert.True(line.CountedAt!.Value >= line.CreatedAt.AddMilliseconds(-1), "لحظة العد بعد بدء الجلسة");

        // إلغاء العد يصفّر الحالة
        await SetCountedAsync(lineId, null, seed.UserId);
        line = (await LineAsync(lineId))!;
        Assert.Null(line.CountedQuantity);
        Assert.Null(line.CountedAt);
        Assert.Null(line.BookQuantityAtCount);

        // إغلاق الجلسة المفتوحة — حتى لا تتسرب إلى اختبار لاحق
        await CloseAnyOpenSessionAsync(seed.UserId);
    }

    /// <summary>إغلاق أي جلسة مفتوحة (عدّ الكل بالكتيب + ترحيل) — حاجز ضد تسرب جلسة بين الاختبارات.</summary>
    private async Task CloseAnyOpenSessionAsync(int userId)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        if (!await svc.HasOpenSessionAsync())
            return;
        foreach (var l in await svc.GetOpenSessionAsync())
            Assert.True((await svc.SetCountedAsync(l.CountId, l.BookQuantity, userId)).IsSuccess);
        await PostAsync(userId);
    }
}