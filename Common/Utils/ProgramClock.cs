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
    ///    clsStoreClockService ← ProgramClock.ConfigureStoreClock).
    ///  • الإزاحة الثابتة (+3 السعودية) هي احتياطي إرثي فقط: يعمل قبل ضبط
    ///    التوقيت المركزي (فترة الانتقال بين النشرات أو عند فشل إقلاع
    ///    ساعة المتجر — برسالة تحذير وإكمال إقلاع) — ولا يُستخدم أبدًا
    ///    التوقيت المحلي لويندوز على جهاز البيع.
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
    /// 📖 مصدر الوقت الموثق = ساعة SQL Server (قرار المالك — Task 6):
    ///  • UtcNow = DateTime.UtcNow + انحراف ساعة العميل عن ساعة SQL
    ///    (ClientClockSkew) — يُلتقط عند إقلاع ساعة المتجر من SYSUTCDATETIME
    ///    ويُحدَّث مع كل قراءة GetUtcNowAsync ناجحة، فتُكتب الطوابع الزمنية
    ///    المعاملية من ساعة الخادم الموثقة لا من ساعة كل طرفية منحرفة.
    ///  • قبل الإقلاع (انحراف صفري) UtcNow ≡ DateTime.UtcNow — الاحتياطي الإرثي.
    ///  • الانحراف بين التحديثات على عملية طويلة العمر انحراف ساعة العميل
    ///    الذاتي فقط (زحل بطيء) — مقايضة موثقة مقبولة.
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

        /// <summary>انحراف ساعة العميل عن ساعة SQL Server المنشور (S ← O):
        /// ClientClockSkew = ساعة SQL − ساعة العميل، يُلتقط عند إقلاع ساعة
        /// المتجر ويُحدَّث مع كل قراءة GetUtcNowAsync ناجحة. صفر قبل الضبط —
        /// حينها UtcNow ≡ DateTime.UtcNow (الاحتياطي الإرثي).</summary>
        private static TimeSpan _clientClockSkew;

        /// <summary>ضبط (أو تصفير) التوقيت المركزي لساعة المتجر وانحراف ساعة
        /// العميل عن ساعة SQL معًا. يُنادى من clsStoreClockService عند نجاح
        /// الإقلاع؛ null = رجوع للاحتياطي الإرثي زمنيًا (الانحراف يبقى كما
        /// مُرِّر — الغلاف الإرثي يمرر صفرًا فلا يتسرب انحراف بين الاختبارات).</summary>
        public static void ConfigureStoreClock(TimeZoneInfo? timeZone, TimeSpan clientClockSkew)
        {
            _storeTimeZone = timeZone;
            _clientClockSkew = clientClockSkew;
        }

        /// <summary>الغلاف الإرثي — تكافئ ConfigureStoreClock(timeZone, TimeSpan.Zero).
        /// أبقيته لأي مستدعٍ خارجي؛ المسار الجديد يمرر الانحراف صراحةً.</summary>
        public static void ConfigureStoreTimeZone(TimeZoneInfo? timeZone)
            => ConfigureStoreClock(timeZone, TimeSpan.Zero);

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

        /// <summary>الوقت الحالي الموثق من ساعة SQL Server (بصفر تكلفة نداء):
        /// ساعة العميل UTC + انحراف ساعة SQL المنشور (ClientClockSkew) —
        /// Kind=Utc. الانحراف يُلتقط عند إقلاع ساعة المتجر (SYSUTCDATETIME)
        /// ويُحدَّث مع كل قراءة GetUtcNowAsync ناجحة؛ بين التحديثات على عملية
        /// طويلة العمر يبقى انحراف ساعة العميل الذاتي الزحل البطيء — مقايضة
        /// موثقة. قبل الضبط انحراف صفري فتساوي DateTime.UtcNow تمامًا.</summary>
        public static DateTime UtcNow => DateTime.UtcNow + _clientClockSkew;

        /// <summary>الوقت الحالي بتوقيت البرنامج (الساعة الموثقة من SQL +
        /// التوقيت المركزي، أو + الإزاحة الإرثية قبل الضبط).</summary>
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
        /// الاحتياطي الإرثي (بلا توقيت مركزي): إزاحة ثابتة حسابية صرفة (+3) —
        /// الإزاحة الثابتة لا تنتج أوقاتًا غير صالحة أو ملتبسة أبدًا فلا حاجة
        /// لقواعد المنطقة.</summary>
        public static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly localDate)
        {
            TimeZoneInfo? tz = _storeTimeZone;
            if (tz == null)
            {
                // الاحتياطي الإرثي: +3 ثابتة — حساب صرف
                return (localDate.ToDateTime(TimeOnly.MinValue).AddHours(-UtcOffsetHours),
                        localDate.AddDays(1).ToDateTime(TimeOnly.MinValue).AddHours(-UtcOffsetHours));
            }
            return (ConvertLocalToUtc(localDate.ToDateTime(TimeOnly.MinValue), tz),
                    ConvertLocalToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
        }
    }
}
