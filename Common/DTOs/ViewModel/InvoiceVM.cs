using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class InvoiceVM
    {
        public int InvoiceId { get; set; }
        public string InvoiceNumber { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string Status { get; set; } = null!;
        public string PaymentStatus { get; set; } = null!;
        public int? PartyId { get; set; }
        public string? PartyName { get; set; }
        public string? UserName { get; set; }
        public int? SessionId { get; set; }
        public int? OriginalInvoiceId { get; set; }
        public string? OriginalInvoiceNumber { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal NetAmount { get; set; }
        public string? Notes { get; set; }
        public DateTime? CreatedAt { get; set; }
        public List<InvoiceItemVM> Items { get; set; } = new();

        /// <summary>عدد البنود (يُعبّأ كعدّ فرعي في استعلامات القوائم دون Include).</summary>
        public int ItemsCount { get; set; }
    }

    public class InvoiceItemVM
    {
        public int InvoiceItemId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public decimal Quantity { get; set; }
        public string? BatchNumber { get; set; }
        public decimal ItemPrice { get; set; }
        public decimal? ItemCost { get; set; }

        /// <summary>المرحلة 5: لقطة وحدة الإدخال وقت إنشاء الفاتورة (اسم نصي
        /// مخزن على السطر). null = سطر قديم أو وحدة أساس — الواجهة تعرض
        /// حينها الوحدة الحالية للمنتج (Product.Unit) كخيار ارتجاعي.</summary>
        public string? UnitName { get; set; }

        /// <summary>المرحلة 5: معامل التحويل المخزّن على السطر (لقطة — لا يُقرأ
        /// من ProductUnits الحالية). null = وحدة أساس.</summary>
        public decimal? UnitFactorToBase { get; set; }

        /// <summary>المرحلة 5: الكمية المُدخلة بلقطة الوحدة (مثال: 2 كرتون).
        /// null = سطر قديم/وحدة أساس — تعرض Quantity كما هي.</summary>
        public decimal? EnteredQuantity { get; set; }
    }
}
