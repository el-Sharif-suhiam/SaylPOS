using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// التحقق من دفع الشراء على الصافي النهائي (بعد الخصم والضريبة) — لا على
/// الإجمالي الخام: قياس الدفع على الإجمالي يرفض دفعة صحيحة عند وجود ضريبة
/// (دين وهمي) ويقبل دفعة زائدة عند وجود خصم (رصيد مورد فاسد).
/// </summary>
[Collection("database")]
public class PurchasePaymentValidationTests
{
    private readonly DatabaseFixture _fx;
    public PurchasePaymentValidationTests(DatabaseFixture fx) => _fx = fx;

    private static DtoPurchaseRequest Request(TestSeed seed, decimal total,
        decimal discount = 0m, decimal taxPercent = 0m, DtoPaymentInfo? payment = null)
    {
        var request = new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            DiscountAmount = discount,
            TaxPercent = taxPercent,
            Payment = payment,
            Items =
            {
                // بند واحد قيمته الإجمالية المطلوبة (UnitCost×Quantity = total)
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = 10, UnitCost = total / 10 }
            }
        };
        return request;
    }

    private static DtoPaymentInfo Pay(decimal amount) =>
        new() { Amount = amount, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) };

    // ── ضريبة: الدفعة الصحيحة (الإجمالي + الضريبة) تُقبل ──
    [Fact]
    public async Task Purchase_PaymentWithTax_ExactNet_IsAccepted()
    {
        // 100 خام + 20% ضريبة = 120 صافي — الدفع 120 يطابق الصافي
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, taxPercent: 20m, payment: Pay(120m)));
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(100m, invoice.TotalAmount);
        Assert.Equal(20m, invoice.TaxAmount);
        Assert.Equal(120m, invoice.NetAmount);
        Assert.Equal(enPaymentStatus.paid, invoice.PaymentStatus);

        // رصيد المورد لا يتضح بدين وهمي: الصافي مغطى بالكامل
        var balance = await db.Suppliers.AsNoTracking()
            .Where(s => s.PartyId == seed.SupplierId).Select(s => s.Balance).FirstAsync();
        Assert.Equal(0m, balance);
    }

    // ── ضريبة: الدفع الزائد عن الصافي يُرفض ──
    [Fact]
    public async Task Purchase_PaymentWithTax_AboveNet_IsRejected()
    {
        // 100 خام + 20% ضريبة = 120 صافي — الدفع 121 زائد عن الصافي
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, taxPercent: 20m, payment: Pay(121m)));
        Assert.False(result.IsSuccess);
        Assert.Contains("دفع", result.Message);

        // لا فاتورة ولا دفعة تُسجَّل للرفض
        Assert.False(await db.Invoices.AsNoTracking().AnyAsync(i => i.PartyId == seed.SupplierId));
        Assert.False(await db.Payments.AsNoTracking().AnyAsync(p => p.PartyId == seed.SupplierId));
    }

    // ── خصم: الدفعة الصحيحة (الإجمالي − الخصم) تُقبل ──
    [Fact]
    public async Task Purchase_PaymentWithDiscount_ExactNet_IsAccepted()
    {
        // 100 خام − 20 خصم = 80 صافي — الدفع 80 يطابق الصافي
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, discount: 20m, payment: Pay(80m)));
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(100m, invoice.TotalAmount);
        Assert.Equal(20m, invoice.DiscountAmount);
        Assert.Equal(80m, invoice.NetAmount);
        Assert.Equal(enPaymentStatus.paid, invoice.PaymentStatus);

        // لا دفع زائد يُسرق رصيد المورد: 80 مدفوعة من صافي 80
        var balance = await db.Suppliers.AsNoTracking()
            .Where(s => s.PartyId == seed.SupplierId).Select(s => s.Balance).FirstAsync();
        Assert.Equal(0m, balance);
    }

    // ── خصم: الدفع الزائد عن الصافي يُرفض ──
    [Fact]
    public async Task Purchase_PaymentWithDiscount_AboveNet_IsRejected()
    {
        // 100 خام − 20 خصم = 80 صافي — الدفع 90 زائد عن الصافي (كان يُقبل خطأً على الإجمالي 100)
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, discount: 20m, payment: Pay(90m)));
        Assert.False(result.IsSuccess);
        Assert.Contains("دفع", result.Message);
        Assert.False(await db.Invoices.AsNoTracking().AnyAsync(i => i.PartyId == seed.SupplierId));
    }

    // ── دفع صفر: تُقبل كغير مدفوعة ──
    [Fact]
    public async Task Purchase_PaymentZero_IsAcceptedAsUnpaid()
    {
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, taxPercent: 20m, payment: Pay(0m)));
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(120m, invoice.NetAmount);
        Assert.Equal(enPaymentStatus.unpaid, invoice.PaymentStatus);

        // كامل الصافي على رصيد المورد (دين حقيقي 120)
        var balance = await db.Suppliers.AsNoTracking()
            .Where(s => s.PartyId == seed.SupplierId).Select(s => s.Balance).FirstAsync();
        Assert.Equal(120m, balance);
    }

    // ── دفع سالب: يُرفض ──
    [Fact]
    public async Task Purchase_PaymentNegative_IsRejected()
    {
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, payment: Pay(-5m)));
        Assert.False(result.IsSuccess);
        Assert.Contains("دفع", result.Message);
        Assert.False(await db.Invoices.AsNoTracking().AnyAsync(i => i.PartyId == seed.SupplierId));
    }

    // ── صافي صفر مع دفع صفر: تُقبل ──
    [Fact]
    public async Task Purchase_NetZero_PaymentZero_IsAccepted()
    {
        // 100 خام − 100 خصم = 0 صافي — الدفع 0 يطابق
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, discount: 100m, payment: Pay(0m)));
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(0m, invoice.NetAmount);

        // رصيد المورد صفر — لا دين ولا دفع زائد
        var balance = await db.Suppliers.AsNoTracking()
            .Where(s => s.PartyId == seed.SupplierId).Select(s => s.Balance).FirstAsync();
        Assert.Equal(0m, balance);
    }

    // ── صافي صفر مع دفع موجب: يُرفض (لا دفع زائد) ──
    [Fact]
    public async Task Purchase_NetZero_PaymentPositive_IsRejected()
    {
        string marker = $"pv-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        var result = await service.CreatePurchaseInvoiceAsync(Request(seed, 100m, discount: 100m, payment: Pay(1m)));
        Assert.False(result.IsSuccess);
        Assert.Contains("دفع", result.Message);
    }
}
