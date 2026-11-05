using Common.DTOs.RequestModels;
using Common.Utils;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// VULN-45 — حدود يوم البرنامج (UTC+3):
/// 1) تاريخ دفع الاستردادات = يوم البرنامج لا التاريخ UTC الخام — استرداد
///    بعد منتصف الليل المحلي (00:00–03:00) كان يُختَم بتاريخ الأمس فيختفي
///    من تقرير Z لليوم الفعلي (clsReportService يصفّي على PaymentDate).
/// 2) FEFO: دفعة انتهت صلاحيتها «أمس بتوقيت البرنامج» محجوبة عن البيع حتى
///    في نافذة الفجر التي لا يزال فيها التاريخ UTC الخام للأمس.
/// </summary>
[Collection("database")]
public class ProgramDayBoundaryTests
{
    private readonly DatabaseFixture _fx;
    public ProgramDayBoundaryTests(DatabaseFixture fx) => _fx = fx;

    // ───────── حدود اليوم البرمجي — حساب خالص بلا ساعة حية ─────────

    [Fact]
    public void ProgramDay_UtcBetweenNinePmAndMidnight_IsNextCalendarDay()
    {
        // 22:30 UTC = 01:30 بتوقيت البرنامج من اليوم التالي — النافذة الحرجة
        // التي كان فيها DateOnly.FromDateTime(UtcNow) يعطي تاريخ الأمس (الخلل المُصلَح).
        var utc = new DateTime(2026, 11, 4, 22, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 11, 5), ProgramClock.FromUtc(utc));
        // إثبات سلوك الكود القديم: التاريخ الخام يتأخر يوماً في هذه النافذة
        Assert.Equal(new DateOnly(2026, 11, 4), DateOnly.FromDateTime(utc));
    }

    [Fact]
    public void ProgramDay_UtcBeforeNinePm_IsSameCalendarDay()
    {
        var utc = new DateTime(2026, 11, 4, 20, 59, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 11, 4), ProgramClock.FromUtc(utc));
    }

    // ───────── استرداد العميل: PaymentDate = يوم البرنامج ─────────

    [Fact]
    public async Task CustomerRefund_PaymentDate_IsProgramDay()
    {
        string marker = $"pd1-{Guid.NewGuid():N}"[..12];
        var (saleInvoiceId, userId) = await SeedSaleAsync(marker);

        await using var db = _fx.CreateContext();
        int returnProductId = await db.InvoiceItems.AsNoTracking()
            .Where(ii => ii.InvoiceId == saleInvoiceId)
            .Select(ii => ii.ProductId)
            .FirstAsync();

        var result = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleInvoiceId,
            UserId = userId,
            Items = { new DtoReturnItem { ProductId = returnProductId, Quantity = 1 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        // قيد الاسترداد النقدي (outcome على العميل) يجب أن يحمل يوم البرنامج
        await using var verify = _fx.CreateContext();
        var refundPayments = await verify.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == result.Data)
            .ToListAsync();

        Assert.NotEmpty(refundPayments);
        // يوم البرنامج عند القراءة — التحقق المباشر للسياسة
        DateOnly programToday = ProgramClock.Today;
        foreach (var p in refundPayments)
        {
            Assert.Equal(programToday, p.PaymentDate);
            // حماية إضافية: لا يُقبل تاريخ أقدم من تاريخ UTC الخام —
            // الخلل القديم (التاريخ الخام) كان يُنتج هذا في نافذة الفجر
            Assert.True(p.PaymentDate >= DateOnly.FromDateTime(DateTime.UtcNow),
                "PaymentDate أقدم من تاريخ UTC الخام — استرداد اختفى من يومه البرمجي");
        }
    }

    // ───────── استرداد المورد: PaymentDate = يوم البرنامج ─────────

    [Fact]
    public async Task SupplierRefund_PaymentDate_IsProgramDay()
    {
        string marker = $"pd2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly farExpiry = ProgramClock.Today.AddDays(90);
        var purchaseResult = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            // مدفوعة كاشًا بالكامل — كي يمر استرداد المرتجع نقدًا (قيد Payment) لا خفض رصيد فقط
            Payment = new DtoPaymentInfo { Amount = 50m, Method = "cash" },
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "PD-SUP", Quantity = 5, UnitCost = 10m, ExpiryDate = farExpiry } }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        // سياق جديد للمرتجع — كما في الإنتاج وكمية الاختبارات القائمة
        await using var returnDb = _fx.CreateContext();
        var result = await new clsReturnService(returnDb).CreateSupplierReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = purchaseResult.Data,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        await using var verify = _fx.CreateContext();
        var refundPayments = await verify.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == result.Data)
            .ToListAsync();

        Assert.NotEmpty(refundPayments);
        foreach (var p in refundPayments)
            Assert.Equal(ProgramClock.Today, p.PaymentDate);
    }

    // ───────── FEFO: حدود الصلاحية بيوم البرنامج ─────────

    [Fact]
    public async Task Fefo_BatchExpiredProgramDayYesterday_IsBlockedFromSale()
    {
        string marker = $"pd3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // دفعة انتهت صلاحيتها أمس بتوقيت البرنامج — قد لا يزال تاريخ UTC الخام
        // أمس (نافذة 00:00–03:00 فجرًا) والكود القديم كان يبيعها 3 ساعات إضافية.
        DateOnly expiredYesterday = ProgramClock.Today.AddDays(-1);
        Assert.True((await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = "PD-EXP",
            Quantity = 10,
            ExpiryDate = expiredYesterday
        }, seed.UserId)).IsSuccess);

        await using var sellCtx = _fx.CreateContext();
        var sell = await new clsStockService(sellCtx).SellStockAsync(seed.ProductId, 1);

        Assert.False(sell.IsSuccess, "دفعة منتهية الصلاحية أمس (توقيت البرنامج) بِيعت رغم انتهائها.");
        Assert.Contains("غير كافٍ", sell.Message);
    }

    [Fact]
    public async Task Fefo_BatchExpiringProgramDayToday_IsStillSellable()
    {
        string marker = $"pd4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // دفعة تنتهي اليوم بتوقيت البرنامج — قابلة للبيع في يوم صلاحيتها الأخير كاملًا
        DateOnly expiresToday = ProgramClock.Today;
        Assert.True((await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = "PD-TODAY",
            Quantity = 10,
            ExpiryDate = expiresToday
        }, seed.UserId)).IsSuccess);

        await using var sellCtx = _fx.CreateContext();
        var sell = await new clsStockService(sellCtx).SellStockAsync(seed.ProductId, 1);

        Assert.True(sell.IsSuccess, sell.Message);

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == "PD-TODAY");
        Assert.Equal(9, batch.BatchQuantity);
    }

    // ───────── بذر مشترك ─────────

    /// <summary>شراء + بيع نقدي لوحدة واحدة — يُعيد (فاتورة البيع، المستخدم).</summary>
    private async Task<(int SaleInvoiceId, int UserId)> SeedSaleAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        Assert.True((await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "PD-SALE", Quantity = 10, UnitCost = 10m, ExpiryDate = ProgramClock.Today.AddDays(60) } }
        })).IsSuccess);

        await using var saleDb = _fx.CreateContext();
        var checkout = new clsCheckoutService(saleDb);
        var sale = new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Payment = new DtoPaymentInfo { Amount = 20m, Method = "cash" }
        };
        sale.Items.Add(new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1 });

        var saleResult = await checkout.ProcessCheckoutAsync(sale);
        Assert.True(saleResult.IsSuccess, saleResult.Message);
        return (saleResult.Data, seed.UserId);
    }
}
