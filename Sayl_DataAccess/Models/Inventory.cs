using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Inventory
{
    public int ProductId { get; set; }

    public int? QuantityAvailable { get; set; }

    public int? QuantityReserved { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? LastCountedAt { get; set; }

    public virtual Product Product { get; set; } = null!;
}
