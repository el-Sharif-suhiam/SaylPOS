using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class Role
{
    public byte RoleId { get; set; }

    public string Name { get; set; } = null!;

    public int RoleValue { get; set; }

}
