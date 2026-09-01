using System;
using System.Collections.Generic;
using System.Text;
using Common.Enums;

namespace Common.DTOs
{
    public class DtoEmployee
    {

            public int EmployeeId { get; set; }

            public string Name { get; set; } = null!;

            public string? Phone { get; set; }

            public string? Email { get; set; }

            public decimal? Salary { get; set; }

            public enSalaryType SalaryType { get; set; }

            public DateOnly? HireDate { get; set; }

            public DateOnly? ResignDate { get; set; }

            public string? Address { get; set; }

            public string Status { get; set; } = null!;

            public string Position { get; set; } = null!;

            public string? Department { get; set; }

            public DateTime? CreatedAt { get; set; }

    }

    public class DtoShift
    {
        public int ShiftId { get; set; }
        public string Name { get; set; } = null!;
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }
        public string? Notes { get; set; }

        /// <summary>هل الشفت يعبر منتصف الليل (EndTime أقدم من StartTime)؟</summary>
        public bool CrossesMidnight => EndTime < StartTime;
    }

    public class DtoEmployeeShift
    {
        public int EmployeeShiftId { get; set; }
        public int EmployeeId { get; set; }
        public string EmployeeName { get; set; } = null!;
        public int ShiftId { get; set; }
        public string ShiftName { get; set; } = null!;
        public byte DaysOfWeek { get; set; }
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }
    }

    public class DtoOfficialHoliday
    {
        public int HolidayId { get; set; }
        public string Name { get; set; } = null!;
        public DateOnly HolidayDate { get; set; }
        public int DurationDays { get; set; }
        public bool IsAnnual { get; set; }
    }

    public class DtoPayrollRecord
    {
        public int PayrollId { get; set; }
        public int EmployeeId { get; set; }
        public string EmployeeName { get; set; } = null!;
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
        public string? Notes { get; set; }
    }
}
