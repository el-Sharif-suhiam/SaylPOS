using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoPurchaseRequest
    {
        public int SupplierId { get; set; }
        public int UserId { get; set; }
        public List<DtoPurchaseItem> Items { get; set; } = new();
        public decimal DiscountAmount { get; set; }
        public decimal TaxPercent { get; set; }
        public DtoPaymentInfo? Payment { get; set; }
        public string? Notes { get; set; }
    }

    public class DtoPurchaseItem
    {
        public int ProductId { get; set; }
        public string BatchNumber { get; set; } = null!;
        public int Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public DateOnly? ExpiryDate { get; set; }
    }
}
