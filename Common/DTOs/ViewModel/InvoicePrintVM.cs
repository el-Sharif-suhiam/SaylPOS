using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class InvoicePrintVM
    {
        public int InvoiceId { get; set; }
        public string Type { get; set; } = null!;
        public string Status { get; set; } = null!;
        public string PaymentStatus { get; set; } = null!;
        public int? PartyId { get; set; }
        public string? PartyName { get; set; }
        public string? PartyPhone { get; set; }
        public string? PartyAddress { get; set; }
        public string CashierName { get; set; } = null!;
        public int? SessionId { get; set; }
        public int? OriginalInvoiceId { get; set; }
        public string? Notes { get; set; }
        public DateTime? CreatedAt { get; set; }

        public decimal TotalAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal RemainingAmount { get; set; }

        public List<InvoiceItemVM> Items { get; set; } = new();
        public List<InvoicePaymentLineVM> Payments { get; set; } = new();
    }

    public class InvoicePaymentLineVM
    {
        public int PaymentId { get; set; }
        public decimal Amount { get; set; }
        public string PaymentMethod { get; set; } = null!;
        public DateOnly PaymentDate { get; set; }
        public string? Notes { get; set; }
    }
}
