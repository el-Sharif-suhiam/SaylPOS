using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoStockAdjustmentRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public bool IsIncrease { get; set; }
        public string Reason { get; set; } = null!;
        public string? BatchNumber { get; set; }
        public DateOnly? ExpiryDate { get; set; }
        public int UserId { get; set; }

        /// <summary>
        /// STOCK COUNT IDEMPOTENCY: معرّف السطر المرتبط (CountId) يُنشأ StockMovement
        /// بهذا SourceId (SourceType = "ManualAdjustment") — يسمح بتحديد الحركة المُنشأة
        /// بالضبط عند الترحيل. اختياري (null للتسوية اليدوية المباشرة).
        /// </summary>
        public int? SourceId { get; set; }
    }
}
