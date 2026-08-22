using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoPaymentRequest
    {
        public int? InvoiceId { get; set; }
        public char PartyType { get; set; }
        public int? PartyId { get; set; }
        public decimal Amount { get; set; }
        public DateOnly PaymentDate { get; set; }
        public string Method { get; set; } = "cash";
        public string? Notes { get; set; }
    }
}
