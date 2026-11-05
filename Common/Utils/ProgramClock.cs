namespace Common.Utils
{
    /// <summary>
    /// ساعة «يوم البرنامج» للطبقة المشتركة (VULN-45 — قرار المالك الموثق):
    ///
    /// 📖 السياسة الموحدة:
    ///  • التخزين: كل الطوابع الزمنية المعاملية في قاعدة البيانات UTC
    ///    (DateTime.UtcNow في EF، GETUTCDATE()/SYSUTCDATETIME() في SQL الخام).
    ///  • «اليوم» المفهوم للمحل (تقارير Z، حدود FEFO، الاستردادات، الفترات
    ///    المالية) = تاريخ ساعة البرنامج = UTC + إزاحة ثابتة (+3 السعودية).
    ///
    /// طبقة الوصول للبيانات (Sayl.Core) لا ترى AppClock (Sayl.UI داخلي)،
    /// فهذا هو المصدر الموحّد لليوم البرمجي في الخدمات — ويشترك AppClock
    /// (واجهة المستخدم) معه في الإزاحة نفسها فلا يتفرق الرقمان أبدًا.
    /// الإزاحة ثابتة بقصد (لا ToLocalTime()) — انظر توثيق AppClock.
    /// </summary>
    public static class ProgramClock
    {
        /// <summary>إزاحة توقيت البرنامج عن UTC (ساعات). +3 = توقيت السعودية.
        /// ثابتة بقصد — لو احتجت قابلية ضبط مستقبلًا: انقلها لإعداد Settings
        /// واقرأها من هنا وممن AppClock معًا.</summary>
        public const int UtcOffsetHours = 3;

        /// <summary>الوقت الحالي بتوقيت البرنامج (UTC + الإزاحة).</summary>
        public static DateTime NowLocal => DateTime.UtcNow.AddHours(UtcOffsetHours);

        /// <summary>«اليوم» بمعنى المحل: تاريخ ساعة البرنامج — حدود اليوم
        /// للتقارير اليومية (Z) وفحص صلاحية الدفعات (FEFO) وتواريخ الدفع.</summary>
        public static DateOnly Today => DateOnly.FromDateTime(NowLocal);

        /// <summary>تحويل طابع UTC مخزّن إلى «يوم البرنامج» (للتقارير والحدود اليومية).</summary>
        public static DateOnly FromUtc(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(UtcOffsetHours));
    }
}
