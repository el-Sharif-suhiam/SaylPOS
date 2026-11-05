using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// IDEMPOTENCY OF STOCK COUNT POSTING (ترحيل الجرد الفعلي):
/// كل حركة تسوية يولّدها الترحيل تحمل SourceId = CountId (SourceType = ManualAdjustment)
/// — مفتاح idempotency دائم في القاعدة. عند إعادة الترحيل بعد فشل جزئي
/// (حركة التزمت وحُفظت لكن حالة IsPosted فُقدت) يُتخطى السطر المرتبط بحركة قائمة
/// — لا تطبيق مزدوج أبدًا، حتى بعد إعادة تشغيل التطبيق (سياق جديد لكل عملية).
///
/// السيناريوهات المُغطاة:
/// 1) ترحيل ناجح لكل السطور (فائض وعجز وفرق صفري)
/// 2) فشل جزئي: حركة السطر الأول التزمت ثم انهار الترحيل — إعادة المحاولة تتخطاه
/// 3) فشل طبيعي عند سطر لاحق (مخزون غير كافٍ) — ما قبله محفوظ، والإصلاح + إعادة المحاولة آمنة
/// 4) تكرار طلب ترحيل مكتمل بنجاح — يُرفض بلا حركات إضافية
/// 5) محاولتا ترحيل متزامنتان — تسوية واحدة بالضبط
/// 6) سطر بفرق صفري — يُقفل مرحّلًا بلا أي حركة
/// </summary>
[Collection("database")]
public class StockCountIdempotencyTests
{
    private readonly DatabaseFixture _fx;
    public StockCountIdempotencyTests(DatabaseFixture fx) => _fx = fx;

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

    private async Task<int> AvailableAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
        return inv.QuantityAvailable ?? 0;
    }

    /// <summary>حركات تسوية الجرد المرتبطة بسطر (idempotency key: SourceType + SourceId).</summary>
    private async Task<List<StockMovement>> LineMovementsAsync(int countId)
    {
        await using var db = _fx.CreateContext();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == countId)
            .OrderBy(m => m.MovementId)
            .ToListAsync();
    }

    /// <summary>كل حركات التسوية لمنتج (سطور الجرد والتسويات اليدوية).</summary>
    private async Task<List<StockMovement>> ProductAdjustmentsAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.SourceType == "ManualAdjustment")
            .OrderBy(m => m.MovementId)
            .ToListAsync();
    }

    private async Task<StockCount?> LineAsync(int countId)
    {
        await using var db = _fx.CreateContext();
        return await db.StockCounts.AsNoTracking().SingleOrDefaultAsync(c => c.CountId == countId);
    }

    /// <summary>فتح جلسة وعدّ كل السطور: قيم مخصصة للمنتجات المستهدفة، وقيمة الكتيب الحي للبقية (فرق صفر).</summary>
    private async Task<(int sessionId, Dictionary<int, int> lineIdByProduct)> StartAndCountAsync(
        int userId, Dictionary<int, int> countedByProduct)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        var start = await svc.StartSessionAsync(userId);
        Assert.True(start.IsSuccess, start.Message);

        var map = new Dictionary<int, int>();
        foreach (var line in await svc.GetOpenSessionAsync())
        {
            int counted = countedByProduct.TryGetValue(line.ProductId, out int v) ? v : line.BookQuantity;
            var r = await svc.SetCountedAsync(line.CountId, counted);
            Assert.True(r.IsSuccess, r.Message);
            map[line.ProductId] = line.CountId;
        }
        return (start.Data, map);
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

    /// <summary>محاكاة انهيار بعد التزام حركة سطر: تُنفَّذ تسوية السطر بالضبط كما يفعل الترحيل
    /// (AdjustStockAsync مع SourceId = CountId — تلتزم فورًا بترانزاكشنها) ثم يُترك
    /// IsPosted = false كما لو انهار التطبيق قبل حفظ الحالة.</summary>
    private async Task CommitLineAdjustmentOnlyAsync(int userId, int countId, int productId, int diff)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).AdjustStockAsync(new DtoStockAdjustmentRequest
        {
            ProductId = productId,
            Quantity = Math.Abs(diff),
            IsIncrease = diff > 0,
            Reason = $"ترحيل فرق جرد (محاكاة انهيار قبل حفظ الحالة)",
            BatchNumber = null,
            ExpiryDate = null,
            UserId = userId,
            SourceId = countId
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    // ══ 1) ترحيل ناجح: فائض وعجز وفرق صفري — حركة واحدة لكل سطر بفرق، PostedMovementId محفوظ ══

    [Fact]
    public async Task PostAllLines_SurplusShortageAndZero_EachAppliedExactlyOnce()
    {
        string m = $"id1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pSurplus = await SeedProductAsync(m, "sur");
        int pShort = await SeedProductAsync(m, "sho");
        int pZero = await SeedProductAsync(m, "zro");
        await ReceiveAsync(seed, pSurplus, 10, $"B1-{m}");  // دفتري 10
        await ReceiveAsync(seed, pShort, 10, $"B1-{m}");    // دفتري 10
        await ReceiveAsync(seed, pZero, 7, $"B1-{m}");      // دفتري 7

        var (_, map) = await StartAndCountAsync(seed.UserId, new()
        {
            [pSurplus] = 12,  // فائض +2
            [pShort] = 8,     // عجز −2
            [pZero] = 7       // فرق صفر
        });

        await PostAsync(seed.UserId);

        // الكميات النهائية
        Assert.Equal(12, await AvailableAsync(pSurplus));
        Assert.Equal(8, await AvailableAsync(pShort));
        Assert.Equal(7, await AvailableAsync(pZero));

        // السطر ذو الفائض: حركة واحدة adjustment_in بمصدر السطر
        var surMovements = await LineMovementsAsync(map[pSurplus]);
        var sur = Assert.Single(surMovements);
        Assert.Equal(enStockMovementType.adjustment_in, sur.MovementType);
        Assert.Equal(2, sur.Quantity);

        // السطر ذو العجز: حركة واحدة adjustment_out بمصدر السطر
        var shortMovements = await LineMovementsAsync(map[pShort]);
        var sho = Assert.Single(shortMovements);
        Assert.Equal(enStockMovementType.adjustment_out, sho.MovementType);
        Assert.Equal(-2, sho.Quantity);

        // السطر بفرق صفري: لا حركة إطلاقًا
        Assert.Empty(await LineMovementsAsync(map[pZero]));

        // الحالة المحفوظة: كل السطور مرحّلة، PostedMovementId مضبوط للسطور ذات الحركات فقط
        var surLine = (await LineAsync(map[pSurplus]))!;
        Assert.True(surLine.IsPosted);
        Assert.Equal(sur.MovementId, surLine.PostedMovementId);

        var shoLine = (await LineAsync(map[pShort]))!;
        Assert.True(shoLine.IsPosted);
        Assert.Equal(sho.MovementId, shoLine.PostedMovementId);

        var zeroLine = (await LineAsync(map[pZero]))!;
        Assert.True(zeroLine.IsPosted);
        Assert.Null(zeroLine.PostedMovementId);
    }

    // ══ 2) فشل جزئي: حركة السطر الأول التزمت وانهار التطبيق قبل حفظ IsPosted ══
    //        إعادة الترحيل تتخطى السطر الأول — لا تطبيق مزدوج (العيب الأصلي)

    [Fact]
    public async Task CrashAfterFirstLineCommitted_RetrySkipsCommittedLine_NoDoubleApplication()
    {
        string m = $"id2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int p1 = await SeedProductAsync(m, "crash1");
        int p2 = await SeedProductAsync(m, "crash2");
        await ReceiveAsync(seed, p1, 10, $"B1-{m}");  // دفتري 10
        await ReceiveAsync(seed, p2, 10, $"B1-{m}");  // دفتري 10

        var (_, map) = await StartAndCountAsync(seed.UserId, new()
        {
            [p1] = 13,  // فائض +3 — سيُطبَّق ثم يُحاكى الانهيار
            [p2] = 6    // عجز −4 — يُطبَّق في إعادة المحاولة
        });

        // محاكاة الانهيار: حركة السطر الأول التزمت (بمفتاح idempotency الرسمي)
        // لكن IsPosted لم يُحفظ — الحالة المخلفة للعيب الأصلي تمامًا
        await CommitLineAdjustmentOnlyAsync(seed.UserId, map[p1], p1, diff: +3);
        Assert.Equal(13, await AvailableAsync(p1)); // الحركة التزمت
        Assert.False((await LineAsync(map[p1]))!.IsPosted); // الحالة لم تُحفظ

        // إعادة المحاولة — قبل الإصلاح كانت ستُطبّق +3 مرة أخرى (رصيد 16 زائف)
        var r = await TryPostAsync(seed.UserId);
        Assert.True(r.IsSuccess, r.Message);

        // الكمية النهائية الصحيحة: لا +6
        Assert.Equal(13, await AvailableAsync(p1));
        Assert.Equal(6, await AvailableAsync(p2));

        // حركة واحدة فقط لكل سطر — بلا تكرار
        var p1Movements = await LineMovementsAsync(map[p1]);
        var single1 = Assert.Single(p1Movements);
        Assert.Equal(3, single1.Quantity);

        var p2Movements = await LineMovementsAsync(map[p2]);
        var single2 = Assert.Single(p2Movements);
        Assert.Equal(-4, single2.Quantity);

        // الحالة المحفوظة: كلا السطرين مرحّل ومربوط بحركته
        var l1 = (await LineAsync(map[p1]))!;
        Assert.True(l1.IsPosted);
        Assert.Equal(single1.MovementId, l1.PostedMovementId);
        var l2 = (await LineAsync(map[p2]))!;
        Assert.True(l2.IsPosted);
        Assert.Equal(single2.MovementId, l2.PostedMovementId);
    }

    // ══ 3) فشل جزئي بعد عدة سطور + إصلاح ثم إعادة محاولة — المُرحَّل سابقًا لا يتكرر ══
    //        (تعديل السطر الأول يُستدعى يدويًا قبل المحاولة الأولى — يحاكي ترتيبًا آمنًا للفشل)
    //        بعد الفشل: حركة A موجودة لكن IsPosted=false؛ الإصلاح + إعادة المحاولة تتخطى A وتطبيق B.

    [Fact]
    public async Task NaturalFailureAtLaterLine_FixAndRetry_CommittedLinesNotReapplied()
    {
        string m = $"id3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pA = await SeedProductAsync(m, "okA");
        int pB = await SeedProductAsync(m, "failB");
        await ReceiveAsync(seed, pA, 10, $"B1-{m}");  // دفتري 10
        await ReceiveAsync(seed, pB, 5, $"B1-{m}");   // دفتري 5

        var (_, map) = await StartAndCountAsync(seed.UserId, new()
        {
            [pA] = 14,  // فائض +4 — يُستدعى يدويًا قبل المحاولة الأولى
            [pB] = 3    // عجز −2 — سيُحاكى الفشل: بيع 4 بعد العد يجعل المتاح 1 < 2
        });

        // حركة بعد العد تجعل عجز B غير قابل للتطبيق (المتاح 5−4 = 1، المطلوب 2)
        await SaleAsync(seed, pB, 4);

        // استدعاء يدوي لتسوية A (فائض +4) كمحاكاة لحالة "A رُحّل لكن B فشل"
        // — المفتاح idempotency (SourceId = CountId) يضمن عدم التكرار عند إعادة المحاولة
        await CommitLineAdjustmentOnlyAsync(seed.UserId, map[pA], pA, diff: +4);

        var fail = await TryPostAsync(seed.UserId);
        Assert.False(fail.IsSuccess); // توقف عند B

        // A رُحّل (حركته موجودة) لكن IsPosted لم تُحفظ بعد (فشل قبل SaveChangesAsync)
        Assert.Equal(14, await AvailableAsync(pA));
        var aMovements = await LineMovementsAsync(map[pA]);
        Assert.Single(aMovements); // حركة واحدة رغم أن IsPosted لم تُحفظ بعد

        // الإصلاح: استلام وحدة واحدة → المتاح 2، العجز −2 قابل للتطبيق
        await ReceiveAsync(seed, pB, 1, $"B2-{m}");

        // إعادة المحاولة — A مُتخطى (حركته موجودة)، B يُطبَّق مرة واحدة
        var retry = await TryPostAsync(seed.UserId);
        Assert.True(retry.IsSuccess, retry.Message);

        // A: 10 + 4 = 14 (الفائض مُطبَّق مرة واحدة — لا +8)
        Assert.Equal(14, await AvailableAsync(pA));
        // B: 1 + 2(عجز −2) ... final = counted 3 + postCount(-4+1) = 0 → 2 − 2 = 0
        Assert.Equal(0, await AvailableAsync(pB));

        // حركة واحدة لفائض A (دفعة تلقائية)، وحركتان لعجز B (عبر دفعتَي FEFO: 1+1)
        Assert.Single(await LineMovementsAsync(map[pA]));
        var bRetryMovements = await LineMovementsAsync(map[pB]);
        Assert.Equal(2, bRetryMovements.Count);
        Assert.Equal(-2, bRetryMovements.Sum(x => x.Quantity));

        // كل السطور مرحّلة الآن
        Assert.True((await LineAsync(map[pA]))!.IsPosted);
        Assert.True((await LineAsync(map[pB]))!.IsPosted);
    }

    // ══ 4) تكرار طلب ترحيل مكتمل بنجاح — يُرفض بلا حركات إضافية ══

    [Fact]
    public async Task RepeatCompletedPosting_Rejected_NoAdditionalMovements()
    {
        string m = $"id4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "done");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}");

        var (_, map) = await StartAndCountAsync(seed.UserId, new() { [pid] = 15 });
        await PostAsync(seed.UserId);
        Assert.Equal(15, await AvailableAsync(pid));
        Assert.Single(await LineMovementsAsync(map[pid]));

        // محاولة تكرار — لا جلسة مفتوحة
        var again = await TryPostAsync(seed.UserId);
        Assert.False(again.IsSuccess);
        Assert.Equal(15, await AvailableAsync(pid)); // بلا تسوية ثانية
        Assert.Single(await LineMovementsAsync(map[pid]));
    }

    // ══ 5) محاولتا ترحيل متزامنتان — التسوية تُطبَّق مرة واحدة بالضبط ══

    [Fact]
    public async Task ConcurrentPostAttempts_AdjustmentAppliedExactlyOnce()
    {
        string m = $"id5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "conc");
        await ReceiveAsync(seed, pid, 10, $"B1-{m}");

        await StartAndCountAsync(seed.UserId, new() { [pid] = 16 }); // فائض +6

        // محاولتان متوازيتان من سياقين مستقلين — قفل الترحيل يسلسلهما
        var t1 = TryPostAsync(seed.UserId);
        var t2 = TryPostAsync(seed.UserId);
        var results = await Task.WhenAll(t1, t2);

        // واحدة على الأقل نجحت، ولا حركة مزدوجة مهما كان التداخل
        Assert.Contains(results, r => r.IsSuccess);
        Assert.Equal(16, await AvailableAsync(pid));
        Assert.Single(await ProductAdjustmentsAsync(pid));
    }

    // ══ 6) سطر بفرق صفري — يُقفل مرحّلًا بلا أي حركة، وإعادة الترحيل بعده آمنة ══

    [Fact]
    public async Task ZeroDifferenceLine_PostedWithoutMovement_SecondAttemptRejected()
    {
        string m = $"id6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "zero");
        await ReceiveAsync(seed, pid, 9, $"B1-{m}");

        var (_, map) = await StartAndCountAsync(seed.UserId, new() { [pid] = 9 }); // فرق صفر
        await PostAsync(seed.UserId);

        Assert.Equal(9, await AvailableAsync(pid));
        Assert.Empty(await LineMovementsAsync(map[pid]));  // لا حركة بلا داعٍ
        var line = (await LineAsync(map[pid]))!;
        Assert.True(line.IsPosted);
        Assert.Null(line.PostedMovementId);

        // إعادة المحاولة — مرفوضة (الجلسة مغلقة) والرصيد كما هو
        var again = await TryPostAsync(seed.UserId);
        Assert.False(again.IsSuccess);
        Assert.Equal(9, await AvailableAsync(pid));
    }

    // ══ 7) عجز عبر دفعتين FEFO — عدة حركات لنفس السطر كلها بمفتاح السطر، والمحاولة الثانية لا تكرر ══

    [Fact]
    public async Task ShortageAcrossTwoBatches_MultipleMovementsPerLine_RetryDoesNotDuplicate()
    {
        string m = $"id7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "fefo");
        await ReceiveAsync(seed, pid, 5, $"B1-{m}");
        await ReceiveAsync(seed, pid, 5, $"B2-{m}"); // دفتري 10 عبر دفعتين

        var (_, map) = await StartAndCountAsync(seed.UserId, new() { [pid] = 3 }); // عجز −7 عبر الدفعتين

        await PostAsync(seed.UserId);
        Assert.Equal(3, await AvailableAsync(pid));

        // عدة حركات (حركة لكل دفعة FEFO) كلها مرتبطة بنفس السطر
        var movements = await LineMovementsAsync(map[pid]);
        Assert.True(movements.Count >= 2, $"عجز عبر دفعتين يجب أن يولّد حركتين على الأقل، وُجد {movements.Count}");
        Assert.Equal(-7, movements.Sum(x => x.Quantity));

        // إعادة محاولة مغلقة — لا حركات إضافية
        var again = await TryPostAsync(seed.UserId);
        Assert.False(again.IsSuccess);
        Assert.Equal(movements.Count, (await LineMovementsAsync(map[pid])).Count);
    }
}
