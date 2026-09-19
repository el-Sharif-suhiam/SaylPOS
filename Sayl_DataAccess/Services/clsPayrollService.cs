using Common.DTOs;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// الرواتب: توليد كشوف الفترة (idempotent عبر فهرس فريد)، حساب المرتب حسب نوعه
    /// (بالساعة/يومي/شهري مع Proration)، الإجازات الرسمية لليومي،
    /// والصرف (guarded transition) الذي ينشئ مصروفاً بفئة 'رواتب' —
    /// فيتدفق تلقائيًا في كل تقارير الأرباح والمصروفات دون تعديل أي تقرير.
    /// </summary>
    /// <summary>
    /// كشوف رواتب: توليد idempotent (فهرس فريد) → تحرير → صرف (guarded) → عكس الصرف (guarded + حارس سنة مالية).
    ///
    /// 📖 VULN-37 — قرار معماري موثق (الشفتات والرواتب):
    /// بيانات الشفتات (EmployeeShifts/DaysOfWeek/CrossesMidnight) هي بيانات
    /// تنظيمية وجدولة فقط — لا تُستهلك في أي حساب رواتب. الرواتب تُحسب من
    /// أيام العمل (عدا عطلة الأسبوع الافتراضية) + الإجازات الرسمية + Proration
    /// التعيين/الاستقالة. أي دمج مستقبلي للشفتات في الحساب (أو الحضور الفعلي)
    /// قرار محاسبي صريح يتطلب مواصفة معتمدة — لا يُلمس ضمن إصلاحات أمنية.
    /// </summary>
    public class clsPayrollService
    {
        private readonly SaylDbContext _dbContext;
        private readonly clsFiscalYearService _fiscalYear;

        public clsPayrollService(SaylDbContext dbContext, clsFiscalYearService fiscalYear)
        {
            _dbContext = dbContext;
            _fiscalYear = fiscalYear;
        }

        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return -1;

            return await _dbContext.Users
                .Where(u => u.UserId == userId && u.IsActive && u.Role.IsActive)
                .Select(u => (int?)u.Role.RoleValue)
                .FirstOrDefaultAsync() ?? -1;
        }

        public const string SalaryExpenseCategory = "رواتب";

        /// <summary>كشوف فترة محددة (كل الموظفين) أو كشوف موظف.</summary>
        public async Task<List<DtoPayrollRecord>> GetPayrollsAsync(DateOnly? periodFrom = null, DateOnly? periodTo = null, int? employeeId = null)
        {
            var query = _dbContext.PayrollRecords
                .Include(p => p.Employee)
                .AsQueryable();

            if (periodFrom.HasValue)
                query = query.Where(p => p.PeriodFrom >= periodFrom.Value);
            if (periodTo.HasValue)
                query = query.Where(p => p.PeriodTo <= periodTo.Value);
            if (employeeId.HasValue)
                query = query.Where(p => p.EmployeeId == employeeId.Value);

            return await query
                .OrderByDescending(p => p.PeriodFrom)
                .ThenBy(p => p.Employee.Name)
                .Select(p => new DtoPayrollRecord
                {
                    PayrollId = p.PayrollId,
                    EmployeeId = p.EmployeeId,
                    EmployeeName = p.Employee.Name,
                    PeriodFrom = p.PeriodFrom,
                    PeriodTo = p.PeriodTo,
                    SalaryType = p.SalaryType,
                    WorkedUnits = p.WorkedUnits,
                    HolidayUnits = p.HolidayUnits,
                    Rate = p.Rate,
                    Gross = p.Gross,
                    Deductions = p.Deductions,
                    Additions = p.Additions,
                    Net = p.Net,
                    Status = p.Status,
                    PaidAt = p.PaidAt,
                    Notes = p.Notes
                })
                .ToListAsync();
        }

        /// <summary>
        /// توليد كشوف فترة لكل الموظفين النشطين (active أو on_leave).
        /// idempotent: الموظف الذي له كشف للفترة نفسها يُتجاوز (الفهرس الفريد يحمي أيضاً من التزامن).
        /// - monthly : Proration تلقائي حسب مدة عمل الموظف داخل الفترة (HireDate/الاستقالة).
        /// - daily   : أيام العمل صفر + الإجازات الرسمية داخل الفترة تُقترح مسبقًا.
        /// - hourly  : ساعات صفر — المحاسب يعبئها.
        /// </summary>
        public async Task<Result<int>> GeneratePayrollsAsync(DateOnly periodFrom, DateOnly periodTo, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Employees))
                return Result<int>.Fail("توليد الرواتب يتطلب صلاحية شاشة الموظفين والرواتب.");

            if (periodTo < periodFrom)
                return Result<int>.Fail("نهاية الفترة يجب أن تكون بعد بدايتها.");

            if (periodTo > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)) /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */)
                return Result<int>.Fail("لا يمكن توليد كشوف لفترة لم تنته بعد.");

            var employees = await _dbContext.Employees
                // المستقيل قبل الفترة لا كشف له (تُرشّحه حالة to < from أدناه) —
                // أما من استقال داخل الفترة فله استحقاق أيام عمله حتى تاريخ استقالته
                .Where(e => e.Status != "resigned" || e.ResignDate == null || e.ResignDate >= periodFrom)
                .ToListAsync();

            if (employees.Count == 0)
                return Result<int>.Fail("لا يوجد موظفون لتوليد كشوف.");

            // الفهرس الفريد UQ_PayrollRecords_Employee_Period يمنع التكرار حتى مع طلبين متوازيين؛
            // نلتقطه ونتجاوز الموظف بدل فشل العملية كاملة
            int created = 0;
            foreach (var emp in employees)
            {
                // نطاق عمل الموظف داخل الفترة (Proration): من HireDate إلى ResignDate أو نهاية الفترة
                var from = periodFrom;
                var to = periodTo;
                if (emp.HireDate.HasValue && emp.HireDate.Value > from)
                    from = emp.HireDate.Value;
                if (emp.ResignDate.HasValue && emp.ResignDate.Value < to)
                    to = emp.ResignDate.Value;
                if (to < from)
                    continue; // لم يُعيَّن بعد أو استقال قبل الفترة

                decimal rate = emp.Salary ?? 0;
                decimal workedUnits = 0, holidayUnits = 0, gross = 0;

                switch (emp.SalaryType)
                {
                    case enSalaryType.monthly:
                    {
                        // Proration: نسبة أيام وجوده الفعلي داخل الفترة
                        int totalDays = periodTo.DayNumber - periodFrom.DayNumber + 1;
                        int activeDays = to.DayNumber - from.DayNumber + 1;
                        gross = rate * activeDays / totalDays;
                        workedUnits = activeDays;
                        break;
                    }
                    case enSalaryType.daily:
                    {
                        // الإجازات الرسمية داخل نطاق عمله تُحسب أيامًا مدفوعة
                        holidayUnits = await CountHolidaysInRangeAsync(from, to);

                        // اقتراح أيام العمل: كل أيام النطاق عدا الجمعة والسبت (عطلة أسبوعية افتراضية)
                        // — المحاسب يرى قيمة منطقية يعدّلها (غياب/استئذان) بدل البدء من صفر
                        workedUnits = CountWorkdaysExcludeWeekend(from, to);

                        // الإجازة الرسمية الواقعة في يوم عطلة أسبوعية لا تُحتسب مرتين
                        workedUnits = Math.Max(0, workedUnits - holidayUnits);

                        gross = rate * (workedUnits + holidayUnits);
                        break;
                    }
                    case enSalaryType.hourly:
                        workedUnits = 0;
                        gross = 0;
                        break;
                }

                var record = new PayrollRecord
                {
                    EmployeeId = emp.EmployeeId,
                    PeriodFrom = periodFrom,
                    PeriodTo = periodTo,
                    SalaryType = emp.SalaryType,
                    WorkedUnits = workedUnits,
                    HolidayUnits = holidayUnits,
                    Rate = rate,
                    Gross = Math.Round(gross, 2),
                    Deductions = 0,
                    Additions = 0,
                    Net = Math.Round(gross, 2),
                    Status = enPayrollStatus.pending,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.PayrollRecords.Add(record);
                try
                {
                    await _dbContext.SaveChangesAsync();
                    created++;
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_PayrollRecords_Employee_Period"))
                {
                    // كشف موجود لهذه الفترة (تزامن أو إعادة توليد) — نتجاوز.
                    // VULN-11: كان catch بلا فلتر يبتلع كل أخطاء القاعدة
                    // (CHECK/FK/مهلة) كـ«موجود مسبقًا» — كشوف تختفي بصمت
                    // مع رسالة نجاح للبقية. الآن: نتجاوز التكرار الحقيقي فقط.
                    _dbContext.ChangeTracker.Clear();
                }
            }

            if (created == 0)
                return Result<int>.Fail("كل الموظفين لديهم كشوف لهذه الفترة بالفعل.");

            clsAudit.Log(_dbContext, actorUserId, "GeneratePayrolls",
                $"توليد {created} كشف رواتب للفترة {periodFrom:yyyy-MM-dd} ← {periodTo:yyyy-MM-dd}");
            await _dbContext.SaveChangesAsync();

            return Result<int>.Ok(created, $"تم توليد {created} كشف رواتب.");
        }

        /// <summary>تعديل كشف معلق: وحدات العمل + الخصومات والإضافات — يُعاد حساب الإجمالي والصافي.</summary>
        public async Task<Result> UpdatePayrollAsync(int payrollId, decimal workedUnits, decimal holidayUnits,
            decimal deductions, decimal additions, string? notes, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Employees))
                return Result.Fail("تعديل الرواتب يتطلب صلاحية شاشة الموظفين والرواتب.");

            if (workedUnits < 0 || holidayUnits < 0 || deductions < 0 || additions < 0)
                return Result.Fail("القيم يجب أن تكون صفراً أو أكثر.");

            var record = await _dbContext.PayrollRecords.FindAsync(payrollId);
            if (record == null)
                return Result.Fail("الكشف غير موجود.");

            if (record.Status == enPayrollStatus.paid)
                return Result.Fail("لا يمكن تعديل كشف مصروف.");

            // حماية السنة المالية: كشوف ما قبل السنة الحالية مؤرشفة — لا تعديل
            if (await _fiscalYear.IsBeforeCurrentFiscalYearAsync(record.PeriodFrom))
                return Result.Fail("كشف من سنة مالية مغلقة — لا يمكن تعديله (مؤرشف).");

            record.WorkedUnits = workedUnits;
            record.HolidayUnits = holidayUnits;
            record.Deductions = deductions;
            record.Additions = additions;
            if (notes != null)
                record.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            Recalc(record);
            await _dbContext.SaveChangesAsync();

            clsAudit.Log(_dbContext, actorUserId, "UpdatePayroll",
                $"تعديل كشف راتب #{payrollId} — الصافي: {record.Net:N2}");
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تحديث الكشف.");
        }

        /// <summary>إعادة حساب الإجمالي والصافي حسب نوع الراتب (الشهري يحترم Proration المحفوظ في WorkedUnits).</summary>
        private static void Recalc(PayrollRecord r)
        {
            decimal gross = r.SalaryType switch
            {
                enSalaryType.daily => r.Rate * (r.WorkedUnits + r.HolidayUnits),
                enSalaryType.hourly => r.Rate * r.WorkedUnits,
                _ => r.Rate * r.WorkedUnits   // monthly: WorkedUnits = أيام المدة الفعلية (Proration عند التوليد)
            };

            // إجمالي الفترة الكاملة = PeriodTo-PeriodFrom+1 — نعيد اشتقاقه من الطول الفعلي للفترة
            if (r.SalaryType == enSalaryType.monthly)
            {
                int totalDays = r.PeriodTo.DayNumber - r.PeriodFrom.DayNumber + 1;
                // WorkedUnits هنا أيام الوجود الفعلي (HireDate..ResignDate داخل الفترة)
                if (totalDays > 0 && r.WorkedUnits > 0 && r.WorkedUnits < totalDays)
                    gross = r.Rate * r.WorkedUnits / totalDays;
                else if (r.WorkedUnits >= totalDays)
                    gross = r.Rate; // كامل الفترة
            }

            r.Gross = Math.Round(gross, 2);
            decimal net = r.Gross - r.Deductions + r.Additions;
            r.Net = Math.Round(net < 0 ? 0 : net, 2); // CHECK Net >= 0
        }

        /// <summary>
        /// صرف كشف: guarded transition (pending → paid) داخل معاملة —
        /// ينشئ مصروفاً بفئة 'رواتب' مرتبطاً بالكشف فيظهر تلقائيًا في كل التقارير.
        /// </summary>
        public async Task<Result> PayPayrollAsync(int payrollId, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Employees))
                return Result.Fail("صرف الرواتب يتطلب صلاحية شاشة الموظفين والرواتب.");

            var record = await _dbContext.PayrollRecords.FindAsync(payrollId);
            if (record == null)
                return Result.Fail("الكشف غير موجود.");

            if (record.Status != enPayrollStatus.pending)
                return Result.Fail("الكشف مصروف بالفعل.");

            if (record.Net <= 0)
                return Result.Fail("الصافي يجب أن يكون أكبر من صفر قبل الصرف (قيد المصروفات يرفض صفراً).");

            if (await _fiscalYear.IsBeforeCurrentFiscalYearAsync(record.PeriodTo))
                return Result.Fail("كشف من سنة مالية مغلقة — لا يمكن صرفه (مؤرشف).");

            var employee = await _dbContext.Employees.FindAsync(record.EmployeeId);
            if (employee == null)
                return Result.Fail("الموظف غير موجود.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // انتقال ذري داخل المعاملة: يمنع صرفًا مزدوجًا متوازيًا
                int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE PayrollRecords SET Status = 'paid' WHERE PayrollId = {payrollId} AND Status = 'pending'");
                if (claimed == 0)
                    return Result.Fail("الكشف مصروف بالفعل أو قيد الصرف الآن.");

                var expense = new Expense
                {
                    Description = $"راتب {employee.Name} — {record.PeriodFrom:yyyy-MM-dd} إلى {record.PeriodTo:yyyy-MM-dd}",
                    Amount = record.Net,
                    // نسَب المصروف لآخر يوم في فترة الاستحقاق — كل تقرير شهري يحمل رواتبه الصحيحة،
                    // لا شهر الصرف الفعلي (صرف رواتب يوليو في 31 أغسطس يظل يُحسب على يوليو)
                    ExpenseDate = record.PeriodTo,
                    Category = SalaryExpenseCategory,
                    Notes = $"كشف راتب #{record.PayrollId}",
                    UserId = actorUserId,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Expenses.Add(expense);
                await _dbContext.SaveChangesAsync(); // لاستخراج ExpenseId

                record.Status = enPayrollStatus.paid;
                record.PaidAt = DateTime.UtcNow;
                record.ExpenseId = expense.ExpenseId;

                clsAudit.Log(_dbContext, actorUserId, "PayPayroll",
                    $"صرف راتب {employee.Name} — كشف #{payrollId} — الصافي: {record.Net:N2} — مصروف #{expense.ExpenseId}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok($"تم صرف راتب {employee.Name} بمبلغ {record.Net:N2} وتسجيله في المصروفات.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                string details = ex.Message;
                var inner = ex.InnerException;
                while (inner != null)
                {
                    details += " ← " + inner.Message;
                    inner = inner.InnerException;
                }
                return Result.Fail($"فشل صرف الراتب ({details}).");
            }
        }

        /// <summary>
        /// عكس صرف كشف (خطأ بشري): guarded عكسي (paid → pending) داخل معاملة —
        /// يرد الكشف معلقًا ويحذف المصروف المرتبط به فيرجع الربح الشهري لحالته.
        /// </summary>
        public async Task<Result> UnpayPayrollAsync(int payrollId, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Employees))
                return Result.Fail("عكس صرف الرواتب يتطلب صلاحية شاشة الموظفين والرواتب.");

            var record = await _dbContext.PayrollRecords.FindAsync(payrollId);
            if (record == null)
                return Result.Fail("الكشف غير موجود.");

            if (record.Status != enPayrollStatus.paid)
                return Result.Fail("الكشف ليس مصروفًا.");

            if (record.ExpenseId == null)
                return Result.Fail("لا يوجد مصروف مرتبط بالكشف — استرجع الأمر يدويًا مع المحاسب.");

            // VULN-12: حارس السنة المالية — عكس الصرف يحذف مصروفًا مؤرخًا
            // في الفترة، وعكس كشف من سنة مقفلة يعدل ربح سنة مجمّدة بأثر
            // رجعي (نفس الحارس الذي يملك التعديل — كان سهمًا منسيًا هنا).
            if (await _fiscalYear.IsBeforeCurrentFiscalYearAsync(record.PeriodFrom))
                return Result.Fail("كشف من سنة مالية مغلقة — لا يمكن عكس صرفه (مؤرشف).");

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // انتقال ذري عكسي داخل المعاملة: يمنع عكسًا وصرفًا متوازيين على نفس الكشف
                int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE PayrollRecords SET Status = 'pending' WHERE PayrollId = {payrollId} AND Status = 'paid'");
                if (claimed == 0)
                    return Result.Fail("الكشف قيد المعالجة من طرف آخر.");

                var expense = await _dbContext.Expenses.FindAsync(record.ExpenseId.Value);
                if (expense != null)
                    _dbContext.Expenses.Remove(expense);

                record.Status = enPayrollStatus.pending;
                record.PaidAt = null;
                record.ExpenseId = null;

                clsAudit.Log(_dbContext, actorUserId, "UnpayPayroll",
                    $"عكس صرف كشف راتب #{payrollId} — إرجاع {record.Net:N2} لمعلق وحذف مصروفه");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم عكس الصرف — الكشف معلق من جديد ومصروفه حُذف.");
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail("فشل عكس الصرف.");
            }
        }

        /// <summary>VULN-11: هل الاستثناء انتهاك قيد الفريد المحدد فعلًا؟
        /// (نفس فكرة clsPurchaseService — تجاوز «موجود مسبقًا» للتكرار الحقيقي فقط).</summary>
        private static bool IsUniqueConstraintViolation(DbUpdateException ex, string constraintName)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is Microsoft.Data.SqlClient.SqlException sql
                    && sql.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>عدد أيام النطاق عدا الجمعة والسبت (اقتراح أيام العمل لليومي — قابل للتعديل يدويًا).</summary>
        private static decimal CountWorkdaysExcludeWeekend(DateOnly from, DateOnly to)
        {
            decimal count = 0;
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var dow = day.DayOfWeek;
                if (dow != DayOfWeek.Friday && dow != DayOfWeek.Saturday)
                    count++;
            }
            return count;
        }

        /// <summary>عدد أيام الإجازات الرسمية المدفوعة داخل نطاق (نسخة داخلية بدون استدعاء خدمة أخرى).</summary>
        private async Task<decimal> CountHolidaysInRangeAsync(DateOnly from, DateOnly to)
        {
            var holidays = await _dbContext.OfficialHolidays.ToListAsync();
            decimal total = 0;
            foreach (var h in holidays)
            {
                var start = h.IsAnnual ? new DateOnly(from.Year, h.HolidayDate.Month, h.HolidayDate.Day)
                                       : h.HolidayDate;
                for (int i = 0; i < h.DurationDays; i++)
                {
                    var day = start.AddDays(i);
                    if (day >= from && day <= to)
                        total++;
                }
            }
            return total;
        }
    }
}
