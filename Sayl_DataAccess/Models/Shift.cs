using System;
using System.Collections.Generic;
using Common.Enums;

namespace Sayl.Core.Models
{
    /// <summary>تعريف شفت عمل (بيانات جدول متوقعة — ليست حضوراً). End &lt; Start = يعبر منتصف الليل.</summary>
    public partial class Shift
    {
        public int ShiftId { get; set; }

        public string Name { get; set; } = null!;

        public TimeOnly StartTime { get; set; }

        public TimeOnly EndTime { get; set; }

        public string? Notes { get; set; }

        public virtual ICollection<EmployeeShift> EmployeeShifts { get; set; } = new List<EmployeeShift>();
    }

    /// <summary>تعيين شفت لموظف بأيام أسبوع محددة (بتات: الأحد=1 .. السبت=64).</summary>
    public partial class EmployeeShift
    {
        public int EmployeeShiftId { get; set; }

        public int EmployeeId { get; set; }

        public int ShiftId { get; set; }

        public byte DaysOfWeek { get; set; }

        public DateOnly EffectiveFrom { get; set; }

        public DateOnly? EffectiveTo { get; set; }

        public virtual Employee Employee { get; set; } = null!;

        public virtual Shift Shift { get; set; } = null!;
    }

    /// <summary>إجازة رسمية مدفوعة (تؤثر على اليومي؛ السنوية تتكرر كل عام بنفس اليوم).</summary>
    public partial class OfficialHoliday
    {
        public int HolidayId { get; set; }

        public string Name { get; set; } = null!;

        public DateOnly HolidayDate { get; set; }

        public int DurationDays { get; set; }

        public bool IsAnnual { get; set; }
    }

    /// <summary>كشف راتب لموظف لفترة محددة؛ الصرف ينشئ مصروفاً مرتبطاً (فئة رواتب).</summary>
    public partial class PayrollRecord
    {
        public int PayrollId { get; set; }

        public int EmployeeId { get; set; }

        public DateOnly PeriodFrom { get; set; }

        public DateOnly PeriodTo { get; set; }

        public enSalaryType SalaryType { get; set; }

        public decimal WorkedUnits { get; set; }

        public decimal HolidayUnits { get; set; }

        public decimal Rate { get; set; }

        public decimal Gross { get; set; }

        public decimal Deductions { get; set; }

        public decimal Additions { get; set; }

        public decimal Net { get; set; }

        public enPayrollStatus Status { get; set; }

        public DateTime? PaidAt { get; set; }

        public int? ExpenseId { get; set; }

        public string? Notes { get; set; }

        public DateTime? CreatedAt { get; set; }

        public virtual Employee Employee { get; set; } = null!;

        public virtual Expense? Expense { get; set; }
    }
}
