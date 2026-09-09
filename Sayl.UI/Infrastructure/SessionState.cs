using Common.DTOs.ViewModel;
using Common.Utils;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// حالة الجلسة — نظام البتات (انظر AppPermissions):
    /// • القناع يُحمَّل مرة واحدة عند الدخول وكل الفحوصات بعدها في الذاكرة.
    /// • الكاشير يُحدَّد بالهوية (قيمة 0) لا بالبت — شاشة الكاشير تُفتح به وحده.
    /// • فحص الواجهة: Can(screenBit) — والإنفاذ الحقيقي في طبقة الخدمات.
    /// </summary>
    public static class SessionState
    {
        public static UserVM? CurrentUser { get; private set; }

        /// <summary>قناع بتات دور المستخدم الحالي (من قاعدة البيانات عند الدخول).</summary>
        public static int CurrentRoleValue { get; private set; }

        public static bool IsLoggedIn => CurrentUser != null;

        /// <summary>مدير النظام (4095) — بالهوية لا بالمقارنة العددية.</summary>
        public static bool IsAdmin => AppPermissions.IsAdmin(CurrentRoleValue);

        /// <summary>الكاشير (0) — شاشة الكاشير فقط، يفتح POS مباشرة.</summary>
        public static bool IsCashier => AppPermissions.IsCashier(CurrentRoleValue);

        /// <summary>فحص شاشة: هل يملك القناع بتّها؟ (لوحة التحكم بلا بت — للجميع).</summary>
        public static bool Can(int screenBit) => AppPermissions.Has(CurrentRoleValue, screenBit);

        /// <summary>استلام المخزون — وظيفة كاشير داخل POS.</summary>
        public static bool CanReceiveStock => IsCashier;

        /// <summary>تعيين المستخدم الحالي بقناع دوره (من UserVM المحمَّل عند الدخول).</summary>
        public static void SetUser(UserVM user)
        {
            CurrentUser = user;
            CurrentRoleValue = user.RoleValue;
        }

        public static void Clear()
        {
            CurrentUser = null;
            CurrentRoleValue = 0;
        }
    }
}
