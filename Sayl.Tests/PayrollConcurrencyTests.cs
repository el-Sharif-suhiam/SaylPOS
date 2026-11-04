using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// حماية التداخل الزمني لكشوف الرواتب ضد التزامن:
/// فترات متطابقة/متداخلة جزئيًا/محتواة/متجاورة/موظفون مختلفون،
/// ثم طلبان متوازيان حقيقيان (اتصالان وسياقان مستقلان Task.WhenAll)
/// على نفس الموظف — ضد SQL Server فعلي: واحد فقط يمر، والفاشل لا يترك أثرًا.
/// الحماية: فحص التداخل + الحفظ داخل معاملة Serializable بقفل تطبيقي (sp_getapplock).
/// ملاحظة القاعدة المشتركة: التوليد ينشئ كشوفًا لكل الموظفين —
/// تأكيدات التوليد تُقيَّد دائمًا بموظف الاختبار نفسه لا بعدّ عالمي.
/// </summary>
[Collection("database")]
public class PayrollConcurrencyTests
{
    private readonly DatabaseFixture _fx;
    public PayrollConcurrencyTests(DatabaseFixture fx) => _fx = fx;

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
    // تسلسلي: أشكال التداخل الأربعة + التجاور + موظفون مختلفون
    // ─────────────────────────────────────────────────

    // ══ 1) فترتان متطابقتان تمامًا: الثانية تُرفض (idempotent) ولا تكرار ══
    [Fact]
    public async Task Generate_SecondIdenticalPeriod_Rejected_NoDuplicate()
    {
        string marker = $"pc1-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var first = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), seed.UserId);
            Assert.True(first.IsSuccess, first.Message);

            var second = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), seed.UserId);
            Assert.False(second.IsSuccess);

            var records = await db.PayrollRecords.AsNoTracking().Where(p => p.EmployeeId == empId).ToListAsync();
            Assert.Single(records);
            Assert.Equal(new DateOnly(2025, 3, 1), records[0].PeriodFrom);
            Assert.Equal(new DateOnly(2025, 3, 15), records[0].PeriodTo);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ══ 2) فترة جزئية التداخل (10..20 داخل 1..31): تُرفض ══
    [Fact]
    public async Task Generate_PartiallyOverlappingPeriod_Rejected()
    {
        string marker = $"pc2-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        int existingId = await SeedPayrollAsync(db, empId, enSalaryType.daily,
            new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), 100m, worked: 20);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 20), seed.UserId);
            Assert.False(r.IsSuccess);

            // الكشف الأصلي سليم بلا أثر للطلب الفاشل
            var rec = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == empId);
            Assert.Equal(existingId, rec.PayrollId);
            Assert.Equal(20m, rec.WorkedUnits);
            Assert.Equal(2000m, rec.Net);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ══ 3) فترة محتواة كليًا داخل كشف قائم: تُرفض ══
    [Fact]
    public async Task Generate_ContainedPeriod_Rejected()
    {
        string marker = $"pc3-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        await SeedPayrollAsync(db, empId, enSalaryType.daily,
            new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), 100m, worked: 25);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 20), seed.UserId);
            Assert.False(r.IsSuccess);
            Assert.Equal(1, await db.PayrollRecords.AsNoTracking().CountAsync(p => p.EmployeeId == empId));
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ══ 4) فترتان متجاورتان (1..15 ثم 16..31): كلتاهما تمر — التجاور ليس تداخلًا ══
    [Fact]
    public async Task Generate_AdjacentPeriods_BothSucceed()
    {
        string marker = $"pc4-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var first = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), seed.UserId);
            Assert.True(first.IsSuccess, first.Message);

            var second = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 16), new DateOnly(2025, 3, 31), seed.UserId);
            Assert.True(second.IsSuccess, second.Message);

            var records = await db.PayrollRecords.AsNoTracking()
                .Where(p => p.EmployeeId == empId).OrderBy(p => p.PeriodFrom).ToListAsync();
            Assert.Equal(2, records.Count);
            Assert.Equal(new DateOnly(2025, 3, 15), records[0].PeriodTo);
            Assert.Equal(new DateOnly(2025, 3, 16), records[1].PeriodFrom);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ══ 5) موظفان مختلفان بنفس الفترة المتداخلة: كلتاهما تمر — القيد لكل موظف ══
    [Fact]
    public async Task Generate_DifferentEmployees_OverlappingPeriods_BothSucceed()
    {
        string marker = $"pc5-{Guid.NewGuid():N}"[..10];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int emp1 = await SeedEmployeeAsync(db, marker + "A", enSalaryType.daily, 100m);
        int emp2 = await SeedEmployeeAsync(db, marker + "B", enSalaryType.daily, 100m);
        try
        {
            await SetFiscalYearStartAsync(db, new DateOnly(2025, 1, 1));
            var service = NewService(db);

            var r = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), seed.UserId);
            Assert.True(r.IsSuccess, r.Message);

            // نفس الفترة لموظف جديد — لا علاقة لكشوف موظف بآخر
            int emp3 = await SeedEmployeeAsync(db, marker + "C", enSalaryType.daily, 100m);
            var r2 = await service.GeneratePayrollsAsync(new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 20), seed.UserId);
            Assert.True(r2.IsSuccess, r2.Message);

            Assert.Equal(1, await db.PayrollRecords.AsNoTracking().CountAsync(p => p.EmployeeId == emp1));
            Assert.Equal(1, await db.PayrollRecords.AsNoTracking().CountAsync(p => p.EmployeeId == emp2));
            Assert.Equal(1, await db.PayrollRecords.AsNoTracking().CountAsync(p => p.EmployeeId == emp3));
            var rec3 = await db.PayrollRecords.AsNoTracking().SingleAsync(p => p.EmployeeId == emp3);
            Assert.Equal(new DateOnly(2025, 3, 10), rec3.PeriodFrom);
            Assert.Equal(new DateOnly(2025, 3, 20), rec3.PeriodTo);
        }
        finally
        {
            await ResetFiscalYearStartAsync(db);
        }
    }

    // ─────────────────────────────────────────────────
    // تزامن حقيقي: طلبان متوازيان (اتصالان وسياقان مستقلان) على نفس الموظف
    // ─────────────────────────────────────────────────

    // ══ 6) طلبان متوازيان بنفس الفترة: واحد فقط ينجح — لا تكرار ══
    [Fact]
    public async Task ConcurrentGenerate_SameEmployee_IdenticalPeriods_OnlyOneSucceeds()
    {
        string marker = $"pc6-{Guid.NewGuid():N}"[..10];
        int userId, empId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
            empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        }

        try
        {
            await using (var setup = _fx.CreateContext())
            {
                await SetFiscalYearStartAsync(setup, new DateOnly(2025, 1, 1));
            }

            // سياقان واتصالان مستقلان تمامًا — تنافس حقيقي على SQL Server
            await using var db1 = _fx.CreateContext();
            await using var db2 = _fx.CreateContext();

            var results = await Task.WhenAll(
                NewService(db1).GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), userId),
                NewService(db2).GeneratePayrollsAsync(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 15), userId));

            Assert.Equal(1, results.Count(r => r.IsSuccess)); // واحد فقط مرّ

            await using var verify = _fx.CreateContext();
            var records = await verify.PayrollRecords.AsNoTracking().Where(p => p.EmployeeId == empId).ToListAsync();
            Assert.Single(records); // لا تكرار رغم التوازي
            Assert.Equal(new DateOnly(2025, 3, 1), records[0].PeriodFrom);
            Assert.Equal(new DateOnly(2025, 3, 15), records[0].PeriodTo);
        }
        finally
        {
            await using var cleanup = _fx.CreateContext();
            await ResetFiscalYearStartAsync(cleanup);
        }
    }

    // ══ 7) طلبان متوازيان بفترتين متداخلتين جزئيًا (1..15 ضد 10..20):
    //     كان كلاهما يمرّ (الفهرس الفريد يمنع التطابق فقط) ويدفع 10..15 مرتين —
    //     الآن واحد فقط ينجح، والفاشل لا يترك أي أثر ══
    [Fact]
    public async Task ConcurrentGenerate_SameEmployee_PartiallyOverlappingPeriods_OnlyOneSucceeds_NoSideEffects()
    {
        string marker = $"pc7-{Guid.NewGuid():N}"[..10];
        int userId, empId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
            empId = await SeedEmployeeAsync(db, marker, enSalaryType.daily, 100m);
        }

        var periodA = (From: new DateOnly(2025, 3, 1), To: new DateOnly(2025, 3, 15));
        var periodB = (From: new DateOnly(2025, 3, 10), To: new DateOnly(2025, 3, 20));

        try
        {
            await using (var setup = _fx.CreateContext())
            {
                await SetFiscalYearStartAsync(setup, new DateOnly(2025, 1, 1));
            }

            await using var db1 = _fx.CreateContext();
            await using var db2 = _fx.CreateContext();

            var results = await Task.WhenAll(
                NewService(db1).GeneratePayrollsAsync(periodA.From, periodA.To, userId),
                NewService(db2).GeneratePayrollsAsync(periodB.From, periodB.To, userId));

            Assert.Equal(1, results.Count(r => r.IsSuccess)); // تعارض: واحد فقط يمرّ
            Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Message)));

            await using var verify = _fx.CreateContext();
            var records = await verify.PayrollRecords.AsNoTracking().Where(p => p.EmployeeId == empId).ToListAsync();

            // كشف واحد فقط، وهو أحد الفترتين المطلوبتين — الفاشل لم يحقن شيئًا
            Assert.Single(records);
            Assert.True(
                (records[0].PeriodFrom == periodA.From && records[0].PeriodTo == periodA.To) ||
                (records[0].PeriodFrom == periodB.From && records[0].PeriodTo == periodB.To),
                "الكشف الناجي يجب أن يطابق أحد الفترتين المطلوبتين تمامًا");
            Assert.Equal(100m, records[0].Rate);
            // الحساب سليم للفائز: أجر أيام العمل (بدون الجمعة/السبت) × 100
            decimal expectedNet = records[0].PeriodTo == periodA.To ? 1000m : 800m;
            Assert.Equal(expectedNet, records[0].Net);
        }
        finally
        {
            await using var cleanup = _fx.CreateContext();
            await ResetFiscalYearStartAsync(cleanup);
        }
    }
}
