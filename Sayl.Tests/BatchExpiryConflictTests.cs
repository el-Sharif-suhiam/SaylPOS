using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// صلاحية الدفعات عند الدمج في دفعة قائمة: تاريخ صلاحية الدفعة القائمة لا يُمس أبدًا —
/// توريد نفس رقم الدفعة بصلاحية مختلفة يُرفض برسالة واضحة (هوية الدفعة =
/// ProductId + BatchNumber فقط، فلا يمكن تسجيل صلاحيتين لنفس رقم الدفعة)،
/// والدفعات المتوافقة تُدمج كالمعتاد، وFEFO يبقى سليمًا.
/// كل عملية خدمة في سياقها الخاص — كما تفعل الواجهة (Scope مستقل لكل استدعاء).
/// </summary>
[Collection("database")]
public class BatchExpiryConflictTests
{
    private readonly DatabaseFixture _fx;
    public BatchExpiryConflictTests(DatabaseFixture fx) => _fx = fx;

    private static readonly DateOnly ExpiryA = new(2027, 1, 15);
    private static readonly DateOnly ExpiryB = new(2028, 6, 30);

    private static DtoStockReceiveRequest Receive(int productId, string batch, int qty, DateOnly? expiry) =>
        new() { ProductId = productId, BatchNumber = batch, Quantity = qty, ExpiryDate = expiry };

    private static DtoPurchaseRequest Purchase(int supplierId, int userId, int productId, string batch, int qty, DateOnly? expiry) =>
        new()
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = batch, Quantity = qty, UnitCost = 10m, ExpiryDate = expiry } }
        };

    private async Task<bool> ReceiveAsync(TestSeed seed, string batch, int qty, DateOnly? expiry)
    {
        await using var s = _fx.CreateContext();
        return (await new clsStockService(s).ReceiveStockAsync(Receive(seed.ProductId, batch, qty, expiry), seed.UserId)).IsSuccess;
    }

    private async Task<Result> ReceiveResultAsync(TestSeed seed, string batch, int qty, DateOnly? expiry)
    {
        await using var s = _fx.CreateContext();
        return await new clsStockService(s).ReceiveStockAsync(Receive(seed.ProductId, batch, qty, expiry), seed.UserId);
    }

    private async Task<Result<int>> PurchaseResultAsync(TestSeed seed, string batch, int qty, DateOnly? expiry)
    {
        await using var s = _fx.CreateContext();
        return await new clsPurchaseService(s).CreatePurchaseInvoiceAsync(Purchase(seed.SupplierId, seed.UserId, seed.ProductId, batch, qty, expiry));
    }

    // ───────── استلام المخزون ─────────

    [Fact]
    public async Task Receive_SameBatch_SameExpiry_MergesQuantity_ExpiryUnchanged()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True(await ReceiveAsync(seed, "B1", 5, ExpiryA));
        Assert.True(await ReceiveAsync(seed, "B1", 7, ExpiryA));

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1");
        Assert.Equal(12, batch.BatchQuantity);
        Assert.Equal(ExpiryA, batch.ExpiryDate); // التاريخ الأصلي كما هو
    }

    [Fact]
    public async Task Receive_SameBatch_DifferentExpiry_Rejected_NothingChanged()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True(await ReceiveAsync(seed, "B1", 5, ExpiryA));

        // نفس رقم الدفعة بصلاحية مختلفة → رفض صريح
        var conflict = await ReceiveResultAsync(seed, "B1", 3, ExpiryB);
        Assert.False(conflict.IsSuccess);
        Assert.Contains("صلاحية", conflict.Message);

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1");
        Assert.Equal(5, batch.BatchQuantity);      // الكمية القائمة لم تتأثر
        Assert.Equal(ExpiryA, batch.ExpiryDate);   // تاريخ الصلاحية الأصلي لم يُطمس
        var inventory = await verify.Inventories.SingleAsync(i => i.ProductId == seed.ProductId);
        Assert.Equal(5, inventory.QuantityAvailable); // المخزون الكلي لم يتأثر (تراجع كامل)
    }

    [Fact]
    public async Task Receive_SameBatch_NullExpiry_MergesAndKeepsExistingExpiry()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True(await ReceiveAsync(seed, "B1", 5, ExpiryA));
        // استلام بلا صلاحية محددة → دمج عادي، صلاحية الدفعة القائمة تبقى
        Assert.True(await ReceiveAsync(seed, "B1", 2, null));

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1");
        Assert.Equal(7, batch.BatchQuantity);
        Assert.Equal(ExpiryA, batch.ExpiryDate);
    }

    // ───────── فواتير الشراء ─────────

    [Fact]
    public async Task Purchase_SameBatch_CompatibleExpiry_Merges()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True((await PurchaseResultAsync(seed, "B1", 5, ExpiryA)).IsSuccess);
        Assert.True((await PurchaseResultAsync(seed, "B1", 5, ExpiryA)).IsSuccess);

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1");
        Assert.Equal(10, batch.BatchQuantity);
        Assert.Equal(ExpiryA, batch.ExpiryDate);
    }

    [Fact]
    public async Task Purchase_SameBatch_ConflictingExpiry_Rejected_InvoiceRolledBack()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True((await PurchaseResultAsync(seed, "B1", 5, ExpiryA)).IsSuccess);

        // فاتورة ثانية بنفس رقم الدفعة وصلاحية مختلفة → رفض صريح
        var conflict = await PurchaseResultAsync(seed, "B1", 3, ExpiryB);
        Assert.False(conflict.IsSuccess);
        Assert.Contains("صلاحية", conflict.Message);

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1");
        Assert.Equal(5, batch.BatchQuantity);
        Assert.Equal(ExpiryA, batch.ExpiryDate);

        // الفاتورة المرفوضة تراجعت كليًا: فاتورة واحدة فقط (الأولى الناجحة) —
        // لا فاتورة جديدة ولا زيادة مخزون ولا رصيد مورد إضافي
        var invoices = await verify.Invoices.Where(i => i.Type == enInvoicesType.purchase && i.PartyId == seed.SupplierId).ToListAsync();
        Assert.Single(invoices);
        var inventory = await verify.Inventories.SingleAsync(i => i.ProductId == seed.ProductId);
        Assert.Equal(5, inventory.QuantityAvailable);
    }

    [Fact]
    public async Task Purchase_SameInvoice_SameBatch_ConflictingExpiry_Rejected()
    {
        // بندان في نفس الفاتورة بنفس رقم الدفعة وصلاحيتين مختلفتين → رفض
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var request = new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items =
            {
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B1", Quantity = 5, UnitCost = 10m, ExpiryDate = ExpiryA },
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B1", Quantity = 3, UnitCost = 10m, ExpiryDate = ExpiryB }
            }
        };

        await using var s = _fx.CreateContext();
        var conflict = await new clsPurchaseService(s).CreatePurchaseInvoiceAsync(request);
        Assert.False(conflict.IsSuccess);
        Assert.Contains("صلاحية", conflict.Message);

        await using var verify = _fx.CreateContext();
        Assert.False(await verify.Batches.AnyAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "B1"));
    }

    // ───────── FEFO ─────────

    [Fact]
    public async Task Fefo_CompatibleMerge_ThenSale_ConsumesEarliestExpiryFirst()
    {
        string marker = $"bx-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // دفعتان بصلاحيتين مختلفتين، ثم دمج متوافق في الأقدم صلاحية
        Assert.True(await ReceiveAsync(seed, "OLD", 10, ExpiryA));
        Assert.True(await ReceiveAsync(seed, "NEW", 10, ExpiryB));
        Assert.True(await ReceiveAsync(seed, "OLD", 2, ExpiryA));

        // بيع 12 → يستهلك OLD (الأقرب انتهاءً) أولًا
        await using var sellCtx = _fx.CreateContext();
        var sell = await new clsStockService(sellCtx).SellStockAsync(seed.ProductId, 12);
        Assert.True(sell.IsSuccess, sell.Message);

        await using var verify = _fx.CreateContext();
        var oldBatch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "OLD");
        var newBatch = await verify.Batches.SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "NEW");
        Assert.Equal(0, oldBatch.BatchQuantity);  // 12 − 12
        Assert.Equal(10, newBatch.BatchQuantity); // لم يُمس
        Assert.Equal(ExpiryA, oldBatch.ExpiryDate);
        Assert.Equal(ExpiryB, newBatch.ExpiryDate);
    }
}
