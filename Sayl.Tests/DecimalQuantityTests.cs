using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// المرحلة 4 — الكميات العشرية (DECIMAL(10,3)): استلام وبيع ومرتجع ونقل وعدّ
/// بكسور حقيقية (2.5 / 0.25 / 0.5) مع بقاء قواعد العمل قائمة:
/// FEFO يقسم الكسر على دفعات (صف فاتورة وحركة لكل دفعة)، الاسترداد يتناسب مع الكسر،
/// التقريب مرة واحدة عند المدخل (3 منازل، AwayFromZero)، والمحجوز لا يهبط سالبًا —
/// بقايا دون عتبة 0.0005 تُقصَّص بصمت وانحراف 0.001 حقيقي يُسجَّل تشخيصيًا.
/// ثابت التكامل المحفوظ: مجموع الدفعات = المخزون المتاح بعد كل عملية.
/// </summary>
[Collection("database")]
public class DecimalQuantityTests
{
    private readonly DatabaseFixture _fx;
    public DecimalQuantityTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة (سياق جديد لكل عملية — كما في الإنتاج) ─────────────────

    private async Task<TestSeed> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, marker);
    }

    /// <summary>استلام مخزون كسري عبر clsStockService.ReceiveStockAsync (بوابة الصلاحية بمستخدم البذر).</summary>
    private async Task ReceiveAsync(TestSeed seed, int productId, decimal qty, string batch, decimal unitCost = 10m, DateOnly? expiry = null)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = productId,
            BatchNumber = batch,
            Quantity = qty,
            UnitCost = unitCost,
            ExpiryDate = expiry
        }, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);
    }

    /// <summary>بيع عادي (كاش) — السعر من المنتج (20) لا من الواجهة.</summary>
    private async Task<int> SaleAsync(TestSeed seed, int productId, decimal qty)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = qty } },
            Payment = new DtoPaymentInfo { Amount = 20m * qty, Method = "cash" }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private async Task<decimal> AvailableAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityAvailable ?? 0m;
    }

    private async Task<decimal> BatchQuantityAsync(int batchId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId)).BatchQuantity;
    }

    private async Task<List<(int BatchId, decimal Quantity)>> SaleMovementsAsync(DatabaseFixture fx, int productId, int invoiceId)
    {
        await using var db = fx.CreateContext();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.SourceType == "Invoice" && m.SourceId == invoiceId)
            .OrderBy(m => m.MovementId)
            .Select(m => new ValueTuple<int, decimal>(m.BatchId ?? 0, m.Quantity))
            .ToListAsync();
    }

    /// <summary>منتج مخزني جديد بحد طلب (ReorderLevel) كسري — بلا مخزون بعد.</summary>
    private async Task<int> SeedProductAsync(string marker, string name, decimal reorderLevel)
    {
        await using var db = _fx.CreateContext();
        var product = new Product
        {
            Name = $"{name}-{marker}",
            Type = "stock",
            BasePrice = 20m,
            CostPrice = 10m,
            ReorderLevel = reorderLevel,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    // ══ 1) استلام كسري 2.5 — الدفعة والمخزون بالقيمة الكسرية بالضبط ══

    [Fact]
    public async Task FractionalReceive_BatchAndInventoryExact()
    {
        string marker = $"dq1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 2.5m, $"B-{marker}", unitCost: 7m);

        await using var db = _fx.CreateContext();
        var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(2.5m, batch.BatchQuantity);
        Assert.Equal(7m, batch.UnitCost);
        Assert.Equal(2.5m, await AvailableAsync(seed.ProductId));

        // حركة استلام بالقيمة الكسرية نفسها
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == seed.ProductId);
        Assert.Equal(2.5m, movement.Quantity);
    }

    // ══ 2) بيع كسري 1.25 يستهلك دفعتين FEFO — صف فاتورة وحركة لكل دفعة ══

    [Fact]
    public async Task FractionalSell_FefoSplitAcrossBatches_LinesAndMovementsPerBatch()
    {
        string marker = $"dq2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        // A: 1.0 @10 صلاحية أبعد — B: 0.25 @15 صلاحية أقرب (FEFO يستهلك B أولًا)
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        await ReceiveAsync(seed, seed.ProductId, 1.0m, $"BA-{marker}", unitCost: 10m, expiry: today.AddDays(60));
        await ReceiveAsync(seed, seed.ProductId, 0.25m, $"BB-{marker}", unitCost: 15m, expiry: today.AddDays(10));

        await using var db = _fx.CreateContext();
        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == seed.ProductId).ToListAsync();
        int batchAId = batches.First(b => b.BatchNumber == $"BA-{marker}").BatchId;
        int batchBId = batches.First(b => b.BatchNumber == $"BB-{marker}").BatchId;

        int saleId = await SaleAsync(seed, seed.ProductId, 1.25m);

        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == saleId);
        Assert.Equal(2, invoice.InvoiceItems.Count); // صف لكل دفعة مستهلكة

        var bLine = invoice.InvoiceItems.Single(ii => ii.BatchId == batchBId);
        var aLine = invoice.InvoiceItems.Single(ii => ii.BatchId == batchAId);
        Assert.Equal(0.25m, bLine.Quantity);
        Assert.Equal(15.00m, bLine.ItemCost);
        Assert.Equal(1.0m, aLine.Quantity);
        Assert.Equal(10.00m, aLine.ItemCost);

        // COGS الفعلي من الدفعات: 0.25×15 + 1.0×10 = 13.75
        Assert.Equal(13.75m, invoice.InvoiceItems.Sum(ii => (ii.ItemCost ?? 0m) * ii.Quantity));

        // حركة لكل دفعة بالكمية الفعلية المسحوبة منها
        var movements = await SaleMovementsAsync(_fx, seed.ProductId, saleId);
        Assert.Equal(2, movements.Count);
        Assert.Equal((batchBId, -0.25m), movements[0]);
        Assert.Equal((batchAId, -1.0m), movements[1]);

        // الدفعتان أفرغتا والمخزون صفر — ثابت مجموع الدفعات = المتاح
        Assert.Equal(0m, await BatchQuantityAsync(batchAId));
        Assert.Equal(0m, await BatchQuantityAsync(batchBId));
        Assert.Equal(0m, await AvailableAsync(seed.ProductId));
    }

    // ══ 3) مرتجع جزئي كسري 0.5 من 2.5 — استرداد متناسب ودفعة تعود للكسر ══

    [Fact]
    public async Task PartialFractionalReturn_ProportionalRefund_BatchRestoredToFraction()
    {
        string marker = $"dq3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 2.5m, $"B-{marker}", unitCost: 10m);
        int saleId = await SaleAsync(seed, seed.ProductId, 2.5m);
        Assert.Equal(0m, await AvailableAsync(seed.ProductId));

        // بلا SessionId — ValidateDrawerSessionAsync يقبل null في مسار الاختبار
        await using var db = _fx.CreateContext();
        var returns = new clsReturnService(db);
        var r = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 0.5m } }
        });
        Assert.True(r.IsSuccess, r.Message);

        // الاسترداد متناسب مع الكسر: 0.5 × 20 = 10 (سالب = مبلغ مردود)
        var ret = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == r.Data);
        Assert.Equal(-10.00m, ret.NetAmount);

        var line = Assert.Single(ret.InvoiceItems);
        Assert.Equal(0.5m, line.Quantity);
        Assert.Equal(10.00m, line.ItemCost); // تكلفة الدفعة الأصلية تتبع الكسر

        // الكسر يعود للدفعة والمخزون (2.5 − 2.5 + 0.5)
        Assert.Equal(0.5m, await BatchQuantityAsync(line.BatchId!.Value));
        Assert.Equal(0.5m, await AvailableAsync(seed.ProductId));
    }

    // ══ 4) تخزين كسور 3 منازل — دورة كتابة/قراءة بلا تحويل أو فقد ══

    [Fact]
    public async Task DecimalPersistence_RoundTrip_ExactThreeScales()
    {
        string marker = $"dq4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);

        int productId;
        await using (var db = _fx.CreateContext())
        {
            var batch = new Batch
            {
                ProductId = seed.ProductId,
                BatchNumber = $"B-{marker}",
                BatchQuantity = 12.345m,
                UnitCost = 3.33m,
                CreatedAt = DateTime.UtcNow
            };
            db.Batches.Add(batch);
            db.Inventories.Add(new Inventory
            {
                ProductId = seed.ProductId,
                QuantityAvailable = 12.345m,
                QuantityReserved = 0.125m
            });
            var product = await db.Products.FirstAsync(p => p.ProductId == seed.ProductId);
            product.ReorderLevel = 1.5m;
            db.StockMovements.Add(new StockMovement
            {
                ProductId = seed.ProductId,
                MovementType = enStockMovementType.adjustment_in,
                Quantity = 12.345m,
                SourceType = "Test",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            productId = seed.ProductId;
        }

        await using (var db = _fx.CreateContext())
        {
            var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.ProductId == productId);
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.ProductId == productId);
            var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == productId);
            Assert.Equal(12.345m, batch.BatchQuantity);
            Assert.Equal(12.345m, inv.QuantityAvailable);
            Assert.Equal(0.125m, inv.QuantityReserved);
            Assert.Equal(1.5m, product.ReorderLevel);
            Assert.Equal(12.345m, movement.Quantity);
        }
    }

    // ══ 5) حد التقريب عند المدخل: 1.2345 → 1.235 (AwayFromZero) و1.2344 → 1.234 ══

    [Fact]
    public async Task RoundingBoundary_HalfAwayFromZero_OnAdjustment()
    {
        string marker = $"dq5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int up = await SeedProductAsync(marker, "up", 0m);
        int down = await SeedProductAsync(marker, "down", 0m);

        // منتصف المنزل الرابع يرتفع بعيدًا عن الصفر
        await using (var db = _fx.CreateContext())
        {
            var up1 = await new clsStockService(db).AdjustStockAsync(new DtoStockAdjustmentRequest
            {
                ProductId = up, Quantity = 1.2345m, IsIncrease = true, Reason = "test", UserId = seed.UserId
            });
            Assert.True(up1.IsSuccess, up1.Message);
        }
        Assert.Equal(1.235m, await AvailableAsync(up));

        // دون المنتصف يهبط
        await using (var db = _fx.CreateContext())
        {
            var down1 = await new clsStockService(db).AdjustStockAsync(new DtoStockAdjustmentRequest
            {
                ProductId = down, Quantity = 1.2344m, IsIncrease = true, Reason = "test", UserId = seed.UserId
            });
            Assert.True(down1.IsSuccess, down1.Message);
        }
        Assert.Equal(1.234m, await AvailableAsync(down));

        // الدفعة التلقائية للزيادة تحمل القيمة المقربة نفسها
        await using var dbCheck = _fx.CreateContext();
        var upBatch = await dbCheck.Batches.AsNoTracking().SingleAsync(b => b.ProductId == up);
        Assert.Equal(1.235m, upBatch.BatchQuantity);
    }

    // ══ 6) تقريب نقطة البيع: بيع 1.2345 يخصم 1.235 ويُسجَّل بالفاتورة ══

    [Fact]
    public async Task RoundingBoundary_OnCheckout_DeductsRoundedQuantity()
    {
        string marker = $"dq6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 10m, $"B-{marker}");

        int saleId = await SaleAsync(seed, seed.ProductId, 1.2345m);

        await using var db = _fx.CreateContext();
        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == saleId);
        var line = Assert.Single(invoice.InvoiceItems);
        Assert.Equal(1.235m, line.Quantity);            // الكمية المقربة في الفاتورة
        Assert.Equal(20m, line.ItemPrice);              // السعر من المنتج كما في البيع العادي

        // 10 − 1.235 = 8.765 — الكسر الثلاثي يبقى سليمًا في المخزون
        Assert.Equal(8.765m, await AvailableAsync(seed.ProductId));
        Assert.Equal(8.765m, await BatchQuantityAsync(line.BatchId!.Value));
    }

    // ══ 7) قصّص الحجز القريب من الصفر: بقايا دون 0.0005 صامتة، و0.001 انحراف حقيقي ══

    /// <summary>محاكاة انحراف خارجي على المحجوز المجمّع.</summary>
    private async Task CorruptReservedAsync(int productId, decimal value)
    {
        await using var db = _fx.CreateContext();
        await db.Inventories.Where(i => i.ProductId == productId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.QuantityReserved, value));
    }

    [Fact]
    public async Task SubEpsilonReservedDeviation_ClampsSilently_NoUnderflowEvent()
    {
        string marker = $"dq7a-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 5m, $"B-{marker}");

        int suspendedId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 2.5m } },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(r.IsSuccess, r.Message);
            suspendedId = r.Data;
        }

        // محاولة انحراف دون العتبة (2.5 − 0.0004): عمود DECIMAL(10,3) يخزنها 2.500 —
        // فالانحراف المرئي = صفر ⇒ تحرير سليم بصمت، بلا حدث تشخيصي زائف
        await CorruptReservedAsync(seed.ProductId, 2.4999m);
        Assert.Equal(2.500m, (await ReadReservedAsync(seed.ProductId)));

        await using var db2 = _fx.CreateContext();
        var r2 = await new clsCheckoutService(db2).CancelSuspendedInvoiceAsync(suspendedId, seed.UserId);
        Assert.True(r2.IsSuccess, r2.Message);

        Assert.Equal(0m, await ReadReservedAsync(seed.ProductId));
        Assert.False(await db2.SystemLogs.AsNoTracking().AnyAsync(l =>
            l.Action == "ReservationUnderflow" && l.Details!.Contains($"الفاتورة #{suspendedId}")));
    }

    private async Task<decimal> ReadReservedAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityReserved ?? 0m;
    }

    // ══ 7-ب) انحراف 0.001 (أصغر كسر مخزَّن) فوق العتبة — حدث تشخيصي لا صمت ══

    [Fact]
    public async Task MinimalRepresentableUnderflow_StillLoggedAsDiagnostic()
    {
        string marker = $"dq7b-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 5m, $"B-{marker}");

        int suspendedId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 2.5m } },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(r.IsSuccess, r.Message);
            suspendedId = r.Data;
        }

        // انحراف حقيقي بالحد الأدنى القابل للتخزين: المحجوز 2.499 والحجز 2.5 —
        // |−0.001| ≥ عتبة 0.0005 ⇒ إبسيلون القصّص الصامت لا يبتلع انحرافات مقياس 3 منازل
        await CorruptReservedAsync(seed.ProductId, 2.499m);

        await using var db2 = _fx.CreateContext();
        var r2 = await new clsCheckoutService(db2).CancelSuspendedInvoiceAsync(suspendedId, seed.UserId);
        Assert.True(r2.IsSuccess, r2.Message);

        // المحجوز 0 لا سالب + حدث تشخيصي واحد للفاتورة
        Assert.Equal(0m, await ReadReservedAsync(seed.ProductId));
        var events = await db2.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "ReservationUnderflow" && l.Details!.Contains($"الفاتورة #{suspendedId}"))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains($"المنتج #{seed.ProductId}", events[0].Details);
    }

    // ══ 8) ثابت التكامل: مجموع الدفعات = المخزون المتاح مع الكسور ══

    [Fact]
    public async Task BatchInventoryInvariant_SumOfBatchesEqualsAvailable_WithFractions()
    {
        string marker = $"dq8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 2.5m, $"B1-{marker}", unitCost: 10m);
        await ReceiveAsync(seed, seed.ProductId, 1.25m, $"B2-{marker}", unitCost: 15m);

        await using var db = _fx.CreateContext();
        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == seed.ProductId).ToListAsync();
        Assert.Equal(3.75m, batches.Sum(b => b.BatchQuantity));
        Assert.Equal(3.75m, await AvailableAsync(seed.ProductId));

        // بيع 1.0 — الثابت يبقى مطابقًا بعد البيع مهما كانت الدفعة المستهلكة
        int saleId = await SaleAsync(seed, seed.ProductId, 1.0m);
        Assert.True(saleId > 0);

        await using (var verify = _fx.CreateContext())
        {
            decimal batchSum = await verify.Batches.Where(b => b.ProductId == seed.ProductId).SumAsync(b => b.BatchQuantity);
            Assert.Equal(2.75m, batchSum);
            Assert.Equal(2.75m, await AvailableAsync(seed.ProductId));
        }
    }

    // ══ 9) جرد كسري: معدود 10.5 على دفتري 10 → حركة +0.5، وإعادة الترحيل مرفوضة ══

    private async Task<bool> HasOpenSessionAsync()
    {
        await using var db = _fx.CreateContext();
        return await new clsStockCountService(db).HasOpenSessionAsync();
    }

    /// <summary>حاجز ضد تسرب جلسة جرد مفتوحة من اختبار سابق: عدّ الكل بالكتيب ثم ترحيل.</summary>
    private async Task CloseAnyOpenSessionAsync(int userId)
    {
        while (await HasOpenSessionAsync())
        {
            await using var db = _fx.CreateContext();
            var svc = new clsStockCountService(db);
            foreach (var l in await svc.GetOpenSessionAsync())
                Assert.True((await svc.SetCountedAsync(l.CountId, l.BookQuantity, userId)).IsSuccess);
            Assert.True((await svc.PostSessionAsync(userId)).IsSuccess);
        }
    }

    [Fact]
    public async Task FractionalStockCount_CountedOverBook_ByFraction_PostsAdjustment()
    {
        string marker = $"dq9-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await CloseAnyOpenSessionAsync(seed.UserId);
        await ReceiveAsync(seed, seed.ProductId, 10m, $"B-{marker}");

        // فتح جلسة وعدّ الكل: منتجنا 10.5 والبقية بالكتيب (فرق صفر)
        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);
            var start = await svc.StartSessionAsync(seed.UserId);
            Assert.True(start.IsSuccess, start.Message);
            foreach (var line in await svc.GetOpenSessionAsync())
            {
                decimal counted = line.ProductId == seed.ProductId ? 10.5m : line.BookQuantity;
                Assert.True((await svc.SetCountedAsync(line.CountId, counted, seed.UserId)).IsSuccess);
            }
        }

        await using (var db = _fx.CreateContext())
        {
            var post = await new clsStockCountService(db).PostSessionAsync(seed.UserId);
            Assert.True(post.IsSuccess, post.Message);
        }

        // فرق كسري +0.5 ترحّل كحركة تسوية مرتبطة بالسطر
        Assert.Equal(10.5m, await AvailableAsync(seed.ProductId));
        await using (var db = _fx.CreateContext())
        {
            int countId = await db.StockCounts.AsNoTracking()
                .Where(c => c.ProductId == seed.ProductId && c.IsPosted).Select(c => c.CountId).SingleAsync();
            var movements = await db.StockMovements.AsNoTracking()
                .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == countId).ToListAsync();
            var single = Assert.Single(movements);
            Assert.Equal(0.5m, single.Quantity);
        }

        // إعادة ترحيل (idempotency): الجلسة مغلقة — رفض والرصيد لا يتأثر
        await using (var db = _fx.CreateContext())
        {
            var again = await new clsStockCountService(db).PostSessionAsync(seed.UserId);
            Assert.False(again.IsSuccess);
        }
        Assert.Equal(10.5m, await AvailableAsync(seed.ProductId));
    }

    // ══ 10) نقل كسري بين دفعات: المصدر ينقص والهدف يحمل التكلفة والمخزون ثابت ══

    [Fact]
    public async Task FractionalBatchTransfer_SplitsSource_TargetCarriesCost()
    {
        string marker = $"dq10-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 2.5m, $"SRC-{marker}", unitCost: 7m);

        await using var db = _fx.CreateContext();
        int sourceId = await db.Batches.AsNoTracking()
            .Where(b => b.ProductId == seed.ProductId && b.BatchNumber == $"SRC-{marker}")
            .Select(b => b.BatchId).SingleAsync();

        var r = await new clsStockService(db).TransferBatchAsync(new DtoBatchTransferRequest
        {
            SourceBatchId = sourceId,
            TargetBatchNumber = $"DST-{marker}",
            Quantity = 1.25m,
            UserId = seed.UserId
        });
        Assert.True(r.IsSuccess, r.Message);

        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == seed.ProductId).ToListAsync();
        var src = batches.Single(b => b.BatchNumber == $"SRC-{marker}");
        var dst = batches.Single(b => b.BatchNumber == $"DST-{marker}");
        Assert.Equal(1.25m, src.BatchQuantity);
        Assert.Equal(1.25m, dst.BatchQuantity);
        Assert.Equal(7m, dst.UnitCost); // التكلفة التاريخية تنتقل مع الكسر

        // المخزون الكلي ثابت — النقل بين الدفعات لا يمس المتاح
        Assert.Equal(2.5m, await AvailableAsync(seed.ProductId));
    }

    // ══ 11) حد الطلب الكسري: IsLowStock على المتاح الصافي بكسور ≤ ══

    [Fact]
    public async Task FractionalReorderLevel_LowStockOnNetAvailable()
    {
        string marker = $"dq11-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int below = await SeedProductAsync(marker, "below", 0.5m);  // 0.25 ≤ 0.5 → منخفض
        int boundary = await SeedProductAsync(marker, "boundary", 0.5m); // 0.5 ≤ 0.5 → منخفض (حد ≤)
        int above = await SeedProductAsync(marker, "above", 0.5m);  // 1.0 > 0.5 → ليس منخفضًا

        await ReceiveAsync(seed, below, 0.25m, $"B-{marker}");
        await ReceiveAsync(seed, boundary, 0.5m, $"B-{marker}");
        await ReceiveAsync(seed, above, 1.0m, $"B-{marker}");

        await using var db = _fx.CreateContext();
        var status = await new clsReportService(db).GetInventoryStatusAsync();
        Assert.True(status.IsSuccess, status.Message);

        var rows = status.Data!.Where(x => x.ProductId == below || x.ProductId == boundary || x.ProductId == above).ToDictionary(x => x.ProductId);
        Assert.Equal(3, rows.Count);

        Assert.Equal(0.25m, rows[below].NetAvailable);
        Assert.True(rows[below].IsLowStock);

        Assert.Equal(0.5m, rows[boundary].NetAvailable);
        Assert.True(rows[boundary].IsLowStock); // عتبة ≤ تشمل التساوي الكسري

        Assert.Equal(1.0m, rows[above].NetAvailable);
        Assert.False(rows[above].IsLowStock);
    }
}
