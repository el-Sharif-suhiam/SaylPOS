using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoEmployee
    {

            public int EmployeeId { get; set; }

            public string Name { get; set; } = null!;

            public string? Phone { get; set; }

            public string? Email { get; set; }

            public decimal? Salary { get; set; }

            public DateOnly? HireDate { get; set; }

            public string? Address { get; set; }

            public string Status { get; set; } = null!;

            public string Position { get; set; } = null!;

            public string? Department { get; set; }

            public DateTime? CreatedAt { get; set; }

    }
}
