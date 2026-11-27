using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoStockAdjustmentRequest
    {
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
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

        /// <summary>المرحلة 5: الكمية المُدخلة بوحدة الإدخال — اختياري للتوافق.
        /// Quantity يبقى دائمًا بالوحدة الأساس (الجرد/الحركة) ويُحسب على
        /// الخادم من EnteredQuantity × UnitFactorToBase ويُتحقق تطابقه.</summary>
        public decimal? EnteredQuantity { get; set; }

        /// <summary>المرحلة 5: اسم وحدة الإدخال (للعرض/التحقق فقط — حركة
        /// المخزون دائمًا بالوحدة الأساس). null/فارغ = أساس.</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل تحويل وحدة الإدخال إلى الأساس. null/1 = أساس.</summary>
        public decimal? UnitFactorToBase { get; set; }
    }
}
