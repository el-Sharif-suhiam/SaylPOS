using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// تسوية المخزون (AdjustStockAsync) وسجل الحركات (StockMovement):
/// النقصان بلا رقم دفعة يُوزَّع FEFO على دفعات متعددة — كل دفعة خُصمت
/// فعليًا تحصل على حركتها بالكمية الفعلية المخصومة منها (نفس نمط
/// SellStockAsync)، ومجموع الحركات = النقصان الكلي، وكل ذلك داخل
/// ترانزاكشن واحدة (فشل = تراجع كامل بلا حركات يتيمة).
/// كل عملية خدمة في سياقها المستقل — كما تفعل الواجهة.
/// </summary>
[Collection("database")]
public class AdjustStockMovementTests
{
    private readonly DatabaseFixture _fx;
    public AdjustStockMovementTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة ─────────────────

    private async Task<TestSeed> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, marker);
    }

    /// <summary>بذر دفعة بمخزونها — بلا حركة شراء (الاختبار يركز على حركات التسوية وحدها).</summary>
    private async Task<Batch> SeedBatchAsync(DatabaseFixture fx, int productId, string batchNumber,
        int quantity, DateOnly? expiry = null)
    {
        await using var db = fx.CreateContext();
        var batch = new Batch
        {
            ProductId = productId,
            BatchNumber = batchNumber,
            ExpiryDate = expiry,
            BatchQuantity = quantity,
            CreatedAt = DateTime.UtcNow
        };
        db.Batches.Add(batch);

        var inv = await db.Inventories.FindAsync(productId);
        if (inv == null)
        {
            db.Inventories.Add(new Inventory { ProductId = productId, QuantityAvailable = 0, QuantityReserved = 0 });
            await db.SaveChangesAsync();
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {quantity} WHERE ProductId = {productId}");
        await db.SaveChangesAsync();
        return batch;
    }

    private static async Task<Result> AdjustAsync(DatabaseFixture fx, TestSeed seed,
        int quantity, bool increase, string? batchNumber = null)
    {
        await using var db = fx.CreateContext();
        return await new clsStockService(db).AdjustStockAsync(new DtoStockAdjustmentRequest
        {
            ProductId = seed.ProductId,
            Quantity = quantity,
            IsIncrease = increase,
            Reason = "test-adjustment",
            BatchNumber = batchNumber,
            UserId = seed.UserId
        });
    }

    /// <summary>حركات التسوية (ManualAdjustment) للمنتج مرتبة زمنيًا مع رقم دفعتها.</summary>
    private static async Task<List<(int BatchId, int Quantity)>> MovementsAsync(DatabaseFixture fx, int productId)
    {
        await using var db = fx.CreateContext();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.SourceType == "ManualAdjustment")
            .OrderBy(m => m.MovementId)
            .Select(m => new ValueTuple<int, int>(m.BatchId ?? 0, m.Quantity))
            .ToListAsync();
    }

    private static async Task<int> AvailableAsync(DatabaseFixture fx, int productId)
    {
        await using var db = fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityAvailable ?? 0;
    }

    // ══ 1) نقصان من دفعة واحدة — حركة واحدة بالكمية الصحيحة ══

    [Fact]
    public async Task Decrease_FromSingleBatch_OneMovementWithActualQuantity()
    {
        string marker = $"am1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        var b1 = await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 10);

        var r = await AdjustAsync(_fx, seed, 3, increase: false);
        Assert.True(r.IsSuccess, r.Message);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        var single = Assert.Single(movements);
        Assert.Equal(b1.BatchId, single.BatchId);
        Assert.Equal(-3, single.Quantity);
        Assert.Equal(7, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 2) نقصان عبر دفعتين — حركتان بكميات فعلية صحيحة ══

    [Fact]
    public async Task Decrease_AcrossTwoBatches_MovementPerBatchWithActualQuantities()
    {
        string marker = $"am2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        // نفس الصلاحية + نفس لحظة الإنشاء تقريبًا: FEFO يميز بالإنشاء ثم بالكمية — نحدد الصلاحيات صراحة
        var b1 = await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 5, expiry: new(2027, 1, 1));
        var b2 = await SeedBatchAsync(_fx, seed.ProductId, $"B2-{marker}", 3, expiry: new(2028, 1, 1));

        var r = await AdjustAsync(_fx, seed, 6, increase: false); // B1=5 ثم B2=1
        Assert.True(r.IsSuccess, r.Message);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        Assert.Equal(2, movements.Count);
        Assert.Equal(b1.BatchId, movements[0].BatchId);
        Assert.Equal(-5, movements[0].Quantity);
        Assert.Equal(b2.BatchId, movements[1].BatchId);
        Assert.Equal(-1, movements[1].Quantity);

        // مجموع الحركات = التغيير الفعلي في المخزون
        Assert.Equal(-6, movements.Sum(m => m.Quantity));
        Assert.Equal(2, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 3) نقصان عبر دفعات متعددة (3+) — حركة لكل دفعة والمجموع مطابق ══

    [Fact]
    public async Task Decrease_AcrossMultipleBatches_MovementPerBatch_TotalMatches()
    {
        string marker = $"am3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        var b1 = await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 3, expiry: new(2027, 1, 1));
        var b2 = await SeedBatchAsync(_fx, seed.ProductId, $"B2-{marker}", 3, expiry: new(2027, 6, 1));
        var b3 = await SeedBatchAsync(_fx, seed.ProductId, $"B3-{marker}", 3, expiry: new(2028, 1, 1));

        var r = await AdjustAsync(_fx, seed, 7, increase: false); // 3 + 3 + 1
        Assert.True(r.IsSuccess, r.Message);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        Assert.Equal(3, movements.Count);
        Assert.Equal(b1.BatchId, movements[0].BatchId);
        Assert.Equal(-3, movements[0].Quantity);
        Assert.Equal(b2.BatchId, movements[1].BatchId);
        Assert.Equal(-3, movements[1].Quantity);
        Assert.Equal(b3.BatchId, movements[2].BatchId);
        Assert.Equal(-1, movements[2].Quantity);

        Assert.Equal(-7, movements.Sum(m => m.Quantity));
        Assert.Equal(2, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 4) نقصان بدفعة محددة — حركة واحدة على تلك الدفعة ══

    [Fact]
    public async Task Decrease_WithSpecifiedBatch_OneMovementOnThatBatch()
    {
        string marker = $"am4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        var b1 = await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 4);
        await SeedBatchAsync(_fx, seed.ProductId, $"B2-{marker}", 9);

        var r = await AdjustAsync(_fx, seed, 2, increase: false, batchNumber: $"B2-{marker}");
        Assert.True(r.IsSuccess, r.Message);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        var single = Assert.Single(movements);
        Assert.Equal((await FindBatchIdAsync(seed.ProductId, $"B2-{marker}")), single.BatchId);
        Assert.NotEqual(b1.BatchId, single.BatchId); // الدفعة الأخرى لم تُمَس
        Assert.Equal(-2, single.Quantity);
        Assert.Equal(11, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 5) مخزون متاح غير كافٍ — رفض بلا أي حركة وبلا تغيير ══

    [Fact]
    public async Task Decrease_InsufficientAvailable_Rejected_NoMovements_NoInventoryChange()
    {
        string marker = $"am5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 4);
        await SeedBatchAsync(_fx, seed.ProductId, $"B2-{marker}", 3);

        var r = await AdjustAsync(_fx, seed, 8, increase: false); // المتاح 7
        Assert.False(r.IsSuccess);

        Assert.Empty(await MovementsAsync(_fx, seed.ProductId));
        Assert.Equal(7, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 6) دفعة محددة غير موجودة — رفض وتراجع كامل بلا حركات ══

    [Fact]
    public async Task Decrease_SpecifiedBatchNotFound_Rejected_RollsBack()
    {
        string marker = $"am6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 10);

        var r = await AdjustAsync(_fx, seed, 2, increase: false, batchNumber: $"NOPE-{marker}");
        Assert.False(r.IsSuccess);

        Assert.Empty(await MovementsAsync(_fx, seed.ProductId));
        Assert.Equal(10, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 7) رفض لاحق داخل الترانزاكشن — الحركات المكتوبة مسبقًا تتراجع ══
    // (الزيادة تنشئ الدفعة ثم يفشل شيء بعدها: المنتج غير مخزني سبق فحصه،
    //  لذا نستخدم مسار نقصان بدفعة محددة بكمية غير كافية — فحصها يحدث
    //  بعد فحص المتاح، والرفض يجب أن يترك قاعدة البيانات كما كانت)

    [Fact]
    public async Task Decrease_BatchQuantityInsufficient_FailureIsAtomic()
    {
        string marker = $"am7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        var b1 = await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 4);

        var r = await AdjustAsync(_fx, seed, 5, increase: false, batchNumber: $"B1-{marker}");
        Assert.False(r.IsSuccess); // قاعدة «كمية الدفعة غير كافية» — حتى لو المتاح يكفي

        Assert.Empty(await MovementsAsync(_fx, seed.ProductId));
        Assert.Equal(4, await AvailableAsync(_fx, seed.ProductId));

        await using var db = _fx.CreateContext();
        var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.BatchId == b1.BatchId);
        Assert.Equal(4, batch.BatchQuantity); // الدفعة لم تُخصم
    }

    // ══ 8) الزيادة بلا رقم دفعة — دفعة تلقائية واحدة وحركة واحدة (سلوك قائم يُحفظ) ══

    [Fact]
    public async Task Increase_WithoutBatch_AutoBatchWithSingleMovement_Preserved()
    {
        string marker = $"am8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);

        var r = await AdjustAsync(_fx, seed, 5, increase: true);
        Assert.True(r.IsSuccess, r.Message);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        var single = Assert.Single(movements);
        Assert.Equal(5, single.Quantity);
        Assert.True(single.BatchId > 0);
        Assert.Equal(5, await AvailableAsync(_fx, seed.ProductId));
    }

    // ══ 9) توازن تاريخ الحركات: مجموع حركات التسوية = صافي التغيير ══

    [Fact]
    public async Task MixedAdjustments_MovementTotals_MatchInventoryDelta()
    {
        string marker = $"am9-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await SeedBatchAsync(_fx, seed.ProductId, $"B1-{marker}", 6, expiry: new(2027, 1, 1));
        await SeedBatchAsync(_fx, seed.ProductId, $"B2-{marker}", 6, expiry: new(2028, 1, 1));

        Assert.True((await AdjustAsync(_fx, seed, 4, increase: false)).IsSuccess);
        Assert.True((await AdjustAsync(_fx, seed, 2, increase: true)).IsSuccess);

        var movements = await MovementsAsync(_fx, seed.ProductId);
        // −4 (عبر دفعتين على الأكثر) + 2 (دفعة تلقائية)
        Assert.Equal(-2, movements.Sum(m => m.Quantity));
        Assert.Equal(10, await AvailableAsync(_fx, seed.ProductId)); // 12 − 4 + 2

        // مجموع الدفعات = المتاح (الثابت المحفوظ في كل منطق التسوية)
        await using var db = _fx.CreateContext();
        int batchSum = await db.Batches.Where(b => b.ProductId == seed.ProductId).SumAsync(b => b.BatchQuantity);
        Assert.Equal(10, batchSum);
    }

    private async Task<int> FindBatchIdAsync(int productId, string batchNumber)
    {
        await using var db = _fx.CreateContext();
        return await db.Batches.AsNoTracking()
            .Where(b => b.ProductId == productId && b.BatchNumber == batchNumber)
            .Select(b => b.BatchId)
            .SingleAsync();
    }
}
