using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    /// <summary>
    /// ساعة المتجر المركزية (أساس Task ساعة المحل المركزية):
    ///
    /// 📖 البنية المخدومة: جهاز Windows مركزي واحد يستضيف SQL Server والتطبيق،
    /// وأجهزة البيع (POS) تتصل به عبر الشبكة المحلية. الجهاز المركزي هو المصدر
    /// الموثوق للتوقيت — لا إعدادات توقيت للمستخدم إطلاقًا:
    ///  • الكشف: يُقارَن MachineName للتطبيق مع MachineName لخادم SQL
    ///    (SERVERPROPERTY('MachineName')) بتجاهل الحالة والفراغات الطرفية.
    ///  • الجهاز المضيف وحده يكتب معرّف توقيته المحلي (TimeZoneInfo.Local.Id)
    ///    في الإعدادات (StoreTimeZoneId) عند كل إقلاع — فيلتقط أي تغيير توقيت
    ///    ويندوز أو قواعد التوقيت الصيفي تلقائيًا.
    ///  • أجهزة البيع تقرأ المعرّف فقط ولا تكتب أبدًا — ولا رجوع إلى توقيت الجهاز
    ///    المحلي أبدًا؛ غياب الإعداد أو معرّف تالف = فشل إقلاع الساعة
    ///    (StoreClockInitializationException) برسالة عربية إرشادية، ويكمل
    ///    الإقلاع (محاولة الإقلاع التالي كما في فلسفة الكود المحيط).
    ///  • الساعة نفسها من SQL Server: SYSUTCDATETIME() هو المصدر الموحّد لـ UTC
    ///    (نفس عهد التخزين)، والتحويل إلى توقيت المتجر بقواعد ويندوز الديناميكية
    ///    (TimeZoneInfo.ConvertTimeFromUtc) — بلا أي إزاحة ثابتة.
    /// مسجّل كـ Singleton: يفتح نطاقًا (IServiceScopeFactory) لكل عملية قاعدة
    /// لأنه يعيش أطول من أي نطاق. الحالة القابلة للتغيّر تُضبط مرة واحدة عند
    /// الإقلاع (قبل فتح أي نافذة) ثم تصبح للقراءة فقط — بلا أقفال معقدة.
    /// </summary>
    public class clsStoreClockService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        /// <summary>التوقيت المخزَّن بعد نجاح الإقلاع — null قبل الإقلاع.</summary>
        private TimeZoneInfo? _storeTimeZone;

        /// <summary>هل تعمل العملية على جهاز خادم SQL نفسه — يُحدَّد عند الإقلاع.</summary>
        public bool IsSqlHost { get; private set; }

        private bool _initialized;

        public clsStoreClockService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        /// <summary>مُنشئ الاختبارات فقط (داخلي): forceHost يفرع المسار إجباريًا —
        /// true = مسار المضيف، false = مسار الطرفي (حتى على جهاز IS هو خادم SQL)،
        /// null = الكشف الطبيعي. ويتيح حقن توقيت ثابت بدل توقيت الجهاز.</summary>
        internal clsStoreClockService(SaylDbContext dbContext, TimeZoneInfo? timeZoneInfo = null,
            bool? forceHost = null)
        {
            _scopeFactory = null!;
            _dbContextForTests = dbContext;
            _injectedTimeZone = timeZoneInfo;
            _forcedHost = forceHost;
        }

        // ══ بذور مسار الاختبار (الإقلاع قبل فتح النوافذ — بلا أقفال) ══
        private readonly SaylDbContext? _dbContextForTests;
        private readonly TimeZoneInfo? _injectedTimeZone;
        private readonly bool? _forcedHost;

        /// <summary>استثناء فشل إقلاع ساعة المتجر — رسالة عربية إرشادية للمستدعي.</summary>
        public class StoreClockInitializationException : Exception
        {
            public StoreClockInitializationException(string message, Exception? inner = null)
                : base(message, inner) { }
        }

        /// <summary>مساعد ثابت قابل للاختبار: هل الجهازان نفسه؟ (تجاهل الحالة
        /// والفراغات الطرفية — ordinal ignore-case على نصّين مقصوصين).</summary>
        public static bool IsSameHostIgnoringCase(string a, string b)
        {
            string ta = (a ?? "").Trim();
            string tb = (b ?? "").Trim();
            return string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>إقلاع الساعة: كشف المضيف ثم فرع المضيف (كتابة) أو الطرفي (قراءة).
        /// يُنادى مرة واحدة عند بدء التشغيل قبل فتح أي نافذة.</summary>
        public Task InitializeAsync() => InitializeCoreAsync(forceHost: _forcedHost);

        /// <summary>جوهر الإقلاع (داخلي للاختبارات): forceHost يفرع المسار إجباريًا
        /// (true مضيف / false طرفي / null كشف طبيعي) — LocalDB في الاختبارات يعمل
        /// محليًا فيمر الكشف الطبيعي بمسار المضيف، فالاختبارات تفرع صراحةً.</summary>
        internal async Task InitializeCoreAsync(bool? forceHost)
        {
            if (_dbContextForTests != null)
                await InitOnContextAsync(_dbContextForTests, forceHost);
            else
                await using (var scope = _scopeFactory.CreateAsyncScope())
                    await InitOnContextAsync(scope.ServiceProvider.GetRequiredService<SaylDbContext>(), forceHost);
        }

        private async Task InitOnContextAsync(SaylDbContext db, bool? forceHost)
        {
            // 1) كشف الجهاز: MachineName لخادم SQL مقابل جهاز العملية
            string? sqlHost;
            try
            {
                sqlHost = (await db.Database
                    .SqlQueryRaw<string>("SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)) AS [Value]")
                    .ToListAsync())
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                throw new StoreClockInitializationException(
                    "تعذّر الاتصال بقاعدة البيانات لقراءة اسم خادم SQL — لا يمكن تهيئة ساعة المتجر. تحقق من الاتصال ثم أعد تشغيل البرنامج.",
                    ex);
            }

            IsSqlHost = forceHost ?? IsSameHostIgnoringCase(sqlHost ?? "", Environment.MachineName);

            if (IsSqlHost)
            {
                // 2) فرع المضيف: معرّف التوقيت المحلي يُكتب في الإعدادات (يُحدَّث عند كل إقلاع)
                string hostTzId = _injectedTimeZone?.Id ?? TimeZoneInfo.Local.Id;
                var setting = await db.Settings.FindAsync(clsSettingsService.KeyStoreTimeZoneId);
                if (setting == null)
                {
                    db.Settings.Add(new Sayl.Core.Models.Setting
                    {
                        SettingKey = clsSettingsService.KeyStoreTimeZoneId,
                        SettingValue = hostTzId,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    setting.SettingValue = hostTzId;
                    setting.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
                _storeTimeZone = _injectedTimeZone ?? TimeZoneInfo.Local;
            }
            else
            {
                // 3) فرع الطرفي: قراءة المعرّف فقط — لا كتابة ولا رجوع للتوقيت المحلي
                string? storedId = (await db.Settings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.SettingKey == clsSettingsService.KeyStoreTimeZoneId))?.SettingValue;
                if (string.IsNullOrWhiteSpace(storedId))
                    throw new StoreClockInitializationException(
                        "لم يُضبط توقيت المتجر المركزي بعد — شغّل البرنامج مرة على جهاز الخادم المركزي ليُسجّل توقيت المتجر.");

                try
                {
                    _storeTimeZone = _injectedTimeZone ?? TimeZoneInfo.FindSystemTimeZoneById(storedId);
                }
                catch (Exception ex)
                {
                    throw new StoreClockInitializationException(
                        $"معرّف توقيت المتجر المخزَّن غير صالح ({storedId}) — شغّل البرنامج على جهاز الخادم المركزي لإعادة تسجيله.",
                        ex);
                }
            }

            // نشر التوقيت المركزي للساعات الموحدة (ProgramClock/AppClock) — نجاح الإقلاع فقط؛ الفشل ينشر شيئًا فتبقى الساعات على الاحتياطي الإرثي (+3)
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(_storeTimeZone);

            _initialized = true;
        }

        // ══ الساعة ══

        /// <summary>الوقت الحالي UTC من خادم SQL (SYSUTCDATETIME) — المصدر الموحّد.
        /// يُقرأ Unspecified فيُطبَّع إلى Kind=Utc صراحةً.</summary>
        public async Task<DateTime> GetUtcNowAsync()
        {
            if (_dbContextForTests != null)
                return ReadSqlUtcNow(_dbContextForTests);
            await using (var scope = _scopeFactory.CreateAsyncScope())
                return ReadSqlUtcNow(scope.ServiceProvider.GetRequiredService<SaylDbContext>());
        }

        private static DateTime ReadSqlUtcNow(SaylDbContext db)
        {
            var rows = db.Database
                .SqlQueryRaw<DateTime>("SELECT CAST(SYSUTCDATETIME() AS datetime2(3)) AS [Value]")
                .ToList();
            if (rows.Count == 0)
                throw new StoreClockInitializationException(
                    "تعذّر قراءة ساعة خادم SQL — تحقق من الاتصال ثم أعد المحاولة.");
            return new DateTime(rows[0].Ticks, DateTimeKind.Utc); // Unspecified -> Utc
        }

        /// <summary>تحويل طابع UTC مخزَّن إلى توقيت المتجر (المخزَّن بعد الإقلاع).
        /// Unspecified تُعامل UTC (نفس عهد التخزين) — بلا إزاحة ثابتة أبدًا.</summary>
        public DateTime ToStoreLocal(DateTime utc)
        {
            TimeZoneInfo tz = RequireTimeZone();
            DateTime normalized = utc.Kind == DateTimeKind.Local
                ? utc.ToUniversalTime()
                : new DateTime(utc.Ticks, DateTimeKind.Utc); // Utc أو Unspecified
            return TimeZoneInfo.ConvertTimeFromUtc(normalized, tz);
        }

        /// <summary>«اليوم» بمعنى المحل من طابع UTC معطى (للاختبارات والحدود الثابتة).</summary>
        public DateOnly GetStoreToday(DateTime utc) => DateOnly.FromDateTime(ToStoreLocal(utc));

        /// <summary>«اليوم» بمعنى المحل من ساعة SQL الحالية.</summary>
        public async Task<DateOnly> GetStoreTodayAsync() => GetStoreToday(await GetUtcNowAsync());

        /// <summary>حدود اليوم بمعنى المتجر لليوم المعطى بمعنى UTC: منتصفا ليل اليوم
        /// والتالي محوَّلين عائدًا إلى UTC — شامل البداية وحصري النهاية. طول اليوم
        /// (23/24/25 ساعة) تُحسبه قواعد ويندوز الديناميكية تلقائيًا.</summary>
        public (DateTime StartUtc, DateTime EndUtc) GetStoreDayBoundsUtc(DateOnly storeDate)
        {
            TimeZoneInfo tz = RequireTimeZone();
            return (ConvertStoreLocalToUtc(storeDate.ToDateTime(TimeOnly.MinValue), tz),
                    ConvertStoreLocalToUtc(storeDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
        }

        /// <summary>تحويل توقيت المتجر إلى UTC؛ ثغرة التوقيت الصيفي (لا منتصف ليل
        /// فعليًا) تُزيَّح بمقدار الدلتا الصيفية للقاعدة السارية كي لا ينفجر التحويل.</summary>
        private static DateTime ConvertStoreLocalToUtc(DateTime local, TimeZoneInfo tz)
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
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        // ══ أدوات داخلية ══

        private TimeZoneInfo RequireTimeZone()
            => _initialized && _storeTimeZone != null
                ? _storeTimeZone
                : throw new InvalidOperationException(
                    "لم تُهيَّأ ساعة المتجر بعد — نادِ InitializeAsync عند بدء التشغيل أولًا.");
    }
}
