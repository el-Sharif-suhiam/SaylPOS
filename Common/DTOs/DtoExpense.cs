using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoExpense
    {
        public int ExpenseId { get; set; }

        public string Description { get; set; } = null!;

        public decimal Amount { get; set; }

        public DateOnly ExpenseDate { get; set; }

        public string? Category { get; set; }

        public string? Notes { get; set; }

        public int? UserId { get; set; }

        public DateTime? CreatedAt { get; set; }
    }
}
