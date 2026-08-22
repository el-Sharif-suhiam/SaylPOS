using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    // Common/DTOs/DtoCheckoutRequest.cs
    public class DtoCheckoutRequest
    {
        public int? CustomerId { get; set; }      // null للزائر المجهول
        public int UserId { get; set; }           // الكاشير
        public int? SessionId { get; set; }       // جلسة نقطة البيع (اختياري)
        public List<DtoCheckoutItem> Items { get; set; }
        public decimal DiscountPercent { get; set; }  // نسبة الخصم على الفاتورة ككل
        public decimal DiscountAmount { get; set; }   // أو مبلغ الخصم الثابت
        public decimal TaxPercent { get; set; }
        public DtoPaymentInfo? Payment { get; set; }
        public string? Notes { get; set; }
    }

    public class DtoCheckoutItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public int Quantity { get; set; }
        public int? BatchId { get; set; }
    }

    public class DtoPaymentInfo
    {
        public decimal Amount { get; set; }
        public DateOnly PaymentDate { get; set; }
        public string Method { get; set; } = "cash";  // cash, card, etc.
    }
}
