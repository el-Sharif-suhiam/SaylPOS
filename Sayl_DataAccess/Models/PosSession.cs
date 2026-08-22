using Common.Enums;
using System;
using System.Collections.Generic;
namespace Sayl.Core.Models;

public partial class PosSession
{
    public int SessionId { get; set; }

    public int UserId { get; set; }

    public decimal OpeningAmount { get; set; }

    public decimal? ClosingAmount { get; set; }

    public decimal? ExpectedAmount { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public enPosSessionStatusType Status { get; set; }

    public virtual User User { get; set; } = null!;

    public virtual ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
