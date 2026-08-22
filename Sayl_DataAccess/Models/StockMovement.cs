using Common.Enums;
using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class StockMovement
{
    public int MovementId { get; set; }

    public int ProductId { get; set; }

    public enStockMovementType MovementType { get; set; }

    public int Quantity { get; set; }

    public string SourceType { get; set; } = null!;

    public int? SourceId { get; set; }

    public int? BatchId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Batch? Batch { get; set; }

    public virtual Product Product { get; set; } = null!;
}
