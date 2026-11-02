using Common.DTOs.RequestModels;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// تكلفة المبيعات (COGS): البيع يستهلك دفعات FEFO بتكاليف اقتناء مختلفة —
/// الفاتورة تسجل تكلفة كل دفعة مستهلكة فعلًا (صف لكل دفعة)، لا Product.CostPrice.
/// </summary>
[Collection("database")]
public class SaleCogsTests
{
    private readonly DatabaseFixture _fx;
    public SaleCogsTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>شراء دفعتين لنفس المنتج بتكلفة مختلفة: A (5 @10، صلاحية أبعد) وB (5 @15، صلاحية أقرب).</summary>
    private async Task<(int ProductId, int UserId, int BatchAId, int BatchBId)> SeedTwoBatchesAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await service.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-A", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        })).IsSuccess);

        Assert.True((await service.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-B", Quantity = 5, UnitCost = 15m, ExpiryDate = today.AddDays(10) } }
        })).IsSuccess);

        var batches = await db.Batches.Where(b => b.ProductId == seed.ProductId).ToListAsync();
        return (seed.ProductId, seed.UserId,
            batches.First(b => b.BatchNumber == "B-A").BatchId,
            batches.First(b => b.BatchNumber == "B-B").BatchId);
    }

    [Fact]
    public async Task Sale_ConsumingMultipleBatches_RecordsEachBatchCostOnItsOwnLine()
    {
        string marker = $"sc-{Guid.NewGuid():N}"[..12];
        var (productId, userId, batchAId, batchBId) = await SeedTwoBatchesAsync(marker);

        await using var db = _fx.CreateContext();
        var checkout = new clsCheckoutService(db);

        // FEFO: الدفعة الأقرب صلاحية (B بتكلفة 15) تُستهلك أولًا
        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = userId,
            Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = 7 } },
            Payment = new DtoPaymentInfo { Amount = 140m, Method = "cash" }
        });
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == result.Data);

        // صف لكل دفعة مستهلكة — BatchId واحد لا يتظاهر بتمثيل عدة دفعات
        var items = invoice.InvoiceItems.OrderBy(ii => ii.BatchId).ToList();
        Assert.Equal(2, items.Count);

        var bLine = items.First(ii => ii.BatchId == batchBId); // 5 @15
        var aLine = items.First(ii => ii.BatchId == batchAId); // 2 @10
        Assert.Equal(5, bLine.Quantity);
        Assert.Equal(15.00m, bLine.ItemCost);
        Assert.Equal(2, aLine.Quantity);
        Assert.Equal(10.00m, aLine.ItemCost);

        // COGS الفعلي 85 لا 7×15=105 (Product.CostPrice) — الربحية الصحيحة
        decimal cogs = items.Sum(ii => (ii.ItemCost ?? 0m) * ii.Quantity);
        Assert.Equal(95m, cogs);

        // سعر البيع على كل صف — التكلفة وحدها تتبع الدفعة
        Assert.All(items, ii => Assert.Equal(20m, ii.ItemPrice));

        // المخزون: 3 بقي في A، وB أفرغ
        var batchA = await db.Batches.FirstAsync(b => b.BatchId == batchAId);
        var batchB = await db.Batches.FirstAsync(b => b.BatchId == batchBId);
        Assert.Equal(3, batchA.BatchQuantity);
        Assert.Equal(0, batchB.BatchQuantity);
    }

    [Fact]
    public async Task SuspendedInvoice_Completion_RecordsActualBatchCosts()
    {
        string marker = $"ss-{Guid.NewGuid():N}"[..12];
        var (productId, userId, batchAId, batchBId) = await SeedTwoBatchesAsync(marker);

        await using var db = _fx.CreateContext();
        var checkout = new clsCheckoutService(db);

        // تعليق: حجز فقط — لا خصم ولا تكلفة بعد
        var suspendResult = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = userId,
            Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = 6 } },
            Payment = new DtoPaymentInfo { Amount = 120m, Method = "cash" }
        }, suspend: true);
        Assert.True(suspendResult.IsSuccess, suspendResult.Message);

        var suspended = await db.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == suspendResult.Data);
        var suspendedItem = Assert.Single(suspended.InvoiceItems);
        Assert.Null(suspendedItem.ItemCost); // لا تكلفة قبل استهلاك الدفعات

        // إكمال: يستهلك FEFO — B:5@15 ثم A:1@10
        var completeResult = await checkout.CompleteSuspendedInvoiceAsync(
            suspendResult.Data,
            new List<DtoPaymentInfo> { new() { Amount = 120m, Method = "cash" } },
            userId);
        Assert.True(completeResult.IsSuccess, completeResult.Message);

        await db.Entry(suspended).ReloadAsync();
        await db.Entry(suspended).Collection(i => i.InvoiceItems).LoadAsync();

        var items = suspended.InvoiceItems.OrderBy(ii => ii.BatchId).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, ii => ii.BatchId == batchBId && ii.Quantity == 5 && ii.ItemCost == 15.00m);
        Assert.Contains(items, ii => ii.BatchId == batchAId && ii.Quantity == 1 && ii.ItemCost == 10.00m);

        decimal cogs = items.Sum(ii => (ii.ItemCost ?? 0m) * ii.Quantity);
        Assert.Equal(85m, cogs); // B:5@15 + A:1@10
    }

    [Fact]
    public async Task Sale_FromBatchWithoutCost_FallsBackToProductCostPrice()
    {
        // استلام يدوي بلا تكلفة (دفعة قديمة) — COGS يرجع إلى سعر المنتج الأخير
        string marker = $"fb-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var stockService = new clsStockService(db);
        Assert.True((await stockService.ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = "B-LEGACY",
            Quantity = 4
            // UnitCost = null — استلام قديم بلا تكلفة
        }, seed.UserId)).IsSuccess);

        // سياق جديد للبيع — كسياق الإنتاج لكل طلب
        await using var saleDb = _fx.CreateContext();
        var checkout = new clsCheckoutService(saleDb);
        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 2 } },
            Payment = new DtoPaymentInfo { Amount = 40m, Method = "cash" }
        });
        Assert.True(result.IsSuccess, result.Message);

        var item = await db.InvoiceItems.FirstAsync(ii => ii.InvoiceId == result.Data);
        var product = await db.Products.FirstAsync(p => p.ProductId == seed.ProductId);
        Assert.Equal(10m, product.CostPrice); // Product.CostPrice كما بُذر
        Assert.Equal(10m, item.ItemCost);     // الرجوع لسعر المنتج — سلوك موثق
    }
}
