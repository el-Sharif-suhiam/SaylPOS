using System;

namespace Common.DTOs
{
    /// <summary>المرحلة 5: وحدة منتج نشطة للاختيار في الواجهات (نقطة البيع،
    /// الشراء، الاستلام، التسوية). الوحدة الأساس أولًا ثم الباقية بالاسم.
    /// PricePerUnit = سعر وحدة الإدخال للعرض فقط (Math.Round(سعر الأساس ×
    /// المعامل, 2, AwayFromZero)) — الحقيقة المخزنة تبقى سعر الأساس.</summary>
    public class DtoProductUnit
    {
        public int ProductUnitId { get; set; }

        /// <summary>اسم الوحدة (نصي — يُخزن كلقطة على InvoiceItem).</summary>
        public string Name { get; set; } = null!;

        /// <summary>معامل التحويل إلى الوحدة الأساس (1 كرتون = 12 حبة → 12).
        /// الوحدة الأساس = 1.</summary>
        public decimal FactorToBase { get; set; }

        /// <summary>true للصف المرآة للوحدة الأساس (Product.Unit).</summary>
        public bool IsBase { get; set; }

        /// <summary>سعر وحدة الإدخال للعرض فقط (سعر الأساس × المعامل، مقرّب
        /// لخانتين). null إذا لم يتوفر سعر أساس.</summary>
        public decimal? PricePerUnit { get; set; }

        /// <summary>المرحلة 6: حالة الوحدة — محرر المنتج يعرض المعطلة أيضًا
        /// (includeInactive: true) بينما منتقيات المعاملات تطلب النشطة حصرًا.</summary>
        public bool IsActive { get; set; }
    }
}
