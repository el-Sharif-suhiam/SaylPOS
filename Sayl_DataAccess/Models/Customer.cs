using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Customer
{
    public int PartyId { get; set; }

    public decimal? Balance { get; set; }

    public decimal? CreditLimit { get; set; }

    public virtual Party Party { get; set; } = null!;
}
