using Common.DTOs.RequestModels;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// المرتجعات: البضاعة المرتجعة تقلب تكلفتها التاريخية من دفعتها الأصلية —
/// لا Product.CostPrice الحالي.
/// </summary>
[Collection("database")]
public class ReturnCostingTests
{
    private readonly DatabaseFixture _fx;
    public ReturnCostingTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>شراء دفعتين (A: 5 @10 صلاحية أبعد، B: 5 @15 صلاحية أقرب) وبيع 7 (B:5 ثم A:2).</summary>
    private async Task<(int ProductId, int UserId, int CustomerId, int SaleInvoiceId, int BatchAId, int BatchBId)>
        SeedSaleAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-A", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        })).IsSuccess);

        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-B", Quantity = 5, UnitCost = 15m, ExpiryDate = today.AddDays(10) } }
        })).IsSuccess);

        // سياق جديد للبيع — الإنتاج ينشئ سياقًا لكل طلب، والسياق الواحد يحمل
        // كيانات متتبعة قديمة (المخزون زادته UPDATE خام لا يراها التتبع)
        await using var saleDb = _fx.CreateContext();
        var checkout = new clsCheckoutService(saleDb);

        var sale = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 7 } },
            Payment = new DtoPaymentInfo { Amount = 140m, Method = "cash" }
        });
        Assert.True(sale.IsSuccess, sale.Message);

        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == seed.ProductId).ToListAsync();
        return (seed.ProductId, seed.UserId, seed.CustomerId, sale.Data,
            batches.First(b => b.BatchNumber == "B-A").BatchId,
            batches.First(b => b.BatchNumber == "B-B").BatchId);
    }

    [Fact]
    public async Task CustomerReturn_ReversesOriginalBatchCost_NotCurrentProductCost()
    {
        string marker = $"rc-{Guid.NewGuid():N}"[..12];
        var (productId, userId, customerId, saleInvoiceId, batchAId, batchBId) = await SeedSaleAsync(marker);

        await using var db = _fx.CreateContext();

        // ارتفاع سعر تكلفة المنتج بعد البيع — المرتجع يجب ألا يستخدمه
        (await db.Products.FirstAsync(p => p.ProductId == productId)).CostPrice = 99m;
        await db.SaveChangesAsync();

        var returns = new clsReturnService(db);
        var result = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleInvoiceId,
            UserId = userId,
            Items = { new DtoReturnItem { ProductId = productId, Quantity = 3 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        var returnInvoice = await db.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == result.Data);

        // FEFO للبيع استهلك B أولًا — FEFO التخصيص يعيد إلى B أولًا: 3 @15 من دفعة B
        var item = Assert.Single(returnInvoice.InvoiceItems);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(batchBId, item.BatchId);
        Assert.Equal(15.00m, item.ItemCost); // التكلفة الأصلية للدفعة لا 99

        // البضاعة عادت إلى دفتها الأصلية
        var batchB = await db.Batches.FirstAsync(b => b.BatchId == batchBId);
        Assert.Equal(3, batchB.BatchQuantity);

        // قلب COGS: مرتجع العميل يحمل تكلفة الوحدات المرتجعة
        Assert.Equal(45m, returnInvoice.InvoiceItems.Sum(ii => (ii.ItemCost ?? 0m) * ii.Quantity));
    }

    [Fact]
    public async Task CustomerReturn_SpanningMultipleBatches_KeepsEachBatchCostOnItsOwnLine()
    {
        string marker = $"rm-{Guid.NewGuid():N}"[..12];
        var (productId, userId, customerId, saleInvoiceId, batchAId, batchBId) = await SeedSaleAsync(marker);

        await using var db = _fx.CreateContext();
        var returns = new clsReturnService(db);

        // إرجاع 6: 5 من B (تكلفة 15) ثم 1 من A (تكلفة 10) — صف لكل دفعة
        var result = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleInvoiceId,
            UserId = userId,
            Items = { new DtoReturnItem { ProductId = productId, Quantity = 6 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        var returnInvoice = await db.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == result.Data);

        var bLine = Assert.Single(returnInvoice.InvoiceItems, ii => ii.BatchId == batchBId);
        var aLine = Assert.Single(returnInvoice.InvoiceItems, ii => ii.BatchId == batchAId);
        Assert.Equal(5, bLine.Quantity);
        Assert.Equal(15.00m, bLine.ItemCost);
        Assert.Equal(1, aLine.Quantity);
        Assert.Equal(10.00m, aLine.ItemCost);

        // الكميات عادت لدفعاتها الصحيحة (البيع: B أفرغت وA صارت 3 — المرتجع: B=5 وA=4)
        Assert.Equal(5, (await db.Batches.FirstAsync(b => b.BatchId == batchBId)).BatchQuantity);
        Assert.Equal(4, (await db.Batches.FirstAsync(b => b.BatchId == batchAId)).BatchQuantity);
    }

    [Fact]
    public async Task SupplierReturn_ReversesDiscountedAcquisitionCost()
    {
        string marker = $"rs-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);

        // شراء بخصم: 10 @10 خصم 10 → تكلفة فعالة 9.00 على الدفعة
        var purchaseResult = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            DiscountAmount = 10m,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-P", Quantity = 10, UnitCost = 10m } }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        // سياق جديد للمرتجع (كما في الإنتاج: سياق لكل طلب)
        await using var returnDb = _fx.CreateContext();
        var returns = new clsReturnService(returnDb);
        var result = await returns.CreateSupplierReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = purchaseResult.Data,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        var returnInvoice = await returnDb.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == result.Data);

        var item = Assert.Single(returnInvoice.InvoiceItems);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(9.00m, item.ItemCost); // التكلفة بعد تخصيص الخصم لا 10 الخام

        // الاسترداد نسبي: ما دُفع فعليًا عن وحدتين بعد الخصم = 18
        Assert.Equal(-18.00m, returnInvoice.NetAmount);

        // المخزون نقص من الدفعة نفسها
        await using var verifyDb = _fx.CreateContext();
        var batch = await verifyDb.Batches.FirstAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(8, batch.BatchQuantity);
    }
}
