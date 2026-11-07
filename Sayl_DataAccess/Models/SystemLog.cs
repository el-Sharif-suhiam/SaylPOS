using System;
using System.Collections.Generic;

namespace Sayl.Core.Models;

public partial class SystemLog
{
    public int LogId { get; set; }

    /// <summary>قابل للـ null (Upgrade_14): محاولات الدخول الفاشلة باسم مستخدم
    /// غير معروف تُدقَّق بلا هوية — كان NOT NULL مع FK يمنع تسجيل الرفض بلا فاعل.</summary>
    public int? UserId { get; set; }

    public string Action { get; set; } = null!;

    public string? Details { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual User? User { get; set; }
}
