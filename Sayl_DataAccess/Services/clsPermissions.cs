using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>قيم الأدوار (RoleValue): مدير 100، محاسب 75، كاشير 50.</summary>
    public static class clsPermissions
    {
        public const int Admin = 100;
        public const int Accountant = 75;
        public const int Cashier = 50;

        /// <summary>أقصى نسبة خصم يحق للكاشير دون صلاحية محاسب.</summary>
        public const decimal MaxCashierDiscountPercent = 10m;

        public static bool HasAtLeast(int actorRoleValue, int requiredRole) => actorRoleValue >= requiredRole;
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
