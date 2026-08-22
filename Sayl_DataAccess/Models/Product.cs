using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Product
{
    public int ProductId { get; set; }

    public string Name { get; set; } = null!;

    public string? Barcode { get; set; }

    public string? Unit { get; set; }

    public string Type { get; set; } = null!;

    public decimal BasePrice { get; set; }

    public decimal CostPrice { get; set; }

    public int? ReorderLevel { get; set; }

    public bool IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<Batch> Batches { get; set; } = new List<Batch>();

    public virtual Inventory? Inventory { get; set; }

    public virtual ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();
}
