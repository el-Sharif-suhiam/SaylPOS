using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoStockReceiveRequest
    {
        public int ProductId { get; set; }
        public string BatchNumber { get; set; } = null!;
        public decimal Quantity { get; set; }
        public DateOnly? ExpiryDate { get; set; }

        /// <summary>المرحلة 5: الكمية المُدخلة بوحدة الإدخال — اختياري للتوافق.
        /// Quantity يبقى دائمًا بالوحدة الأساس ويُحسب على الخادم من
        /// EnteredQuantity × UnitFactorToBase ويُتحقق تطابقه.</summary>
        public decimal? EnteredQuantity { get; set; }

        /// <summary>المرحلة 5: اسم وحدة الإدخال (يُخزن كحركة مخزون بلا لقطة
        /// قاعدة بيانات — الحركة دائمًا بالوحدة الأساس). null/فارغ = أساس.</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل تحويل وحدة الإدخال إلى الأساس. null/1 = أساس.</summary>
        public decimal? UnitFactorToBase { get; set; }

        /// <summary>تكلفة الوحدة للاستلام اليدوي (اختياري) — تُخزن على الدفعة
        /// الجديدة فتصير مصدر COGS لبيعها. null = دفعة بلا تكلفة مخزنة
        /// (يُرجع إليها Product.CostPrice وقت البيع).
        /// المرحلة 5: مع وحدة الإدخال هذه تكلفة وحدة الإدخال (1 كرتون)؛
        /// تُحوّل على الخادم إلى تكلفة الأساس = Math.Round(UnitCost /
        /// UnitFactorToBase, 2, AwayFromZero) قبل التخزين على الدفعة.</summary>
        public decimal? UnitCost { get; set; }

        public int? InvoiceId { get; set; }
    }
}
