using Common.DTOs;
using Common.DTOs.ViewModel;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// حالة الجلسة: المستخدم المسجَّل حاليًا ودوره.
    /// القيم مطابقة لبيانات الأدوار المزروعة: مدير النظام = 100، محاسب = 75، كاشير = 50.
    /// </summary>
    public static class SessionState
    {
        private const int AdminRoleValue = 100;
        private const int AccountantRoleValue = 75;
        private const int CashierRoleValue = 50;

        public static UserVM? CurrentUser { get; private set; }

        /// <summary>قيمة دور المستخدم الحالي (تُحلَّل من قائمة الأدوار عند تسجيل الدخول).</summary>
        public static int CurrentRoleValue { get; private set; }

        public static bool IsLoggedIn => CurrentUser != null;
        public static bool IsAdmin => CurrentRoleValue >= AdminRoleValue;
        public static bool IsAccountant => !IsAdmin && CurrentRoleValue >= AccountantRoleValue;
        public static bool IsCashier => !IsAdmin && !IsAccountant && CurrentRoleValue >= CashierRoleValue;

        /// <summary>
        /// صلاحية can_receive_stock مسندة لدور الكاشير (حسب UI_TASK).
        /// </summary>
        public static bool CanReceiveStock => IsCashier;

        /// <summary>تعيين المستخدم الحالي وحلّ قيمة دوره من قائمة الأدوار.</summary>
        public static void SetUser(UserVM user, IEnumerable<DtoRole> roles)
        {
            CurrentUser = user;
            CurrentRoleValue = roles
                .FirstOrDefault(r => string.Equals(r.Name, user.RoleName, StringComparison.Ordinal))
                ?.RoleValue ?? 0;
        }

        public static void Clear()
        {
            CurrentUser = null;
            CurrentRoleValue = 0;
        }
    }
}
