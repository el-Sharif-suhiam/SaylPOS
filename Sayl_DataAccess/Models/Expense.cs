using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Expense
{
    public int ExpenseId { get; set; }

    public string Description { get; set; } = null!;

    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }

    public string? Category { get; set; }

    public string? Notes { get; set; }

    public int? UserId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? User { get; set; }
}
