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

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();

    public virtual Product Product { get; set; } = null!;

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();
}
