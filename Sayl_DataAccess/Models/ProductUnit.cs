using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

/// <summary>
/// وحدة منتج (المرحلة 3 — مخطط خامل): صف أساس واحد لكل منتج
/// (IsBase = 1, FactorToBase = 1) هو المرآة القانونية لـ Product.Unit،
/// ووحدات بديلة اختيارية بعامل تحويل (1 كرتون = 12 حبة أساس).
/// المعاملات التاريخية تقرأ اللقطة النصية (InvoiceItem.UnitName) ولا
/// تنضم لهذا الجدول أبدًا — تعديل/تعطيل صف هنا لا يعيد كتابة التاريخ.
/// </summary>
public partial class ProductUnit
{
    public int ProductUnitId { get; set; }

    public int ProductId { get; set; }

    public string Name { get; set; } = null!;

    public decimal FactorToBase { get; set; }

    public bool IsBase { get; set; }

    public decimal? PricePerUnit { get; set; }

    public bool IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();
}
