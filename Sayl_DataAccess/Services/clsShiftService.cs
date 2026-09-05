using Common.DTOs;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// إدارة الشفتات (تعريف جدول متوقع — ليست حضوراً)، الإجازات الرسمية،
    /// وتعيين الشفتات للموظفين بأيام أسبوع محددة.
    /// </summary>
    public class clsShiftService
    {
        private readonly SaylDbContext _dbContext;

        public clsShiftService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────
        // الشفتات
        // ─────────────────────────────────────────────

        public async Task<List<DtoShift>> GetShiftsListAsync()
        {
            return await _dbContext.Shifts
                .OrderBy(s => s.StartTime)
                .Select(s => new DtoShift
                {
                    ShiftId = s.ShiftId,
                    Name = s.Name,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    Notes = s.Notes
                })
                .ToListAsync();
        }

        public async Task<Result<int>> AddShiftAsync(DtoShift dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Result<int>.Fail("اسم الشفت مطلوب.");

            if (dto.StartTime == dto.EndTime)
                return Result<int>.Fail("وقت البداية والنهاية لا يمكن أن يتطابقا.");

            bool nameExists = await _dbContext.Shifts.AnyAsync(s => s.Name == dto.Name.Trim());
            if (nameExists)
                return Result<int>.Fail("يوجد شفت بهذا الاسم بالفعل.");

            var shift = new Shift
            {
                Name = dto.Name.Trim(),
                StartTime = dto.StartTime,
                EndTime = dto.EndTime,
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim()
            };

            _dbContext.Shifts.Add(shift);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;
            return saved ? Result<int>.Ok(shift.ShiftId, "تمت إضافة الشفت بنجاح.") : Result<int>.Fail("فشلت الإضافة.");
        }

        public async Task<Result> UpdateShiftAsync(DtoShift dto)
        {
            if (dto.ShiftId <= 0)
                return Result.Fail("معرف الشفت غير صالح.");

            var shift = await _dbContext.Shifts.FindAsync(dto.ShiftId);
            if (shift == null)
                return Result.Fail("الشفت غير موجود.");

            if (dto.StartTime == dto.EndTime)
                return Result.Fail("وقت البداية والنهاية لا يمكن أن يتطابقا.");

            if (!string.IsNullOrWhiteSpace(dto.Name) && dto.Name.Trim() != shift.Name)
            {
                bool nameExists = await _dbContext.Shifts.AnyAsync(s => s.Name == dto.Name.Trim() && s.ShiftId != dto.ShiftId);
                if (nameExists)
                    return Result.Fail("يوجد شفت آخر بهذا الاسم.");
                shift.Name = dto.Name.Trim();
            }

            shift.StartTime = dto.StartTime;
            shift.EndTime = dto.EndTime;
            if (dto.Notes != null)
                shift.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث الشفت بنجاح.");
        }

        public async Task<Result> DeleteShiftAsync(int shiftId)
        {
            var shift = await _dbContext.Shifts.FindAsync(shiftId);
            if (shift == null)
                return Result.Fail("الشفت غير موجود.");

            bool assigned = await _dbContext.EmployeeShifts.AnyAsync(es => es.ShiftId == shiftId && es.EffectiveTo == null);
            if (assigned)
                return Result.Fail("لا يمكن حذف شفت معيّن لموظفين. أنهِ تعييناته أولاً.");

            _dbContext.Shifts.Remove(shift);
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم حذف الشفت.");
        }

        // ─────────────────────────────────────────────
        // تعيين الشفتات للموظفين
        // ─────────────────────────────────────────────

        /// <summary>بتات الأيام: الأحد=1 .. السبت=64 (bit0=Sunday .. bit6=Saturday).</summary>
        public const byte Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8,
                          Thursday = 16, Friday = 32, Saturday = 64;

        public async Task<List<DtoEmployeeShift>> GetEmployeeShiftsAsync(int? employeeId = null)
        {
            var query = _dbContext.EmployeeShifts
                .Include(es => es.Employee)
                .Include(es => es.Shift)
                .AsQueryable();

            if (employeeId.HasValue)
                query = query.Where(es => es.EmployeeId == employeeId.Value);

            return await query
                .Where(es => es.EffectiveTo == null)   // التعيينات السارية
                .OrderBy(es => es.Employee.Name)
                .Select(es => new DtoEmployeeShift
                {
                    EmployeeShiftId = es.EmployeeShiftId,
                    EmployeeId = es.EmployeeId,
                    EmployeeName = es.Employee.Name,
                    ShiftId = es.ShiftId,
                    ShiftName = es.Shift.Name,
                    DaysOfWeek = es.DaysOfWeek,
                    EffectiveFrom = es.EffectiveFrom,
                    EffectiveTo = es.EffectiveTo
                })
                .ToListAsync();
        }

        public async Task<Result<int>> AssignShiftAsync(int employeeId, int shiftId, byte daysOfWeek, DateOnly effectiveFrom)
        {
            if (daysOfWeek < 1 || daysOfWeek > 127)
                return Result<int>.Fail("اختر يومًا واحدًا على الأقل.");

            var employee = await _dbContext.Employees.FindAsync(employeeId);
            if (employee == null)
                return Result<int>.Fail("الموظف غير موجود.");

            var shift = await _dbContext.Shifts.FindAsync(shiftId);
            if (shift == null)
                return Result<int>.Fail("الشفت غير موجود.");

            // تعيين سابق لنفس الشفت → يُستبدل (نغلق القديم ونفتح الجديد بالأيام الجديدة)
            var previous = await _dbContext.EmployeeShifts
                .FirstOrDefaultAsync(es => es.EmployeeId == employeeId
                                        && es.ShiftId == shiftId
                                        && es.EffectiveTo == null);

            if (previous != null)
            {
                previous.EffectiveTo = effectiveFrom.AddDays(-1);
            }

            var assignment = new EmployeeShift
            {
                EmployeeId = employeeId,
                ShiftId = shiftId,
                DaysOfWeek = daysOfWeek,
                EffectiveFrom = effectiveFrom
            };

            _dbContext.EmployeeShifts.Add(assignment);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;
            return saved
                ? Result<int>.Ok(assignment.EmployeeShiftId, "تم تعيين الشفت.")
                : Result<int>.Fail("فشل التعيين.");
        }

        public async Task<Result> EndAssignmentAsync(int employeeShiftId)
        {
            var assignment = await _dbContext.EmployeeShifts.FindAsync(employeeShiftId);
            if (assignment == null)
                return Result.Fail("التعيين غير موجود.");

            if (assignment.EffectiveTo != null)
                return Result.Fail("التعيين منتهٍ بالفعل.");

            assignment.EffectiveTo = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)) /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */;
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم إنهاء تعيين الشفت.");
        }

        // ─────────────────────────────────────────────
        // الإجازات الرسمية
        // ─────────────────────────────────────────────

        public async Task<List<DtoOfficialHoliday>> GetHolidaysAsync(int year)
        {
            return await _dbContext.OfficialHolidays
                .Where(h => h.HolidayDate.Year == year || h.IsAnnual)
                .OrderBy(h => h.HolidayDate)
                .Select(h => new DtoOfficialHoliday
                {
                    HolidayId = h.HolidayId,
                    Name = h.Name,
                    HolidayDate = h.HolidayDate,
                    DurationDays = h.DurationDays,
                    IsAnnual = h.IsAnnual
                })
                .ToListAsync();
        }

        public async Task<Result<int>> AddHolidayAsync(DtoOfficialHoliday dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Result<int>.Fail("اسم الإجازة مطلوب.");

            if (dto.DurationDays < 1)
                return Result<int>.Fail("المدة يجب أن تكون يومًا واحدًا على الأقل.");

            var holiday = new OfficialHoliday
            {
                Name = dto.Name.Trim(),
                HolidayDate = dto.HolidayDate,
                DurationDays = dto.DurationDays,
                IsAnnual = dto.IsAnnual
            };

            _dbContext.OfficialHolidays.Add(holiday);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;
            return saved ? Result<int>.Ok(holiday.HolidayId, "تمت إضافة الإجازة.") : Result<int>.Fail("فشلت الإضافة.");
        }

        public async Task<Result> DeleteHolidayAsync(int holidayId)
        {
            var holiday = await _dbContext.OfficialHolidays.FindAsync(holidayId);
            if (holiday == null)
                return Result.Fail("الإجازة غير موجودة.");

            _dbContext.OfficialHolidays.Remove(holiday);
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم حذف الإجازة.");
        }

        /// <summary>
        /// عدد أيام الإجازات الرسمية (مدفوعة) التي تقع داخل فترة محددة.
        /// السنوية تُسقط على سنة الفترة. تُستخدم في حساب الرواتب اليومية.
        /// </summary>
        public async Task<decimal> CountPaidHolidayDaysAsync(DateOnly periodFrom, DateOnly periodTo)
        {
            if (periodTo < periodFrom) return 0;

            var holidays = await _dbContext.OfficialHolidays.ToListAsync();
            decimal total = 0;

            foreach (var h in holidays)
            {
                // السنوية: نسقط تاريخها على سنة بداية الفترة
                var start = h.IsAnnual ? new DateOnly(periodFrom.Year, h.HolidayDate.Month, h.HolidayDate.Day)
                                       : h.HolidayDate;

                // مدة الإجازة = أيام متتالية تبدأ من start
                for (int i = 0; i < h.DurationDays; i++)
                {
                    var day = start.AddDays(i);
                    if (day >= periodFrom && day <= periodTo)
                        total++;
                }
            }

            return total;
        }
    }
}
