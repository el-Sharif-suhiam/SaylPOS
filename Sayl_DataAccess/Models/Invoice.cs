using System;
using System.Collections.Generic;
using Common.Enums;
namespace Sayl.Core.Models;

public partial class Invoice
{
    public int InvoiceId { get; set; }

    public enInvoicesType Type { get; set; }

    public enInvoicesStatus Status { get; set; }

    public enPaymentStatus PaymentStatus { get; set; }

    public char? PartyType { get; set; }

    public int? PartyId { get; set; }

    public int UserId { get; set; }

    public int? SessionId { get; set; }

    public int? OriginalInvoiceId { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal NetAmount { get; set; }

    public string? Notes { get; set; }

    /// <summary>الرقم التجاري السنوي المشترك لكل أنواع الفواتير (INV-YYYY-NNNNNN)
    /// — غير فارغ هيكليًا (NOT NULL): المولّد يسنده داخل معاملة كل عملية إنتاج،
    /// والترحيل (Upgrade_19) ملأ القديم ورفض أي صف بقي بلا رقم.</summary>
    public string InvoiceNumber { get; set; } = null!;

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();

    public virtual Invoice? OriginalInvoice { get; set; }

    public virtual Party? Party { get; set; }

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual PosSession? PosSession { get; set; }

    public virtual User User { get; set; } = null!;
}
