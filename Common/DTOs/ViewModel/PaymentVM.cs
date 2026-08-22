using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class PaymentVM
    {
        public int PaymentId { get; set; }
        public string Type { get; set; } = null!;
        public int? InvoiceId { get; set; }
        public char PartyType { get; set; }
        public int? PartyId { get; set; }
        public string? PartyName { get; set; }
        public decimal Amount { get; set; }
        public DateOnly PaymentDate { get; set; }
        public string PaymentMethod { get; set; } = null!;
        public string? Notes { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
