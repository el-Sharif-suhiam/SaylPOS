using System;

namespace Sayl.Core.Models;

/// <summary>حجز مخزون على مستوى الفاتورة — صف لكل (فاتورة معلقة، منتج).
/// المرجع الصحيح لملكية الحجز؛ Inventory.QuantityReserved يبقى مجموعًا مترابطًا مع هذه الصفوف.</summary>
public partial class StockReservation
{
    public int ReservationId { get; set; }

    public int InvoiceId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Invoice Invoice { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
