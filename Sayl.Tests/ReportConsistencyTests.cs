using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اتساق تصنيف الدفعات عبر التقارير: تحصيلات العملاء (PartyType='C') لا تشمل
/// استردادات الموردين (دخل بطرف 'S') — دفعات الطرف المشترك ('B') تحمل جهة كل
/// حركة — والجلسة تُنسب لها دفعاتها بـ SessionId ولو بلا فاتورة أو بفاتورة أقدم
/// بلا احتساب مزدوج — وحدود اليوم البرمجي (UTC+3) موحّدة بين كل التقارير.
/// (سياق جديد لكل خطوة — كما في ReportCalculationTests.)
/// </summary>
[Collection("database")]
public class ReportConsistencyTests
{
    private readonly DatabaseFixture _fx;
    public ReportConsistencyTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>يوم فريد لكل اختبار — قاعدة مشتركة بين الاختبارات فالتواريخ لا تتقاطع.</summary>
    private static DateOnly D(int juneDay) => new(2026, 6, juneDay);

    private static DateTime LocalDayUtc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).AddHours(-3);

    /// <summary>نقل CreatedAt لأي لحظة داخل اليوم البرنامجي (كسور ساعة مسموحة).</summary>
    private static async Task SetInvoiceCreatedAsync(SaylDbContext db, int invoiceId, DateTime utc)
        => await db.Invoices.Where(i => i.InvoiceId == invoiceId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.CreatedAt, utc));

    private static async Task MovePaymentDayAsync(SaylDbContext db, int invoiceId, DateOnly day)
        => await db.Payments.Where(p => p.InvoiceId == invoiceId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.PaymentDate, day));

    /// <summary>شراء مخزون (سداد اختياري) — يعيد معرف فاتورة الشراء.</summary>
    private static async Task<int> PurchaseAsync(SaylDbContext db, int supplierId, int userId,
        int productId, int qty, decimal unitCost, decimal paid = 0m, DateOnly? paymentDate = null)
    {
        var request = new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = qty, UnitCost = unitCost } }
        };
        if (paid > 0m)
            request.Payment = new DtoPaymentInfo { Amount = paid, Method = "cash", PaymentDate = paymentDate!.Value };

        var result = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(request);
        Assert.True(result.IsSuccess, result.Message);
        return result.Data;
    }

    /// <summary>بيع بسيط — يعيد معرف فاتورة البيع.</summary>
    private static async Task<int> SaleAsync(SaylDbContext db, int userId, int? customerId,
        (int ProductId, int Qty, decimal UnitPrice)[] items, decimal paid = 0m, DateOnly? paymentDate = null, int? sessionId = null)
    {
        var request = new DtoCheckoutRequest
        {
            CustomerId = customerId,
            UserId = userId,
            SessionId = sessionId,
            Payment = new DtoPaymentInfo { Amount = paid, Method = "cash", PaymentDate = paymentDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)) }
        };
        foreach (var (pid, qty, price) in items)
            request.Items.Add(new DtoCheckoutItem { ProductId = pid, ProductName = "x", Quantity = qty, UnitPrice = price });

        var result = await new clsCheckoutService(db).ProcessCheckoutAsync(request);
        Assert.True(result.IsSuccess, result.Message);
        return result.Data;
    }

    // ══ 1) تحصيلات العملاء لا تشمل استرداد المورد — عبر اليومي والدوري واللوحة والتدفق ══
    // شراء مدفوع كاش 100 (مخرج 'S')، بيع آجل 100 (دين عميل)، تحصيل من العميل 30 (دخل
    // 'C' بلا فاتورة)، مرتجع مورد مسترد نقدًا 20 (دخل 'S').
    // الصحيح: تحصيلات العملاء = 30 في كل التقارير — الكود القديم كان يضيف استرداد المورد (50).
    [Fact]
    public async Task CustomerCollections_ExcludeSupplierRefunds_AcrossAllReports()
    {
        string marker = $"rc1-{Guid.NewGuid():N}"[..12];
        DateOnly day = D(1); // يوم فريد لهذا الاختبار

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int purchaseId = await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m, paid: 100m, paymentDate: day);

        await using var saleDb = _fx.CreateContext();
        int saleId = await SaleAsync(saleDb, seed.UserId, seed.CustomerId, new[] { (seed.ProductId, 4, 25m) }, paid: 0m); // بيع آجل → دين عميل
        await SetInvoiceCreatedAsync(saleDb, saleId, LocalDayUtc(day).AddHours(12));

        await using var payDb = _fx.CreateContext();
        var pay = await new clsPaymentService(payDb).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
        {
            PartyId = seed.CustomerId,
            Amount = 30m,
            PaymentDate = day,
            Method = "cash",
            UserId = seed.UserId
        });
        Assert.True(pay.IsSuccess, pay.Message);

        await using var retDb = _fx.CreateContext();
        var ret = await new clsReturnService(retDb).CreateSupplierReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = purchaseId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
        });
        Assert.True(ret.IsSuccess, ret.Message);
        await MovePaymentDayAsync(retDb, ret.Data, day); // تاريخ الدفعة الافتراضي = اليوم الفعلي

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var daily = await reports.GetDailySalesAsync(day);
        Assert.True(daily.IsSuccess, daily.Message);
        Assert.Equal(30m, daily.Data!.TotalCollected); // التحصيل العام 30 فقط (لا 50 مع استرداد المورد)

        var dashboard = await reports.GetDashboardSummaryAsync(day);
        Assert.True(dashboard.IsSuccess, dashboard.Message);
        Assert.Equal(30m, dashboard.Data!.TodayCollected);

        var periodic = await reports.GetPeriodicReportAsync(day, day, enPeriodReportType.Quarterly);
        Assert.True(periodic.IsSuccess, periodic.Message);
        Assert.Equal(30m, periodic.Data!.TotalCollected);
        Assert.Equal(daily.Data.TotalCollected, periodic.Data.TotalCollected); // اتساق اليومي/الدوري

        var cashFlow = await reports.GetCashFlowReportAsync(day, day);
        Assert.True(cashFlow.IsSuccess, cashFlow.Message);
        Assert.Equal(30m, cashFlow.Data!.CashSales);             // الدخل النقدي من العملاء
        Assert.Equal(0m, cashFlow.Data.CustomerPayments);        // لا دخل غير نقدي
        Assert.Equal(100m, cashFlow.Data.SupplierPayments);      // سداد الشراء النقدي
        Assert.Equal(20m, cashFlow.Data.SupplierRefunds);        // استرداد المورد مستقل
        // اتساق: تحصيلات اليوم = الدخل من العملاء (نقدي + غير نقدي) في التدفق النقدي
        Assert.Equal(daily.Data.TotalCollected, cashFlow.Data.CashSales + cashFlow.Data.CustomerPayments);
        Assert.Equal(-50m, cashFlow.Data.NetMovement); // 30 + 20 − 100
    }

    // ══ 2) الطرف المشترك ('B'): كل حركة تُصنَّف بجهتها في سجل الدفعة نفسه ══
    // طرف مشترك: بيع له نقدًا 60 ('C' دخل) وشراء منه نقدًا 40 ('S' مخرج)
    // ومرتجع مورد مسترد نقدًا 8 ('S' دخل).
    // الصحيح: تحصيلات العملاء = 60 — الكود القديم كان يضيف استرداد المورد (68).
    [Fact]
    public async Task CombinedParty_PaymentsClassifiedBySide()
    {
        string marker = $"rc2-{Guid.NewGuid():N}"[..12];
        DateOnly day = D(2); // يوم فريد لهذا الاختبار

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var party = await new clsPartyService(db).AddNewPartyWithRoleAsync(
            new DtoParty { PartyType = 'B', Name = $"مشترك-{marker}" }, actorUserId: seed.UserId);
        Assert.True(party.IsSuccess, party.Message);
        int combinedId = party.Data;

        int purchaseId = await PurchaseAsync(db, combinedId, seed.UserId, seed.ProductId, 5, 8m, paid: 40m, paymentDate: day);

        await using var saleDb = _fx.CreateContext();
        int saleId = await SaleAsync(saleDb, seed.UserId, combinedId, new[] { (seed.ProductId, 3, 20m) }, paid: 60m, paymentDate: day);
        await SetInvoiceCreatedAsync(saleDb, saleId, LocalDayUtc(day).AddHours(12));

        await using var retDb = _fx.CreateContext();
        var ret = await new clsReturnService(retDb).CreateSupplierReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = purchaseId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
        });
        Assert.True(ret.IsSuccess, ret.Message);
        await MovePaymentDayAsync(retDb, ret.Data, day);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var daily = await reports.GetDailySalesAsync(day);
        Assert.True(daily.IsSuccess, daily.Message);
        Assert.Equal(60m, daily.Data!.TotalCollected); // تحصيل جهة العميل فقط

        var cashFlow = await reports.GetCashFlowReportAsync(day, day);
        Assert.True(cashFlow.IsSuccess, cashFlow.Message);
        Assert.Equal(60m, cashFlow.Data!.CashSales);
        Assert.Equal(40m, cashFlow.Data.SupplierPayments);
        Assert.Equal(8m, cashFlow.Data.SupplierRefunds); // جهة المورد للطرف المشترك
    }

    // ══ 3) جلسة الدرج: تحصيل بلا فاتورة + دَين قديم بجلسة حالية + سداد مورد — بلا ازدواج ══
    // جلسة قديمة: بيع آجل 100 ثم أُغلقت. جلسة حالية (افتتاحي 50):
    // تحصيل عام نقدًا 40 (بلا فاتورة)، سداد الفاتورة القديمة نقدًا 20 (جلسة حالية)،
    // سداد مورد نقدًا 25 ('S' مخرج).
    // الصحيح لسطر الجلسة الحالية: مبيعات نقدي 60، سداد مورد 25، المتوقع 85.
    [Fact]
    public async Task PeriodicSessionLines_AttributeSessionPayments_WithoutDuplication()
    {
        string marker = $"rc3-{Guid.NewGuid():N}"[..12];
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        int seedUserId, customerId, supplierId, productId, session1, session2, debtInvoiceId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            seedUserId = seed.UserId;
            customerId = seed.CustomerId;
            supplierId = seed.SupplierId;
            productId = seed.ProductId;
            await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m);
        }

        await using (var db = _fx.CreateContext())
        {
            var open1 = await new clsPosSessionService(db).OpenSessionAsync(seedUserId, 100m);
            Assert.True(open1.IsSuccess, open1.Message);
            session1 = open1.Data;
        }

        await using (var db = _fx.CreateContext())
            debtInvoiceId = await SaleAsync(db, seedUserId, customerId, new[] { (productId, 5, 20m) }, sessionId: session1);

        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session1, 100m, actorUserId: seedUserId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var open2 = await new clsPosSessionService(db).OpenSessionAsync(seedUserId, 50m);
            Assert.True(open2.IsSuccess, open2.Message);
            session2 = open2.Data;
        }

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            // تحصيل عام بلا فاتورة — الجلسة الحالية (رصيد العميل 100 من البيع الآجل)
            var general = await payments.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId,
                Amount = 40m, PaymentDate = today, Method = "cash",
                SessionId = session2, UserId = seedUserId
            });
            Assert.True(general.IsSuccess, general.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            // سداد الفاتورة القديمة (جلسة البيع الأصلية مغلقة) نقدًا — الجلسة الحالية
            var oldDebt = await payments.PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = debtInvoiceId, Amount = 20m, PaymentDate = today,
                Method = "cash", SessionId = session2, UserId = seedUserId
            });
            Assert.True(oldDebt.IsSuccess, oldDebt.Message);

            // سداد مورد نقدي — الجلسة الحالية (رصيد المورد 100 من الشراء الآجل)
            var supplierPay = await payments.PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 25m, PaymentDate = today,
                Method = "cash", SessionId = session2, UserId = seedUserId
            });
            Assert.True(supplierPay.IsSuccess, supplierPay.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session2, 85m, actorUserId: seedUserId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        await using var reportDb = _fx.CreateContext();
        var periodic = await new clsReportService(reportDb)
            .GetPeriodicReportAsync(today, today, enPeriodReportType.Quarterly);
        Assert.True(periodic.IsSuccess, periodic.Message);

        var line1 = periodic.Data!.SessionLines.Single(s => s.SessionId == session1);
        Assert.Equal(0m, line1.CashSales);              // التحصيل المتأخر لا يُحمَّل على الجلسة القديمة
        Assert.Equal(100m, line1.ExpectedCash);

        var line2 = periodic.Data!.SessionLines.Single(s => s.SessionId == session2);
        Assert.Equal(60m, line2.CashSales);             // 40 بلا فاتورة + 20 دَين قديم — مرة واحدة لكل دفعة
        Assert.Equal(0m, line2.CashReturns);
        Assert.Equal(25m, line2.CashSupplierPayments);  // سداد المورد ليس مرتجع عميل
        Assert.Equal(85m, line2.ExpectedCash);          // 50 + 60 − 25 (لا ازدواج ولا إغفال)
    }

    // ══ 4) حدود اليوم البرمجي (UTC+3) في التدفق النقدي والتقرير اليومي ══
    // حركات نقد وفواتير عند الحواف: 00:30 فجرًا (UTC اليوم السابق 21:30)،
    // 21:00 مساءً، و23:00 — كلها ليوم البرنامج؛ و00:30 فجر اليوم التالي لا تنتمي.
    // (الكود القديم قارن UTC بالحدود المحلية فأوقع الحركات بين 21:00–24:00 في اليوم الخطأ.)
    [Fact]
    public async Task CashFlowAndDaily_UseProgramDayBoundaries()
    {
        string marker = $"rc4-{Guid.NewGuid():N}"[..12];
        DateOnly day = D(3);        // يوم فريد لهذا الاختبار
        DateOnly nextDay = D(4);    // فجر اليوم التالي (لا تقاطع مع اختبار آخر)

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m);
        int userId = seed.UserId;
        int productId = seed.ProductId;

        // حركات نقد (SessionId = null مباشرة — نافذة التقرير على CreatedAt فقط)
        db.CashMovements.Add(new Sayl.Core.Models.CashMovement
        {
            MovementType = 'I', Amount = 7m, UserId = userId,
            CreatedAt = LocalDayUtc(day).AddMinutes(30)          // فجر اليوم 00:30
        });
        db.CashMovements.Add(new Sayl.Core.Models.CashMovement
        {
            MovementType = 'I', Amount = 11m, UserId = userId,
            CreatedAt = LocalDayUtc(day).AddHours(21)            // مساء 21:00
        });
        db.CashMovements.Add(new Sayl.Core.Models.CashMovement
        {
            MovementType = 'I', Amount = 5m, UserId = userId,
            CreatedAt = LocalDayUtc(nextDay).AddMinutes(30)      // فجر اليوم التالي
        });
        await db.SaveChangesAsync();

        // فاتورتان داخلا اليوم وفاتورة فجر اليوم التالي
        await using var saleDb = _fx.CreateContext();
        int saleMidnight = await SaleAsync(saleDb, userId, null, new[] { (productId, 1, 20m) }, paid: 20m, paymentDate: day);
        int saleEvening = await SaleAsync(saleDb, userId, null, new[] { (productId, 2, 20m) }, paid: 40m, paymentDate: day);
        int saleNextDay = await SaleAsync(saleDb, userId, null, new[] { (productId, 3, 20m) }, paid: 60m, paymentDate: nextDay);
        await SetInvoiceCreatedAsync(saleDb, saleMidnight, LocalDayUtc(day).AddMinutes(30));
        await SetInvoiceCreatedAsync(saleDb, saleEvening, LocalDayUtc(day).AddHours(23));
        await SetInvoiceCreatedAsync(saleDb, saleNextDay, LocalDayUtc(nextDay).AddMinutes(30));

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var cashFlow = await reports.GetCashFlowReportAsync(day, day);
        Assert.True(cashFlow.IsSuccess, cashFlow.Message);
        Assert.Equal(18m, cashFlow.Data!.CashIn);   // 7 + 11 (حركة فجر الغد مستثناة)

        var daily = await reports.GetDailySalesAsync(day);
        Assert.True(daily.IsSuccess, daily.Message);
        Assert.Equal(2, daily.Data!.InvoiceCount);  // فجرًا ومساءً — فاتورة فجر الغد مستثناة
        Assert.Equal(60m, daily.Data.TotalGross);   // 20 + 40

        // نفس الفاتورة الحدّية تُحسب ليومها في لوحة التحكم أيضًا
        var dashboard = await reports.GetDashboardSummaryAsync(day);
        Assert.True(dashboard.IsSuccess, dashboard.Message);
        Assert.Equal(2, dashboard.Data!.TodayInvoiceCount);

        var cashFlowNext = await reports.GetCashFlowReportAsync(nextDay, nextDay);
        Assert.True(cashFlowNext.IsSuccess, cashFlowNext.Message);
        Assert.Equal(5m, cashFlowNext.Data!.CashIn);
    }
}
