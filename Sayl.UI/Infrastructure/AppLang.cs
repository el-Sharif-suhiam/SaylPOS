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
