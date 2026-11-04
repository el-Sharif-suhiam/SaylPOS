using Common.DTOs;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

        /// <summary>حد العمود DECIMAL(10,2) — سقف أي وحدة أو مبلغ يُدخل يدويًا.</summary>
        public const decimal MaxAmount = 99_999_999m;

        /// <summary>الحد الفيزيائي لساعات العمل في اليوم الواحد (سقف تحقق لا قاعدة حضور).</summary>
        public const decimal MaxDailyHours = 24m;

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
        /// idempotent: الموظف الذي له كشف متداخل مع الفترة يُتجاوز.
        /// التزامن: معاملة Serializable + قفل تطبيقي (sp_getapplock) تُسلسلان
        /// فحص التداخل مع الحفظ — لا فحص مسروق بين طلبين متوازيين.
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

            // حماية السنة المالية: فترة انتهت قبل بداية السنة الحالية = سنة مغلقة مؤرشفة —
            // لا تُولَّد لها كشوف (نفس حارس التعديل/الصرف/العكس أدناه)
            if (await _fiscalYear.IsBeforeCurrentFiscalYearAsync(periodTo))
                return Result<int>.Fail("الفترة تسبق بداية السنة المالية الحالية — سنة مغلقة لا تُولَّد لها كشوف.");

            // ══ حماية التزامن (check-then-insert): ══
            // فحص التداخل السابق كان قراءة عادية خارج أي معاملة — طلبان متوازيان
            // لفترتين متداخلتين جزئيًا (1–15 و10–20) يقرآن معًا «لا تداخل» ثم
            // يححقنان معًا: الفهرس الفريد UQ_PayrollRecords_Employee_Period
            // يمنع التطابق التام فقط، فيمر التداخل الجزئي ويدفع نفس الأيام مرتين.
            // العلاج (نمط StartSessionAsync نفسه): معاملة Serializable + قفل تطبيقي
            // مسمّى sp_getapplock — يُسلسِل التوليدَين: الثاني لا يبدأ فحص التداخل
            // إلا بعد التزام الأول (أو رفضه)، فلا «فحص مسروق» ولا deadlock.
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            // قفل تطبيقي مسمّى بمالك المعاملة — التوليد عملية دفعية نادرة، التسلسل الكامل بين طلبين لا كلفة تُذكر
            var lockConn = (Microsoft.Data.SqlClient.SqlConnection)_dbContext.Database.GetDbConnection();
            int lockResult;
            using (var cmd = lockConn.CreateCommand())
            {
                cmd.Transaction = (Microsoft.Data.SqlClient.SqlTransaction)_dbContext.Database.CurrentTransaction!.GetDbTransaction();
                cmd.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'PayrollGenerate', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r";
                lockResult = (int)(await cmd.ExecuteScalarAsync())!;
            }

            if (lockResult < 0)
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("ازدحام في توليد الرواتب — طلب آخر قيد التنفيذ، حاول بعد لحظات.");
            }

            var employees = await _dbContext.Employees
                // المستقيل قبل الفترة لا كشف له (تُرشّحه حالة to < from أدناه) —
                // أما من استقال داخل الفترة فله استحقاق أيام عمله حتى تاريخ استقالته
                .Where(e => e.Status != "resigned" || e.ResignDate == null || e.ResignDate >= periodFrom)
                .ToListAsync();

            if (employees.Count == 0)
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("لا يوجد موظفون لتوليد كشوف.");
            }

            // ⚠ حماية الدفع المزدوج: نرفض أي تداخل زمني مع كشف قائم للموظف —
            // القراءة داخل قفل التطبيق + Serializable، فالمدى المقروء محمي من
            // إقحام كشوف متداخلة حتى الالتزام (idempotent: المتداخل يُتجاوز لا يُخطأ).
            var employeesWithOverlap = (await _dbContext.PayrollRecords
                .Where(r => r.PeriodFrom <= periodTo && r.PeriodTo >= periodFrom)
                .Select(r => r.EmployeeId)
                .ToListAsync()).ToHashSet();

            var holidays = await _dbContext.OfficialHolidays.ToListAsync();

            // الفحص والحفظ داخل القفل نفسه — لا فجوة بين «لا تداخل» و«حقن»
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

                if (employeesWithOverlap.Contains(emp.EmployeeId))
                    continue; // له كشف متداخل مع هذه الفترة — لا دفع مزدوج لنفس الأيام (idempotent)

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
                        // الإجازات الرسمية المدفوعة داخل نطاق عمله — أيام مميزة (HashSet):
                        // تداخل إجازتين في اليوم نفسه كان يُحسب مرتين، والإجازة السنوية
                        // الواقعة 29 فبراير كانت ترمي استثناءً في السنوات غير الكبيسة.
                        // الإجازة الواقعة في عطلة أسبوعية (الجمعة/السبت) لا تُدفع —
                        // قاعدة الكود الأصلي «لا تُحتسب مرتين».
                        holidayUnits = GetPaidHolidayDays(holidays, from, to).Count;

                        // اقتراح أيام العمل: كل أيام النطاق عدا الجمعة والسبت (عطلة أسبوعية افتراضية)
                        // — المحاسب يرى قيمة منطقية يعدّلها (غياب/استئذان) بدل البدء من صفر.
                        // يوم الإجازة الرسمية على يوم عمل ليس يوم عمل — يُخصم من أيام العمل
                        // (الجمع القديم طرح الإجازات الأسبوعية أيضًا فأظهر وحدات مضللة).
                        workedUnits = Math.Max(0, CountWorkdaysExcludeWeekend(from, to) - holidayUnits);

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
                created++;
            }

            if (created == 0)
            {
                await transaction.RollbackAsync();
                // رسالة الصراع تُظهر الفترة المسببة — يعرف المحاسب أي فترة تصادمت
                return Result<int>.Fail(
                    $"كل الموظفين لديهم كشوف تتداخل مع الفترة {periodFrom:yyyy-MM-dd} ← {periodTo:yyyy-MM-dd} بالفعل.");
            }

            clsAudit.Log(_dbContext, actorUserId, "GeneratePayrolls",
                $"توليد {created} كشف رواتب للفترة {periodFrom:yyyy-MM-dd} ← {periodTo:yyyy-MM-dd}");
            try
            {
                // حفظ واحد بعد الحلقة داخل معاملة الفحص نفسها — التداخل لا يُحقن بين فحصين متوازيين
                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_PayrollRecords_Employee_Period"))
            {
                // دفاعي (القفل التطبيقي يجب أن يمنع وصولنا إلى هنا): تكرار تام لفترة موظف —
                // نعاملها كأن الكل موجود (idempotent)
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result<int>.Fail(
                    $"كل الموظفين لديهم كشوف تتداخل مع الفترة {periodFrom:yyyy-MM-dd} ← {periodTo:yyyy-MM-dd} بالفعل.");
            }

            return Result<int>.Ok(created, $"تم توليد {created} كشف رواتب."); // النجاح بعد الالتزام فقط
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

            // ══ التحقق من سقف الوحدات حسب نوع الراتب ══
            // (الحد فيزيائي/تقويمي لا قاعدة حضور — قيم الوحدات تُعبأ يدويًا)
            int periodDays = record.PeriodTo.DayNumber - record.PeriodFrom.DayNumber + 1;
            switch (record.SalaryType)
            {
                case enSalaryType.monthly:
                    // WorkedUnits = أيام الوجود الفعلي داخل الفترة (Proration) — لا تتجاوز طولها
                    if (workedUnits > periodDays)
                        return Result.Fail($"أيام الوجود ({workedUnits:0.##}) تتجاوز طول الفترة ({periodDays} يوم).");
                    break;

                case enSalaryType.daily:
                    // أيام العمل + الإجازات لا تتجاوز أيام الفترة — لا أجر لأيام غير موجودة
                    if (workedUnits + holidayUnits > periodDays)
                        return Result.Fail($"مجموع أيام العمل والإجازات ({workedUnits + holidayUnits:0.##}) يتجاوز طول الفترة ({periodDays} يوم).");
                    break;

                case enSalaryType.hourly:
                    // سقف فيزيائي فقط: 24 ساعة كحد أقصى لكل يوم في الفترة
                    if (workedUnits > periodDays * MaxDailyHours)
                        return Result.Fail($"ساعات العمل ({workedUnits:0.##}) تتجاوز الحد الأقصى للفترة ({periodDays * MaxDailyHours:0.#} ساعة = 24×{periodDays} يوم).");
                    break;
            }

            // سقف المقادير: حدود العمود DECIMAL(10,2) — لا فيض قاعدة بيانات بقيمة شاذة
            if (workedUnits > MaxAmount || holidayUnits > MaxAmount || deductions > MaxAmount || additions > MaxAmount)
                return Result.Fail($"القيمة تتجاوز الحد الأقصى المسموح ({MaxAmount:N0}).");

            // الخصومات تُحقَّن قبل الحفظ: لا صافٍ سالب بصمت (كان يُقصَّص إلى صفر دون إخبار)
            decimal grossAfterEdit = ComputeGross(record.SalaryType, record.Rate, workedUnits, holidayUnits, record.PeriodFrom, record.PeriodTo);
            if (deductions > grossAfterEdit + additions)
                return Result.Fail($"الخصومات ({deductions:0.##}) تتجاوز الإجمالي زائد الإضافات ({grossAfterEdit + additions:0.##}) — الصافي لا يكون سالباً.");

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

        /// <summary>حساب الإجمالي حسب نوع الراتب (دالّة نقية — للتحقق قبل أي تعديل للحالة).
        /// الشهري يحترم Proration المحفوظ في WorkedUnits.</summary>
        private static decimal ComputeGross(enSalaryType salaryType, decimal rate, decimal workedUnits,
            decimal holidayUnits, DateOnly periodFrom, DateOnly periodTo)
        {
            decimal gross = salaryType switch
            {
                enSalaryType.daily => rate * (workedUnits + holidayUnits),
                enSalaryType.hourly => rate * workedUnits,
                _ => rate * workedUnits   // monthly: WorkedUnits = أيام المدة الفعلية (Proration عند التوليد)
            };

            // إجمالي الفترة الكاملة = PeriodTo-PeriodFrom+1 — نعيد اشتقاقه من الطول الفعلي للفترة
            if (salaryType == enSalaryType.monthly)
            {
                int totalDays = periodTo.DayNumber - periodFrom.DayNumber + 1;
                // WorkedUnits هنا أيام الوجود الفعلي (HireDate..ResignDate داخل الفترة)
                if (totalDays > 0 && workedUnits > 0 && workedUnits < totalDays)
                    gross = rate * workedUnits / totalDays;
                else if (totalDays > 0 && workedUnits >= totalDays)
                    gross = rate; // كامل الفترة
            }

            return gross;
        }

        /// <summary>إعادة حساب الإجمالي والصافي حسب نوع الراتب.</summary>
        private static void Recalc(PayrollRecord r)
        {
            r.Gross = Math.Round(ComputeGross(r.SalaryType, r.Rate, r.WorkedUnits, r.HolidayUnits, r.PeriodFrom, r.PeriodTo), 2);
            decimal net = r.Gross - r.Deductions + r.Additions;
            r.Net = Math.Round(net < 0 ? 0 : net, 2); // CHECK Net >= 0 — الخصومات تُرفض قبل الحفظ في Update
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

            // دفاعي: كشف معلق لا يحمل مصروفًا — مرجع يتيم يعني مصروفًا قائمًا بالفعل،
            // والصرف هنا سيُنشئ مصروفًا ثانيًا لنفس الكشف (دفع مزدوج محاسبي)
            if (record.ExpenseId != null)
                return Result.Fail("الكشف يحمل مرجع مصروف رغم حالته المعلقة — عالج الأمر يدويًا مع المحاسب قبل الصرف.");

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
                {
                    // العكس يحذف مصروف هذا الكشف وحده — المرجع الموجَّه يدويًا إلى
                    // مصروف آخر (Notes لا تحمل بصمة الكشف) لا يُحذف تلقائيًا
                    string expectedMarker = $"كشف راتب #{record.PayrollId}";
                    if (expense.Notes == null || !expense.Notes.Contains(expectedMarker, StringComparison.Ordinal))
                        return Result.Fail("المصروف المرتبط لا يطابق هذا الكشف — لم يُحذف شيء، راجع المحاسب.");

                    _dbContext.Expenses.Remove(expense);
                }

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

        /// <summary>
        /// أيام الإجازات الرسمية المدفوعة داخل نطاق — قائمة أيام مميزة لا مجموع:
        /// 1) تداخل إجازتين في اليوم نفسه يُحسب مرة واحدة (كان يُحسب مرتين).
        /// 2) الإجازة السنوية الواقعة 29 فبراير تُثبَّت على 28 فبراير في السنوات غير
        ///    الكبيسة (كانت DateOnly ترمي ArgumentOutOfRange فتُفشل التوليد كله).
        /// 3) الإجازة السنوية الممتدة عبر رأس السنة (30 ديسمبر ← 2 يناير) تُفحص
        ///    بسنةَي البدء المرشحتين حتى يُحتسب الامتداد داخل الفترة.
        /// 4) الإجازة الواقعة على عطلة أسبوعية (الجمعة/السبت) مستبعدة — لا تُدفع مرتين.
        /// 📖 قاعدة عمل موثقة: إجازة يوم عطلة أسبوعية لا تُضاف كأجرة — سلوك الكود الأصلي.
        /// </summary>
        private static List<DateOnly> GetPaidHolidayDays(List<OfficialHoliday> holidays, DateOnly from, DateOnly to)
        {
            var days = new HashSet<DateOnly>();
            foreach (var h in holidays)
            {
                if (h.DurationDays <= 0)
                    continue;

                // سنوات البدء المرشحة: السنة نفسها والسنة السابقة (امتداد رأس السنة)
                int clampDay = h.IsAnnual && h.HolidayDate.Month == 2 && h.HolidayDate.Day == 29 ? 28 : h.HolidayDate.Day;
                IEnumerable<int> candidateYears = h.IsAnnual
                    ? new[] { from.Year - 1, from.Year }
                    : new[] { h.HolidayDate.Year };

                foreach (int year in candidateYears)
                {
                    var start = new DateOnly(year, h.HolidayDate.Month, clampDay);
                    for (int i = 0; i < h.DurationDays; i++)
                    {
                        var day = start.AddDays(i);
                        if (day < from || day > to)
                            continue;
                        if (day.DayOfWeek == DayOfWeek.Friday || day.DayOfWeek == DayOfWeek.Saturday)
                            continue;
                        days.Add(day);
                    }
                }
            }
            return days.ToList();
        }
    }
}
