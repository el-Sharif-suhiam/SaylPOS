using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class CashMovement
{
    public int MovementId { get; set; }

    public int? SessionId { get; set; }

    /// <summary>'I' = قبض (دخول نقد)، 'O' = صرف (خروج نقد)</summary>
    public char MovementType { get; set; }

    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public int UserId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual PosSession? PosSession { get; set; }

    public virtual User User { get; set; } = null!;
}
