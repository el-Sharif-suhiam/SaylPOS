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

    /// <summary>VULN-05: جلسة الدرج التي استلمت هذه الدفعة فعليًا —
    /// قد تختلف عن جلسة الفاتورة (إكمال معلقة بجلسة لاحقة، مرتجع اليوم لبيع قديم).
    /// null = دفعات بلا جلسة أو بيانات ما قبل الترقية (تقارير الجلسة تسقطها
    /// على جلسة الفاتورة كمسار متوافق — راجع Upgrade_06).</summary>
    public int? SessionId { get; set; }

    public string? Notes { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Invoice? Invoice { get; set; }

    public virtual PosSession? Session { get; set; }

    public virtual Party? Party { get; set; }
}
