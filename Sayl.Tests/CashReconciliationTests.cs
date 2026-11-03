using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// مصالحة الصندوق وتصنيف حركة النقد:
///  • المعادلة: افتتاحي + مبيعات نقدي + تحصيلات نقدية − مرتجعات عملاء نقدي
///    − مدفوعات موردين نقدي + قبض − صرف (الشبكة/التحويل لا تدخل الدرج).
///  • مرتجع العميل يخص درج جلسة الفعل.
///  • استرداد المورد ليس تحصيل عميل، والسداد النقدي للمورد يظهر في التقرير.
///  • الرواتب تُحسب مرة واحدة في حركة الصندوق.
/// (نمط السياقات: سياق جديد لكل خطوة — كما في ReportCalculationTests —
///  كي لا يقرأ FindAsync كيانات متتبعة قديمة بعد UPDATE الخام.)
/// </summary>
[Collection("database")]
public class CashReconciliationTests
{
    private readonly DatabaseFixture _fx;
    public CashReconciliationTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    /// <summary>يوم معزول للتقرير — بعيد عن أيام اختبارات أخرى في القاعدة المشتركة.</summary>
    private static readonly DateOnly ReportDay = new(2025, 6, 15);
    /// <summary>فترة الراتب تنتهي اليوم (حارس السنة المالية يرفض فترات ما قبل السنة الحالية).</summary>
    private static DateOnly PayrollPeriodEnd => Today;

    private static async Task<int> OpenSessionAsync(SaylDbContext db, int userId, decimal opening)
    {
        var r = await new clsPosSessionService(db).OpenSessionAsync(userId, opening);
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static async Task<int> PurchaseStockAsync(SaylDbContext db, TestSeed seed, int qty)
    {
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = qty, UnitCost = 10m } }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static async Task<int> SaleAsync(SaylDbContext db, TestSeed seed, int? sessionId,
        int qty, decimal unitPrice, decimal paid, string method, DateOnly? paymentDate = null)
    {
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            SessionId = sessionId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = qty, UnitPrice = unitPrice } },
            Payment = new DtoPaymentInfo { Amount = paid, Method = method, PaymentDate = paymentDate ?? Today }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static TestSeed SeedOf(int userId, int supplierId, int customerId, int productId) =>
        new() { UserId = userId, SupplierId = supplierId, CustomerId = customerId, ProductId = productId };

    // ══ 4) مصالحة الجلسة: كل مكونات المعادلة ══
    [Fact]
    public async Task SessionReconciliation_SumsAllCashComponents()
    {
        string marker = $"rc1-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, customerId, productId, session;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            customerId = seed.CustomerId; productId = seed.ProductId;
            session = await OpenSessionAsync(db, userId, 100m);
        }

        var seedIds = SeedOf(userId, supplierId, customerId, productId);

        await using (var db = _fx.CreateContext())
            await PurchaseStockAsync(db, seedIds, 20);

        // + مبيعات نقدي 40
        await using (var db = _fx.CreateContext())
            await SaleAsync(db, seedIds, session, 2, 20m, 40m, "cash");

        // شبكة 20 — لا تدخل الدرج
        await using (var db = _fx.CreateContext())
            await SaleAsync(db, seedIds, session, 1, 20m, 20m, "card");

        // بيع آجل 60 ثم + تحصيل نقدي 30
        await using (var db = _fx.CreateContext())
            await SaleAsync(db, seedIds, session, 3, 20m, 0m, "cash");

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 30m, PaymentDate = Today,
                Method = "cash", SessionId = session, UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // − سداد مورد نقدي 25
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 25m, PaymentDate = Today,
                Method = "cash", SessionId = session, UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // + قبض 50 / − صرف 10
        await using (var db = _fx.CreateContext())
        {
            var cash = new clsCashService(db);
            Assert.True((await cash.AddMovementAsync(session, 'I', 50m, "توريد نقدي", userId)).IsSuccess);
            Assert.True((await cash.AddMovementAsync(session, 'O', 10m, "مصروف نثري", userId)).IsSuccess);
        }

        // المعادلة: 100 + 40 + 30 − 25 + 50 − 10 = 185 (الشبكة 20 مستبعدة)
        await using (var db = _fx.CreateContext())
        {
            var summary = await new clsCashService(db).GetSessionCashSummaryAsync(session);
            Assert.True(summary.IsSuccess, summary.Message);
            Assert.Equal(100m, summary.Data!.OpeningAmount);
            Assert.Equal(70m, summary.Data!.CashSales);   // 40 بيع + 30 تحصيل
            Assert.Equal(0m, summary.Data!.CashReturns);  // لا مرتجعات عملاء
            Assert.Equal(25m, summary.Data!.CashSupplierPayments); // السداد النقدي تصنيفه مستقل
            Assert.Equal(50m, summary.Data!.CashIn);
            Assert.Equal(10m, summary.Data!.CashOut);
            Assert.Equal(185m, summary.Data!.ExpectedCash);
        }

        // إغلاق الجلسة يختم المتوقع نفسه
        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session, 185m, actorUserId: userId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        await using (var verifyDb = _fx.CreateContext())
        {
            var stored = await verifyDb.PosSessions.AsNoTracking().SingleAsync(s => s.SessionId == session);
            Assert.Equal(185m, stored.ExpectedAmount);
        }
    }

    private static TestSeed SeedOf(int userId, int customerId, int productId) =>
        new() { UserId = userId, SupplierId = 0, CustomerId = customerId, ProductId = productId };

    // ══ 5أ) مرتجع عميل يخص درج جلسة الفعل ══
    [Fact]
    public async Task CustomerRefund_WithdrawsFromActingSession()
    {
        string marker = $"rc2-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, customerId, productId, session1, saleInvoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId; customerId = seed.CustomerId; productId = seed.ProductId;
            session1 = await OpenSessionAsync(db, userId, 0m);
        }

        var seedIds = SeedOf(userId, supplierId, customerId, productId);

        await using (var db = _fx.CreateContext())
            await PurchaseStockAsync(db, seedIds, 10);

        // بيع نقدي 3×20=60 في جلسة قديمة ثم إغلاقها
        await using (var db = _fx.CreateContext())
            saleInvoiceId = await SaleAsync(db, seedIds, session1, 3, 20m, 60m, "cash");

        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session1, 60m, actorUserId: userId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        // اليوم: جلسة جديدة ومرتجع وحدة واحدة (استرداد 20 نقدًا)
        int session2 = 0, returnInvoiceId = 0;
        await using (var db = _fx.CreateContext())
            session2 = await OpenSessionAsync(db, userId, 100m);

        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = saleInvoiceId,
                UserId = userId,
                SessionId = session2,
                Items = { new DtoReturnItem { ProductId = productId, Quantity = 1 } }
            });
            Assert.True(ret.IsSuccess, ret.Message);
            returnInvoiceId = ret.Data;
        }

        // دفعة الاسترداد على فاتورة المرتجع موجهة لدرج اليوم
        await using (var db = _fx.CreateContext())
        {
            var refundRow = await db.Payments.AsNoTracking()
                .SingleAsync(p => p.InvoiceId == returnInvoiceId && p.Type == enPaymentType.outcome);
            Assert.Equal(session2, refundRow.SessionId);
            Assert.Equal("cash", refundRow.PaymentMethod);

            var summary = await new clsCashService(db).GetSessionCashSummaryAsync(session2);
            Assert.Equal(20m, summary.Data!.CashReturns);
            Assert.Equal(80m, summary.Data!.ExpectedCash); // 100 − 20
        }
    }

    // ══ 5ب) تصنيف حركة الصندوق ══
    [Fact]
    public async Task CashFlowReport_ClassifiesPartiesAndMethodsCorrectly()
    {
        string marker = $"rc3-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, customerId, productId, purchaseInvoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId; customerId = seed.CustomerId; productId = seed.ProductId;
        }

        var seedIds = SeedOf(userId, supplierId, customerId, productId);

        // مخزون: شراء 10 @10 (رصيد مورد 100)
        await using (var db = _fx.CreateContext())
            purchaseInvoiceId = await PurchaseStockAsync(db, seedIds, 10);

        // بيع نقدي 40 + دَين 60 — بتاريخ معزول (التقرير يرى دفعات ReportDay فقط)
        await using (var db = _fx.CreateContext())
            await SaleAsync(db, seedIds, null, 2, 20m, 40m, "cash", ReportDay);
        await using (var db = _fx.CreateContext())
            await SaleAsync(db, seedIds, null, 3, 20m, 0m, "cash", ReportDay);

        await using (var payDb = _fx.CreateContext())
        {
            var svc = new clsPaymentService(payDb);
            // + تحصيل نقدي 30 وتحصيل شبكة 20 — بتاريخ معزول
            Assert.True((await svc.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 30m, PaymentDate = ReportDay,
                Method = "cash", UserId = userId
            })).IsSuccess);
            Assert.True((await svc.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 20m, PaymentDate = ReportDay,
                Method = "card", UserId = userId
            })).IsSuccess);

            // سداد مورد نقدي كامل للفاتورة (60 على الفاتورة + 40 على الرصيد)
            // كي يرد استرداد المرتجع نقدًا لا خصمًا من الرصيد
            Assert.True((await svc.PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = purchaseInvoiceId, PartyId = 0, Amount = 60m,
                PaymentDate = ReportDay, Method = "cash", UserId = userId
            })).IsSuccess);
        }
        await using (var payDb2 = _fx.CreateContext())
        {
            var svc2 = new clsPaymentService(payDb2);
            Assert.True((await svc2.PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 40m, PaymentDate = ReportDay,
                Method = "cash", UserId = userId
            })).IsSuccess);
        }

        // مرتجع شراء وحدة واحدة (استرداد 10 من المورد): الفاتورة مسددة بالكامل
        // → الاسترداد كله نقدًا بطرف 'S'
        int returnInvoiceId;
        await using (var retDb = _fx.CreateContext())
        {
            var ret = await new clsReturnService(retDb).CreateSupplierReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = purchaseInvoiceId,
                UserId = userId,
                SessionId = null,
                Items = { new DtoReturnItem { ProductId = productId, Quantity = 1 } }
            });
            Assert.True(ret.IsSuccess, ret.Message);
            returnInvoiceId = ret.Data;
        }

        // الخدمة تؤرخ دفعة الاسترداد بيوم التنفيذ (UtcNow) — نحملها على اليوم
        // المعزول للتقرير (نفس نمط MoveInvoiceDayAsync في اختبارات التقارير)
        await using (var db = _fx.CreateContext())
        {
            await db.Payments.Where(p => p.InvoiceId == returnInvoiceId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PaymentDate, ReportDay));

            // دفعة الاسترداد: دخل بطرف مورد — ليست تحصيل عميل
            var refundRow = await db.Payments.AsNoTracking().SingleAsync(p => p.InvoiceId == returnInvoiceId);
            Assert.Equal(enPaymentType.income, refundRow.Type);
            Assert.Equal('S', refundRow.PartyType);
            Assert.Equal("cash", refundRow.PaymentMethod);
        }

        await using (var reportDb = _fx.CreateContext())
        {
            var report = await new clsReportService(reportDb).GetCashFlowReportAsync(ReportDay, ReportDay);
            Assert.True(report.IsSuccess, report.Message);
            var cf = report.Data!;

            Assert.Equal(70m, cf.CashSales);          // 40 بيع + 30 تحصيل — بلا استرداد المورد
            Assert.Equal(0m, cf.CashReturns);         // لا مرتجعات عملاء هنا
            Assert.Equal(20m, cf.CustomerPayments);   // الشبكة فقط — بلا استرداد المورد
            Assert.Equal(100m, cf.SupplierPayments);  // 60 + 40 — النقدي مُدرج
            Assert.Equal(10m, cf.SupplierRefunds);    // استرداد المورد تصنيفه مستقل
            Assert.Equal(70m - 100m + 20m + 10m, cf.NetMovement);
        }
    }

    // ══ 5ج) الرواتب تُحسب مرة واحدة في حركة الصندوق ══
    [Fact]
    public async Task CashFlowReport_DoesNotCountPayrollTwice()
    {
        string marker = $"rc4-{Guid.NewGuid():N}"[..12];
        int userId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;

            var employee = new Employee
            {
                Name = $"موظف-{marker}",
                SalaryType = enSalaryType.monthly,
                Salary = 300m,
                Status = "active",
                Position = "بائع",
                CreatedAt = DateTime.UtcNow
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();

            // صرف راتب حقيقي: توليد كشف الفترة ثم صرفه (ينشئ مصروفًا مرتبطًا بفئة رواتب)
            var payroll = new clsPayrollService(db, new clsFiscalYearService(db, new clsSettingsService(db)));
            var generated = await payroll.GeneratePayrollsAsync(PayrollPeriodEnd.AddDays(-30), PayrollPeriodEnd, userId);
            Assert.True(generated.IsSuccess, generated.Message);

            var records = await db.PayrollRecords.Where(p => p.EmployeeId == employee.EmployeeId).ToListAsync();
            var record = Assert.Single(records);
            var paidResult = await payroll.PayPayrollAsync(record.PayrollId, userId);
            Assert.True(paidResult.IsSuccess, paidResult.Message);

            // مصروف عادي غير مرتبط بالرواتب
            db.Expenses.Add(new Expense
            {
                Description = $"إيجار-{marker}",
                Amount = 120m,
                ExpenseDate = PayrollPeriodEnd,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var reportDb = _fx.CreateContext())
        {
            // المدى يغطي PaidAt (UTC — قد يتأخر عن اليوم البرمجي) وتاريخ المصروف معًا —
            // اختبارات أخرى لا تنشئ مصروفات ولا كشوف رواتب فلا تلويث
            var report = await new clsReportService(reportDb)
                .GetCashFlowReportAsync(PayrollPeriodEnd.AddDays(-1), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));
            Assert.True(report.IsSuccess, report.Message);
            var cf = report.Data!;

            // مصروف الرواتب المرتبط مستثنى من ExpensesPaid — الرواتب تُحسب مرة واحدة فقط
            Assert.Equal(120m, cf.ExpensesPaid);
            Assert.Equal(300m, cf.SalariesPaid);
        }
    }
}
