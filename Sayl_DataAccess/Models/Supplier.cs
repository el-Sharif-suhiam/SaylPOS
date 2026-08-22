using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Supplier
{
    public int PartyId { get; set; }

    public decimal? Balance { get; set; }

    public string? Category { get; set; }

    public virtual Party Party { get; set; } = null!;
}
