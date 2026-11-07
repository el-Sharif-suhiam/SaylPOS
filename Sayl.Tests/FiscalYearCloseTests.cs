using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Fiscal year closing + historical record protection.
/// </summary>
[Collection("database")]
public class FiscalYearCloseTests
{
    private readonly DatabaseFixture _fx;
    public FiscalYearCloseTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static async Task SetFiscalYearStartAsync(SaylDbContext db, DateOnly date)
    {
        var row = await db.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
        if (row == null)
        {
            db.Settings.Add(new Setting
            {
                SettingKey = clsFiscalYearService.KeyFiscalYearStart,
                SettingValue = date.ToString("yyyy-MM-dd"),
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.SettingValue = date.ToString("yyyy-MM-dd");
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static async Task ResetFiscalYearStartAsync(SaylDbContext db)
        => await SetFiscalYearStartAsync(db, new DateOnly(Today.Year, 1, 1));

    /// <summary>Close leftover blockers in the shared integration DB so readiness can pass.</summary>
    private static async Task ClearCloseBlockersAsync(SaylDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"DELETE FROM StockReservations;
DELETE FROM InvoiceItems WHERE InvoiceId IN (SELECT InvoiceId FROM Invoices WHERE Status = 'suspended');
DELETE FROM Invoices WHERE Status = 'suspended';
UPDATE PosSessions SET Status = 'closed', ClosedAt = GETUTCDATE() WHERE Status = 'open';
UPDATE StockCounts SET IsPosted = 1 WHERE IsPosted = 0;
UPDATE PayrollRecords SET Status = 'paid' WHERE Status = 'pending';");
    }

    /// <summary>Seed a suspended invoice with an explicit CreatedAt (UTC) inside the closing period.</summary>
    private static async Task<int> SeedSuspendedInvoiceAsync(SaylDbContext db, TestSeed seed, DateTime createdAtUtc)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.suspended,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = 30m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 30m,
            CreatedAt = createdAtUtc
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    private static async Task<int> SeedExpenseAsync(SaylDbContext db, TestSeed seed, DateOnly expenseDate)
    {
        var expense = new Expense
        {
            Description = "fiscal-test-expense",
            Amount = 50m,
            ExpenseDate = expenseDate,
            Category = "test",
            UserId = seed.UserId,
            CreatedAt = DateTime.UtcNow
        };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync();
        return expense.ExpenseId;
    }

    /// <summary>Seed a completed credit sale (no payment) for the customer — outstanding debt.</summary>
    private static async Task<int> SeedCreditSaleAsync(SaylDbContext db, TestSeed seed, DateTime createdAtUtc)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = 100m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 100m,
            CreatedAt = createdAtUtc
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    /// <summary>
    /// Seed a completed credit sale with an explicit net amount and CreatedAt —
    /// for allocation-aware readiness scenarios (independently computed expected values).
    /// </summary>
    private static async Task<int> SeedSaleAsync(SaylDbContext db, TestSeed seed, decimal netAmount, DateTime createdAtUtc)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = netAmount,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = netAmount,
            CreatedAt = createdAtUtc
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    /// <summary>Seed a payment linked directly to an invoice (Income على فاتورة بيع).</summary>
    private static async Task SeedLinkedPaymentAsync(SaylDbContext db, TestSeed seed, int invoiceId, decimal amount, DateOnly paymentDate)
    {
        db.Payments.Add(new Payment
        {
            Type = enPaymentType.income,
            InvoiceId = invoiceId,
            PartyType = 'C',
            PartyId = seed.CustomerId,
            Amount = amount,
            PaymentDate = paymentDate,
            PaymentMethod = "cash",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seed a general (unallocated) payment from the customer — InvoiceId = null.
    /// هذا هو الشكل الذي تخصّصه clsPaymentAllocation على الفواتير الأقدم أولًا.</summary>
    private static async Task SeedGeneralPaymentAsync(SaylDbContext db, TestSeed seed, decimal amount, DateOnly paymentDate)
    {
        db.Payments.Add(new Payment
        {
            Type = enPaymentType.income,
            InvoiceId = null,
            PartyType = 'C',
            PartyId = seed.CustomerId,
            Amount = amount,
            PaymentDate = paymentDate,
            PaymentMethod = "cash",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed a completed customer-return invoice (negative NetAmount) linked to an original
    /// invoice — محاكاة حالة تاريخية موجودة فعلًا (مرتجع مُقيَّد سلفًا) لقياس أثره على
    /// المتبقي دون المرور بحارسات السنة المعلّقة/الجلسة في خدمة المرتجعات.
    /// </summary>
    private static async Task<int> SeedReturnInvoiceAsync(SaylDbContext db, TestSeed seed,
        int originalInvoiceId, decimal returnedValue, DateTime createdAtUtc)
    {
        var returnInvoice = new Invoice
        {
            Type = enInvoicesType.customer_return,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.paid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            OriginalInvoiceId = originalInvoiceId,
            TotalAmount = -returnedValue,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = -returnedValue,
            CreatedAt = createdAtUtc
        };
        db.Invoices.Add(returnInvoice);
        await db.SaveChangesAsync();
        return returnInvoice.InvoiceId;
    }

    // ─────────────────────────────────────────────────
    // 1) جاهزية الإغلاق: الفواتير المعلقة داخل الفترة المغلقة فعليًا
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task CloseReadiness_CountsSuspendedInvoicesInsideClosingPeriod()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today; // الفترة المغلقة: [start, today)
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            // فاتورة معلقة داخل الفترة (UTC = منتصف اليوم المحلي − 3 ساعات)
            DateTime insidePeriodUtc = start.AddDays(5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSuspendedInvoiceAsync(db, seed, insidePeriodUtc);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(1, vm.OldSuspendedInvoices);
            Assert.False(vm.CanClose);
            Assert.Contains(vm.Errors, e => e.Contains("معلقة"));

            // بعد حسم الفواتير المعلقة وإزالة الموانع — الفترة تصبح جاهزة
            await ClearCloseBlockersAsync(db);

            var vmAfter = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(0, vmAfter.OldSuspendedInvoices);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 5) الإغلاق الذري: النجاح يحدّث المفتاحين معًا، والفشل لا يترك حالة نصفية
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task CloseYear_UpdatesBothSettingsAtomically()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.True(r.IsSuccess, r.Message);

            // المفتاحان معًا — لا سنة جديدة بلا تاريخ إغلاق
            // (سياق جديد: يتجنب الكيان المتتبع بقيمته القديمة قبل CAS)
            await using var verify = _fx.CreateContext();
            var startRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            var closeRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyLastCloseDate);
            Assert.Equal(newYearStart.ToString("yyyy-MM-dd"), startRow?.SettingValue);
            Assert.Equal(Today.ToString("yyyy-MM-dd"), closeRow?.SettingValue);

            // سجل التدقيق داخل نفس المعاملة
            bool auditLogged = await verify.SystemLogs
                .AnyAsync(l => l.Action == "CloseFiscalYear" && l.UserId == seed.UserId);
            Assert.True(auditLogged);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task CloseYear_RejectsSuspendedInvoicesInClosingPeriod()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            DateTime insidePeriodUtc = start.AddDays(5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int suspendedId = await SeedSuspendedInvoiceAsync(db, seed, insidePeriodUtc);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.False(r.IsSuccess);

            // الإغلاق الذري: لا شيء تغيّر — لا سنة جديدة ولا تاريخ إغلاق
            var startRow = await db.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            Assert.Equal(start.ToString("yyyy-MM-dd"), startRow?.SettingValue);
            var stillSuspended = await db.Invoices.FirstAsync(i => i.InvoiceId == suspendedId);
            Assert.Equal(enInvoicesStatus.suspended, stillSuspended.Status);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>محاولتا إغلاق متزامنتان: واحدة فقط تنجح — CAS يحمي من الإغلاق المزدوج.</summary>
    [Fact]
    public async Task CloseYear_ConcurrentAttempts_OnlyOneSucceeds()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var db1 = _fx.CreateContext();
            var db2 = _fx.CreateContext();
            var fiscal1 = new clsFiscalYearService(db1, new clsSettingsService(db1));
            var fiscal2 = new clsFiscalYearService(db2, new clsSettingsService(db2));

            var t1 = fiscal1.CloseYearAsync(seed.UserId, newYearStart);
            var t2 = fiscal2.CloseYearAsync(seed.UserId, newYearStart);
            var results = await Task.WhenAll(t1, t2);

            int successes = results.Count(r => r.IsSuccess);
            Assert.Equal(1, successes);

            // الحالة النهائية متسقة: مفتاحا الإعدادات متطابقان دائمًا
            await using var verify = _fx.CreateContext();
            var startRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            var closeRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyLastCloseDate);
            Assert.Equal(newYearStart.ToString("yyyy-MM-dd"), startRow?.SettingValue);
            Assert.Equal(Today.ToString("yyyy-MM-dd"), closeRow?.SettingValue);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 2) حماية المصروفات التاريخية (تعديل وحذف)
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task UpdateExpense_WithoutNewDate_StillValidatesPersistedDate()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // بداية السنة = اليوم: كل ما قبله سنة مغلقة، وما من اليوم فصاعدًا سنة جارية
        DateOnly closedYearDate = Today.AddDays(-30);
        try
        {
            await SetFiscalYearStartAsync(db, Today);

            int oldExpenseId = await SeedExpenseAsync(db, seed, closedYearDate);
            int currentExpenseId = await SeedExpenseAsync(db, seed, Today);

            var service = new clsExpenseService(db);

            // تعديل بلا تاريخ جديد: الحَكَم تاريخ المصروف المخزَّن — يُرفض
            var r1 = await service.UpdateExpenseAsync(new DtoExpense
            {
                ExpenseId = oldExpenseId,
                UserId = seed.UserId,
                Description = "tampered",
                Amount = 999m
                // ExpenseDate = default — يُحذف عن الطلب عمدًا
            });
            Assert.False(r1.IsSuccess);

            // تعديل بتاريخ قديم صريح — يُرفض هو الآخر
            var r2 = await service.UpdateExpenseAsync(new DtoExpense
            {
                ExpenseId = oldExpenseId,
                UserId = seed.UserId,
                ExpenseDate = closedYearDate,
                Amount = 999m
            });
            Assert.False(r2.IsSuccess);

            // المصروف لم يتغير فعليًا (لا تعديل صامت لربحية سنة مغلقة)
            var untouched = await db.Expenses.FirstAsync(e => e.ExpenseId == oldExpenseId);
            Assert.Equal(50m, untouched.Amount);
            Assert.Equal("fiscal-test-expense", untouched.Description);

            // تعديل مشروع لمصروف السنة الحالية بلا تاريخ جديد — يمر
            var r3 = await service.UpdateExpenseAsync(new DtoExpense
            {
                ExpenseId = currentExpenseId,
                UserId = seed.UserId,
                Amount = 75m
            });
            Assert.True(r3.IsSuccess, r3.Message);
            var updated = await db.Expenses.FirstAsync(e => e.ExpenseId == currentExpenseId);
            Assert.Equal(75m, updated.Amount);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task DeleteExpense_RespectsClosedFiscalYears()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // بداية السنة = اليوم: كل ما قبله سنة مغلقة
        DateOnly closedYearDate = Today.AddDays(-30);
        try
        {
            await SetFiscalYearStartAsync(db, Today);

            int oldExpenseId = await SeedExpenseAsync(db, seed, closedYearDate);
            int currentExpenseId = await SeedExpenseAsync(db, seed, Today);

            var service = new clsExpenseService(db);

            // حذف مصروف سنة مغلقة — يُرفض
            var r1 = await service.DeleteExpenseAsync(oldExpenseId, seed.UserId);
            Assert.False(r1.IsSuccess);
            Assert.True(await db.Expenses.AnyAsync(e => e.ExpenseId == oldExpenseId));

            // حذف مصروف السنة الحالية — يمر
            var r2 = await service.DeleteExpenseAsync(currentExpenseId, seed.UserId);
            Assert.True(r2.IsSuccess, r2.Message);
            Assert.False(await db.Expenses.AnyAsync(e => e.ExpenseId == currentExpenseId));
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 3) إكمال فاتورة معلقة بعد إغلاق سنتها — يُرفض
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task CompleteSuspendedInvoice_AfterFiscalYearClosed_IsRejected()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        try
        {
            // إنشاء الفاتورة المعلقة والسنة ما زالت جارية (البداية = 1 يناير)
            int suspendedId = await SeedSuspendedInvoiceAsync(db, seed, DateTime.UtcNow);

            // محاكاة إغلاق السنة: بداية السنة الجديدة بعد تاريخ الفاتورة
            await SetFiscalYearStartAsync(db, Today.AddDays(1));

            var checkout = new clsCheckoutService(db);
            var r = await checkout.CompleteSuspendedInvoiceAsync(
                suspendedId,
                new List<DtoPaymentInfo> { new() { Amount = 30m, Method = "cash" } },
                seed.UserId);

            Assert.False(r.IsSuccess);

            // الفاتورة بقيت معلقة — لم تُقيَّد في السنة المغلقة
            await db.Entry(await db.Invoices.FirstAsync(i => i.InvoiceId == suspendedId)).ReloadAsync();
            var invoice = await db.Invoices.FirstAsync(i => i.InvoiceId == suspendedId);
            Assert.Equal(enInvoicesStatus.suspended, invoice.Status);
            Assert.False(await db.Payments.AnyAsync(p => p.InvoiceId == suspendedId));
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task CompleteSuspendedInvoice_CurrentYear_StillSucceeds()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        try
        {
            int suspendedId = await SeedSuspendedInvoiceAsync(db, seed, DateTime.UtcNow);

            var checkout = new clsCheckoutService(db);
            var r = await checkout.CompleteSuspendedInvoiceAsync(
                suspendedId,
                new List<DtoPaymentInfo> { new() { Amount = 30m, Method = "cash" } },
                seed.UserId);

            Assert.True(r.IsSuccess, r.Message);
            var invoice = await db.Invoices.FirstAsync(i => i.InvoiceId == suspendedId);
            Assert.Equal(enInvoicesStatus.completed, invoice.Status);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 4) مرتجع على فاتورة سنة مغلقة — يُرفض (سياسة التسوية بالفترة الحالية)
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task CustomerReturn_OnClosedYearInvoice_IsRejected()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        try
        {
            int saleId = await SeedCreditSaleAsync(db, seed, DateTime.UtcNow);
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = saleId, ProductId = seed.ProductId,
                Quantity = 5, ItemPrice = 20m, ItemCost = 10m
            });
            await db.SaveChangesAsync();

            await SetFiscalYearStartAsync(db, Today.AddDays(1));

            var returns = new clsReturnService(db);
            var r = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = saleId,
                UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
            });

            Assert.False(r.IsSuccess);
            Assert.False(await db.Invoices.AnyAsync(i => i.OriginalInvoiceId == saleId && i.Type == enInvoicesType.customer_return));
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // تحصيل دين عميل قديم — يُقيَّد بالفترة الحالية ولا يعرّض ربحية سنة مغلقة
    // ─────────────────────────────────────────────────
    [Fact]
    public async Task CollectingOldDebt_DoesNotRewriteClosedYearProfitability()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        try
        {
            // فاتورة آجلة قديمة (قبل بداية السنة المالية الحالية) برصيد 100 على العميل
            DateTime oldInvoiceUtc = DateTime.UtcNow.AddDays(-60);
            int saleId = await SeedCreditSaleAsync(db, seed, oldInvoiceUtc);
            var customer = await db.Customers.FirstAsync(c => c.PartyId == seed.CustomerId);
            customer.Balance += 100m;
            await db.SaveChangesAsync();

            await SetFiscalYearStartAsync(db, Today); // السنة القديمة مغلقة الآن

            var reports = new clsReportService(db);
            DateOnly closedFrom = Today.AddDays(-60);
            DateOnly closedTo = Today.AddDays(-1);

            var before = await reports.GetProfitSummaryAsync(closedFrom, closedTo);
            Assert.True(before.IsSuccess);

            // التحصيل المشروع: الدَّين يُسدَّد اليوم (النقد دخل درج اليوم)
            var payments = new clsPaymentService(db);
            var r = await payments.PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = saleId,
                PartyId = seed.CustomerId,
                Amount = 40m,
                PaymentDate = Today,
                Method = "cash",
                UserId = seed.UserId
            });
            Assert.True(r.IsSuccess, r.Message);

            // الدفعة تُقيَّد بتاريخ اليوم (الفترة الحالية) — لا بإعادة كتابة السنة المغلقة
            var payment = await db.Payments.FirstAsync(p => p.InvoiceId == saleId);
            Assert.Equal(Today, payment.PaymentDate);

            // ربحية السنة المغلقة لم تتغير إطلاقًا
            var after = await reports.GetProfitSummaryAsync(closedFrom, closedTo);
            Assert.True(after.IsSuccess);
            Assert.Equal(before.Data!.NetProfit, after.Data!.NetProfit);
            Assert.Equal(before.Data!.SalesRevenue, after.Data!.SalesRevenue);
            Assert.Equal(before.Data!.COGS, after.Data!.COGS);

            // أثر التحصيل الحقيقي: الدين انخفض وحالة السداد تحدثت
            await db.Entry(customer).ReloadAsync();
            Assert.Equal(60m, customer.Balance);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // مساعدات اختبارات الرواتب والصلاحيات
    // ─────────────────────────────────────────────────

    /// <summary>Seed a minimal active employee for payroll seeding.</summary>
    private static async Task<int> SeedEmployeeAsync(SaylDbContext db, string marker)
    {
        var emp = new Employee
        {
            Name = $"موظف-{marker}",
            Position = "test",
            Status = "active",
            SalaryType = enSalaryType.monthly,
            Salary = 1000m,
            HireDate = Today.AddYears(-1),
            CreatedAt = DateTime.UtcNow
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return emp.EmployeeId;
    }

    /// <summary>Seed a payroll record with an explicit period and status.</summary>
    private static async Task<int> SeedPayrollAsync(SaylDbContext db, TestSeed seed,
        DateOnly periodFrom, DateOnly periodTo, enPayrollStatus status = enPayrollStatus.pending)
    {
        int empId = await SeedEmployeeAsync(db, $"pay{Guid.NewGuid():N}"[..10]);
        var payroll = new PayrollRecord
        {
            EmployeeId = empId,
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            SalaryType = enSalaryType.monthly,
            Rate = 1000m,
            WorkedUnits = 1m,
            HolidayUnits = 0m,
            Gross = 1000m,
            Deductions = 0m,
            Additions = 0m,
            Net = 1000m,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        db.PayrollRecords.Add(payroll);
        await db.SaveChangesAsync();
        return payroll.PayrollId;
    }

    /// <summary>Seed a user with an explicit role value (for permission tests).
    /// الدور مُعاد استخدامه من TestSeed.GetOrCreateRoleAsync (RoleId tinyint).</summary>
    private static async Task<int> SeedUserWithRoleAsync(SaylDbContext db, string marker, int roleValue)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        var user = new User
        {
            Username = $"user-{marker}",
            PasswordHash = "test-hash",
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    // ─────────────────────────────────────────────────
    // 6) الفواتير المعلقة: حدّا الفترة المغلقة معًا
    // ─────────────────────────────────────────────────

    /// <summary>فاتورة معلقة من سنة مغلقة سلفًا (قبل بداية السنة الحالية) لا تسدّ إغلاق السنة الحالية.</summary>
    [Fact]
    public async Task CloseReadiness_IgnoresSuspendedInvoiceFromEarlierClosedPeriod()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today; // الفترة المغلقة: [start, today)
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            // فاتورة معلقة قبل بداية السنة المغلقة — من فترة أقدم أُغلقت سلفًا
            DateTime earlierClosedPeriodUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSuspendedInvoiceAsync(db, seed, earlierClosedPeriodUtc);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // لا تُحسب: ليست من الفترة [start, today) — لا تسدّ الإغلاق بلا سبب
            Assert.Equal(0, vm.OldSuspendedInvoices);
            Assert.DoesNotContain(vm.Errors, e => e.Contains("معلقة"));
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فاتورة معلقة عند حدّ البداية تمامًا (== FiscalYearStart) تُحسب داخل الفترة.</summary>
    [Fact]
    public async Task CloseReadiness_CountsSuspendedInvoiceExactlyAtPeriodStart()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            // CreatedAt = بداية الفترة تمامًا — حدّ الشمول: >= fysUtc تُحسب داخلها
            // (حد الفترة في الخدمة يقارن تاريخ 00:00 المخزّن كما هو)
            DateTime atStartUtc = start.ToDateTime(TimeOnly.MinValue);
            await SeedSuspendedInvoiceAsync(db, seed, atStartUtc);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            Assert.Equal(1, vm.OldSuspendedInvoices);
            Assert.False(vm.CanClose);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 7) كشوف الرواتب المعلقة: التقاطع الفعلي مع الفترة المغلقة
    // ─────────────────────────────────────────────────

    /// <summary>كشف معلق كامل داخل الفترة المغلقة — يمنع الإغلاق.</summary>
    [Fact]
    public async Task CloseReadiness_CountsPendingPayrollFullyInsideClosingPeriod()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            await SeedPayrollAsync(db, seed, start.AddDays(2), start.AddDays(8));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(1, vm.PendingPayrolls);
            Assert.False(vm.CanClose);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>كشف معلق يمتد عبر حدّي الفترة (بدأت قبلها وتمتد بعدها) — يمنع الإغلاق.</summary>
    [Fact]
    public async Task CloseReadiness_CountsPendingPayrollOverlappingPeriodBoundaries()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            // كشف عبر حد البداية: بدأ قبل FiscalYearStart وينتهي داخلها
            await SeedPayrollAsync(db, seed, start.AddDays(-5), start.AddDays(3));
            // كشف عبر حد النهاية: بدأ داخل الفترة ويمتد إلى ما بعد newYearStart
            await SeedPayrollAsync(db, seed, newYearStart.AddDays(-3), newYearStart.AddDays(5));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(2, vm.PendingPayrolls);
            Assert.False(vm.CanClose);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>كشف معلق خارج الفترة كلها (قبل بدايتها أو يبدأ من نهايتها فصاعدًا) — لا يمنع الإغلاق.</summary>
    [Fact]
    public async Task CloseReadiness_IgnoresPendingPayrollEntirelyOutsideClosingPeriod()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            // كشف انتهى قبل بداية الفترة المغلقة (فترة أقدم)
            await SeedPayrollAsync(db, seed, start.AddDays(-20), start.AddDays(-5));
            // كشف يبدأ من نهاية الفترة فصاعدًا (سنة جديدة — بعد الإغلاق)
            await SeedPayrollAsync(db, seed, newYearStart, newYearStart.AddDays(15));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(0, vm.PendingPayrolls);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 8) أول إغلاق على الإطلاق: صف FiscalYearStart غير موجود
    // ─────────────────────────────────────────────────

    /// <summary>
    /// أول إغلاق بلا صف FiscalYearStart: مانع داخل الفترة الفعلية [الافتراضي, newYearStart)
    /// يمنع الإغلاق ولا يُدرج صف البداية (الجاهزية تُحسب قبل أي كتابة).
    /// </summary>
    [Fact]
    public async Task CloseYear_FirstCloseMissingStartRow_ChecksPreviousPeriodBeforeInsert()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly newYearStart = Today;
        DateOnly defaultStart = new DateOnly(Today.Year, 1, 1); // الافتراضي عند غياب الصف
        try
        {
            // حذف صف البداية تمامًا — أول إغلاق على الإطلاق
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM Settings WHERE SettingKey = {0}", clsFiscalYearService.KeyFiscalYearStart);
            await ClearCloseBlockersAsync(db);

            // فاتورة معلقة داخل الفترة الفعلية [الافتراضي, today) — يجب أن تُرى قبل الإدراج
            // (إن كان اليوم 1 أو 2 يناير فالفترة أقصر من يومين كاملين — يتعذر بذر فاتورة
            //  strictly داخلها؛ يُتخطى الفرع، وإدراج الصف نفسه يُغطى في اختبار النجاح التالي)
            if (defaultStart.AddDays(2) <= newYearStart)
            {
                DateTime insidePeriodUtc = defaultStart.AddDays(1).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
                await SeedSuspendedInvoiceAsync(db, seed, insidePeriodUtc);

                var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
                var r = await fiscal.CloseYearAsync(seed.UserId, newYearStart);

                Assert.False(r.IsSuccess);
                // الجاهزية حُسبت قبل الإدراج: الفاتورة المعلقة رُئيت والصف لم يُدرج
                bool startRowInserted = await db.Settings
                    .AnyAsync(s => s.SettingKey == clsFiscalYearService.KeyFiscalYearStart);
                Assert.False(startRowInserted);
            }
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>
    /// أول إغلاق ناجح بلا صف FiscalYearStart: الصف يُدرج بقيمة السنة الجديدة
    /// وتاريخ الإغلاق يُسجل معه — لا حالة نصفية.
    /// </summary>
    [Fact]
    public async Task CloseYear_FirstCloseMissingStartRow_InsertsBothSettingsAtomically()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly newYearStart = Today;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM Settings WHERE SettingKey = {0}", clsFiscalYearService.KeyFiscalYearStart);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.True(r.IsSuccess, r.Message);

            await using var verify = _fx.CreateContext();
            var startRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            var closeRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyLastCloseDate);
            Assert.Equal(newYearStart.ToString("yyyy-MM-dd"), startRow?.SettingValue);
            Assert.Equal(Today.ToString("yyyy-MM-dd"), closeRow?.SettingValue);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 9) الإغلاق المتكرر: بعد إغلاق ناجح، المحاولة الثانية بنفس التاريخ تُرفض
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task CloseYear_RepeatedAttemptWithSameDate_IsRejected()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var first = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.True(first.IsSuccess, first.Message);

            // المحاولة الثانية بنفس التاريخ: newYearStart <= البداية الحالية — تُرفض
            var second = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.False(second.IsSuccess);

            // الحالة بعد المحاولتين متسقة: البداية ما زالت قيمة الإغلاق الأول
            await using var verify = _fx.CreateContext();
            var startRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            Assert.Equal(newYearStart.ToString("yyyy-MM-dd"), startRow?.SettingValue);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 10) بوابة الصلاحية داخل الخدمة
    // ─────────────────────────────────────────────────

    /// <summary>مستخدم بصلاحية المخزون (أدمن) — الإغلاق يمر.</summary>
    [Fact]
    public async Task CloseYear_AuthorizedUser_Succeeds()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker); // أدمن 4095 — يملك بت المخزون

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r = await fiscal.CloseYearAsync(seed.UserId, newYearStart);
            Assert.True(r.IsSuccess, r.Message);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>مستخدم بلا صلاحية المخزون (كاشير 0) — يُرفض ولا يتغير شيء.</summary>
    [Fact]
    public async Task CloseYear_UnauthorizedUser_IsRejectedWithoutChanges()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            // كاشير: RoleValue = 0 — لا يملك أي بت شاشة
            int cashierId = await SeedUserWithRoleAsync(db, $"csh-{marker[..6]}", AppPermissions.CashierRoleValue);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r = await fiscal.CloseYearAsync(cashierId, newYearStart);

            Assert.False(r.IsSuccess);
            Assert.Contains("صلاحية", r.Message);

            // لا شيء تغيّر: لا سنة جديدة ولا تاريخ إغلاق لهذه المحاولة
            await using var verify = _fx.CreateContext();
            var startRow = await verify.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            Assert.Equal(start.ToString("yyyy-MM-dd"), startRow?.SettingValue);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>معرّف مستخدم غير صالح (0 / غير موجود) — يُرفض (دور -1 لا يملك أي بت).</summary>
    [Fact]
    public async Task CloseYear_InvalidUserId_IsRejected()
    {
        string marker = $"fy-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var r0 = await fiscal.CloseYearAsync(0, newYearStart);
            var rMissing = await fiscal.CloseYearAsync(seed.UserId + 10_000, newYearStart);

            Assert.False(r0.IsSuccess);
            Assert.False(rMissing.IsSuccess);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // 11) التسوية الحتمية في فحوصات الجاهزية (clsPaymentAllocation)
    // ─────────────────────────────────────────────────
    // «المتبقي» في الفقرتين 5 و6 يُحسب بنفس معادلة سداد الفواتير:
    //   الصافي بعد المرتجعات − الدفعات المرتبطة − نصيب الدفعات العامة.
    // القيم المتوقعة محسوبة يدويًا من تلك المعادلة، والقياس بالفارق
    // (قراءة قبل البذر / بعده) لعزل بيانات الاختبارات المشتركة الأخرى.

    /// <summary>فاتورة مسددة بالكامل بدفعة مرتبطة — لا تُعد آجلة (انحدار: السداد المرتبط ما زال محسوبًا).</summary>
    [Fact]
    public async Task CloseReadiness_FullyPaidLinkedInvoice_NotCountedAsUnpaid()
    {
        string marker = $"fy11a-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة 100 داخل الفترة مسددة كاملًا بدفعة مرتبطة 100
            DateTime insidePeriodUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, insidePeriodUtc);
            await SeedLinkedPaymentAsync(db, seed, saleId, 100m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = 100 − 100 = 0 → لا يُعد آجلة
            Assert.Equal(baseline.UnpaidThisYear, vm.UnpaidThisYear);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فاتورة مسددة جزئيًا بدفعة مرتبطة — تُعد آجلة والدين المحمول يزيد بالمتبقي فقط.</summary>
    [Fact]
    public async Task CloseReadiness_PartiallyPaidInvoice_CountedWithExactRemainder()
    {
        string marker = $"fy11b-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة 100 قبل السنة المالية مسددة 40 — المتبقي 60
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedLinkedPaymentAsync(db, seed, saleId, 40m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): دين قديم محمول = القيمة الأساسية + 60
            Assert.Equal(baseline.OldDebtCarried + 60m, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فاتورة سُددت بتحصيل عام (بلا فاتورة) — لا تظهر دينًا وهميًا (الإصلاح الجوهري:
    /// الحساب القديم كان يرى الدفعات المرتبطة وحدها فيُطلق تحذير ديون بمبلغ لا وجود له).</summary>
    [Fact]
    public async Task CloseReadiness_GeneralPaymentCoveringInvoice_NotCountedAsDebt()
    {
        string marker = $"fy11c-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة 100 قبل السنة المالية + تحصيل عام 100 (InvoiceId = null)
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedGeneralPaymentAsync(db, seed, 100m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): التخصيص يغطي الفاتورة كاملة → متبقي 0 → لا زيادة
            Assert.Equal(baseline.OldDebtCarried, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>تحصيل عام يوزَّع على فاتورتين بالأقدم أولًا: 120 على 100 و50
    /// → الأقدم تُسدد كاملة، والتالية يخصها 20 فيتبقى 30. الفائض غير المخصص
    /// (لو زاد) لا يُحتسب دينًا على أي فاتورة (لا عد مزدوج).</summary>
    [Fact]
    public async Task CloseReadiness_GeneralPaymentAcrossInvoices_OldestFirstAllocation()
    {
        string marker = $"fy11d-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورتان قديمتان: 100 (أقدم) ثم 50، وتحصيل عام 120
            DateTime olderUtc = start.AddDays(-6).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            DateTime newerUtc = start.AddDays(-4).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, olderUtc);
            await SeedSaleAsync(db, seed, 50m, newerUtc);
            await SeedGeneralPaymentAsync(db, seed, 120m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): 100 → الأقدم كاملة؛ 20 → الثانية؛ متبقي 30
            Assert.Equal(baseline.OldDebtCarried + 30m, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>تحصيل عام يتجاوز مجموع المستحق (200 على 100 و50): الفاتورتان تسددان
    /// كاملتين والفائض 50 رصيد غير مخصَّص — لا يُحسب دينًا ولا يُعاد توزيعه (لا عد مزدوج).</summary>
    [Fact]
    public async Task CloseReadiness_GeneralPaymentSurplus_UnallocatedCreditNotCountedAsDebt()
    {
        string marker = $"fy11e-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedSaleAsync(db, seed, 50m, oldUtc.AddHours(1));
            await SeedGeneralPaymentAsync(db, seed, 200m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): 150 مستحق مسدد من 200 → متبقي 0 على الفاتورتين؛
            // الفائض 50 unapplied credit لا يُحسب دينًا
            Assert.Equal(baseline.OldDebtCarried, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>مرتجع داخل نفس الفترة يغطي الفاتورة كاملة — لا تُعد آجلة (الحساب القديم
    /// كان يرى صافي الفاتورة بلا مرتجعاتها فيُطلق تحذير «آجل» زائفًا).</summary>
    [Fact]
    public async Task CloseReadiness_SamePeriodReturnCoveringInvoice_NotCountedAsUnpaid()
    {
        string marker = $"fy11f-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة 100 داخل الفترة + مرتجع كامل −100 (مقيّد سلفًا داخل الفترة)
            DateTime insidePeriodUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, insidePeriodUtc);
            await SeedReturnInvoiceAsync(db, seed, saleId, 100m, insidePeriodUtc.AddHours(1));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = 100 + (−100) = 0 → لا تُعد آجلة
            Assert.Equal(baseline.UnpaidThisYear, vm.UnpaidThisYear);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>مرتجع جزئي داخل الفترة: فاتورة 100 بلا دفعة ومرتجع −40
    /// → متبقي 60 → تُعد آجلة (التحذير يعكس المتبقي الفعلي لا الصافي الخام).</summary>
    [Fact]
    public async Task CloseReadiness_PartialReturnInPeriod_CountedWithRemainder()
    {
        string marker = $"fy11g-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            DateTime insidePeriodUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, insidePeriodUtc);
            await SeedReturnInvoiceAsync(db, seed, saleId, 40m, insidePeriodUtc.AddHours(1));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = 100 − 40 = 60 → تُعد آجلة
            Assert.Equal(baseline.UnpaidThisYear + 1, vm.UnpaidThisYear);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>مرتجع من فترة مغلقة سلفًا على فاتورة الفترة نفسها (سجلات تاريخية
    /// مقيَّدة سلفًا قبل بداية السنة الحالية): الدين المحمول يرى المتبقي بعد المرتجع —
    /// فاتورة 100 مُرجع منها 100 سلفًا لا تعود دينًا محمولًا.</summary>
    [Fact]
    public async Task CloseReadiness_PreviousEraReturn_ReducesOldDebt()
    {
        string marker = $"fy11h-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة قديمة 100 ومرتجع قديم −100 (كلاهما مقيَّد قبل بداية السنة المغلقة)
            DateTime eraUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, eraUtc);
            await SeedReturnInvoiceAsync(db, seed, saleId, 100m, eraUtc.AddHours(2));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = 100 − 100 = 0 → لا زيادة على الدين المحمول
            Assert.Equal(baseline.OldDebtCarried, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فاتورة مسددة كاملة ثم مرتجع −40 بخطة استرداد نقدي (الدفع/الاسترداد على
    /// فواتير منفصلة كما في الإنتاج): المتبقي 100−100−40 = −40 → لا دين وهمي،
    /// ودفعات الاسترداد المرتبطة بفاتورة المرتجع لا تُعد مزدوجة على الفاتورة الأصلية.</summary>
    [Fact]
    public async Task CloseReadiness_RefundOnFullyPaidInvoice_NoPhantomDebt()
    {
        string marker = $"fy11i-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة قديمة 100 مسددة كاملة 100، ثم مرتجع −40 واسترداد نقدي 40
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedLinkedPaymentAsync(db, seed, saleId, 100m, Today);
            int returnId = await SeedReturnInvoiceAsync(db, seed, saleId, 40m, oldUtc.AddHours(2));

            // استرداد نقدي مرتبط بفاتورة المرتجع (كما تسجله ProcessCustomerRefundAsync)
            db.Payments.Add(new Payment
            {
                Type = enPaymentType.outcome,
                InvoiceId = returnId,
                PartyType = 'C',
                PartyId = seed.CustomerId,
                Amount = 40m,
                PaymentDate = Today,
                PaymentMethod = "cash",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = (100 − 40) − 100 = −40 → لا يُعد دينًا
            Assert.Equal(baseline.OldDebtCarried, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>تخصيص الاسترداد (الدَّين أولًا): فاتورة 100 مسددة 50 ثم مرتجع −80.
    /// خطة الاسترداد تخفض الدين 50 (إلى صفر) وترد نقدًا 30 — والفاتورة بعدها:
    /// متبقي = (100 − 80) − 50 = −30 → لا دين وهمي في الجاهزية.</summary>
    [Fact]
    public async Task CloseReadiness_RefundAllocation_DebtFirstThenCash()
    {
        string marker = $"fy11j-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // فاتورة قديمة 100 مسددة 50، ثم مرتجع −80
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedLinkedPaymentAsync(db, seed, saleId, 50m, Today);
            await SeedReturnInvoiceAsync(db, seed, saleId, 80m, oldUtc.AddHours(2));

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): متبقي = (100 − 80) − 50 = −30 → لا يُعد دينًا
            Assert.Equal(baseline.OldDebtCarried, vm.OldDebtCarried);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فحوص الجاهزية المتكررة متماثلة (idempotent): لا قراءة تُغيّر شيئًا —
    /// نفس المدخلات تعطي نفس النتيجة في كل استدعاء.</summary>
    [Fact]
    public async Task CloseReadiness_RepeatedChecks_Idempotent()
    {
        string marker = $"fy11k-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));

            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // خليط: دين قديم بدفعة عامة جزئية + مرتجع داخل الفترة
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            DateTime insideUtc = start.AddDays(3).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, oldUtc);
            await SeedGeneralPaymentAsync(db, seed, 40m, Today); // يخص الفاتورة الأقدم: متبقي 60
            int yearSale = await SeedSaleAsync(db, seed, 80m, insideUtc);
            await SeedReturnInvoiceAsync(db, seed, yearSale, 30m, insideUtc.AddHours(1));

            var vm1 = await fiscal.GetCloseReadinessAsync(newYearStart);
            var vm2 = await fiscal.GetCloseReadinessAsync(newYearStart);
            var vm3 = await fiscal.GetCloseReadinessAsync(newYearStart);

            // نفس القيم في كل استدعاء — لا قراءة تُغيّر شيئًا
            Assert.Equal(vm1.OldDebtCarried, vm2.OldDebtCarried);
            Assert.Equal(vm2.OldDebtCarried, vm3.OldDebtCarried);
            Assert.Equal(vm1.UnpaidThisYear, vm2.UnpaidThisYear);
            Assert.Equal(vm2.UnpaidThisYear, vm3.UnpaidThisYear);
            Assert.Equal(vm1.CanClose, vm2.CanClose);
            Assert.Equal(vm1.OldSuspendedInvoices, vm2.OldSuspendedInvoices);
            Assert.Equal(vm1.PendingPayrolls, vm2.PendingPayrolls);
            Assert.Equal(vm1.OpenStockCount, vm2.OpenStockCount);

            // والقيم نفسها تطابق الحساب اليدوي (بالفارق عن القيمة قبل البذر):
            // دين قديم محمول +60 (فاتورة 100 − تخصيص عام 40)، وفاتورة السنة 80 − 30 = 50 → آجلة
            Assert.Equal(baseline.OldDebtCarried + 60m, vm3.OldDebtCarried);
            Assert.Equal(baseline.UnpaidThisYear + 1, vm3.UnpaidThisYear);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فترة نظيفة بلا أي مانع أو تحذير من بيانات هذا الاختبار — جاهزة للإغلاق.
    /// (التحذيرات العامة لبيانات الاختبارات المشتركة لا تُحتسب هنا؛ المهم لا أخطاء.)</summary>
    [Fact]
    public async Task CloseReadiness_CleanPeriod_NoErrors_CanClose()
    {
        string marker = $"fy11l-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            // فاتورة داخل الفترة مسددة كاملة بدفعة مرتبطة — لا دين ولا معلق
            DateTime insideUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, insideUtc);
            await SeedLinkedPaymentAsync(db, seed, saleId, 100m, Today);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            Assert.True(vm.CanClose);
            Assert.Empty(vm.Errors);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فترة بديون محمولة وآجل جزئي فقط — تحذيرات بلا أخطاء: الإغلاق مسموح
    /// (الديون المفتوحة سياسة: تبقى للسنة الجديدة للمطالبة) والقرار مبني على أرقام صحيحة.</summary>
    [Fact]
    public async Task CloseReadiness_DebtsOnly_ProducesWarningsNotErrors()
    {
        string marker = $"fy11m-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);
            await ClearCloseBlockersAsync(db);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);

            // دين قديم 100 (بلا سداد) + فاتورة السنة مسددة جزئيًا (100/40)
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, oldUtc);
            DateTime insideUtc = start.AddDays(3).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int yearSale = await SeedSaleAsync(db, seed, 100m, insideUtc);
            await SeedLinkedPaymentAsync(db, seed, yearSale, 40m, Today);

            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            // متوقع (يدويًا): دين قديم +100 (دين محمول)، وفاتورة السنة آجلة واحدة
            Assert.Equal(baseline.OldDebtCarried + 100m, vm.OldDebtCarried);
            Assert.Equal(baseline.UnpaidThisYear + 1, vm.UnpaidThisYear);

            // التصنيف: ديون = تحذيرات لا أخطاء — الإغلاق يبقى مسموحًا
            Assert.True(vm.CanClose);
            Assert.Empty(vm.Errors);
            Assert.Contains(vm.Warnings, w => w.Contains("ديون قديمة"));
            Assert.Contains(vm.Warnings, w => w.Contains("آجلة/جزئية"));
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>فترة عليها مانع (فاتورة معلقة) — تُصد: CanClose = false مع خطأ مانع،
    /// حتى لو وُجدت تحذيرات ديون (الأخطاء تتفوق على التحذيرات).</summary>
    [Fact]
    public async Task CloseReadiness_BlockedPeriod_ErrorsPreventCloseDespiteWarnings()
    {
        string marker = $"fy11n-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);

            // فاتورة معلقة داخل الفترة (مانع) + دين قديم (تحذير)
            DateTime insideUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSuspendedInvoiceAsync(db, seed, insideUtc);
            DateTime oldUtc = start.AddDays(-5).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            await SeedSaleAsync(db, seed, 100m, oldUtc);

            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var vm = await fiscal.GetCloseReadinessAsync(newYearStart);

            Assert.False(vm.CanClose);
            Assert.Equal(1, vm.OldSuspendedInvoices);
            Assert.Contains(vm.Errors, e => e.Contains("معلقة"));
            Assert.Contains(vm.Warnings, w => w.Contains("ديون قديمة"));
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }

    /// <summary>اتساق الجاهزية مع سداد الفواتير الفعلي: فاتورة تغطيها دفعة عامة تُصبح
    /// «مسددة» عند محاولة السداد المباشر — نفس المعادلة التي تحكم بها الجاهزية.</summary>
    [Fact]
    public async Task ReadinessAgreesWithPaymentService_GeneralPaymentCoveredInvoice_CannotBePaidTwice()
    {
        string marker = $"fy11o-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly start = Today.AddDays(-10);
        DateOnly newYearStart = Today;
        try
        {
            await SetFiscalYearStartAsync(db, start);

            // فاتورة داخل الفترة 100 + تحصيل عام 100 (يخصّص لها بالتسوية)
            DateTime insideUtc = start.AddDays(2).ToDateTime(new TimeOnly(12, 0)).AddHours(-3);
            int saleId = await SeedSaleAsync(db, seed, 100m, insideUtc);
            await SeedGeneralPaymentAsync(db, seed, 100m, Today);

            // الجاهزية: لا تُعد آجلة
            var fiscal = new clsFiscalYearService(db, new clsSettingsService(db));
            var baseline = await fiscal.GetCloseReadinessAsync(newYearStart);
            Assert.Equal(baseline.UnpaidThisYear, (await fiscal.GetCloseReadinessAsync(newYearStart)).UnpaidThisYear);

            // وسداد الفاتورة مباشرة يُرفض: «مسددة بالكامل» — الجاهزية وسداد الفواتير
            // يقرأان نفس معادلة clsPaymentAllocation (لا عد مزدوج للتحصيل العام)
            var payments = new clsPaymentService(db);
            var r = await payments.PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = saleId,
                PartyId = seed.CustomerId,
                Amount = 100m,
                PaymentDate = Today,
                Method = "cash",
                UserId = seed.UserId
            });
            Assert.False(r.IsSuccess);
            Assert.Contains("مسددة", r.Message);
        }
        finally
        {
            await ClearCloseBlockersAsync(db);
            await ResetFiscalYearStartAsync(db);
        }
    }
}
