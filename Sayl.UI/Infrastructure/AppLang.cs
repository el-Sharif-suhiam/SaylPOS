using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// لغة الواجهة الحالية — تُقرأ مرة واحدة عند الإقلاع من App.Lang ثم تبقى
    /// ثابتة طوال عمر العملية (تبديل اللغة = حفظ الإعداد + إعادة تشغيل).
    /// قيمة افتراضية "ar" عند أي خلل في الإعداد — العربية هي الأصل.
    /// </summary>
    public enum AppLanguage
    {
        Arabic,
        English
    }

    /// <summary>
    /// خدمة الترجمة المركزية (المرحلة 0 من خطة ثنائية اللغة):
    /// • المفاتيح تعيش في Strings/ar.json و Strings/en.json بجانب البرنامج.
    /// • T(key): ترجعة المفتاح باللغة الحالية؛ مفتاح مفقود = fallback للعربية
    ///   ثم للمفتاح نفسه — لا انهيار أبدًا، والترجمة تُستكمل تدريجيًا.
    /// • T(key, args): مفاتيح صيغة — "العميل {0} تجاوز حده" بترتيب معاملات ثابت.
    /// • مبدأ العزل: هذه البنية لا تغيّر أي نص حالي — الشاشات تُرحَّل
    ///   فورمًا فورمًا في مراحل لاحقة، والفورم غير المُرحّل يعرض نصه الحرفي كما هو.
    /// </summary>
    public static class AppLang
    {
        private static readonly Dictionary<string, string> _ar = Load("ar");
        private static readonly Dictionary<string, string> _en = Load("en");

        /// <summary>اللغة الحالية — ثابتة بعد الإقلاع (تُقرأ من appsettings قبل أي UI).</summary>
        public static AppLanguage Current { get; private set; } = AppLanguage.Arabic;

        /// <summary>هل الواجهة إنجليزية؟ (للقرارات البصرية المستقبلية: اتجاه، محاذاة).</summary>
        public static bool IsEnglish => Current == AppLanguage.English;

        /// <summary>يُستدعى مرة واحدة في Program.Main قبل بناء أي نافذة.</summary>
        public static void Init(AppLanguage lang) => Current = lang;

        /// <summary>قراءة اللغة المحفوظة — أولوية: ملف الإعداد المحلي الصالح
        /// (بجانب بيانات المستخدم — %LocalAppData%\Sayl_POS\lang) ثم appsettings.
        /// ملف LocalAppData هو المصدر الحقيقي لأن Program Files للقراءة —
        /// appsettings يبقى القيمة الافتراضية للتركيب الأول فقط.</summary>
        public static AppLanguage LoadSaved()
        {
            try
            {
                string file = SavedLangFile();
                if (File.Exists(file))
                {
                    string code = File.ReadAllText(file).Trim();
                    return code.Equals("en", StringComparison.OrdinalIgnoreCase)
                        ? AppLanguage.English
                        : AppLanguage.Arabic;
                }
            }
            catch
            {
                // قراءة فاشلة = عربي (الأصل)
            }
            return AppLanguage.Arabic;
        }

        /// <summary>حفظ اختيار اللغة ثم إعادة تشغيل البرنامج.
        /// Application.Restart آمنة هنا: تُطلق عملية جديدة بنفس مسار exe
        /// وتغلق الحالية — الفورمات المفتوحة تُغلق عبر دورة الإغلاق الطبيعية.</summary>
        public static void SwitchAndRestart(AppLanguage lang)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Sayl_POS");
                Directory.CreateDirectory(dir);
                File.WriteAllText(SavedLangFile(), lang == AppLanguage.English ? "en" : "ar");
            }
            catch
            {
                // فشل الحفظ: نكمل بالتبديل عبر سطر الأوامر — اللغة تسري لهذه الجلسة فقط
            }
            Application.Restart();
        }

        private static string SavedLangFile() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sayl_POS", "lang.txt");

        /// <summary>ترجعة نصية للمفتاح — مع fallback عربي ثم المفتاح نفسه.</summary>
        public static string T(string key)
        {
            var table = Current == AppLanguage.English ? _en : _ar;
            if (table.TryGetValue(key, out var v)) return v;
            if (_ar.TryGetValue(key, out var fallback)) return fallback;
            return key;
        }

        /// <summary>مفتاح صيغة بمعاملات — "المبلغ {0} غير صالح" مع قيم مرتبة.</summary>
        public static string T(string key, params object[] args)
        {
            var raw = T(key);
            try { return string.Format(raw, args); }
            catch { return raw; }
        }

        /// <summary>وصف دور بصري حسب لغة الواجهة — يستبدل نصوص AppPermissions
        /// الصلبة في كل مكان يُعرض فيه وصف الدور للمستخدم (جداول/قوائم).
        /// أسماء الشاشات نفسها تُستبدل عبر PermScreenName أدناه.</summary>
        public static string PermDescribe(int roleValue)
        {
            if (roleValue == Common.Utils.AppPermissions.AdminRoleValue) return T("Perm.Admin");
            if (roleValue == Common.Utils.AppPermissions.AccountantRoleValue) return T("Perm.Accountant");
            if (roleValue == Common.Utils.AppPermissions.CashierRoleValue) return T("Perm.Cashier");
            if (roleValue == 0) return T("Perm.None");

            int count = 0;
            foreach (var (bit, _) in Common.Utils.AppPermissions.EditableScreens)
                if (Common.Utils.AppPermissions.Has(roleValue, bit)) count++;

            if (count == Common.Utils.AppPermissions.EditableScreens.Length) return T("Perm.AllScreens");
            return count == 1 ? T("Perm.OneScreen") : T("Perm.NScreens", count);
        }

        /// <summary>اسم الشاشة من بتّها بلغة الواجهة — فهرس البت داخل كتالوج
        /// EditableScreens يوازي ترتيب مفاتيح Perm.Screens.* في ملفات اللغة.</summary>
        public static string PermScreenName(int screenBit)
        {
            var catalog = Common.Utils.AppPermissions.EditableScreens;
            int idx = Array.FindIndex(catalog, s => s.Bit == screenBit);
            return idx >= 0 ? T("Perm.Screens." + idx switch
            {
                0 => "Products",
                1 => "Inventory",
                2 => "Customers",
                3 => "Suppliers",
                4 => "SalesInvoices",
                5 => "PurchaseInvoices",
                6 => "Expenses",
                7 => "Reports",
                8 => "Employees",
                9 => "Users",
                _ => "Settings"
            }) : screenBit.ToString();
        }

        // ── تحميل ملفات اللغة: قراءة متسامحة — أي خلل = جدول فارغ و fallback كامل للعربية ──
        private static Dictionary<string, string> Load(string code)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Strings", code + ".json");
                if (!File.Exists(path)) return new Dictionary<string, string>();
                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                return dict ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
