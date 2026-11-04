using Common.DTOs.RequestModels;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// توزيع المرتجع على سطور الفاتورة الأصلية ودفعاتها مع تكرار المرتجعات الجزئية:
/// سعة كل دفعة = المباع منها ناقص المرتجع سابقًا منها — لا يعاد إلى دفعة
/// أكثر مما بِيع/اشتري منها أصلًا، والتكلفة والاسترداد يتبعان الدفعة الأصلية.
/// </summary>
[Collection("database")]
public class ReturnAllocationTests
{
    private readonly DatabaseFixture _fx;
    public ReturnAllocationTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>شراء دفعتين لمنتج واحد (A: 5 @10 صلاحية أبعد، B: 5 @15 صلاحية أقرب)
    /// وبيع كمية واحدة (FEFO يستهلك B أولًا ثم A).</summary>
    private async Task<(int ProductId, int UserId, int SaleInvoiceId, int BatchAId, int BatchBId)>
        SeedSaleAsync(string marker, params int[] quantities)
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

        // سياق جديد للبيع — كما في الإنتاج: سياق لكل طلب
        await using var saleDb = _fx.CreateContext();
        var checkout = new clsCheckoutService(saleDb);

        var sale = new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Payment = new DtoPaymentInfo { Amount = 20m * quantities.Sum(), Method = "cash" }
        };
        foreach (var q in quantities)
            sale.Items.Add(new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = q });

        var saleResult = await checkout.ProcessCheckoutAsync(sale);
        Assert.True(saleResult.IsSuccess, saleResult.Message);

        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == seed.ProductId).ToListAsync();
        return (seed.ProductId, seed.UserId, saleResult.Data,
            batches.First(b => b.BatchNumber == "B-A").BatchId,
            batches.First(b => b.BatchNumber == "B-B").BatchId);
    }

    private static DtoReturnRequest ReturnRequest(int invoiceId, int userId, int productId, int qty) => new()
    {
        OriginalInvoiceId = invoiceId,
        UserId = userId,
        Items = { new DtoReturnItem { ProductId = productId, Quantity = qty } }
    };

    /// <summary>بيع 7 (B:5 ثم A:2) ثم مرتجعان متتاليان 3 و3 — الثاني يكمل التوزيع:
    /// 2 من B (ما تبقى لها) و1 من A، لا 3 من B مجددًا فوق ما بِيع منها.</summary>
    [Fact]
    public async Task TwoConsecutivePartialReturns_SecondContinuesAcrossBatches()
    {
        string marker = $"ra1-{Guid.NewGuid():N}"[..12];
        var (productId, userId, saleInvoiceId, batchAId, batchBId) = await SeedSaleAsync(marker, 7);

        await using var db = _fx.CreateContext();
        var returns = new clsReturnService(db);

        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3))).IsSuccess);
        var second = await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3));
        Assert.True(second.IsSuccess, second.Message);

        var return2 = await db.Invoices.Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == second.Data);

        // التوزيع الصحيح للمرتجع الثاني: B تحمل 2 فقط (5 المباعة − 3 المرتجعة سابقًا) وA تحمل 1
        var bLine = Assert.Single(return2.InvoiceItems, ii => ii.BatchId == batchBId);
        var aLine = Assert.Single(return2.InvoiceItems, ii => ii.BatchId == batchAId);
        Assert.Equal(2, bLine.Quantity);
        Assert.Equal(15.00m, bLine.ItemCost);
        Assert.Equal(1, aLine.Quantity);
        Assert.Equal(10.00m, aLine.ItemCost);

        // المخزون: B استوعبت 5 المباعة منها بالضبط (لا 6) وA استوعبت 3 من 5
        Assert.Equal(5, (await db.Batches.FirstAsync(b => b.BatchId == batchBId)).BatchQuantity);
        Assert.Equal(4, (await db.Batches.FirstAsync(b => b.BatchId == batchAId)).BatchQuantity);

        // الاسترداد يطابق سعر البيع للكميات (3×20 بلا خصم/ضريبة) وتكلفة المرتجع من الدفعات الأصلية
        Assert.Equal(-60.00m, return2.NetAmount);
        Assert.Equal(40m, return2.InvoiceItems.Sum(ii => (ii.ItemCost ?? 0m) * ii.Quantity));
    }

    /// <summary>مرتجعات جزئية متكررة حتى استنفاد المباع (3+2+2 = 7) — الدفعات تعود
    /// لكميتها قبل البيع بالضبط، وما بعدها يُرفض.</summary>
    [Fact]
    public async Task RepeatedPartialReturns_ExhaustSale_ThenFurtherReturnRejected()
    {
        string marker = $"ra2-{Guid.NewGuid():N}"[..12];
        var (productId, userId, saleInvoiceId, batchAId, batchBId) = await SeedSaleAsync(marker, 7);

        await using var db = _fx.CreateContext();
        var returns = new clsReturnService(db);

        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3))).IsSuccess);
        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 2))).IsSuccess);
        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 2))).IsSuccess);

        // استنفدت الكمية المباعة — الكميات التراكمية = 7 لا أكثر، والدفعات كاملة
        Assert.Equal(5, (await db.Batches.FirstAsync(b => b.BatchId == batchBId)).BatchQuantity);
        Assert.Equal(5, (await db.Batches.FirstAsync(b => b.BatchId == batchAId)).BatchQuantity);

        var totalReturned = await db.InvoiceItems
            .Where(ii => ii.Invoice.OriginalInvoiceId == saleInvoiceId
                      && ii.Invoice.Type == Common.Enums.enInvoicesType.customer_return)
            .SumAsync(ii => (int?)ii.Quantity) ?? 0;
        Assert.Equal(7, totalReturned);

        var rejected = await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 1));
        Assert.False(rejected.IsSuccess);
        Assert.Contains("المتبقي: 0", rejected.Message);
    }

    /// <summary>مرتجع يتجاوز المتبقي (بعد مرتجع سابق) يُرفض ولا يُنشأ له أثر.</summary>
    [Fact]
    public async Task AttemptedReturn_ExceedingRemainingQuantity_IsRejected()
    {
        string marker = $"ra3-{Guid.NewGuid():N}"[..12];
        var (productId, userId, saleInvoiceId, _, _) = await SeedSaleAsync(marker, 7);

        await using var db = _fx.CreateContext();
        var returns = new clsReturnService(db);

        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3))).IsSuccess);

        // المتبقي 4 والمطلوب 5
        var result = await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 5));
        Assert.False(result.IsSuccess);
        Assert.Contains("المتبقي: 4", result.Message);

        // لا فاتورة مرتجع جديدة أُنشئت بالرفض
        int returnCount = await db.Invoices
            .CountAsync(i => i.OriginalInvoiceId == saleInvoiceId
                          && i.Type == Common.Enums.enInvoicesType.customer_return);
        Assert.Equal(1, returnCount);
    }

    /// <summary>دفعة واحدة تمتد على سطرين من نفس الفاتورة (السحب FEFO عند البيع يقسم
    /// السطر صفًا لكل دفعة فتتكرر الدفعة) — سعتها تُجمع: مرتجعان 3 و3 يعيدان إلى B
    /// ما بِيع منها (5) بالضبط ثم يكملان إلى A، لا 6 إلى B.</summary>
    [Fact]
    public async Task SameBatch_SpanningMultipleSaleLines_PooledCapacity()
    {
        string marker = $"ra4-{Guid.NewGuid():N}"[..12];
        // سطرا بيع 4 و3: FEFO يستهلك B:4 ثم B:1 ثم A:2 — B على سطرين
        var (productId, userId, saleInvoiceId, batchAId, batchBId) = await SeedSaleAsync(marker, 4, 3);

        await using var db = _fx.CreateContext();

        // التأكد من الفرضية: الدفعة B فعلًا على سطرين في فاتورة البيع
        int bLineCount = await db.InvoiceItems
            .CountAsync(ii => ii.InvoiceId == saleInvoiceId && ii.BatchId == batchBId);
        Assert.Equal(2, bLineCount);

        var returns = new clsReturnService(db);
        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3))).IsSuccess);
        Assert.True((await returns.CreateCustomerReturnAsync(ReturnRequest(saleInvoiceId, userId, productId, 3))).IsSuccess);

        // B استوعبت 5 المباعة منها (لا 6) وA استوعبت 1 من 2
        Assert.Equal(5, (await db.Batches.FirstAsync(b => b.BatchId == batchBId)).BatchQuantity);
        Assert.Equal(4, (await db.Batches.FirstAsync(b => b.BatchId == batchAId)).BatchQuantity);
    }

    /// <summary>مرتجع مورد: منطق التوزيع مشترك — مرتجعان جزئيان متتاليان على فاتورة
    /// شراء بدفعتين: الثاني يكمل التوزيع (2 من B ثم 1 من A) بدل الفشل على دفعة نُفدت
    /// بمرتجع أول أعاد التوزيع إليها كاملًا.</summary>
    [Fact]
    public async Task SupplierReturn_SecondConsecutivePartialReturn_ContinuesAcrossBatches()
    {
        string marker = $"ra5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        var purchaseResult = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items =
            {
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "P-A", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) },
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "P-B", Quantity = 5, UnitCost = 15m, ExpiryDate = today.AddDays(10) }
            }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        var batches = await db.Batches.AsNoTracking()
            .Where(b => b.ProductId == seed.ProductId).ToListAsync();
        int batchAId = batches.First(b => b.BatchNumber == "P-A").BatchId;
        int batchBId = batches.First(b => b.BatchNumber == "P-B").BatchId;

        await using var returnDb = _fx.CreateContext();
        var returns = new clsReturnService(returnDb);

        Assert.True((await returns.CreateSupplierReturnAsync(ReturnRequest(purchaseResult.Data, seed.UserId, seed.ProductId, 3))).IsSuccess);
        var second = await returns.CreateSupplierReturnAsync(ReturnRequest(purchaseResult.Data, seed.UserId, seed.ProductId, 3));
        Assert.True(second.IsSuccess, second.Message);

        var return2 = await returnDb.Invoices.Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == second.Data);

        // المرتجع الثاني: 2 من B (ما اشتري منها 5 − 3 المرتجع سابقًا) و1 من A
        var bLine = Assert.Single(return2.InvoiceItems, ii => ii.BatchId == batchBId);
        var aLine = Assert.Single(return2.InvoiceItems, ii => ii.BatchId == batchAId);
        Assert.Equal(2, bLine.Quantity);
        Assert.Equal(15.00m, bLine.ItemCost);
        Assert.Equal(1, aLine.Quantity);
        Assert.Equal(10.00m, aLine.ItemCost);

        // المخزون نقص من الدفعات الصحيحة: B خمستها رجعت للمورد (5−3−2) وA باقية 4
        Assert.Equal(0, (await returnDb.Batches.FirstAsync(b => b.BatchId == batchBId)).BatchQuantity);
        Assert.Equal(4, (await returnDb.Batches.FirstAsync(b => b.BatchId == batchAId)).BatchQuantity);

        // الاسترداد من المورد يطابق ما دُفع عن الكميات: (2×15 + 1×10)
        Assert.Equal(-40.00m, return2.NetAmount);

        // التراكمي على فاتورة الشراء = 6 لا يتجاوز المشترى 10
        int totalReturned = await returnDb.InvoiceItems
            .Where(ii => ii.Invoice.OriginalInvoiceId == purchaseResult.Data
                      && ii.Invoice.Type == Common.Enums.enInvoicesType.supplier_return)
            .SumAsync(ii => (int?)ii.Quantity) ?? 0;
        Assert.Equal(6, totalReturned);
    }
}
