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
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public DateOnly? ExpiryDate { get; set; }

        /// <summary>المرحلة 5: الكمية المُدخلة بوحدة الإدخال (مثال: 1 كرتون) —
        /// اختياري للتوافق. Quantity يبقى دائمًا بالوحدة الأساس (الحقيقة
        /// المخزنة) ويُحسب على الخادم = Math.Round(EnteredQuantity ×
        /// UnitFactorToBase, 3, AwayFromZero) ويُتحقق تطابقه هنا.</summary>
        public decimal? EnteredQuantity { get; set; }

        /// <summary>المرحلة 5: اسم وحدة الإدخال وقت الشراء (لقطة نصية تُخزن على
        /// InvoiceItem). null/فارغ = الوحدة الأساس (سلوك قديم — لا لقطة).</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل تحويل وحدة الإدخال إلى الأساس
        /// (1 كرتون = 12 حبة أساس → 12). null/1 = وحدة أساس.</summary>
        public decimal? UnitFactorToBase { get; set; }

        /// <summary>المرحلة 5: الدلالة المرجعية — UnitCost هنا تكلفة وحدة
        /// الإدخال إن مُلئت الوحدة (1 كرتون)؛ الخدمة تحوّلها إلى تكلفة
        /// الأساس = Math.Round(UnitCost / UnitFactorToBase, 2, AwayFromZero)
        /// قبل التخزين على الدفعة/InvoiceItems.ItemCost.</summary>
    }
}
