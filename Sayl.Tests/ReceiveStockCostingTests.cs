using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// استلام المخزون اليدوي (ReceiveStockAsync): التكلفة تتوسط بالوزن بالكمية
/// (متوسط مرجح) عند الدمج في دفعة قائمة — نفس معادلة دمج الشراء ودمج النقل
/// (COSTING.md). لا تُخترع تكلفة بديلة ولا تُطمس تكلفة قائمة.
/// كل عملية خدمة في سياقها المستقل — كما تفعل الواجهة (Scope مستقل لكل استدعاء).
/// </summary>
/// <remarks>
/// بذر واحد مشترك لكل الصف: عمود Roles.RoleId هوية tinyint (حد 255 صفًا) —
/// بذر دور لكل اختبار يفيض الهوية في الرَن الكامل. دور واحد للمستخدم المشترك،
/// ومنتج جديد لكل اختبار (معرف int بلا حد عملي) حتى لا تتلوث الكميات والتكاليف.
/// </remarks>
[Collection("database")]
public class ReceiveStockCostingTests
{
    private readonly DatabaseFixture _fx;
    public ReceiveStockCostingTests(DatabaseFixture fx) => _fx = fx;

    private static readonly DateOnly ExpiryA = new(2027, 3, 15);
    private static readonly DateOnly ExpiryB = new(2028, 6, 30);

    private static TestSeed? _sharedSeed;

    /// <summary>دور/مستخدم/مورد مشترك لكل اختبارات الصف — بذر مرة واحدة.</summary>
    private async Task<TestSeed> SharedSeedAsync()
    {
        if (_sharedSeed is not null)
            return _sharedSeed;

        await using var db = _fx.CreateContext();
        _sharedSeed = await TestSeed.SeedAsync(db, $"rcv-{Guid.NewGuid():N}"[..8]);
        return _sharedSeed;
    }

    /// <summary>منتج مخزني جديد لكل اختبار — الكميات والتكاليف معزولة بين الاختبارات.</summary>
    private async Task<int> NewProductAsync()
    {
        var seed = await SharedSeedAsync();
        await using var db = _fx.CreateContext();
        var product = new Product
        {
            Name = $"منتج-rcv-{Guid.NewGuid():N}"[..16],
            Type = "stock",
            BasePrice = 20m,
            CostPrice = 10m, // قديم/خارجي — يجب ألا يُستخدم كـ COGS عند وجود دفعات
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    /// <summary>بذر دفعة يدوية بتكلفة محددة — الاختبار يركز على منطق الاستلام وحده.</summary>
    private async Task<(int ProductId, int BatchId, string BatchNumber)> SeedBatchAsync(
        int productId, decimal? unitCost, int quantity, DateOnly? expiry = null)
    {
        await using var db = _fx.CreateContext();
        var batchNumber = $"R-{Guid.NewGuid():N}"[..12];
        var batch = new Batch
        {
            ProductId = productId,
            BatchNumber = batchNumber,
            ExpiryDate = expiry ?? ExpiryA,
            BatchQuantity = quantity,
            UnitCost = unitCost,
            CreatedAt = DateTime.UtcNow
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        return (productId, batch.BatchId, batchNumber);
    }

    private async Task<Result> ReceiveAsync(TestSeed seed, int productId,
        string batchNumber, int qty, DateOnly? expiry = null, decimal? unitCost = null)
    {
        await using var s = _fx.CreateContext();
        return await new clsStockService(s).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = productId,
            BatchNumber = batchNumber,
            Quantity = qty,
            ExpiryDate = expiry,
            UnitCost = unitCost
        }, seed.UserId);
    }

    private static async Task<Batch> GetBatchAsync(DatabaseFixture fx, int batchId)
    {
        await using var db = fx.CreateContext();
        return await db.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
    }

    // ───────── 1) استلام في دفعة جديدة ─────────

    [Fact]
    public async Task Receive_NewBatch_CreatesWithIncomingCost_QuantityAndMovement()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();

        var r = await ReceiveAsync(seed, productId, "B-NEW", 6, ExpiryA, 12.50m);
        Assert.True(r.IsSuccess, r.Message);

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == productId && b.BatchNumber == "B-NEW");
        Assert.Equal(6, batch.BatchQuantity);
        Assert.Equal(12.50m, batch.UnitCost); // تكلفة الاقتناء كما وردت — لا وسط مع لا شيء

        var movement = await verify.StockMovements.SingleAsync(m => m.BatchId == batch.BatchId);
        Assert.Equal(enStockMovementType.purchase, movement.MovementType);
        Assert.Equal(6, movement.Quantity);

        var inventory = await verify.Inventories.SingleAsync(i => i.ProductId == productId);
        Assert.Equal(6, inventory.QuantityAvailable);
    }

    // ───────── 2) دمج في دفعة قائمة بتكلفة مختلفة → متوسط مرجح ─────────

    [Fact]
    public async Task Receive_ExistingBatch_DifferentCosts_WeightedAverageByQuantity()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 10m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 20m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(10, merged.BatchQuantity);
        // (10×5 + 20×5) ÷ 10 = 15.00 — متوسط مرجح بالكمية
        Assert.Equal(15.00m, merged.UnitCost);
    }

    [Fact]
    public async Task Receive_Merge_PreservesTotalInventoryCost()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 10m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 20m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        // قبل: 5×10 + 5×20 = 150 — بعد: 10×15.00 = 150 (متوسط مثالي بلا فروق تقريب)
        Assert.Equal(150m, merged.BatchQuantity * (merged.UnitCost ?? 0m));
    }

    // ───────── 3) دفعة قائمة بلا تكلفة → أول تكلفة صريحة تملؤها ─────────

    [Fact]
    public async Task Receive_ExistingBatch_NullCost_FilledWithIncomingCost()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: null, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 20m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Equal(20.00m, merged.UnitCost); // الكمية القديمة بلا تكلفة — الوحدات كلها بتكلفة الاستلام
    }

    // ───────── 4) استلام بلا تكلفة في دفعة قائمة → لا تطميس ولا اختراع ─────────

    [Fact]
    public async Task Receive_ExistingBatch_IncomingCostNull_KeepsExistingCost()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 15m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: null);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Equal(15.00m, merged.UnitCost); // تكلفة القائمة لم تُطمس ولا تُستبدل باختراع
    }

    // ───────── 5) تكلفة الاستلام = تكلفة القائمة ─────────

    [Fact]
    public async Task Receive_EqualCosts_QuantityMerges_CostUnchanged()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 10m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 10m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(10, merged.BatchQuantity);
        Assert.Equal(10.00m, merged.UnitCost);
    }

    // ───────── 6) دمج في دفعة قائمة فارغة (كمية 0) ─────────

    [Fact]
    public async Task Receive_IntoEmptyBatch_TakesIncomingCost()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        // دفعة مستهلكة (كمية 0) بتكلفة قديمة — كل الوحدات الجديدة من الاستلام
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 15m, quantity: 0);

        var r = await ReceiveAsync(seed, productId, batchNumber, 4, unitCost: 10m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(4, merged.BatchQuantity);
        Assert.Equal(10.00m, merged.UnitCost); // الدفعة كانت فارغة — كل الوحدات بتكلفة الاستلام
    }

    [Fact]
    public async Task Receive_IntoEmptyNullCostBatch_TakesIncomingCost()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: null, quantity: 0);

        var r = await ReceiveAsync(seed, productId, batchNumber, 4, unitCost: 10m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(4, merged.BatchQuantity);
        Assert.Equal(10.00m, merged.UnitCost);
    }

    // ───────── 7) تكاليف عشرية ─────────

    [Fact]
    public async Task Receive_DecimalCosts_WeightedAverageExact()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 12.10m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 3, unitCost: 7.33m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(8, merged.BatchQuantity);
        // (12.10×5 + 7.33×3) ÷ 8 = 82.49 ÷ 8 = 10.31125 → 10.31 (قرصان)
        Assert.Equal(10.31m, merged.UnitCost);
    }

    // ───────── 8) التقريب: قرصان بعيدًا عن الصفر (كمعادلة الشراء) ─────────

    [Fact]
    public async Task Receive_WeightedAverage_RoundsAwayFromZero_AtTwoDecimals()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 12m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 3, unitCost: 7m);
        Assert.True(r.IsSuccess, r.Message);

        var merged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(8, merged.BatchQuantity);
        // (12×5 + 7×3) ÷ 8 = 81 ÷ 8 = 10.125 → 10.13 (بعيدًا عن الصفر)
        Assert.Equal(10.13m, merged.UnitCost);
    }

    // ───────── 9) فشل الاستلام: تراجع كامل لا مساس بالتكلفة ولا الكمية ─────────

    [Fact]
    public async Task Receive_Failure_ExpiryConflict_RollsBackFully()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 15m, quantity: 4, expiry: ExpiryA);

        // نفس رقم الدفعة بصلاحية مختلفة → رفض صريح
        var r = await ReceiveAsync(seed, productId, batchNumber, 3, ExpiryB, 10m);
        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);

        await using var verify = _fx.CreateContext();
        var unchanged = await verify.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
        Assert.Equal(4, unchanged.BatchQuantity);       // الكمية القائمة لم تتأثر
        Assert.Equal(15.00m, unchanged.UnitCost);       // التكلفة لم تُمس — لا دمج
        Assert.Equal(ExpiryA, unchanged.ExpiryDate);    // الصلاحية الأصلية كما هي

        // لا صف مخزون أصلًا: البذر لا ينشئه، والاستلام الفاشل تراجع كليًا فلم يُنشئه
        Assert.False(await verify.Inventories.AnyAsync(i => i.ProductId == productId));
        Assert.False(await verify.StockMovements
            .AnyAsync(m => m.BatchId == batchId && m.Quantity == 3)); // لا حركة يتيمة
    }

    [Fact]
    public async Task Receive_NegativeCost_Rejected_NothingChanged()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 15m, quantity: 4);

        var r = await ReceiveAsync(seed, productId, batchNumber, 3, unitCost: -1m);
        Assert.False(r.IsSuccess);

        var unchanged = await GetBatchAsync(_fx, batchId);
        Assert.Equal(4, unchanged.BatchQuantity);
        Assert.Equal(15.00m, unchanged.UnitCost);
    }

    // ───────── 10) استلام مكرر على نفس الدفعة: تراكم الكمية ووسط التكلفة كل مرة ─────────

    [Fact]
    public async Task Receive_DuplicateReceipts_AccumulateQuantity_AndReaverageCostEachTime()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 10m, quantity: 5);

        // استلام أول: 5 @ 10 (نفس التكلفة) → 10 @ 10.00
        Assert.True((await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 10m)).IsSuccess);

        // استلام ثانٍ مكرر: 5 @ 20 → (10×10 + 20×5) ÷ 15 = 200 ÷ 15 = 13.333… → 13.33
        Assert.True((await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 20m)).IsSuccess);

        // استلام ثالث مكرر: 5 @ 23 → (13.33×15 + 23×5) ÷ 20 = (199.95 + 115) ÷ 20 = 15.7475 → 15.75
        Assert.True((await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 23m)).IsSuccess);

        await using var verify = _fx.CreateContext();
        var merged = await verify.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId);
        Assert.Equal(20, merged.BatchQuantity);
        Assert.Equal(15.75m, merged.UnitCost);

        // حركة لكل استلام، ومجموع الكميات المستلمة = 15
        var movements = await verify.StockMovements
            .Where(m => m.BatchId == batchId && m.MovementType == enStockMovementType.purchase)
            .ToListAsync();
        Assert.Equal(3, movements.Count);
        Assert.Equal(15, movements.Sum(m => m.Quantity));

        // المخزون الكلي = الكميات المستلمة فقط (الدفعة المبذورة يدويًا بلا صف مخزون)
        var inventory = await verify.Inventories.SingleAsync(i => i.ProductId == productId);
        Assert.Equal(15, inventory.QuantityAvailable);
    }

    // ───────── سياسة التكلفة: Product.CostPrice لا يُمس بالاستلام اليدوي ─────────

    [Fact]
    public async Task Receive_NeverTouchesProductCostPrice()
    {
        var seed = await SharedSeedAsync();
        int productId = await NewProductAsync();
        var (_, batchId, batchNumber) = await SeedBatchAsync(productId, unitCost: 10m, quantity: 5);

        var r = await ReceiveAsync(seed, productId, batchNumber, 5, unitCost: 20m);
        Assert.True(r.IsSuccess, r.Message);

        await using var verify = _fx.CreateContext();
        var product = await verify.Products.AsNoTracking().SingleAsync(p => p.ProductId == productId);
        Assert.Equal(10m, product.CostPrice); // سعر آخر شراء للعرض فقط — الاستلام اليدوي لا يكتبه
    }
}
