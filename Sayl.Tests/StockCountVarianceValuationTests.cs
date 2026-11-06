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
/// تقييم قيمة فروق الجرد بتكاليف الدفعات الفعلية (COSTING.md) — لا Product.CostPrice العامة:
///
/// القاعدة المستقلة المستخدمة لحساب القيم المتوقعة في هذه الاختبارات:
/// • العجز: الترحيل يستهلك الدفعات FEFO (الأقرب انتهاءً أولًا، ثم المحجوبة، ثم الأقدم
///   إنشاءً — نفس ترتيب AdjustStockAsync للنقصان بلا رقم دفعة)، وكل دفعة تُخصم بتكلفتها
///   (Batches.UnitCost؛ الدفعة بلا تكلفة تعود إلى Product.CostPrice). قيمة عجز الكمية q
///   = Σ (المأخوذ من كل دفعة × تكلفة الدفعة) — قيمة سالبة.
/// • الفائض: الترحيل ينشئ دفعة تلقائية (ADJ-…) بلا تكلفة، فاستهلاكها اللاحق يرجع إلى
///   Product.CostPrice (الاحتياط الموثق) — قيمة فائض q = q × Product.CostPrice.
/// • القيمة المعروضة بعد الترحيل تُقرأ من حركات التسوية نفسها (حركة لكل دفعة) —
///   فيجب أن تطابق التقدير القبلي حرفيًا ما لم تتغير الدفعات بينهما.
///
/// في كل الاختبارات: Product.CostPrice = 10 (قديم/خارجي — يجب ألا يُقيَّم به الفرق
/// ما دامت الدفعات تحمل تكاليفها).
/// </summary>
[Collection("database")]
public class StockCountVarianceValuationTests
{
    private readonly DatabaseFixture _fx;
    public StockCountVarianceValuationTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة (سياق جديد لكل عملية) ─────────────────

    /// <summary>بذر بيانات دون إنشاء دور جديد: Roles.RoleId عمود TINYINT IDENTITY —
    /// المجموعة الكاملة للاختبارات تستهلك مداه كاملًا (255) فأي دور إضافي يُفجّر
    /// «Arithmetic overflow». نعيد استخدام دور مدير نشط (يمتلك بت المخزون) عبر
    /// TestSeed.GetOrCreateRoleAsync — يُنشأ فقط في أول بذر بالرَن.
    /// المستخدم/المورد/العميل/المنتج أعمدة INT فتنشأ لكل اختبار كالمعتاد.</summary>
    private async Task<TestSeed> SeedAsync(string m)
    {
        await using var db = _fx.CreateContext();
        var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);

        var user = new User
        {
            Username = $"test-user-{m}",
            PasswordHash = "test-hash",
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);

        var supplierParty = new Party { PartyType = 'S', Name = $"مورد-{m}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(supplierParty);

        var customerParty = new Party { PartyType = 'C', Name = $"عميل-{m}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(customerParty);

        var product = new Product
        {
            Name = $"منتج-{m}",
            Type = "stock",
            BasePrice = 20m,
            CostPrice = 10m, // قديم/خارجي — يجب ألا يُستخدم كـ COGS عند وجود دفعات
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);

        await db.SaveChangesAsync();

        db.Customers.Add(new Customer { PartyId = customerParty.PartyId, Balance = 0, CreditLimit = 0 });
        db.Suppliers.Add(new Supplier { PartyId = supplierParty.PartyId, Balance = 0 });
        await db.SaveChangesAsync();

        return new TestSeed
        {
            UserId = user.UserId,
            SupplierId = supplierParty.PartyId,
            CustomerId = customerParty.PartyId,
            ProductId = product.ProductId
        };
    }

    /// <summary>منتج مخزني بسعر تكلفة قديم (10) — يجب ألا يُستخدم أبدًا لتقييم فرق
    /// تستحقه دفعات بتكاليف مختلفة.</summary>
    private async Task<int> SeedProductAsync(string m, string name, decimal costPrice = 10m)
    {
        await using var db = _fx.CreateContext();
        var product = new Product
        {
            Name = $"{name}-{m}",
            Type = "stock",
            BasePrice = 25m,
            CostPrice = costPrice,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    /// <summary>استلام شراء بدفعة بتكلفة وصلاحية محددين — الدفعة تحمل UnitCost حرفيًا
    /// (بلا خصم فاتورة: التكلفة كما هي).</summary>
    private async Task ReceiveBatchAsync(TestSeed seed, int productId, int qty, decimal unitCost,
        string batch, int expiryInDays)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem
            {
                ProductId = productId,
                BatchNumber = batch,
                Quantity = qty,
                UnitCost = unitCost,
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(expiryInDays)
            } }
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    /// <summary>ضبط Product.CostPrice صراحةً بعد الشراء — الشراء نفسه يحدّثه لسعر آخر
    /// شراء (سلوك موثق)؛ هنا نعيده لقيمة قديمة ليتسنى الاختبار إثبات أن التقييم
    /// من تكاليف الدفعات/الاحتياط لا من CostPrice العامة.</summary>
    private async Task SetProductCostAsync(int productId, decimal costPrice)
    {
        await using var db = _fx.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Products SET CostPrice = {costPrice} WHERE ProductId = {productId}");
    }

    /// <summary>استلام يدوي بلا تكلفة — دفعة UnitCost = null (الاحتياط الموثق).</summary>
    private async Task ReceiveWithoutCostAsync(TestSeed seed, int productId, int qty, string batch)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = productId,
            Quantity = qty,
            UnitCost = null,
            BatchNumber = batch
        }, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);
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

    /// <summary>حركات التسوية المرتبطة بسطر (idempotency key: SourceType + SourceId).</summary>
    private async Task<List<StockMovement>> LineMovementsAsync(int countId)
    {
        await using var db = _fx.CreateContext();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == countId)
            .OrderBy(m => m.MovementId)
            .ToListAsync();
    }

    /// <summary>القيمة الفعلية لحركات سطر من القاعدة: Σ (كمية الحركة × تكلفة دفعتها
    /// أو Product.CostPrice احتياطًا) — محسوبة مستقلة عن الخدمة.</summary>
    private async Task<decimal> MovementBasedValueAsync(int countId, decimal productCost)
    {
        await using var db = _fx.CreateContext();
        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == countId)
            .Select(m => new { m.Quantity, m.BatchId })
            .ToListAsync();
        decimal value = 0m;
        foreach (var m in movements)
        {
            decimal? unitCost = m.BatchId.HasValue
                ? await db.Batches.Where(b => b.BatchId == m.BatchId.Value).Select(b => b.UnitCost).SingleAsync()
                : null;
            value += m.Quantity * (unitCost ?? productCost);
        }
        return value;
    }

    /// <summary>فتح جلسة وعدّ كل السطور: قيم مخصصة للمنتجات المستهدفة، وقيمة الكتيب الحي للبقية (فرق صفر).
    /// يعيد (معرف السطر لكل منتج، ملخص ما قبل الترحيل).</summary>
    private async Task<(Dictionary<int, int> lineIdByProduct, StockCountSummaryVM summary)> StartAndCountAsync(
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
            var r = await svc.SetCountedAsync(line.CountId, counted, userId);
            Assert.True(r.IsSuccess, r.Message);
            map[line.ProductId] = line.CountId;
        }
        return (map, await svc.GetSummaryAsync());
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

    private async Task<List<StockCountVM>> LastPostedLinesAsync()
    {
        await using var db = _fx.CreateContext();
        return await new clsStockCountService(db).GetLastPostedSessionAsync();
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

    // ══ 1) دفعة واحدة بتكلفة واحدة: عجز −2 يُقيَّم بتكلفة الدفعة 6 لا CostPrice 10 ══
    // المتوقع المستقل: −2 × 6 = −12 (لا −20)

    [Fact]
    public async Task SingleBatch_ShortageValuedAtBatchCost()
    {
        string m = $"vv1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "single"); // CostPrice = 10 قديم
        await ReceiveBatchAsync(seed, pid, 10, 6m, $"B1-{m}", expiryInDays: 30); // دفتري 10
        // الشراء يحدّث CostPrice لسعر آخر شراء (6) — نعيده للقيمة القديمة 10
        // فيثبت الاختبار أن التقييم من تكلفة الدفعة لا من CostPrice العامة
        await SetProductCostAsync(pid, 10m);

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 8 }); // عجز −2
        int lineId = map[pid];

        // القيمة القبلية: −12 لا −20
        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-12m, open.DifferenceCost);
        Assert.Equal(-12m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(8, await AvailableAsync(pid));

        // الاتساق: حركة واحدة −2 على الدفعة — قيمتها −12 تطابق التقييم
        var movements = await LineMovementsAsync(lineId);
        var single = Assert.Single(movements);
        Assert.Equal(enStockMovementType.adjustment_out, single.MovementType);
        Assert.Equal(-2, single.Quantity);
        Assert.Equal(-12m, await MovementBasedValueAsync(lineId, 10m));

        // تقرير آخر جلسة مرحّلة: نفس القيمة من الحركات
        Assert.Equal(-12m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }

    // ══ 2) عجز عبر دفعتين مختلفتي التكلفة (FEFO): −(5×6 + 1×9) = −39 ══
    // B1 (صلاحية أقرب) 5@6 ثم B2 5@9 — عجز −6 يستهلك 5 من B1 و1 من B2

    [Fact]
    public async Task ShortageAcrossMultipleBatches_ValuedPerBatch_Fefo()
    {
        string m = $"vv2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "multi");
        await ReceiveBatchAsync(seed, pid, 5, 6m, $"B1-{m}", expiryInDays: 10);
        await ReceiveBatchAsync(seed, pid, 5, 9m, $"B2-{m}", expiryInDays: 40); // دفتري 10

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 4 }); // عجز −6
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-39m, open.DifferenceCost);   // −(30 + 9)
        Assert.Equal(-39m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(4, await AvailableAsync(pid));

        // حركتان: −5 على B1 و−1 على B2 — قيمتهما −39 تطابق التقييم القبلي
        var movements = await LineMovementsAsync(lineId);
        Assert.Equal(2, movements.Count);
        Assert.Equal(-6, movements.Sum(x => x.Quantity));
        Assert.Equal(-39m, await MovementBasedValueAsync(lineId, 10m));
        Assert.Equal(-39m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }

    // ══ 3) استهلاك جزئي لدفعة واحدة: عجز −1 يخصم 1 فقط من B1 — القيمة −6 ══

    [Fact]
    public async Task PartialBatchConsumption_OnlyAffectedQuantityValued()
    {
        string m = $"vv3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "partial");
        await ReceiveBatchAsync(seed, pid, 5, 6m, $"B1-{m}", expiryInDays: 10);
        await ReceiveBatchAsync(seed, pid, 5, 9m, $"B2-{m}", expiryInDays: 40); // دفتري 10

        var (map, _) = await StartAndCountAsync(seed.UserId, new() { [pid] = 9 }); // عجز −1
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-6m, open.DifferenceCost); // 1 × 6 — الدفعة الثانية لا تمس

        await PostAsync(seed.UserId);
        Assert.Equal(9, await AvailableAsync(pid));

        // حركة واحدة −1 على B1 — B2 بكامل 5
        var movements = await LineMovementsAsync(lineId);
        var single = Assert.Single(movements);
        Assert.Equal(-1, single.Quantity);
        await using var db = _fx.CreateContext();
        var b2 = await db.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == pid && b.BatchNumber == $"B2-{m}");
        Assert.Equal(5, b2.BatchQuantity);
        Assert.Equal(-6m, await MovementBasedValueAsync(lineId, 10m));
    }

    // ══ 4) فائض +2: دفعة تلقائية بلا تكلفة → القيمة 2 × Product.CostPrice ══
    // (الاحتياط الموثق: CostPrice الحالية 8 بعد تعديل يدوي — لا تكلفة الدفعة 6 ولا مُخترعة)

    [Fact]
    public async Task Surplus_ValuedAtProductCostPriceDocumentedFallback()
    {
        string m = $"vv4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "surplus");
        await ReceiveBatchAsync(seed, pid, 10, 6m, $"B1-{m}", expiryInDays: 30); // دفتري 10
        // تعديل تكلفة المنتج يدويًا (كما يسمح ProductEditForm): 6 → 8
        await SetProductCostAsync(pid, 8m);

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 12 }); // فائض +2
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(16m, open.DifferenceCost);   // +2 × 8 (الاحتياط الموثق — لا 6 تكلفة الدفعة)
        Assert.Equal(16m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(12, await AvailableAsync(pid));

        // حركة الفائض: دفعة ADJ بلا تكلفة (UnitCost null) — استهلاكها لاحقًا يرجع لـCostPrice
        var movements = await LineMovementsAsync(lineId);
        var single = Assert.Single(movements);
        Assert.Equal(enStockMovementType.adjustment_in, single.MovementType);
        Assert.Equal(2, single.Quantity);
        await using var db = _fx.CreateContext();
        var surplusBatch = await db.Batches.AsNoTracking()
            .SingleAsync(b => b.BatchId == single.BatchId!.Value);
        Assert.StartsWith("ADJ-", surplusBatch.BatchNumber);
        Assert.Null(surplusBatch.UnitCost);

        // تقرير آخر جلسة مرحّلة: من الحركة — دفعة الفائض بلا تكلفة → 2 × 8 = 16
        Assert.Equal(16m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }

    // ══ 5) فرق صفري: قيمة صفر، لا حركة، والسطر يُقفل مرحّلًا ══

    [Fact]
    public async Task ZeroVariance_ZeroCost_NoMovement()
    {
        string m = $"vv5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "zero");
        await ReceiveBatchAsync(seed, pid, 7, 6m, $"B1-{m}", expiryInDays: 30);

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 7 });
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(0m, open.DifferenceCost);

        await PostAsync(seed.UserId);
        Assert.Equal(7, await AvailableAsync(pid));
        Assert.Empty(await LineMovementsAsync(lineId));

        var posted = (await LastPostedLinesAsync()).Single(l => l.CountId == lineId);
        Assert.Equal(0m, posted.DifferenceCost);
        Assert.True(posted.IsPosted);
    }

    // ══ 6) دفعة بلا تكلفة (استلام يدوي): عجز −2 يُقيَّم باحتياط CostPrice = −20 ══

    [Fact]
    public async Task NullBatchCost_ShortageFallsBackToProductCostPrice()
    {
        string m = $"vv6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "nullcost"); // CostPrice = 10
        await ReceiveWithoutCostAsync(seed, pid, 10, $"B1-{m}"); // دفعة بلا تكلفة
        await using (var db = _fx.CreateContext())
        {
            var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.ProductId == pid);
            Assert.Null(batch.UnitCost);
        }

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 8 }); // عجز −2
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-20m, open.DifferenceCost); // −2 × 10 الاحتياط الموثق
        Assert.Equal(-20m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(8, await AvailableAsync(pid));
        Assert.Equal(-20m, await MovementBasedValueAsync(lineId, 10m)); // نفس الاحتياط في الحركة
        Assert.Equal(-20m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }

    // ══ 7) تكاليف عشرية (دقتان): −(5×6.55 + 4×7.25) = −61.75 — بلا انزياح تقريب ══

    [Fact]
    public async Task DecimalCosts_ExactTwoDecimals_NoRoundingDrift()
    {
        string m = $"vv7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "decimal");
        await ReceiveBatchAsync(seed, pid, 5, 6.55m, $"B1-{m}", expiryInDays: 10);
        await ReceiveBatchAsync(seed, pid, 5, 7.25m, $"B2-{m}", expiryInDays: 40); // دفتري 10

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 1 }); // عجز −9
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        // القيمة المستقلة: 5×6.55 = 32.75 و4×7.25 = 29.00 → −61.75
        Assert.Equal(-61.75m, open.DifferenceCost);
        Assert.Equal(-61.75m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(1, await AvailableAsync(pid));
        Assert.Equal(-9, (await LineMovementsAsync(lineId)).Sum(x => x.Quantity));
        Assert.Equal(-61.75m, await MovementBasedValueAsync(lineId, 10m));
        Assert.Equal(-61.75m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }

    // ══ 8) التقريب: تكلفة أقل من قرش لكل وحدة لا تُخترع — القيم على دقتين حرفيًا ══
    // دفعتان @0.05 — عجز −3 → −0.15 (لا −0.1500 ولا تقريب لقرص صحيح)

    [Fact]
    public async Task Rounding_SmallUnitCosts_ExactCents()
    {
        string m = $"vv8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "rounding");
        await ReceiveBatchAsync(seed, pid, 10, 0.05m, $"B1-{m}", expiryInDays: 30); // دفتري 10

        var (map, _) = await StartAndCountAsync(seed.UserId, new() { [pid] = 7 }); // عجز −3
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-0.15m, open.DifferenceCost); // −3 × 0.05 حرفيًا

        await PostAsync(seed.UserId);
        Assert.Equal(7, await AvailableAsync(pid));
        Assert.Equal(-0.15m, await MovementBasedValueAsync(lineId, 10m));
    }

    // ══ 9) دفعات غير كافية (انزياح المخزون عن الدفعات): التقدير يقيّم المتبقي
    //        باحتياط CostPrice صراحةً (−10×6 −2×10 = −80)، والترحيل نفسه يُرفض ══

    [Fact]
    public async Task InsufficientBatches_EstimateFallsBackToProductCost_PostRejected()
    {
        string m = $"vv9-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "insufficient");
        await ReceiveBatchAsync(seed, pid, 10, 6m, $"B1-{m}", expiryInDays: 30); // دفعات 10
        await SetProductCostAsync(pid, 10m); // تكلفة عامة قديمة — المتبقي غير المغطى بالدفعات يُقيَّم بها

        // انزياح: المخزون +4 فوق مجموع الدفعات (دفتري 14 — كما تكتبه خدمات أخرى تاريخيًا)
        await using (var db = _fx.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + 4 WHERE ProductId = {pid}");
        }

        var (map, _) = await StartAndCountAsync(seed.UserId, new() { [pid] = 2 }); // عجز −12
        int lineId = map[pid];

        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        // الدفعات تغطي 10 بواقع 6 = −60، والمتبقي −2 باحتياط 10 = −20 → −80
        Assert.Equal(-80m, open.DifferenceCost);

        // الترحيل نفسه يُرفض (المخزون الفعلي 14 ≥ 12... لكن مجموع الدفعات 10 < 12؟
        // لا — النقصان يمر عبر الدفعات: 10 فقط متاحة → رفض)
        var post = await TryPostAsync(seed.UserId);
        Assert.False(post.IsSuccess);
        Assert.Empty(await LineMovementsAsync(lineId)); // لا حركة — التقدير أشار للمشكلة

        // إغلاق الجلسة المفتوحة — حاجز ضد التسرب لاختبارات لاحقة
        await CloseAnyOpenSessionAsync(seed.UserId);
    }

    // ══ 10) تجميع الملخص: عجز −2@6 (−12) + فائض +2@10 (+20) + فرق صفري ══
    //         CostOfDifferences = +8؛ صفوف العجز/الفائض وحيدتان (بقية سطور الجلسة
    //         — بيانات SaylDB.sql البذرية ومنتجات أخرى — تُعدّ بكتيبها الحي = فرق صفري)

    [Fact]
    public async Task SummaryAggregates_BatchAwareValuation()
    {
        string m = $"vv10-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pShort = await SeedProductAsync(m, "ashort");
        int pSurplus = await SeedProductAsync(m, "bsurplus");
        int pZero = await SeedProductAsync(m, "czero");
        await ReceiveBatchAsync(seed, pShort, 10, 6m, $"B1-{m}", expiryInDays: 30);
        await ReceiveBatchAsync(seed, pSurplus, 10, 6m, $"B1-{m}", expiryInDays: 30);
        await ReceiveBatchAsync(seed, pZero, 7, 6m, $"B1-{m}", expiryInDays: 30);
        // تكلفة عامة قديمة (10) لكل المنتجات — الشراء وراءها حدّثها لـ6
        await SetProductCostAsync(pShort, 10m);
        await SetProductCostAsync(pSurplus, 10m);
        await SetProductCostAsync(pZero, 10m);

        var (_, summary) = await StartAndCountAsync(seed.UserId, new()
        {
            [pShort] = 8,    // عجز −2 → −12
            [pSurplus] = 12, // فائض +2 → +20
            [pZero] = 7      // فرق صفر
        });

        Assert.Equal(1, summary.ShortageLines);
        Assert.Equal(1, summary.SurplusLines);
        // المطابقة: منتجي صفر الفرق + كل سطور الجلسة الأخرى (عدّت بكتيبها الحي)
        Assert.Equal(summary.TotalLines - 2, summary.MatchedLines);
        // القيمة المستقلة: −12 + 20 = +8
        Assert.Equal(8m, summary.CostOfDifferences);
        // صافي الفروق: −2 (عجز) + 2 (فائض) = 0
        Assert.Equal(0, summary.NetDiff);
        Assert.Equal(4, summary.AbsolutDiff);

        await PostAsync(seed.UserId);
        Assert.Equal(8, await AvailableAsync(pShort));
        Assert.Equal(12, await AvailableAsync(pSurplus));
        Assert.Equal(7, await AvailableAsync(pZero));
    }

    // ══ 11) اتساق شامل: تقييم ما قبل الترحيل يطابق حركات المخزون بعد الترحيل حرفيًا ══
    // عجز −8 عبر ثلاث دفعات (3@4 + 3@6 + 2@9): −(12 + 18 + 18) = −48

    [Fact]
    public async Task Consistency_PrePostValuation_MatchesGeneratedMovements()
    {
        string m = $"vv11-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(m);
        int pid = await SeedProductAsync(m, "consistency");
        await ReceiveBatchAsync(seed, pid, 3, 4m, $"B1-{m}", expiryInDays: 10);
        await ReceiveBatchAsync(seed, pid, 3, 6m, $"B2-{m}", expiryInDays: 20);
        await ReceiveBatchAsync(seed, pid, 3, 9m, $"B3-{m}", expiryInDays: 30); // دفتري 9

        var (map, summary) = await StartAndCountAsync(seed.UserId, new() { [pid] = 1 }); // عجز −8
        int lineId = map[pid];

        // قبل الترحيل: −(3×4 + 3×6 + 2×9) = −48
        var open = (await new clsStockCountService(_fx.CreateContext()).GetOpenSessionAsync())
            .Single(l => l.CountId == lineId);
        Assert.Equal(-48m, open.DifferenceCost);
        Assert.Equal(-48m, summary.CostOfDifferences);

        await PostAsync(seed.UserId);
        Assert.Equal(1, await AvailableAsync(pid));

        // بعد الترحيل: حركة لكل دفعة خُصمت — مجموع قيمها −48 (نفس التقدير)
        var movements = await LineMovementsAsync(lineId);
        Assert.True(movements.Count >= 3, $"عجز عبر ثلاث دفعات يجب أن يولّد 3 حركات على الأقل، وُجد {movements.Count}");
        Assert.Equal(-8, movements.Sum(x => x.Quantity));
        Assert.Equal(-48m, await MovementBasedValueAsync(lineId, 10m));
        Assert.Equal(-48m, (await LastPostedLinesAsync()).Single(l => l.CountId == lineId).DifferenceCost);
    }
}

