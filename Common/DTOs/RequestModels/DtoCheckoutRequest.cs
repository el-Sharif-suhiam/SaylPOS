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
        public List<DtoCheckoutItem> Items { get; set; } = new();
        public decimal DiscountPercent { get; set; }  // نسبة الخصم على الفاتورة ككل
        public decimal DiscountAmount { get; set; }   // أو مبلغ الخصم الثابت
        public decimal TaxPercent { get; set; }
        public DtoPaymentInfo? Payment { get; set; }

        /// <summary>دفعات متعددة (نقدي + شبكة على نفس الفاتورة). إذا مُلئت تُستخدم بدل Payment.</summary>
        public List<DtoPaymentInfo>? Payments { get; set; }

        public string? Notes { get; set; }

        /// <summary>Task-3: معرف اعتماد المدير لتجاوز حد الائتمان (اختياري).
        /// يُستهلك داخل معاملة البيع نفسها بعد إعادة فحص الربط الصارم (نفس
        /// العميل + نفس المبلغ + نفس لقطة الرصيد/الحد + حالة granted) —
        /// استهلاك واحد بالضبط؛ الواجهة لا تستطيع التزييف: الربط يُتحقق
        /// مقابل سجل CreditApprovals على الخادم.</summary>
        public int? CreditApprovalId { get; set; }
    }

    public class DtoCheckoutItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public int? BatchId { get; set; }

        /// <summary>VULN-14: سعر الوحدة وقت إضافة البند للسلة (snapshot) —
        /// اختياري للتوافق؛ إن مُلئ تُبنى الفاتورة عليه لا على سعر المنتج
        /// الحالي (تعديل سعر أثناء بناء سلة كان يفترس الزبون بصمت).
        /// null = السلوك القديم (اقرأ السعر الحالي من Products).
        /// الدلالة: سعر الوحدة الأساس (PER-BASE-UNIT) — الحقيقة المخزنة؛
        /// سعر وحدة الإدخال يُشتق للعرض فقط (سعر الأساس × المعامل).</summary>
        public decimal? UnitPrice { get; set; }

        /// <summary>المرحلة 5: الكمية المُدخلة بوحدة الإدخال (مثال: 2 كرتون) —
        /// اختياري للتوافق. Quantity يبقى دائمًا بالوحدة الأساس (الحقيقة
        /// المخزنة) ويُحسب على الخادم = Math.Round(EnteredQuantity ×
        /// UnitFactorToBase, 3, AwayFromZero) ويُتحقق تطابقه هنا.</summary>
        public decimal? EnteredQuantity { get; set; }

        /// <summary>المرحلة 5: اسم وحدة الإدخال وقت البيع (لقطة نصية تُخزن على
        /// InvoiceItem). null/فارغ = الوحدة الأساس (سلوك قديم — لا لقطة).</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل تحويل وحدة الإدخال إلى الأساس
        /// (1 كرتون = 12 حبة أساس → 12). null/1 = وحدة أساس.</summary>
        public decimal? UnitFactorToBase { get; set; }
    }

    public class DtoPaymentInfo
    {
        public decimal Amount { get; set; }
        public DateOnly PaymentDate { get; set; }
        public string Method { get; set; } = "cash";  // cash, card, etc.
    }
}
