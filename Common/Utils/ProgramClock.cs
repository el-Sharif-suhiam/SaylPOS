using System.Diagnostics;

namespace Common.Utils
{
    /// <summary>
    /// ساعة «يوم البرنامج» للطبقة المشتركة (VULN-45 — قرار المالك الموثق):
    ///
    /// 📖 السياسة الموحدة:
    ///  • التخزين: كل الطوابع الزمنية المعاملية في قاعدة البيانات UTC
    ///    (ساعة SQL الموثقة في EF، GETUTCDATE()/SYSUTCDATETIME() في SQL الخام).
    ///  • «اليوم» المفهوم للمحل (تقارير Z، حدود FEFO، الاستردادات، الفترات
    ///    المالية) = تاريخ ساعة البرنامج — مشتق من UTC بتوقيت المتجر المركزي
    ///    (إعداد StoreTimeZoneId الذي ينشره جهاز المضيف عند الإقلاع عبر
    ///    clsStoreClockService ← ProgramClock).
    ///
    /// طبقة الوصول للبيانات (Sayl.Core) لا ترى AppClock (Sayl.UI داخلي)،
    /// فهذا هو المصدر الموحّد لليوم البرمجي في الخدمات — ويشترك AppClock
    /// (واجهة المستخدم) معه في التحويل نفسه عبر التفويض فلا يتفرق الرقمان أبدًا.
    ///
    /// 📖 المصدر الوحيد لكل عمليات التحويل (VULN-45 — قرار المالك الموثق):
    ///  • UTC ← توقيت البرنامج: ToLocal/FromUtc.
    ///  • توقيت البرنامج ← UTC: ConvertLocalToUtc (القواعد الملتبسة/غير الصالحة).
    ///  • حدود اليوم/الفترة بمعنى UTC: LocalDayBoundsUtc — الخدمات المُخدومة
    ///    (ساعة المتجر، التقارير، الفواتير) تفوّض إليه ولا تبني الحدود يدويًا
    ///    بأي إزاحة مكتوبة بشريًا، فلا يتفرق رقمان بين أي طرفين أبدًا.
    /// بلا اعتماديات EF/قاعدة بيانات — طبقة مشتركة نقية (Common).
    ///
    /// 📖 مصدر الوقت الموثق = ساعة SQL Server + مرساة رتيبة (قرار المالك — Task 1):
    ///  • clsStoreClockService يقرأ SYSUTCDATETIME عند الإقلاع ومع كل قراءة
    ///    ناجحة فينشر «مرساة SQL»: لحظة UTC من الخادم + لقطة Stopwatch لحظة
    ///    النشر معًا (تحت قفل — لا يمزج القارئ مرساة قديمة بلقطة جديدة).
    ///  • UtcNow = المرساة + الزمن الرتيب المنقضي (Stopwatch) — لا ساعة حائط
    ///    للعميل إطلاقًا: انحراف الأجهزة وتراجع الساعة مستحيلان بين القراءات.
    ///  • قبل النشر (بلا إقلاع ناجح) كل العمليات ترمي استثناء الفشل المغلق
    ///    (ClockNotInitializedException) — لا احتياطي +3 ولا ساعة جهاز أبدًا.
    ///  • هذه المرساة للعرض والتقدم الرتيب بين القراءات؛ الطوابع المعاملية
    ///    المحفوظة (Task 5) تلتقط ساعة SQL مباشرة عبر
    ///    clsStoreClockService.GetUtcNowAsync(db) — لا خدمة تحفظ من ساكن.
    /// </summary>
    public static class ProgramClock
    {
        /// <summary>استثناء الفشل المغلق: عملية ساعة قبل نشر التوقيت المركزي
        /// والمرساة (بلا إقلاع ناجح لساعة المتجر) — رسالة عربية إرشادية.
        /// بديل الاحتياطي الإرثي الملغى (+3): لا وقت افتراضي بعد اليوم.</summary>
        public class ClockNotInitializedException : Exception
        {
            public ClockNotInitializedException(string message)
                : base(message) { }
        }

        /// <summary>التوقيت المركزي المنشور لساعة المتجر — null قبل الإقلاع
        /// الناجح (كل عمليات التحويل ترمي ClockNotInitializedException حينها).</summary>
        private static TimeZoneInfo? _storeTimeZone;

        // ══ مرساة ساعة SQL + الزمن الرتيب (النشر تحت قفل واحد) ══
        /// <summary>قفل النشر/القراءة — يضمن ذرّية الزوج (لحظة SQL + لقطة
        /// Stopwatch) كي لا يمزج قارئ مرساة قديمة بلقطة Stopwatch جديدة.</summary>
        private static readonly object _anchorGate = new();

        /// <summary>آخر لحظة UTC مقروءة من ساعة SQL Server (SYSUTCDATETIME).</summary>
        private static DateTime _sqlAnchorUtc;

        /// <summary>لقطة Stopwatch.GetTimestamp() لحظة نشر المرساة — الإزاحة
        /// الرتيبة منها هي «المنقضي منذ آخر قراءة SQL» لا ساعة الحائط.</summary>
        private static long _anchorStopwatchTicks;

        /// <summary>هل نُشرت المرساة والتوقيت (نجاح إقلاع) — القراءة تحت القفل.</summary>
        private static bool _anchorPublished;

        /// <summary>ضبط التوقيت المركزي لساعة المتجر. يُنادى من
        /// clsStoreClockService بعد نجاح الإقلاع (التوقيت + المرساة معًا) —
        /// وnull يصفّر الحالة كليًا (نشر شيئًا = فشل مغلق، كما في الاختبارات).</summary>
        public static void ConfigureStoreTimeZone(TimeZoneInfo? timeZone)
        {
            if (timeZone == null)
            {
                // تصفير كامل: بلا توقيت فلا مرساة — الفشل المغلق هو الحالة الوحيدة
                lock (_anchorGate)
                {
                    _storeTimeZone = null;
                    _anchorPublished = false;
                    _sqlAnchorUtc = default;
                    _anchorStopwatchTicks = 0;
                }
                return;
            }
            // نشر توقيت فقط (بلا مرساة جديدة) — المستخدم في الاختبارات المحلية
            lock (_anchorGate) { _storeTimeZone = timeZone; }
        }

        /// <summary>نشر مرساة ساعة SQL: آخر لحظة UTC من الخادم + لقطة Stopwatch
        /// لحظة النشر — ذرّيًا تحت القفل. يُنادى من clsStoreClockService عند
        /// الإقلاع ومع كل قراءة GetUtcNowAsync ناجحة (لاحق الزحل الذاتي).</summary>
        public static void PublishSqlAnchor(DateTime sqlUtc)
        {
            lock (_anchorGate)
            {
                if (sqlUtc.Kind != DateTimeKind.Utc)
                    sqlUtc = new DateTime(sqlUtc.Ticks, DateTimeKind.Utc);
                _sqlAnchorUtc = sqlUtc;
                _anchorStopwatchTicks = Stopwatch.GetTimestamp();
                _anchorPublished = true;
            }
        }

        /// <summary>بذرة الاختبارات (داخلي): توقيت + مرساة اصطناعية بلقطة
        /// Stopwatch معطاة — يسمح بمحاكاة «مرساة في الماضي» لاختبار التقدم
        /// الرتيب بشكل محدد لا معتمد على ساعة الحائط.</summary>
        internal static void ConfigureForTests(TimeZoneInfo timeZone, DateTime sqlAnchorUtc, long stopwatchTicks)
        {
            lock (_anchorGate)
            {
                _storeTimeZone = timeZone;
                _sqlAnchorUtc = new DateTime(sqlAnchorUtc.Ticks, DateTimeKind.Utc);
                _anchorStopwatchTicks = stopwatchTicks;
                _anchorPublished = true;
            }
        }

        /// <summary>التوقيت المركزي المنشور حاليًا (للتشخيص والاختبارات) —
        /// null يعني بلا إقلاع ناجح بعد (الفشل المغلق).</summary>
        public static TimeZoneInfo? StoreTimeZone => _storeTimeZone;

        /// <summary>الوقت الحالي الموثق: مرساة ساعة SQL + الزمن الرتيب المنقضي
        /// منذ نشرها (Stopwatch — لا ساعة الحائط أبدًا، فتراجع ساعة الجهاز
        /// أو قفزاته لا تلمس هذه الساعة). قبل النشر: فشل مغلق (استثناء).</summary>
        public static DateTime UtcNow
        {
            get
            {
                lock (_anchorGate)
                {
                    if (!_anchorPublished)
                        throw new ClockNotInitializedException(
                            "لم تُهيَّأ ساعة البرنامج بعد — يجب نجاح إقلاع ساعة المتجر (InitializeAsync) قبل أي قراءة للوقت.");
                    long elapsedTicks = Stopwatch.GetTimestamp() - _anchorStopwatchTicks;
                    return _sqlAnchorUtc.AddTicks(StopwatchTicksToTimeSpan(elapsedTicks).Ticks);
                }
            }
        }

        /// <summary>تحويل تردد Stopwatch إلى TimeSpan (مقسوم على التردد — دقيق).</summary>
        private static TimeSpan StopwatchTicksToTimeSpan(long elapsedStopwatchTicks)
        {
            // Stopwatch.Frequency تردد ثابت للعملية؛ الضرب أولًا يقلل خطأ القسمة
            return TimeSpan.FromTicks(elapsedStopwatchTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        }

        /// <summary>تحويل طابع UTC مخزّن إلى توقيت البرنامج: بالتوقيت المركزي
        /// المنشور — وبلا توقيت منشور: فشل مغلق (استثناء). الاستثناء الوحيد:
        /// Kind==Local تُعاد كما هي (بيانات قديمة قبل الترحيل — عُرضت محلية
        /// دائمًا فلا تُزاح مرتين) — قاعدة توافق تاريخية لا إذن توقيت ثابت.
        /// Unspecified/Utc تُعامل UTC (نفس عهد التخزين).</summary>
        public static DateTime ToLocal(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local)
                return utc; // بيانات قديمة قبل الترحيل — عُرضت محلية دائمًا فلا تُزاح مرتين
            TimeZoneInfo? tz = _storeTimeZone
                ?? throw new ClockNotInitializedException(
                    "لم يُضبط توقيت المتجر المركزي بعد — لا يمكن تحويل الوقت دون إقلاع ساعة المتجر ناجحًا.");
            return TimeZoneInfo.ConvertTimeFromUtc(
                new DateTime(utc.Ticks, DateTimeKind.Utc), tz); // Unspecified -> Utc
        }

        /// <summary>الوقت الحالي بتوقيت البرنامج (مرساة SQL الرتيبة + التوقيت
        /// المركزي) — قبل الإقلاع يرمي فشلًا مغلقًا عبر UtcNow/ToLocal.</summary>
        public static DateTime NowLocal => ToLocal(UtcNow);

        /// <summary>«اليوم» بمعنى المحل: تاريخ ساعة البرنامج — حدود اليوم
        /// للتقارير اليومية (Z) وفحص صلاحية الدفعات (FEFO) وتواريخ الدفع.</summary>
        public static DateOnly Today => DateOnly.FromDateTime(NowLocal);

        /// <summary>تحويل طابع UTC مخزّن إلى «يوم البرنامج» (للتقارير والحدود اليومية).</summary>
        public static DateOnly FromUtc(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

        /// <summary>تحويل توقيت البرنامج المحلي إلى UTC — المصدر الموحّد للتحويل
        /// العكسي (خدمة ساعة المتجر تفوّض إليه):
        ///  • الوقت غير الصالح (ثغرة تقديم الساعة — لا منتصف ليل فعليًا) يُزيَّح
        ///    بمقدار الدلتا الصيفية للقاعدة السارية كي لا ينفجر التحويل.
        ///  • الوقت الملتبس (تأخير الساعة — منتصف ليل يتكرر مرتين) يُحوَّل إلى
        ///    أبكر لحظة UTC له محسوبة من إزاحات المنطقة الفعلية نفسها
        ///    (GetAmbiguousTimeOffsets — تراعي BaseUtcOffsetDelta لقواعد تُزيح
        ///    الإزاحة الأساسية) لا من إعادة بناء يدوية — ConvertTimeToUtc يفترض
        ///    التفسير القياسي للملتبس فيختار اللحظة الأحدث ويُسقط الساعة
        ///    المتكررة الأولى من حدود اليوم، لذا نحسمه بأنفسنا.</summary>
        private static DateTime ConvertLocalToUtc(DateTime local, TimeZoneInfo tz)
        {
            if (tz.IsInvalidTime(local))
            {
                TimeSpan delta = tz.GetAdjustmentRules()
                    .Where(r => r.DateStart.Date <= local.Date && r.DateEnd.Date >= local.Date)
                    .Select(r => r.DaylightDelta)
                    .Append(TimeSpan.Zero)
                    .First(d => !tz.IsInvalidTime(local + d));
                local += delta;
            }

            if (tz.IsAmbiguousTime(local))
            {
                // أبكر لحظة UTC من إزاحات المنطقة الفعلية للوقت الملتبس —
                // GetAmbiguousTimeOffsets تراعي BaseUtcOffsetDelta أيضًا.
                return tz.GetAmbiguousTimeOffsets(local)
                    .Select(offset => new DateTime(local.Ticks - offset.Ticks, DateTimeKind.Utc))
                    .Min();
            }

            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        /// <summary>حدود «يوم البرنامج» للتاريخ المحلي المعطى بمعنى UTC — المصدر
        /// الموحّد لكل حدود الأيام والفترات (تقارير، فواتير، جلسات): منتصفا ليل
        /// اليوم والتالي محوَّلان عائدًا إلى UTC — شامل البداية وحصري النهاية
        /// (قارِن بـ ≥ البداية و < النهاية). طول اليوم (23/24/25 ساعة) تُحسبه
        /// قواعد ويندوز الديناميكية تلقائيًا مع التوقيت المركزي المنشور.
        /// قبل الإقلاع الناجح: فشل مغلق (استثناء) — لا إزاحة ثابتة بديلة أبدًا.</summary>
        public static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate)
        {
            TimeZoneInfo tz = _storeTimeZone
                ?? throw new ClockNotInitializedException(
                    "لم يُضبط توقيت المتجر المركزي بعد — لا يمكن حساب حدود اليوم دون إقلاع ساعة المتجر ناجحًا.");
            return (ConvertLocalToUtc(localDate.ToDateTime(TimeOnly.MinValue), tz),
                    ConvertLocalToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
        }
    }
}
