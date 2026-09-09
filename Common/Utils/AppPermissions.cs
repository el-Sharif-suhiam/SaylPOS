namespace Common.Utils
{
    /// <summary>
    /// ═══════════════════════════════════════════════════════════════════════
    /// عقد صلاحيات سيل — نظام البتات (Bitmask) المعتمد بقرار المالك
    /// ═══════════════════════════════════════════════════════════════════════
    /// كل شاشة إدارية تشغل بتًا واحدًا ثابتًا في قناع الأذونات (RoleValue):
    /// من يُمنح الشاشة يُمنح كل ما فيها (كل شيء أو لا شيء).
    ///
    /// ⚠️⚠️⚠️ عقد مؤبد — لا يُكسر أبدًا: ⚠️⚠️⚠️
    /// مواقع البتات مجمّدة إلى الأبد. أي إعادة ترقيم = أدوار مخزنة في قاعدة
    /// البيانات تنفتح على شاشات خاطئة. الإضافة الجديدة تأخذ أول بت حر بعد
    /// آخر بت مستخدم حصرًا، ولا يُعاد استخدام بت شاشة محذوفة أبدًا.
    ///
    /// الأدوار الثابتة (لا تُعدَّل ولا تُعطَّل — محمية في الخدمة والواجهة):
    ///   مدير النظام = 4095 (كل الشاشات الإدارية)
    ///   محاسب       = 508  (كل الشاشات ما عدا: المنتجات، المخزون،
    ///                        المستخدمين، الأدوار، الإعدادات — أي العملاء
    ///                        والموردون والفواتير والمصروفات والتقارير والرواتب)
    ///   كاشير       = 0    (شاشة الكاشير فقط — تُفتح بفحص هوية الدور،
    ///                        لا بالبت: 0 & أي بت = 0 دائمًا)
    ///
    /// سلم الحماية: أي دور مخصص سقفه 3071 (كل الشاشات إلا الأدوار) —
    /// بت الأدوار (1024) لا يظهر في محرر الأدوار أبدًا، فعلوّ المدير
    /// مضمون بالبناء لا بالفحص.
    /// ═══════════════════════════════════════════════════════════════════════
    /// </summary>
    public static class AppPermissions
    {
        // ── البتات الثابتة: بت واحد لكل شاشة ──
        public const int Products       = 1;      // 2^0  — المنتجات
        public const int Inventory       = 2;      // 2^1  — المخزون
        public const int Customers       = 4;      // 2^2  — العملاء
        public const int Suppliers       = 8;      // 2^3  — الموردون
        public const int SalesInvoices   = 16;     // 2^4  — فواتير البيع
        public const int PurchaseInvoices = 32;    // 2^5  — فواتير الشراء
        public const int Expenses        = 64;     // 2^6  — المصروفات
        public const int Reports        = 128;    // 2^7  — التقارير
        public const int Employees       = 256;    // 2^8  — الموظفون والرواتب
        public const int Users           = 512;    // 2^9  — المستخدمون
        public const int Roles           = 1024;   // 2^10 — الأدوار والصلاحيات (المدير وحده)
        public const int Settings        = 2048;   // 2^11 — الإعدادات

        // ── قيم الأدوار الثابتة ──
        public const int AdminRoleValue    = 4095;  // كل الشاشات
        public const int AccountantRoleValue = 508; // كل الشاشات إلا الإدارة والمخزون والأسعار
        public const int CashierRoleValue  = 0;     // شاشة الكاشير فقط (فحص هوية)

        /// <summary>أقصى قيمة يمكن لأي دور مخصص حملها: كل شيء إلا شاشة الأدوار.</summary>
        public const int CustomRoleCeiling = 3071;

        /// <summary>أقصى بت مستخدم حاليًا — الشاشة الجديدة تأخذ أول بت حر بعده.</summary>
        public const int HighestUsedBit = Settings;

        /// <summary>فحص القناع: هل يملك الدور صلاحية الشاشة المعطاة؟</summary>
        public static bool Has(int roleValue, int screenBit) => (roleValue & screenBit) != 0;

        /// <summary>هل الدور كاشير؟ (فحص هوية لا بت — شاشة الكاشير تُفتح به وحده).</summary>
        public static bool IsCashier(int roleValue) => roleValue == CashierRoleValue;

        /// <summary>هل الدور مدير النظام؟</summary>
        public static bool IsAdmin(int roleValue) => roleValue == AdminRoleValue;

        /// <summary>
        /// هل يملك الدور سلطة على قيمة أخرى؟ (سلم الحماية: يوازي أو أعلى = ممنوع اللمس).
        /// ملاحظة: قيمة 0 (كاشير) أدنى الجميع — يمكن لأي دور إدارته.
        /// </summary>
        public static bool Outranks(int actorValue, int targetValue) => actorValue > targetValue;

        /// <summary>
        /// كتالوج الشاشات المتاحة لمحرر الأدوار — بلا شاشة الأدوار أبدًا
        /// (للمدير وحده، فسقف أي دور مخصص 3071 بنيويًا).
        /// الترتيب هنا هو ترتيب العرض في الواجهة.
        /// </summary>
        public static readonly (int Bit, string Name)[] EditableScreens =
        {
            (Products,         "المنتجات"),
            (Inventory,        "المخزون"),
            (Customers,        "العملاء"),
            (Suppliers,        "الموردون"),
            (SalesInvoices,    "فواتير البيع"),
            (PurchaseInvoices, "فواتير الشراء"),
            (Expenses,         "المصروفات"),
            (Reports,          "التقارير"),
            (Employees,        "الموظفون والرواتب"),
            (Users,            "المستخدمون"),
            (Settings,         "الإعدادات"),
        };

        /// <summary>اسم الشاشة من بتّها — للتقارير والوصف (بلا تدقيق فريد: قد تندمج بتات).</summary>
        public static string Describe(int roleValue)
        {
            if (roleValue == AdminRoleValue) return "مدير النظام — كامل الصلاحيات";
            if (roleValue == AccountantRoleValue) return "محاسب — كل الأقسام إلا المخزون والإدارة";
            if (roleValue == CashierRoleValue) return "كاشير — شاشة الكاشير فقط";
            if (roleValue == 0) return "بلا صلاحيات";

            int count = 0;
            foreach (var (bit, _) in EditableScreens)
                if (Has(roleValue, bit)) count++;

            if (count == EditableScreens.Length) return "كل الشاشات (بلا إدارة الأدوار)";
            return count == 1 ? "شاشة واحدة" : $"{count} شاشات";
        }
    }
}
