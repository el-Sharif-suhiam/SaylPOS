using Common.Enums;
using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Payment
{
    public int PaymentId { get; set; }

    public enPaymentType Type { get; set; } 

    public int? InvoiceId { get; set; }

    public char PartyType { get; set; }

    public int? PartyId { get; set; }

    public decimal Amount { get; set; }

    public DateOnly PaymentDate { get; set; }

    public string PaymentMethod { get; set; } = null!;

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Invoice? Invoice { get; set; }

    public virtual Party? Party { get; set; }
}
