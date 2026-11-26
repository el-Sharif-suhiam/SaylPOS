using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class InvoiceItem
{
    public int InvoiceItemId { get; set; }

    public int InvoiceId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public int? BatchId { get; set; }

    public decimal ItemPrice { get; set; }

    public decimal? ItemCost { get; set; }

    // Upgrade_21: لقطات وحدة الإدخال — NULL = صف إرثي/وحدة أساس (لا تُختلق تاريخيًا).
    // Quantity يبقى بوحدة الأساس؛ العرض يقرأ اللقطة النصية أولًا لا المفتاح.
    public string? UnitName { get; set; }

    public decimal? UnitFactorToBase { get; set; }

    public decimal? EnteredQuantity { get; set; }

    public int? ProductUnitId { get; set; }

    public virtual Batch? Batch { get; set; }

    public virtual Invoice Invoice { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;

    public virtual ProductUnit? ProductUnit { get; set; }
}
