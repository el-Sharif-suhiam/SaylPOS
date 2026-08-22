using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class User
{
    public int UserId { get; set; }

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public int? EmployeeId { get; set; }

    public byte RoleId { get; set; }

    public bool IsActive { get; set; }
    public DateTime? CreatedAt { get; set; }

    public virtual Employee? Employee { get; set; }

    public virtual Role Role { get; set; } = null!;

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<PosSession> PosSessions { get; set; } = new List<PosSession>();
    public virtual ICollection<SystemLog> SystemLogs { get; set; } = new List<SystemLog>(); 
}
