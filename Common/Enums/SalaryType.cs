using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Enums
{
    /// <summary>طريقة احتساب الراتب: بالساعة / يومي / شهري. (لا تجعل أي عضو = 0 — يتعارض مع HasDefaultValue في EF).</summary>
    public enum enSalaryType { monthly = 1, daily = 2, hourly = 3 }

    /// <summary>حالة كشف الراتب: معلق / مصروف.</summary>
    public enum enPayrollStatus { pending = 1, paid = 2 }
}
