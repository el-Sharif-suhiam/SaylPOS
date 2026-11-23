namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// ساعة النظام الموحدة (VULN-45 — الحل ب، بأمر المالك):
    ///
    /// 📖 الفلسفة بعد الترحيل UTC وترحيل الساعة للمتجر المركزي:
    ///  • التخزين: كل الطواسب في قاعدة البيانات UTC (DateTime.UtcNow) —
    ///    لا يتأثر بساعة الجهاز ولا بمنطقها، والترحيل التاريخي (Upgrade_08)
    ///    حوّل كل البيانات القديمة للUTC فلا انقطاع زمني.
    ///  • العرض: «توقيت المتجر المركزي» — الإعداد StoreTimeZoneId الذي ينشره
    ///    جهاز المضيف عند الإقلاع (clsStoreClockService ← ProgramClock).
    ///    قبل ضبطه (فترة انتقال النشرات / فشل إقلاع الساعة) يعمل الاحتياطي
    ///    الإرثي: UTC + إزاحة ثابتة (+3 السعودية). لماذا الإزاحة الثابتة
    ///    احتياطيًا لا ToLocalTime()؟ لأن هدف المالك صريح: الكاشير والأدمن
    ///    يعتمدون على ساعة البرنامج «هكذا لن تحدث لخبطة» — إعدادات ويندوز
    ///    الإقليمية (تغيير المنطقة/التوقيت الصيفي في أجهزة البيع) لا تُقفز
    ///    فجأة أمام المستخدمين، والتوقيت الفعّال يأتي من المضيف المركزي وحده.
    ///
    /// 🎯 قاعدة استخدام موحدة:
    ///  • أينما كتبت «CreatedAt = ...» في الكود: UtcNow (مباشر).
    ///  • أينما عرضت وقتًا للمستخدم (شاشة/تقرير/PDF/Excel/ساعة حية):
    ///    AppClock.ToLocal(...) — التحويل الوحيد المعتمد.
    ///  • منطق الأعمال اليومي (تقارير «اليوم»): AppClock.Today /
    ///    AppClock.NowLocal — لأن «اليوم» مفهوم محلي للمحل.
    /// </summary>
    internal static class AppClock
    {
        /// <summary>إزاحة العرض عن UTC (ساعات) — قيمة الاحتياطي الإرثي فقط،
        /// تُستخدم قبل ضبط التوقيت المركزي. المصدر الموحّد: ProgramClock
        /// (Common) — التوقيت الفعّال بعد الإقلاع يأتي من التوقيت المركزي
        /// المنشور (StoreTimeZoneId من جهاز المضيف).</summary>
        public const int UtcOffsetHours = Common.Utils.ProgramClock.UtcOffsetHours;

        /// <summary>الساعة الحية المعروضة على الشاشات — توقيت البرنامج
        /// (تفويض لساعة الطبقة المشتركة: توقيت المتجر المركزي أو الاحتياطي الإرثي).</summary>
        public static DateTime NowLocal => Common.Utils.ProgramClock.NowLocal;

        /// <summary>«اليوم» بمعنى المحل: تاريخ ساعة البرنامج (للتقارير اليومية).</summary>
        public static DateOnly Today => Common.Utils.ProgramClock.Today;

        /// <summary>تحويل أي طابع UTC مخزن إلى توقيت البرنامج للعرض
        /// (تفويض لساعة الطبقة المشتركة — القاعدة الوحيدة المعتمدة).</summary>
        public static DateTime ToLocal(DateTime utc) => Common.Utils.ProgramClock.ToLocal(utc);

        /// <summary>تنسيق العرض الموحد: التاريخ والوقت كاملين بتوقيت البرنامج.</summary>
        public static string Format(DateTime? utc) =>
            utc.HasValue ? ToLocal(utc.Value).ToString("yyyy-MM-dd HH:mm") : "-";

        /// <summary>تنسيق العرض القصير (تاريخ فقط) بتوقيت البرنامج.</summary>
        public static string FormatDate(DateTime? utc) =>
            utc.HasValue ? ToLocal(utc.Value).ToString("dd/MM/yyyy") : "-";
    }

    /// <summary>
    /// 📖 ساعة حية (VULN-45 + طلب المالك): عنصر عنوان يعرض تاريخ ووقت
    /// «البرنامج» (توقيت المتجر المركزي / الاحتياطي +3) ويتحدث كل ثانية —
    /// ناشر موحد واحد للتطبيق كله.
    ///
    /// الاستخدام: أضف AppTheme.ClockStrip للشريط العلوي لأي شاشة، أو
    /// استدعِ AppClockStatusLabel.Attach(label) لربط Label موجود.
    /// الساعة تقرأ من AppClock.NowLocal — مصدر واحد لكل النظام.
    /// </summary>
    internal static class AppClockLabel
    {
        private static readonly Dictionary<Label, System.Windows.Forms.Timer> _bound = new();

        /// <summary>ربط Label بساعة البرنامج الحية (يحدث كل ثانية).</summary>
        public static void Attach(Label label, bool withDate = true)
        {
            if (label == null) return;
            if (_bound.ContainsKey(label)) return; // لا تكرار

            void Update() =>
                label.Text = withDate
                    ? AppClock.NowLocal.ToString("dddd dd/MM/yyyy  —  HH:mm:ss")
                    : AppClock.NowLocal.ToString("HH:mm:ss");

            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) => Update();
            Update(); // أول رسم فوري
            timer.Start();

            // موت المالك = موت المؤقت (لا تسريب)
            label.Disposed += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                _bound.Remove(label);
            };

            _bound[label] = timer;
        }
    }
}
