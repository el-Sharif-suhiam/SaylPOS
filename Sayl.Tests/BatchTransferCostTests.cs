using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// نقل الدفعات وتكلفة الوحدة (UnitCost): الكمية المنقولة تحمل تكلفتها التاريخية —
/// دفعة هدف جديدة تأخذ تكلفة المصدر كما هي، والدمج في دفعة قائمة يتوسط بالوزن
/// بالكمية (نفس معادلة دمج الشراء — COSTING.md) فيظل إجمالي تكلفة المخزون محفوظًا.
/// التكلفة null لا تُخترع: دمجها لا يطمس تكلفة القائمة، والهدف القديم بلا تكلفة
/// يمتلئ بأول تكلفة صريحة (نفس قاعدة الاستلام).
/// كل عملية خدمة في سياقها المستقل — كما تفعل الواجهة.
/// </summary>
[Collection("database")]
public class BatchTransferCostTests
{
    private readonly DatabaseFixture _fx;
    public BatchTransferCostTests(DatabaseFixture fx) => _fx = fx;

    private static readonly DateOnly ExpiryA = new(2027, 3, 15);
    private static readonly DateOnly ExpiryB = new(2028, 6, 30);
    private static readonly DateOnly DefaultExpiry = new(2027, 3, 15);

    /// <summary>بذر دفعتين للمنتج نفسه بتكاليف محددة يدويًا — الاختبار يركز على منطق النقل وحده.</summary>
    private async Task<(TestSeed Seed, Batch Source, Batch Target)> SeedBatchesAsync(
        string marker, decimal? sourceCost, decimal? targetCost,
        int sourceQty, int targetQty, DateOnly? expiry = null)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        DateOnly effectiveExpiry = expiry ?? DefaultExpiry;

        var source = new Batch
        {
            ProductId = seed.ProductId, BatchNumber = $"T-SRC-{marker}",
            ExpiryDate = effectiveExpiry, BatchQuantity = sourceQty, UnitCost = sourceCost, CreatedAt = DateTime.UtcNow
        };
        var target = new Batch
        {
            ProductId = seed.ProductId, BatchNumber = $"T-DST-{marker}",
            ExpiryDate = effectiveExpiry, BatchQuantity = targetQty, UnitCost = targetCost, CreatedAt = DateTime.UtcNow
        };
        db.Batches.AddRange(source, target);
        await db.SaveChangesAsync();
        return (seed, source, target);
    }

    private static async Task<Result> TransferAsync(DatabaseFixture fx, TestSeed seed, int sourceBatchId,
        string targetNumber, int qty, DateOnly? targetExpiry = null)
    {
        await using var s = fx.CreateContext();
        return await new clsStockService(s).TransferBatchAsync(new DtoBatchTransferRequest
        {
            SourceBatchId = sourceBatchId,
            TargetBatchNumber = targetNumber,
            Quantity = qty,
            TargetExpiryDate = targetExpiry,
            UserId = seed.UserId
        });
    }

    private static async Task<Batch> GetBatchAsync(DatabaseFixture fx, int batchId)
    {
        await using var db = fx.CreateContext();
        return await db.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
    }

    // ───────── دفعة هدف جديدة ─────────

    [Fact]
    public async Task Transfer_ToNewBatch_CopiesSourceUnitCost()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, _) = await SeedBatchesAsync(marker, sourceCost: 10m, targetCost: null, sourceQty: 6, targetQty: 0);

        var r = await TransferAsync(_fx, seed, source.BatchId, "T-NEW", 6);
        Assert.True(r.IsSuccess, r.Message);

        var created = await GetBatchAsync(_fx, (await FindBatchIdAsync(_fx, seed.ProductId, "T-NEW")));
        Assert.Equal(6, created.BatchQuantity);
        Assert.Equal(10.00m, created.UnitCost); // التكلفة التاريخية نُقلت مع الكمية
    }

    [Fact]
    public async Task Transfer_ToNewBatch_NullSourceCost_KeepsNull_NoInventedCost()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, _) = await SeedBatchesAsync(marker, sourceCost: null, targetCost: null, sourceQty: 4, targetQty: 0);

        var r = await TransferAsync(_fx, seed, source.BatchId, "T-NEW-NULL", 4);
        Assert.True(r.IsSuccess, r.Message);

        var created = await GetBatchAsync(_fx, await FindBatchIdAsync(_fx, seed.ProductId, "T-NEW-NULL"));
        Assert.Null(created.UnitCost); // لا تكلفة تُخترع — null يظل null
        Assert.Equal(4, created.BatchQuantity);
    }

    // ───────── الدمج في دفعة قائمة ─────────

    [Fact]
    public async Task Transfer_Merge_DifferentCosts_WeightedAverageByQuantity()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: 10m, targetCost: 15m, sourceQty: 6, targetQty: 4);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(10, merged.BatchQuantity);
        // (15×4 + 10×6) ÷ 10 = 12.00 — متوسط مرجح بالكمية
        Assert.Equal(12.00m, merged.UnitCost);
    }

    [Fact]
    public async Task Transfer_Merge_WeightedAverage_RoundsAwayFromZero_AtTwoDecimals()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: 7m, targetCost: 12m, sourceQty: 3, targetQty: 5);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 3);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(8, merged.BatchQuantity);
        // (12×5 + 7×3) ÷ 8 = 10.125 → 10.13 (تقريب قرصين بعيدًا عن الصفر، كمعادلة الشراء)
        Assert.Equal(10.13m, merged.UnitCost);
    }

    [Fact]
    public async Task Transfer_Merge_PreservesTotalInventoryCost()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: 10m, targetCost: 15m, sourceQty: 6, targetQty: 4);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        // قبل: 4×15 + 6×10 = 120 — بعد: 10×12.00 = 120 (متوسط مثالي بلا فروق تقريب)
        Assert.Equal(120m, merged.BatchQuantity * (merged.UnitCost ?? 0m));
    }

    [Fact]
    public async Task Transfer_Merge_SourceCostNull_TargetCostKept()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: null, targetCost: 15m, sourceQty: 6, targetQty: 4);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Equal(15.00m, merged.UnitCost); // تكلفة القائمة لم تُطمس ولا تُستبدل باختراع
    }

    [Fact]
    public async Task Transfer_Merge_TargetCostNull_FilledWithSourceCost()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: 10m, targetCost: null, sourceQty: 6, targetQty: 4);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Equal(10.00m, merged.UnitCost); // أول تكلفة صريحة تملؤها (نفس قاعدة الاستلام)
    }

    [Fact]
    public async Task Transfer_Merge_BothCostsNull_StaysNull()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: null, targetCost: null, sourceQty: 6, targetQty: 4);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Null(merged.UnitCost);
    }

    [Fact]
    public async Task Transfer_Merge_IntoEmptyBatch_TakesSourceCost()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        // دفعة هدف مستهلكة (كمية 0) بتكلفة قديمة — كل وحدات النقل من المصدر
        var (seed, source, target) = await SeedBatchesAsync(marker, sourceCost: 10m, targetCost: 15m, sourceQty: 5, targetQty: 0);

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 5);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(5, merged.BatchQuantity);
        Assert.Equal(10.00m, merged.UnitCost); // الدفعة كانت فارغة — كل الوحدات بتكلفة المصدر
    }

    // ───────── الرفض: الدفعات غير المتوافقة لا تُدمج ─────────

    [Fact]
    public async Task Transfer_Merge_DifferentExpiry_Rejected_NothingChanged()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var source = new Batch { ProductId = seed.ProductId, BatchNumber = $"T-SRC-{marker}", ExpiryDate = ExpiryB, BatchQuantity = 6, UnitCost = 10m, CreatedAt = DateTime.UtcNow };
        var target = new Batch { ProductId = seed.ProductId, BatchNumber = $"T-DST-{marker}", ExpiryDate = ExpiryA, BatchQuantity = 4, UnitCost = 15m, CreatedAt = DateTime.UtcNow };
        db.Batches.AddRange(source, target);
        await db.SaveChangesAsync();

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);

        var unchanged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(4, unchanged.BatchQuantity);
        Assert.Equal(15.00m, unchanged.UnitCost); // لا دمج ولا مساس بالتكلفة
        Assert.Equal(ExpiryA, unchanged.ExpiryDate);
    }

    [Fact]
    public async Task Transfer_Merge_QuarantineMismatch_Rejected_NothingChanged()
    {
        string marker = $"bt-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var source = new Batch { ProductId = seed.ProductId, BatchNumber = $"T-SRC-{marker}", ExpiryDate = ExpiryA, BatchQuantity = 6, UnitCost = 10m, CreatedAt = DateTime.UtcNow };
        var target = new Batch { ProductId = seed.ProductId, BatchNumber = $"T-DST-{marker}", ExpiryDate = ExpiryA, BatchQuantity = 4, UnitCost = 15m, IsQuarantined = true, CreatedAt = DateTime.UtcNow };
        db.Batches.AddRange(source, target);
        await db.SaveChangesAsync();

        var r = await TransferAsync(_fx, seed, source.BatchId, target.BatchNumber, 6);
        Assert.False(r.IsSuccess);

        var unchanged = await GetBatchAsync(_fx, target.BatchId);
        Assert.Equal(4, unchanged.BatchQuantity);
        Assert.Equal(15.00m, unchanged.UnitCost);
        Assert.True(unchanged.IsQuarantined);
    }

    private static async Task<int> FindBatchIdAsync(DatabaseFixture fx, int productId, string batchNumber)
    {
        await using var db = fx.CreateContext();
        return await db.Batches.AsNoTracking()
            .Where(b => b.ProductId == productId && b.BatchNumber == batchNumber)
            .Select(b => b.BatchId)
            .SingleAsync();
    }
}
