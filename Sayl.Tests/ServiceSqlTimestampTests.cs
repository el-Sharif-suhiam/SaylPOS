using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Task 2 — أطوابع الخدمات من ساعة SQL الموثقة:
///  (a) الطابع المحفوظ يأتي من ساعة SQL Server (SYSUTCDATETIME) عبر
///      clsStoreClockService لا من ساعة العميل — يقع داخل نافذة قراءتي
///      ساعة SQL المحيطتين بالعملية.
///  (b) السجلات المرتبطة بعملية واحدة تشترك في اللقطة نفسها — فاتورة البيع
///      ودفعتها وحركة مخزونها: CreatedAt متطابقة تمامًا (لقطة واحدة للعملية).
///  (c) قرار اليوم التجاري يُقاد بتوقيت المتجر المحقون: بوابة أهلية المرتجع
///      تحوّل الطابع التاريخي بتوقيت المتجر (Test/FixedPlus3) لا بالتاريخ
///      UTC الخام — لقطتان تاريخيتان ثابتتان تحيطان بمنتصف ليل المتجر
///      فتُحسمان باتجاهين متعاكسين بلا أي اعتماد على وقت تشغيل الاختبار.
/// قاعدة مشتركة [Collection("database")].
/// </summary>
[Collection("database")]
public class ServiceSqlTimestampTests
{
    private readonly DatabaseFixture _fx;
    public ServiceSqlTimestampTests(DatabaseFixture fx) => _fx = fx;

    private static string Marker(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..12];

    /// <summary>بيع نقدي مباشر عبر الخدمة (بند واحد بسعر الأساس 20 مدفوع كاشًا)
    /// — نفس تدفق الاختبارات القائمة — يعيد معرّف الفاتورة.</summary>
    private async Task<int> SeedCashSaleAsync(int userId, int productId)
    {
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

    // ══ (a) الطابع المحفوظ يأتي من ساعة SQL Server لا من ساعة العميل ══
    [Fact]
    public async Task Checkout_InvoiceCreatedAt_FallsInSqlClockWindow()
    {
        string marker = Marker("sqlts1");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var stock = new clsStockService(db);
        Assert.True((await stock.ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = "SQLTS-B1",
            Quantity = 5,
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60),
            UnitCost = 10m
        }, seed.UserId)).IsSuccess);

        var clock = new clsStoreClockService(db, TestClockConfig.Tz);
        DateTime before = await clock.GetUtcNowAsync(db);

        int saleId = await SeedCashSaleAsync(seed.UserId, seed.ProductId);

        DateTime after = await clock.GetUtcNowAsync(db);

        var invoice = await db.Invoices.AsNoTracking()
            .FirstAsync(i => i.InvoiceId == saleId);
        Assert.NotNull(invoice.CreatedAt);
        Assert.InRange(invoice.CreatedAt!.Value, before, after);
    }

    // ══ (b) سجلات العملية الواحدة تشترك في اللقطة نفسها ══
    // فاتورة البيع ودفعتها النقدية وحركة مخزونها تُكتب بلقطة SQL واحدة
    // التُقطت مرة عند بدء العملية — CreatedAt متطابقة تمامًا لا متقاربة.
    [Fact]
    public async Task Checkout_RelatedRecords_ShareOneCapturedInstant()
    {
        string marker = Marker("sqlts2");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var stock = new clsStockService(db);
        Assert.True((await stock.ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = "SQLTS-B2",
            Quantity = 5,
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60),
            UnitCost = 10m
        }, seed.UserId)).IsSuccess);

        int saleId = await SeedCashSaleAsync(seed.UserId, seed.ProductId);

        var invoice = await db.Invoices.AsNoTracking()
            .FirstAsync(i => i.InvoiceId == saleId);
        var payment = await db.Payments.AsNoTracking()
            .FirstAsync(p => p.InvoiceId == saleId);
        var movement = await db.StockMovements.AsNoTracking()
            .FirstAsync(m => m.SourceType == "Invoice" && m.SourceId == saleId);

        Assert.NotNull(invoice.CreatedAt);
        Assert.NotNull(payment.CreatedAt);
        Assert.Equal(invoice.CreatedAt, payment.CreatedAt);
        Assert.Equal(invoice.CreatedAt, movement.CreatedAt);
    }

    // ══ (c) قرار اليوم التجاري بتوقيت المتجر المحقون ══
    // بداية السنة المالية = 2024-01-01. فاتورتان تاريخيتان ثابتتان حول منتصف
    // ليل المتجر (FixedPlus3): 22:00Z = 01:00 يوم 2024-01-01 بالمتجر → داخل
    // السنة → المرتجع مسموح. 20:00Z = 23:00 يوم 2023-12-31 بالمتجر → قبل
    // السنة → مرفوض. لو فُسِّر الطابع بالتاريخ UTC الخام لانقلب الحكمين.
    [Fact]
    public async Task ReturnEligibility_Gate_DrivenByInjectedStoreTimeZone()
    {
        string marker = Marker("sqlts3");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // بداية السنة المالية المطلوبة مع حفظ القيمة الأصلية لاستعادتها
        var fiscalRow = await db.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
        string? previousValue = fiscalRow?.SettingValue;
        bool rowExisted = fiscalRow != null;
        if (fiscalRow == null)
        {
            db.Settings.Add(new Setting
            {
                SettingKey = clsFiscalYearService.KeyFiscalYearStart,
                SettingValue = "2024-01-01",
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            fiscalRow.SettingValue = "2024-01-01";
        }
        await db.SaveChangesAsync();
        try
        {
            // فاتورتا بيع مكتملتان بطابع تاريخي ثابت (UTC) وبندان بلا دفعة مصدر
            Invoice MakeSale(DateTime createdAtUtc)
            {
                var invoice = new Invoice
                {
                    Type = enInvoicesType.sale,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.unpaid,
                    PartyId = seed.CustomerId,
                    PartyType = 'C',
                    UserId = seed.UserId,
                    TotalAmount = 20m,
                    DiscountAmount = 0m,
                    TaxAmount = 0m,
                    NetAmount = 20m,
                    InvoiceNumber = TestSeed.NewInvoiceNumber(),
                    CreatedAt = createdAtUtc
                };
                db.Invoices.Add(invoice);
                db.InvoiceItems.Add(new InvoiceItem
                {
                    Invoice = invoice,
                    ProductId = seed.ProductId,
                    Quantity = 2,
                    ItemPrice = 10m,
                    ItemCost = 10m
                });
                return invoice;
            }

            var allowedInvoice = MakeSale(new DateTime(2023, 12, 31, 22, 0, 0, DateTimeKind.Utc));
            var rejectedInvoice = MakeSale(new DateTime(2023, 12, 31, 20, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // ساعة محقونة +3 تُمرَّر صراحةً للخدمة (لا الافتراضي المبذور)
            var clock = new clsStoreClockService(db, TestClockConfig.Tz);
            var returns = new clsReturnService(db, clock);

            // 22:00Z → 2024-01-01 01:00 بالمتجر → داخل السنة → مسموح ويكتمل
            var allowed = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = allowedInvoice.InvoiceId,
                UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
            });
            Assert.True(allowed.IsSuccess, allowed.Message);

            // 20:00Z → 2023-12-31 23:00 بالمتجر → قبل السنة → مرفوض برسالة السنة المغلقة
            var rejected = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = rejectedInvoice.InvoiceId,
                UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
            });
            Assert.False(rejected.IsSuccess);
            Assert.Contains("مغلقة", rejected.Message);
        }
        finally
        {
            // استعادة صف بداية السنة كما كان (قيمة أصلية أو حذف الصف) —
            // بإعادة القراءة لأن المتتبع وُضِّح أثناء العملية
            var row = await db.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            if (rowExisted)
            {
                row!.SettingValue = previousValue;
                await db.SaveChangesAsync();
            }
            else if (row != null)
            {
                db.Settings.Remove(row);
                await db.SaveChangesAsync();
            }
        }
    }
}
