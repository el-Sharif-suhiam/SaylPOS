using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoStockReceiveRequest
    {
        public int ProductId { get; set; }
        public string BatchNumber { get; set; } = null!;
        public int Quantity { get; set; }
        public DateOnly? ExpiryDate { get; set; }

        /// <summary>تكلفة الوحدة للاستلام اليدوي (اختياري) — تُخزن على الدفعة
        /// الجديدة فتصير مصدر COGS لبيعها. null = دفعة بلا تكلفة مخزنة
        /// (يُرجع إليها Product.CostPrice وقت البيع).</summary>
        public decimal? UnitCost { get; set; }

        public int? InvoiceId { get; set; }
    }
}
