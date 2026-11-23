namespace Common.Utils
{
    /// <summary>
    /// ساعة «يوم البرنامج» للطبقة المشتركة (VULN-45 — قرار المالك الموثق):
    ///
    /// 📖 السياسة الموحدة:
    ///  • التخزين: كل الطوابع الزمنية المعاملية في قاعدة البيانات UTC
    ///    (DateTime.UtcNow في EF، GETUTCDATE()/SYSUTCDATETIME() في SQL الخام).
    ///  • «اليوم» المفهوم للمحل (تقارير Z، حدود FEFO، الاستردادات، الفترات
    ///    المالية) = تاريخ ساعة البرنامج — مشتق من UTC بتوقيت المتجر المركزي
    ///    (إعداد StoreTimeZoneId الذي ينشره جهاز المضيف عند الإقلاع عبر
    ///    clsStoreClockService ← ProgramClock.ConfigureStoreTimeZone).
    ///  • الإزاحة الثابتة (+3 السعودية) هي احتياطي إرثي فقط: يعمل قبل ضبط
    ///    التوقيت المركزي (فترة الانتقال بين النشرات أو عند فشل إقلاع
    ///    ساعة المتجر — برسالة تحذير وإكمال إقلاع) — ولا يُستخدم أبدًا
    ///    التوقيت المحلي لويندوز على جهاز البيع.
    ///
    /// طبقة الوصول للبيانات (Sayl.Core) لا ترى AppClock (Sayl.UI داخلي)،
    /// فهذا هو المصدر الموحّد لليوم البرمجي في الخدمات — ويشترك AppClock
    /// (واجهة المستخدم) معه في التحويل نفسه عبر التفويض فلا يتفرق الرقمان أبدًا.
    ///
    /// ⚠️ ملاحظة: NowLocal ما زال مشتقًا من ساعة العميل DateTime.UtcNow +
    /// التوقيت المركزي. التقاط انحراف ساعة SQL (GetUtcNowAsync) كمصدر للوقت
    /// مهمة مستقبلية صريحة — لم تُنفَّذ هنا.
    /// </summary>
    public static class ProgramClock
    {
        /// <summary>إزاحة توقيت البرنامج عن UTC (ساعات). +3 = توقيت السعودية.
        /// قيمة الاحتياطي الإرثي فقط — تُستخدم حين لا يُضبط التوقيت المركزي.
        /// التوقيت الفعّال بعد الإقلاع يأتي من التوقيت المنشور (StoreTimeZone).</summary>
        public const int UtcOffsetHours = 3;

        /// <summary>التوقيت المركزي المنشور لساعة المتجر — null = الاحتياطي الإرثي
        /// (UTC + الإزاحة الثابتة). يُضبط عند نجاح إقلاع clsStoreClockService.</summary>
        private static TimeZoneInfo? _storeTimeZone;

        /// <summary>ضبط (أو تصفير) التوقيت المركزي لساعة المتجر. يُنادى من
        /// clsStoreClockService عند نجاح الإقلاع؛ null = رجوع للاحتياطي الإرثي.</summary>
        public static void ConfigureStoreTimeZone(TimeZoneInfo? timeZone)
            => _storeTimeZone = timeZone;

        /// <summary>التوقيت المركزي المنشور حاليًا (للتشخيص والاختبارات) —
        /// null يعني الاحتياطي الإرثي (لم يُضبط التوقيت المركزي بعد).</summary>
        public static TimeZoneInfo? StoreTimeZone => _storeTimeZone;

        /// <summary>تحويل طابع UTC مخزّن إلى توقيت البرنامج: بالتوقيت المركزي
        /// المنشور إن ضُبط، وإلا بالإزاحة الثابتة (+3 الاحتياطي الإرثي).
        /// Kind==Local تُعاد كما هي (بيانات قديمة قبل الترحيل — عُرضت محلية
        /// دائمًا فلا تُزاح مرتين)، وUnspecified/Utc تُعامل UTC (نفس عهد التخزين).</summary>
        public static DateTime ToLocal(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local)
                return utc; // بيانات قديمة قبل الترحيل — عُرضت محلية دائمًا فلا تُزاح مرتين
            if (_storeTimeZone != null)
                return TimeZoneInfo.ConvertTimeFromUtc(
                    new DateTime(utc.Ticks, DateTimeKind.Utc), _storeTimeZone); // Unspecified -> Utc
            return utc.AddHours(UtcOffsetHours);
        }

        /// <summary>الوقت الحالي بتوقيت البرنامج (ساعة العميل UTC + التوقيت
        /// المركزي، أو + الإزاحة الإرثية قبل الضبط).</summary>
        public static DateTime NowLocal => ToLocal(DateTime.UtcNow);

        /// <summary>«اليوم» بمعنى المحل: تاريخ ساعة البرنامج — حدود اليوم
        /// للتقارير اليومية (Z) وفحص صلاحية الدفعات (FEFO) وتواريخ الدفع.</summary>
        public static DateOnly Today => DateOnly.FromDateTime(NowLocal);

        /// <summary>تحويل طابع UTC مخزّن إلى «يوم البرنامج» (للتقارير والحدود اليومية).</summary>
        public static DateOnly FromUtc(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));
    }
}
