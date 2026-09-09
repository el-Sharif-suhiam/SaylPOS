using Sayl.Core.Data;
using Sayl.Core.Models;
using Common.Utils;

namespace Sayl.Core.Services
{
    /// <summary>
    /// مساعدات الصلاحيات الخدمية — نظام البتات (عقد موثق في AppPermissions).
    /// فحص الشاشة: Has(roleValue, AppPermissions.Xxx) — المسؤولية على الخدمة،
    /// والواجهة تخفي فقط (إخفاء الزر تجميل؛ البوابة هنا).
    /// </summary>
    public static class clsPermissions
    {
        /// <summary>أقصى نسبة خصم يحق للكاشير دون صلاحية محاسب.</summary>
        public const decimal MaxCashierDiscountPercent = 10m;
    }

    /// <summary>مساعد تدقيق: يكتب في SystemLogs سجل كل عملية حساسة.</summary>
    public static class clsAudit
    {
        public static void Log(SaylDbContext db, int userId, string action, string details)
        {
            db.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = action,
                Details = details,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
