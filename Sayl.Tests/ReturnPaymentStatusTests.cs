using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// VULN-19: حالة رأس فاتورة المرتجع تُشتق من النقد المُردَّد فعليًا (refundPlan.Payments)
/// لا من إجمالي الاسترداد — كامل نقد = paid، جزء نقد وجزء رصيد = partial،
/// رصيد بالكامل = unpaid (لا يُحتسب BalanceRefund دفعًا نقديًا).
/// يغطي مسارَي مرتجع العميل ومرتجع المورد.
/// </summary>
[Collection("database")]
public class ReturnPaymentStatusTests
{
    private readonly DatabaseFixture _fx;
    public ReturnPaymentStatusTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    // ───────── بذور (TestSeed مرة واحدة لكل اختبار — قيد UQ_Users_Username) ─────────

    /// <summary>بذرة واحدة: مستخدم/منتج/عميل/مورد لسياق مستقل.</summary>
    private async Task<(int UserId, int ProductId, int CustomerId, int SupplierId)> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var s = await TestSeed.SeedAsync(db, marker);
        return (s.UserId, s.ProductId, s.CustomerId, s.SupplierId);
    }

    /// <summary>بيع نقدي كامل (تدفق حقيقي: كاشير): بند واحد @20 مدفوع كاشًا —
    /// دفعة مرتبطة تغطي الصافي كاملًا، فلا دين متبقٍ.</summary>
    private async Task<int> SeedCashSaleAsync(int userId, int supplierId, int productId)
    {
        await using var db = _fx.CreateContext();
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = "RS-A", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        })).IsSuccess);

        await using var saleDb = _fx.CreateContext();
        var checkout = new clsCheckoutService(saleDb);
        var sale = new DtoCheckoutRequest
        {
            UserId = userId,
            Payment = new DtoPaymentInfo { Amount = 20m, Method = "cash" }
        };
        sale.Items.Add(new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = 1 });
        var saleResult = await checkout.ProcessCheckoutAsync(sale);
        Assert.True(saleResult.IsSuccess, saleResult.Message);
        return saleResult.Data;
    }

    /// <summary>بيع آجل مبذور مباشرة: بند @unitPrice، صافي = unitPrice × qty،
    /// رصيد العميل يرتفع بالصافي (كما يفعل الكاشير للآجل). يشترط دفعة شراء حقيقية
    /// أولًا ليرتبط البند بدفعة صالحة (مسار المرتجع يمر عبر الدفعات).</summary>
    private async Task<int> SeedCreditSaleAsync(int userId, int supplierId, int productId, int customerId,
        decimal unitPrice = 10m, int qty = 2)
    {
        await using var db = _fx.CreateContext();
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = "RS-C", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        })).IsSuccess);

        int batchId = (await db.Batches.AsNoTracking()
            .FirstAsync(b => b.ProductId == productId && b.BatchNumber == "RS-C")).BatchId;

        decimal net = unitPrice * qty;
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = customerId,
            PartyType = 'C',
            UserId = userId,
            TotalAmount = net,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = net,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        db.InvoiceItems.Add(new InvoiceItem
        {
            Invoice = invoice,
            ProductId = productId,
            Quantity = qty,
            BatchId = batchId,
            ItemPrice = unitPrice,
            ItemCost = 10m
        });
        await db.SaveChangesAsync();

        // رصيد العميل يرتفع بالصافي — كما يفعل الكاشير للبيع الآجل
        await db.Customers.Where(c => c.PartyId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, net));
        return invoice.InvoiceId;
    }

    private static DtoReturnRequest ReturnRequest(int invoiceId, int userId, int productId, int qty) => new()
    {
        OriginalInvoiceId = invoiceId,
        UserId = userId,
        Items = { new DtoReturnItem { ProductId = productId, Quantity = qty } }
    };

    private async Task<enPaymentStatus> ReturnInvoiceStatusAsync(int returnInvoiceId) =>
        (await _fx.CreateContext().Invoices.AsNoTracking().FirstAsync(i => i.InvoiceId == returnInvoiceId)).PaymentStatus;

    // ───────── مرتجع العميل ─────────

    [Fact]
    public async Task CustomerReturn_FullyCashRefund_InvoicePaid()
    {
        string marker = $"rps1-{Guid.NewGuid():N}"[..12];
        var (userId, productId, _, supplierId) = await SeedAsync(marker);
        int saleId = await SeedCashSaleAsync(userId, supplierId, productId);

        await using var db = _fx.CreateContext();
        var result = await new clsReturnService(db)
            .CreateCustomerReturnAsync(ReturnRequest(saleId, userId, productId, 1));
        Assert.True(result.IsSuccess, result.Message);

        // الصافي مدفوع كاشًا بالكامل → الاسترداد كله نقد → paid
        Assert.Equal(enPaymentStatus.paid, await ReturnInvoiceStatusAsync(result.Data));
    }

    [Fact]
    public async Task CustomerReturn_FullyBalanceRefund_InvoiceUnpaid()
    {
        string marker = $"rps2-{Guid.NewGuid():N}"[..12];
        var (userId, productId, customerId, supplierId) = await SeedAsync(marker);
        int saleId = await SeedCreditSaleAsync(userId, supplierId, productId, customerId); // آجل — دين 20

        await using var db = _fx.CreateContext();
        var result = await new clsReturnService(db)
            .CreateCustomerReturnAsync(ReturnRequest(saleId, userId, productId, 1)); // استرداد 10
        Assert.True(result.IsSuccess, result.Message);

        // الدين يغطي الاسترداد كاملًا → BalanceRefund فقط، لا نقد → unpaid
        Assert.Equal(enPaymentStatus.unpaid, await ReturnInvoiceStatusAsync(result.Data));

        // الرصيد خُفِض بالاسترداد فعلًا (20 → 10)
        decimal balance = await db.Customers.AsNoTracking()
            .Where(c => c.PartyId == customerId)
            .Select(c => c.Balance ?? 0m).SingleAsync();
        Assert.Equal(10m, balance);
    }

    [Fact]
    public async Task CustomerReturn_MixedCashAndBalance_InvoicePartial()
    {
        string marker = $"rps3-{Guid.NewGuid():N}"[..12];
        var (userId, productId, customerId, supplierId) = await SeedAsync(marker);
        int saleId = await SeedCreditSaleAsync(userId, supplierId, productId, customerId); // آجل — صافي 20

        await using (var db = _fx.CreateContext())
        {
            // سداد لاحق 10 نقدًا — يتبقى دين 10
            var pay = await new clsPaymentService(db).PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = saleId,
                PartyId = customerId,
                Amount = 10m,
                PaymentDate = Today,
                Method = "cash",
                UserId = userId
            });
            Assert.True(pay.IsSuccess, pay.Message);
        }

        await using var retDb = _fx.CreateContext();
        // مرتجع الوحدتين = 20: دين 10 (رصيد) + 10 نقد → partial
        var result = await new clsReturnService(retDb)
            .CreateCustomerReturnAsync(ReturnRequest(saleId, userId, productId, 2));
        Assert.True(result.IsSuccess, result.Message);

        Assert.Equal(enPaymentStatus.partial, await ReturnInvoiceStatusAsync(result.Data));
    }

    // ───────── مرتجع المورد ─────────

    [Fact]
    public async Task SupplierReturn_FullyCashRefund_InvoicePaid()
    {
        string marker = $"rps4-{Guid.NewGuid():N}"[..12];
        var (userId, productId, _, supplierId) = await SeedAsync(marker);

        await using var db = _fx.CreateContext();
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        // مدفوعة كاشًا بالكامل — استرداد المرتجع يمر نقدًا
        var purchaseResult = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Payment = new DtoPaymentInfo { Amount = 50m, Method = "cash" },
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = "RS-P1", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        await using var retDb = _fx.CreateContext();
        var result = await new clsReturnService(retDb)
            .CreateSupplierReturnAsync(ReturnRequest(purchaseResult.Data, userId, productId, 2));
        Assert.True(result.IsSuccess, result.Message);

        // الشراء مدفوع كاملًا → الاسترداد كله نقد → paid
        Assert.Equal(enPaymentStatus.paid, await ReturnInvoiceStatusAsync(result.Data));
    }

    [Fact]
    public async Task SupplierReturn_FullyBalanceRefund_InvoiceUnpaid()
    {
        string marker = $"rps5-{Guid.NewGuid():N}"[..12];
        var (userId, productId, _, supplierId) = await SeedAsync(marker);

        await using var db = _fx.CreateContext();
        var purchase = new clsPurchaseService(db);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        // آجلة بالكامل (بلا سداد) — دين المورد 50
        var purchaseResult = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = "RS-P2", Quantity = 5, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        await using var retDb = _fx.CreateContext();
        var result = await new clsReturnService(retDb)
            .CreateSupplierReturnAsync(ReturnRequest(purchaseResult.Data, userId, productId, 2)); // استرداد 20
        Assert.True(result.IsSuccess, result.Message);

        // الدين يغطي الاسترداد → BalanceRefund فقط → unpaid
        Assert.Equal(enPaymentStatus.unpaid, await ReturnInvoiceStatusAsync(result.Data));
    }
}
