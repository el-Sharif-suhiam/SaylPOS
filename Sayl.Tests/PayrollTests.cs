using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// كشوف الرواتب: توليد (شهري/يومي/بالساعة + إجازات رسمية)، سقوف الوحدات،
/// التداخل الزمني (لا دفع مزدوج)، الصرف والعكس وتناسق المصروف، وحارس السنة المالية.
/// ملاحظة: قاعدة الاختبار مشتركة بين الاختبارات — التوليد ينشئ كشوفًا لكل الموظفين،
/// لذا تأكيدات التوليد تُقيَّد دائمًا بموظف الاختبار نفسه لا بعدّ عالمي.
/// </summary>
[Collection("database")]
public class PayrollTests
{
    private readonly DatabaseFixture _fx;
    public PayrollTests(DatabaseFixture fx) => _fx = fx;

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

    private static async Task<int> SeedEmployeeAsync(SaylDbContext db, string marker,
        enSalaryType type, decimal salary, DateOnly? hire = null, DateOnly? resign = null)
    {
        var emp = new Employee
        {
            Name = $"موظف-{marker}",
            Salary = salary,
            SalaryType = type,
            HireDate = hire,
            ResignDate = resign,
            Status = "active",
            Position = "محاسب"
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        return emp.EmployeeId;
    }

    private static async Task<int> SeedPayrollAsync(SaylDbContext db, int employeeId,
        enSalaryType type, DateOnly from, DateOnly to, decimal rate,
        decimal worked = 0, decimal holiday = 0, string status = "pending")
    {
        // إجمالي أولي منطقي (كما كان سيُحسب) — الصفر يُرفض عند الصرف بلا علاقة بالاختبار
        int totalDays = from.DayNumber - to.DayNumber == 0 ? 1 : to.DayNumber - from.DayNumber + 1;
        decimal gross = type switch
        {
            enSalaryType.daily => rate * (worked + holiday),
            enSalaryType.monthly when totalDays > 0 && worked > 0 && worked < totalDays => rate * worked / totalDays,
            enSalaryType.monthly => rate * worked,
            _ => rate * worked
        };
        var rec = new PayrollRecord
        {
            EmployeeId = employeeId,
            PeriodFrom = from,
            PeriodTo = to,
            SalaryType = type,
            WorkedUnits = worked,
            HolidayUnits = holiday,
            Rate = rate,
            Gross = gross,
            Deductions = 0,
            Additions = 0,
            Net = gross,
            Status = status == "paid" ? enPayrollStatus.paid : enPayrollStatus.pending,
            CreatedAt = DateTime.UtcNow
        };
        db.PayrollRecords.Add(rec);
        await db.SaveChangesAsync();
        return rec.PayrollId;
    }

    private static clsPayrollService NewService(SaylDbContext db)
        => new(db, new clsFiscalYearService(db, new clsSettingsService(db)));

    // ─────────────────────────────────────────────────
    // التوليد — الشهري (Proration) واليومي والساعي
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task Generate_Monthly_ProratesByHireDate()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.monthly, 2800m, hire: new DateOnly(2025, 2, 15));
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            // فبراير 2025 = 28 يومًا؛ التعيين يوم 15 → 14 يوم وجود
            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            Assert.Equal(enSalaryType.monthly, rec.SalaryType);
            Assert.Equal(14m, rec.WorkedUnits);
            Assert.Equal(0m, rec.HolidayUnits);
            Assert.Equal(2800m * 14m / 28m, rec.Gross); // 1400
            Assert.Equal(rec.Gross, rec.Net);
            Assert.Equal(enPayrollStatus.pending, rec.Status);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task Generate_Monthly_ResignationCutsProration()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.monthly, 3000m,
            hire: new DateOnly(2024, 1, 1), resign: new DateOnly(2025, 2, 10));
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            Assert.Equal(10m, rec.WorkedUnits);           // 1..10
            Assert.Equal(1071.43m, rec.Gross);            // 3000×10/28 مقرَّبًا لمنزلتين
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task Generate_Monthly_FullPeriod_PaysFullRate()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.monthly, 2500m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            Assert.Equal(28m, rec.WorkedUnits);
            Assert.Equal(2500m, rec.Gross);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task Generate_Hourly_StartsAtZero()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.hourly, 25m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            Assert.Equal(0m, rec.WorkedUnits);
            Assert.Equal(0m, rec.HolidayUnits);
            Assert.Equal(0m, rec.Net);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // التوليد — الإجازات الرسمية لليومي
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task Generate_Daily_HolidaysCountedDistinct_WeekendHolidayUnpaid()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));

            // 2025-03-02 الأحد .. 03-08 السبت — أيام العمل: الأحد..الخميس (5)
            db.OfficialHolidays.AddRange(
                new OfficialHoliday { Name = $"h1-{marker}", HolidayDate = new DateOnly(2025, 3, 4), DurationDays = 2, IsAnnual = false }, // الثلاثاء+الأربعاء
                new OfficialHoliday { Name = $"h2-{marker}", HolidayDate = new DateOnly(2025, 3, 5), DurationDays = 1, IsAnnual = false }, // يتراكب مع h1
                new OfficialHoliday { Name = $"h3-{marker}", HolidayDate = new DateOnly(2025, 3, 7), DurationDays = 1, IsAnnual = false }  // الجمعة — عطلة أسبوعية
            );
            await db.SaveChangesAsync();

            var service = NewService(db);
            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 2), new DateOnly(2025, 3, 8), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            // 4 مارس + 5 مارس فقط — التراكب لا يُضاعف، والجمعة لا تُدفع
            Assert.Equal(2m, rec.HolidayUnits);
            Assert.Equal(3m, rec.WorkedUnits);   // 5 أيام عمل − يومَي الإجازة
            Assert.Equal(100m * 5m, rec.Gross);  // 3 عمل + 2 إجازة
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task Generate_Daily_AnnualFeb29_NoCrashAndClampedToFeb28()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int leapEmpId = await SeedEmployeeAsync(db, marker + "L", enSalaryType.daily, 100m);
        int nonLeapEmpId = await SeedEmployeeAsync(db, marker + "N", enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2024, 1, 1));

            // إجازة سنوية يوم 29 فبراير — كانت DateOnly ترمي استثناءً في السنوات غير الكبيسة
            db.OfficialHolidays.Add(new OfficialHoliday
            {
                Name = $"feb29-{marker}", HolidayDate = new DateOnly(2024, 2, 29), DurationDays = 1, IsAnnual = true
            });
            await db.SaveChangesAsync();

            var service = NewService(db);

            // سنة كبيسة: 29 فبراير 2024 = خميس → يوم إجازة مدفوع واحد
            var leap = await service.GeneratePayrollsAsync(new DateOnly(2024, 2, 25), new DateOnly(2024, 3, 1), seed.UserId);
            Assert.True(leap.IsSuccess, leap.Message);

            // سنة غير كبيسة: يجب ألا يرمي استثناء — 28 فبراير 2025 جمعة → لا إجازة مدفوعة
            var nonLeap = await service.GeneratePayrollsAsync(new DateOnly(2025, 2, 23), new DateOnly(2025, 3, 1), seed.UserId);
            Assert.True(nonLeap.IsSuccess, nonLeap.Message);

            var leapRec = await db.PayrollRecords.AsNoTracking()
                .SingleAsync(p => p.EmployeeId == leapEmpId && p.PeriodFrom == new DateOnly(2024, 2, 25));
            Assert.Equal(1m, leapRec.HolidayUnits);

            var nonLeapRec = await db.PayrollRecords.AsNoTracking()
                .SingleAsync(p => p.EmployeeId == nonLeapEmpId && p.PeriodFrom == new DateOnly(2025, 2, 23));
            Assert.Equal(0m, nonLeapRec.HolidayUnits);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // التوليد — رفض الفترات غير الصالحة والتداخل
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task Generate_RejectsFuturePeriod()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);

        var service = NewService(db);
        var r = await service.GeneratePayrollsAsync(Today.AddDays(1), Today.AddDays(10), seed.UserId);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Generate_RejectsClosedFiscalYear()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, Today); // كل ما قبله سنة مغلقة

            var service = NewService(db);
            var r = await service.GeneratePayrollsAsync(Today.AddDays(-30), Today.AddDays(-10), seed.UserId);
            Assert.False(r.IsSuccess);
            Assert.False(await db.PayrollRecords.AnyAsync(p => p.EmployeeId == empId));
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    [Fact]
    public async Task Generate_OverlappingPeriod_DoesNotPayTwice()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            // 1..15 ثم 10..20 — تداخل جزئي كان يمر من الفهرس الفريد ويدفع 10..15 مرتين.
            // (القاعدة مشتركة: موظفون بلا كشوف متداخلة قد ينشأ لهم كشف هنا —
            //  المهم أن موظف الاختبار لا يحصل على كشف ثانٍ متداخل)
            var first = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), seed.UserId);
            Assert.True(first.IsSuccess, first.Message);

            await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 20), seed.UserId);

            var afterOverlap = await db.PayrollRecords.AsNoTracking().Where(p => p.EmployeeId == empId).ToListAsync();
            Assert.Single(afterOverlap);
            Assert.Equal(new DateOnly(2025, 3, 1), afterOverlap[0].PeriodFrom);
            Assert.Equal(new DateOnly(2025, 3, 15), afterOverlap[0].PeriodTo);

            // فترة لاحقة غير متداخلة — تمر طبيعيًا
            var next = await service.GeneratePayrollsAsync(new DateOnly(2025, 4, 1), new DateOnly(2025, 4, 10), seed.UserId);
            Assert.True(next.IsSuccess, next.Message);

            var records = await db.PayrollRecords.AsNoTracking()
                .Where(p => p.EmployeeId == empId).OrderBy(p => p.PeriodFrom).ToListAsync();
            Assert.Equal(2, records.Count);
            Assert.Equal(new DateOnly(2025, 4, 1), records[1].PeriodFrom);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // التعديل — التحقق من الوحدات والخصومات
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task Update_RejectsNegativeValues()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-15), Today.AddDays(-1), 100m);

        var service = NewService(db);
        var r = await service.UpdatePayrollAsync(id, -1, 0, 0, 0, null, seed.UserId);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Update_Daily_RejectsUnitsBeyondPeriodLength()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        // فترة 15 يومًا منتهية داخل السنة الحالية
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-16), Today.AddDays(-2), 100m);

        var service = NewService(db);
        var r = await service.UpdatePayrollAsync(id, 14, 2, 0, 0, null, seed.UserId); // 16 > 15 يومًا
        Assert.False(r.IsSuccess);

        var ok = await service.UpdatePayrollAsync(id, 13, 2, 0, 0, null, seed.UserId); // 15 بالضبط
        Assert.True(ok.IsSuccess, ok.Message);
        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(1500m, rec.Gross);
        Assert.Equal(1500m, rec.Net);
    }

    [Fact]
    public async Task Update_Monthly_RejectsPresenceDaysBeyondPeriod()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.monthly, 2800m);
        // فترة 30 يومًا — 31 يوم وجود مرفوض
        int id = await SeedPayrollAsync(db, empId, enSalaryType.monthly, Today.AddDays(-31), Today.AddDays(-2), 2800m, worked: 30);

        var service = NewService(db);
        var r = await service.UpdatePayrollAsync(id, 31, 0, 0, 0, null, seed.UserId);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public async Task Update_Hourly_RejectsHoursBeyondPhysicalCap()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.hourly, 25m);
        // فترة 7 أيام — السقف 24×7 = 168 ساعة
        int id = await SeedPayrollAsync(db, empId, enSalaryType.hourly, Today.AddDays(-8), Today.AddDays(-2), 25m);

        var service = NewService(db);
        var r = await service.UpdatePayrollAsync(id, 169, 0, 0, 0, null, seed.UserId); // > 24×7
        Assert.False(r.IsSuccess);

        var ok = await service.UpdatePayrollAsync(id, 168, 0, 0, 0, null, seed.UserId);
        Assert.True(ok.IsSuccess, ok.Message);
        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(25m * 168m, rec.Net);
    }

    [Fact]
    public async Task Update_RejectsDeductionsExceedingGrossPlusAdditions()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-15), Today.AddDays(-1), 100m, worked: 10);

        var service = NewService(db); // gross = 1000

        var r = await service.UpdatePayrollAsync(id, 10, 0, 1001, 0, null, seed.UserId);
        Assert.False(r.IsSuccess);

        // الإضافات ترفع السقف: 1000 خصم + 50 إضافة → صافي 50
        var ok = await service.UpdatePayrollAsync(id, 10, 0, 1000, 50, null, seed.UserId);
        Assert.True(ok.IsSuccess, ok.Message);
        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(1000m, rec.Gross);
        Assert.Equal(50m, rec.Net);
    }

    [Fact]
    public async Task Update_FailureLeavesRecordUntouched()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-15), Today.AddDays(-1), 100m, worked: 10);

        var service = NewService(db);
        await service.UpdatePayrollAsync(id, 14, 2, 0, 0, null, seed.UserId); // رفض — سقف الفترة

        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(10m, rec.WorkedUnits);
        Assert.Equal(0m, rec.HolidayUnits);
        Assert.Equal(1000m, rec.Net);
    }

    // ─────────────────────────────────────────────────
    // الصرف والعكس — تناسق المصروف
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task Pay_CreatesExpenseExactlyOnce()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-10), Today.AddDays(-5), 100m, worked: 10);

        var service = NewService(db);

        var r = await service.PayPayrollAsync(id, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);

        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(enPayrollStatus.paid, rec.Status);
        Assert.NotNull(rec.PaidAt);
        Assert.NotNull(rec.ExpenseId);

        // مصروف واحد بالضبط — بفئة رواتب وبصمة الكشف ومؤرخ بآخر يوم استحقاق
        var expense = await db.Expenses.AsNoTracking().FirstAsync(e => e.ExpenseId == rec.ExpenseId);
        Assert.Equal(clsPayrollService.SalaryExpenseCategory, expense.Category);
        Assert.Equal(1000m, expense.Amount);
        Assert.Contains($"كشف راتب #{id}", expense.Notes ?? "");
        Assert.Equal(Today.AddDays(-5), expense.ExpenseDate);
        Assert.Equal(1, await db.Expenses.CountAsync(e => e.Notes == $"كشف راتب #{id}"));

        // صرف ثانٍ — يُرفض ولا ينشئ مصروفًا إضافيًا
        var again = await service.PayPayrollAsync(id, seed.UserId);
        Assert.False(again.IsSuccess);
        Assert.Equal(1, await db.Expenses.CountAsync(e => e.Notes == $"كشف راتب #{id}"));
    }

    [Fact]
    public async Task Pay_RejectsZeroNet()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.hourly, 25m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.hourly, Today.AddDays(-10), Today.AddDays(-5), 25m, worked: 0);

        var service = NewService(db);
        var r = await service.PayPayrollAsync(id, seed.UserId);
        Assert.False(r.IsSuccess);
        Assert.Equal(enPayrollStatus.pending, (await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id)).Status);
    }

    [Fact]
    public async Task Unpay_RestoresPendingAndDeletesItsOwnExpense()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-10), Today.AddDays(-5), 100m, worked: 10);

        var service = NewService(db);
        Assert.True((await service.PayPayrollAsync(id, seed.UserId)).IsSuccess);

        // مصروف غير ذي صلة يجب ألا يُمس
        var foreignExpense = new Expense
        {
            Description = "غير ذي صلة",
            Amount = 77m,
            ExpenseDate = Today,
            Category = "اختبار",
            UserId = seed.UserId,
            CreatedAt = DateTime.UtcNow
        };
        db.Expenses.Add(foreignExpense);
        await db.SaveChangesAsync();
        int foreignExpenseId = foreignExpense.ExpenseId; // الهوية بعد الحفظ لا قبله

        var r = await service.UnpayPayrollAsync(id, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);

        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(enPayrollStatus.pending, rec.Status);
        Assert.Null(rec.PaidAt);
        Assert.Null(rec.ExpenseId);
        Assert.False(await db.Expenses.AnyAsync(e => e.Notes == $"كشف راتب #{id}")); // مصروفه حُذف
        Assert.True(await db.Expenses.AnyAsync(e => e.ExpenseId == foreignExpenseId)); // غريب سليم

        // دورة صرف ثانية — مصروف جديد واحد بالضبط
        Assert.True((await service.PayPayrollAsync(id, seed.UserId)).IsSuccess);
        Assert.Equal(1, await db.Expenses.CountAsync(e => e.Notes == $"كشف راتب #{id}"));
    }

    [Fact]
    public async Task Unpay_RejectsForeignLinkedExpense()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int id = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-10), Today.AddDays(-5), 100m, worked: 10);

        // مصروف يدوي لا صلة له بالكشف
        var foreignExpense = new Expense
        {
            Description = "مصروف يدوي",
            Amount = 55m,
            ExpenseDate = Today,
            Category = "إيجار",
            UserId = seed.UserId,
            CreatedAt = DateTime.UtcNow
        };
        db.Expenses.Add(foreignExpense);
        await db.SaveChangesAsync();
        int foreignExpenseId = foreignExpense.ExpenseId; // الهوية بعد الحفظ لا قبله

        // توجيه الكشف يدويًا إلى المصروف الغريب (محاكاة عبث/خطأ يدوي)
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE PayrollRecords SET Status = 'paid', PaidAt = GETDATE(), ExpenseId = {foreignExpenseId} WHERE PayrollId = {id}");

        // سياق جديد — يتجنب الكيان المتتبع بقيمته القديمة قبل التحديث الخام
        await using var db2 = _fx.CreateContext();
        var service = NewService(db2);
        var r = await service.UnpayPayrollAsync(id, seed.UserId);
        Assert.False(r.IsSuccess);

        // المصروف الغريب باقٍ — العكس لا يحذف إلا مصروف بصمة الكشف نفسه
        Assert.True(await db.Expenses.AnyAsync(e => e.ExpenseId == foreignExpenseId));
        var rec = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == id);
        Assert.Equal(enPayrollStatus.paid, rec.Status);
    }

    // ─────────────────────────────────────────────────
    // حارس السنة المالية — تعديل/صرف/عكس كشوف السنوات المغلقة
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task ClosedYearPayroll_UpdateAndPayAndUnpay_AllRejected()
    {
        string marker = $"pr-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int pendingId = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-30), Today.AddDays(-25), 100m, worked: 10);
        int paidId = await SeedPayrollAsync(db, empId, enSalaryType.daily, Today.AddDays(-24), Today.AddDays(-20), 100m, worked: 10, status: "paid");

        try
        {
            await SetFiscalYearStartAsync(db, Today); // السنة القديمة مغلقة الآن

            var service = NewService(db);

            var upd = await service.UpdatePayrollAsync(pendingId, 12, 0, 0, 0, null, seed.UserId);
            Assert.False(upd.IsSuccess);

            var pay = await service.PayPayrollAsync(pendingId, seed.UserId);
            Assert.False(pay.IsSuccess);

            var unpay = await service.UnpayPayrollAsync(paidId, seed.UserId);
            Assert.False(unpay.IsSuccess);

            // لا شيء تغيّر
            var pending = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == pendingId);
            Assert.Equal(enPayrollStatus.pending, pending.Status);
            Assert.Equal(10m, pending.WorkedUnits);
            var paid = await db.PayrollRecords.AsNoTracking().FirstAsync(p => p.PayrollId == paidId);
            Assert.Equal(enPayrollStatus.paid, paid.Status);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }
}
