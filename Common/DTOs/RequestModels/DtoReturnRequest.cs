using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoReturnRequest
    {
        public int OriginalInvoiceId { get; set; }
        public int UserId { get; set; }

        /// <summary>VULN-05: جلسة الدرج التي يخرج منها نقد الاسترداد (إن وُجد) —
        /// درج اليوم لا درج جلسة البيع الأصلية المتجمدة. null = خارج جلسات البيع.</summary>
        public int? SessionId { get; set; }

        public List<DtoReturnItem> Items { get; set; } = new();
        public string? Notes { get; set; }
    }

    public class DtoReturnItem
    {
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
        public int? BatchId { get; set; }

        /// <summary>المرحلة 5: الكمية المُدخلة بوحدة الإرجاع — يجب أن تكون
        /// بوحدة الإرجاع الأصلي نفسها (لقطة سطر الفاتورة الأصلي) أو الوحدة
        /// الأساس للسطور القديمة. اختياري للتوافق؛ Quantity يبقى دائمًا
        /// بالوحدة الأساس ويُحسب على الخادم من EnteredQuantity ×
        /// UnitFactorToBase ويُتحقق تطابقه مقابل الحد المتبقي بالأساس.</summary>
        public decimal? EnteredQuantity { get; set; }

        /// <summary>المرحلة 5: اسم وحدة الإرجاع (يجب أن يطابق لقطة السطر
        /// الأصلي — لا يُقبل حلّ وحدة جديدة هنا). null/فارغ = أساس
        /// (سطور الفواتير القديمة أو الإرجاع بوحدة الأساس).</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل تحويل وحدة الإرجاع إلى الأساس — يُتحقق
        /// مطابقته للقطة المخزنة على السطر الأصلي (مطابقة نصية للوحدة
        /// ورقمية للمعامل، لا قراءة من ProductUnits الحالية).</summary>
        public decimal? UnitFactorToBase { get; set; }
    }
}
