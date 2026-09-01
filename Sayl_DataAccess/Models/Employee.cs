using System;
using System.Collections.Generic;
using Common.Enums;

namespace Sayl.Core.Models;

public partial class Employee
{
    public int EmployeeId { get; set; }

    public string Name { get; set; } = null!;

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public decimal? Salary { get; set; }

    /// <summary>طريقة احتساب الراتب (القيمة Salary هي المعدل حسب هذا النوع).</summary>
    public enSalaryType SalaryType { get; set; }

    public DateOnly? HireDate { get; set; }

    /// <summary>تاريخ الاستقالة — يقطع Proration عند حساب الرواتب الشهرية للفترات الممتدة بعده.</summary>
    public DateOnly? ResignDate { get; set; }

    public string? Address { get; set; }

    public string Status { get; set; } = null!;

    public string Position { get; set; } = null!;

    public string? Department { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<EmployeeShift> EmployeeShifts { get; set; } = new List<EmployeeShift>();
    public virtual ICollection<PayrollRecord> PayrollRecords { get; set; } = new List<PayrollRecord>();
    public virtual User? User { get; set; }
}
