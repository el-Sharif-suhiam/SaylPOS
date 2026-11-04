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
UPDATE PosSessions SET Status = 'closed', ClosedAt = GETDATE() WHERE Status = 'open';
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

    /// <summary>Seed a user with an explicit role value (for permission tests).</summary>
    private static async Task<int> SeedUserWithRoleAsync(SaylDbContext db, string marker, int roleValue)
    {
        var role = new Role { Name = $"role-{marker}", RoleValue = roleValue, IsActive = true };
        db.Roles.Add(role);
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
}
