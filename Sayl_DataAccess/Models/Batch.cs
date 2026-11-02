using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Batch
{
    public int BatchId { get; set; }

    public int ProductId { get; set; }

    public string BatchNumber { get; set; } = null!;

    public DateOnly? ExpiryDate { get; set; }

    public int BatchQuantity { get; set; }

    /// <summary>حجب/حجر: مخزون وُجد فعليًا لكنه غير صالح للبيع (مرتجع منتهي الصلاحية،
    /// مرتجع بلا دفعة مصدر…) — يبقى محسوبًا في المخزون الكلي ومستثنى من البيع
    /// وحساب المتاح حتى يُفحص ويُعتمد صراحة.</summary>
    public bool IsQuarantined { get; set; }

    /// <summary>تكلفة الاقتناء التاريخية للوحدة (بعد تخصيص خصم الشراء) — سياسة التكلفة:
    /// تكلفة البيع (COGS) تُقرأ من الدفعة المستهلكة فعليًا لا من Product.CostPrice.
    /// null = دفعة قديمة/استلام يدوي بلا تكلفة → يُرجع إليها Product.CostPrice وقت البيع.</summary>
    public decimal? UnitCost { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();

    public virtual Product Product { get; set; } = null!;

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();
}
