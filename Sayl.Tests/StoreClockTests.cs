using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ساعة المتجر المركزية (Task ساعة المحل المركزية — الأساس):
/// كشف المضيف، فرع المضيف (يكتب StoreTimeZoneId عند كل إقلاع)، فرع الطرفي
/// (يقرأ فقط ولا يكتب أبدًا ولا يرجع لتوقيت الجهاز)، والتحويل الديناميكي
/// DST (لا إزاحة ثابتة). كل الاختبارات عبر مُنشئ الاختبارات الداخلي —
/// لا مُنشئ DI — وقاعدة مشتركة [Collection("database")].
/// </summary>
[Collection("database")]
public class StoreClockTests
{
    private readonly DatabaseFixture _fx;
    public StoreClockTests(DatabaseFixture fx) => _fx = fx;

    private const string Key = "StoreTimeZoneId";

    private static async Task WriteSettingAsync(SaylDbContext db, string? value)
    {
        var row = await db.Settings.FindAsync(Key);
        if (row == null)
            db.Settings.Add(new Sayl.Core.Models.Setting { SettingKey = Key, SettingValue = value, UpdatedAt = DateTime.UtcNow });
        else
            row.SettingValue = value;
        await db.SaveChangesAsync();
    }

    private static async Task<string?> ReadSettingAsync(SaylDbContext db)
        => (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.SettingKey == Key))?.SettingValue;

    // ══ 1) مساعد مقارنة أسماء الأجهزة (وحدة صرفة) ══
    [Theory]
    [InlineData("PC-01", "pc-01", true)]
    [InlineData(" PC-01 ", "pc-01", true)]   // فراغات طرفية تُقص
    [InlineData("", "", true)]               // فراغان = فراغان (لا كشف هنا — في المستدعي)
    [InlineData("PC-01", "PC-02", false)]
    [InlineData("PC-01", "", false)]
    [InlineData("", "   ", true)]            // فراغ مقابل فراغات: القصّ يجعلهما "" متساويين
    public void IsSameHostIgnoringCase_TrimsAndIgnoresCase(string a, string b, bool expected)
        => Assert.Equal(expected, clsStoreClockService.IsSameHostIgnoringCase(a, b));

    // ══ 2) الطرفي لا يكتب أبدًا: قيمة موجودة تبقى كما هي ══
    [Fact]
    public async Task Terminal_Init_DoesNotOverwriteStoredTimeZone()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "America/New_York");
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: false);

            await clock.InitializeAsync();

            Assert.False(clock.IsSqlHost);
            Assert.Equal("America/New_York", await ReadSettingAsync(db));
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 3) غياب الإعداد على الطرفي -> استثناء برسالة عربية ══
    [Fact]
    public async Task Terminal_Init_MissingSetting_ThrowsWithArabicMessage()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, null);
            var clock = new clsStoreClockService(db, forceHost: false);

            var ex = await Assert.ThrowsAsync<clsStoreClockService.StoreClockInitializationException>(
                () => clock.InitializeAsync());
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.Contains("توقيت", ex.Message);
        }
        finally { await WriteSettingAsync(db, null); }
    }

    // ══ 4) معرّف مخزَّن تالف على الطرفي -> استثناء (لا رجوع للتوقيت المحلي) ══
    [Fact]
    public async Task Terminal_Init_InvalidStoredId_Throws()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "Bogus/Zone_99");
            var clock = new clsStoreClockService(db, forceHost: false);

            await Assert.ThrowsAsync<clsStoreClockService.StoreClockInitializationException>(
                () => clock.InitializeAsync());
            Assert.Equal("Bogus/Zone_99", await ReadSettingAsync(db)); // بقي التالف كما هو — الطرفي لا يصلّحه
        }
        finally { await WriteSettingAsync(db, null); }
    }

    // ══ 5) المضيف يكتب/يُحدِّث عند كل إقلاع حتى فوق قيمة قديمة ══
    [Fact]
    public async Task Host_Init_OverwritesStaleSettingWithLocalId()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "Bogus/Stale_1");
            var clock = new clsStoreClockService(db, forceHost: true);

            await clock.InitializeAsync();

            Assert.True(clock.IsSqlHost);
            Assert.Equal(TimeZoneInfo.Local.Id, await ReadSettingAsync(db));
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 6) تحويل UTC -> توقيت المتجر بقواعد DST الديناميكية (نيويورك) ══
    [Fact]
    public async Task ToStoreLocal_NewYork_DynamicDst()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "America/New_York"); // مسار الطرفي يحتاج إعدادًا صالحًا
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: false);
            await clock.InitializeAsync();

            // الشتاء EST (-5): 05:00Z = منتصف ليل 2026-03-08 محليًا
            DateTime winter = new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc);
            Assert.Equal(new DateTime(2026, 3, 8, 0, 0, 0), clock.ToStoreLocal(winter));

        // الصيف EDT (-4): 04:00Z = منتصف ليل 2026-07-01 محليًا
        DateTime summer = new DateTime(2026, 7, 1, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0), clock.ToStoreLocal(summer));
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 7) حدود اليوم: يوم 23 ساعة / 24 / 25 (شامل البداية حصري النهاية) ══
    [Fact]
    public async Task GetStoreDayBoundsUtc_NewYork_SpanFollowsDst()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "America/New_York"); // مسار الطرفي يحتاج إعدادًا صالحًا
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: false);
            await clock.InitializeAsync();

            (DateTime s, DateTime e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 3, 8)); // تقديم الساعة: 23 ساعة
            Assert.Equal(TimeSpan.FromHours(23), e - s);
            Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), s);

            (s, e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 11, 1)); // تأخير الساعة: 25 ساعة
            Assert.Equal(TimeSpan.FromHours(25), e - s);
            Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), s); // منتصف ليل 1 نوفمبر ما زال EDT (-4)

            (s, e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 6, 15)); // يوم عادي: 24 ساعة
            Assert.Equal(TimeSpan.FromHours(24), e - s);
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 9) نجاح إقلاع المضيف ينشر التوقيت المركزي للساعات الموحدة ══
    [Fact]
    public async Task Host_Init_PublishesStoreTimeZoneToProgramClock()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: true);

            await clock.InitializeAsync();

            Assert.Equal("America/New_York", Common.Utils.ProgramClock.StoreTimeZone?.Id);
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 10) نجاح إقلاع الطرفي ينشر التوقيت المركزي أيضًا ══
    [Fact]
    public async Task Terminal_Init_PublishesStoreTimeZoneToProgramClock()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "America/New_York");
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: false);

            await clock.InitializeAsync();

            Assert.Equal("America/New_York", Common.Utils.ProgramClock.StoreTimeZone?.Id);
        }
        finally
        {
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 11) فشل الإقلاع ينشر شيئًا — الساعات تبقى على الفشل المغلق ══
    [Fact]
    public async Task FailedInit_DoesNotPublishStoreTimeZone()
    {
        await using var db = _fx.CreateContext();
        try
        {
            // نبدأ من الحالة غير المهيأة (بلا توقيت ولا مرساة) لنعزل نشر هذا الإقلاع
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);
            await WriteSettingAsync(db, null);
            var clock = new clsStoreClockService(db, forceHost: false);

            await Assert.ThrowsAsync<clsStoreClockService.StoreClockInitializationException>(
                () => clock.InitializeAsync());

            Assert.Null(Common.Utils.ProgramClock.StoreTimeZone);

            // التقوية (الفشل المغلق الجديد — لا احتياطي +3 بعد اليوم): بعد فشل
            // الإقلاع كل عمليات الساعة ترمي ClockNotInitializedException —
            // بلا أي وقت افتراضي يمكن العمل عليه قبل نجاح إقلاع الساعة
            DateTime utcProbe = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.ToLocal(utcProbe));
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.UtcNow);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.Today);
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 8) ساعة SQL: ضمن دقيقتين من ساعة الجهاز (المصدر SQL — تسامح واسع) ══
    [Fact]
    public async Task GetUtcNowAsync_WithinToleranceOfMachineUtc()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, TimeZoneInfo.Local.Id); // إعداد صالح لمسار الطرفي
            var clock = new clsStoreClockService(db, forceHost: false);
            await clock.InitializeAsync();

            DateTime sqlNow = await clock.GetUtcNowAsync();

            Assert.Equal(DateTimeKind.Utc, sqlNow.Kind);
            Assert.True(Math.Abs((sqlNow - DateTime.UtcNow).TotalMinutes) < 2,
                $"SQL UTC {sqlNow:O} بعيد عن ساعة الجهاز {DateTime.UtcNow:O}");
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 12) FromUtc بالتوقيت المركزي: DST الديناميكي نفسه (نيويورك) ══
    [Fact]
    public void ProgramClock_FromUtc_NewYork_DynamicDst()
    {
        try
        {
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(ny);

            // الشتاء EST (-5): 05:00Z = منتصف ليل 2026-03-08 محليًا
            Assert.Equal(new DateOnly(2026, 3, 8),
                Common.Utils.ProgramClock.FromUtc(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc)));

            // الصيف EDT (-4): 04:00Z = منتصف ليل 2026-07-01 محليًا
            Assert.Equal(new DateOnly(2026, 7, 1),
                Common.Utils.ProgramClock.FromUtc(new DateTime(2026, 7, 1, 4, 0, 0, DateTimeKind.Utc)));
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 13) ToLocal بالتوقيت المركزي: يساوي ConvertTimeFromUtc + حرس البيانات القديمة ══
    [Fact]
    public void ProgramClock_ToLocal_NewYork_MatchesConvertTimeFromUtc_AndLocalGuard()
    {
        try
        {
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(ny);

            // لحظة ثابتة: 2026-03-08 05:00Z = منتصف ليل EST
            DateTime utc = new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc);
            Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(utc, ny),
                Common.Utils.ProgramClock.ToLocal(utc));

            // حرس البيانات القديمة: Kind==Local تُعاد كما هي (لا إزاحة مرتين)
            DateTime local = new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Local);
            Assert.Equal(local, Common.Utils.ProgramClock.ToLocal(local));
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 14) بعد التصفير: فشل مغلق — لا احتياطي +3 ولا ساعة جهاز ══
    // الاستثناء الوحيد: Kind==Local تمر عابرة في ToLocal (توافق البيانات
    // القديمة — قاعدة عرض تاريخية لا إذن توقيت).
    [Fact]
    public void ProgramClock_AfterReset_OperationsFailClosed()
    {
        try
        {
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);

            // كل عمليات الساعة قبل النشر ترمي استثناء الفشل المغلق
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.UtcNow);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.NowLocal);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.Today);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.ToLocal(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)));
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.FromUtc(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)));
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2026, 7, 1)));

            // الاستثناء الوحيد: Kind==Local تُعاد كما هي (بيانات قديمة قبل الترحيل)
            DateTime local = new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Local);
            Assert.Equal(local, Common.Utils.ProgramClock.ToLocal(local));
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 15) منتصف الليل الملتبس أثناء تأخير الساعة: أبكر لحظة UTC (الجهة الصيفية) ══
    // توقيت مخصص ثابت التواريخ (بلا اعتماد على سنة الجهاز أو توقيته): أساس -5،
    // دلتا صيفية +1، فترة DST من 1 يناير 00:00 إلى 2 يناير 01:00 (تواريخ ثابتة).
    // عند نهاية DST يقفز الساعة من 2 يناير 01:00 (صيفي، 05:00Z) إلى 2 يناير 00:00
    // (قياسي، 05:00Z) — فيتكرر منتصف ليل 2 يناير مرتين: جهة صيفية 04:00Z وجهة
    // قياسية 05:00Z. يجب أن تختار حدود اليوم الأبكر (04:00Z) كي لا تُسقط الساعة
    // المتكررة الأولى — ConvertTimeToUtc الافتراضي يفترض التفسير القياسي فيخطئ.
    [Fact]
    public async Task GetStoreDayBoundsUtc_AmbiguousMidnight_TakesEarliestUtcInstant()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date,
                TimeSpan.FromHours(1), // دلتا صيفية +1
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), month: 1, day: 1), // بدء DST: 1 يناير 00:00
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), month: 1, day: 2)); // نهاية DST: 2 يناير 01:00
            var tz = TimeZoneInfo.CreateCustomTimeZone("Test/AmbiguousMidnight",
                TimeSpan.FromHours(-5), "Test/AmbiguousMidnight", "Test Standard", "Test Daylight", new[] { rule });

            await WriteSettingAsync(db, tz.Id); // مسار الطرفي يحتاج إعدادًا صالحًا
            var clock = new clsStoreClockService(db, tz, forceHost: false);
            await clock.InitializeAsync();

            // حرس الفرضية: منتصف ليل 2 يناير ملتبس فعلًا في هذا التوقيت المخصص
            DateTime midnight = new DateTime(2026, 1, 2, 0, 0, 0);
            Assert.True(tz.IsAmbiguousTime(midnight),
                "الفرضية فسدت: منتصف ليل 2 يناير يجب أن يكون ملتبسًا في التوقيت المخصص");

            (DateTime s, DateTime e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 1, 2));

            // البداية = أبكر لحظة UTC (الجهة الصيفية 04:00Z) لا القياسية 05:00Z
            Assert.Equal(new DateTime(2026, 1, 2, 4, 0, 0, DateTimeKind.Utc), s);
            // النهاية = منتصف ليل اليوم التالي (قياسي -5) = 05:00Z
            Assert.Equal(new DateTime(2026, 1, 3, 5, 0, 0, DateTimeKind.Utc), e);
            // اليوم الملتبس طوله 25 ساعة
            Assert.Equal(TimeSpan.FromHours(25), e - s);

            // الساعة المتكررة [04:00Z، 05:00Z) داخل حدود 2 يناير — لا تُسقط
            DateTime repeated = new DateTime(2026, 1, 2, 4, 30, 0, DateTimeKind.Utc);
            Assert.True(repeated >= s && repeated < e, $"الساعة المتكررة {repeated:O} خارج حدود اليوم");
            Assert.Equal(new DateTime(2026, 1, 2, 0, 30, 0), clock.ToStoreLocal(repeated));

            // يوم عادي بعد التوقيت الصيفي يبقى 24 ساعة (sanity على التوقيت نفسه)
            (s, e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 2, 1));
            Assert.Equal(TimeSpan.FromHours(24), e - s);
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 16) منتصف الليل الملتبس مع قاعدة تُزيح الإزاحة الأساسية (BaseUtcOffsetDelta):
    // يجب أن تُحسب أبكر لحظة UTC من إزاحات المنطقة الفعلية (GetAmbiguousTimeOffsets)
    // لا من إعادة بناء يدوية تتجاهل BaseUtcOffsetDelta. توقيت مخصص ثابت التواريخ:
    // أساس -5، دلتا صيفية +1، إزاحة قاعدية إضافية +1 (فترة القاعدة: -3 صيفي)،
    // فترة DST من 1 يناير 00:00 إلى 2 يناير 01:00 (تواريخ ثابتة).
    // عند نهاية DST يقفز الساعة من 2 يناير 01:00 (صيفي -3) إلى 2 يناير 00:00
    // (قياسي داخل القاعدة -4) — فيتكرر منتصف ليل 2 يناير مرتين: -3 (03:00Z)
    // و -4 (04:00Z). الأبكر الحقيقي 03:00Z؛ الإعادة اليدوية القديمة كانت
    // تعطي 04:00Z لأنها تتجاهل BaseUtcOffsetDelta.
    [Fact]
    public async Task GetStoreDayBoundsUtc_AmbiguousMidnight_BaseUtcOffsetDelta_UsesActualOffsets()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date,
                TimeSpan.FromHours(1), // دلتا صيفية +1
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), month: 1, day: 1), // بدء DST: 1 يناير 00:00
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), month: 1, day: 2), // نهاية DST: 2 يناير 01:00
                TimeSpan.FromHours(1)); // إزاحة قاعدية إضافية +1 خلال القاعدة (فترة القاعدة: -4 قياسي، -3 صيفي)
            var tz = TimeZoneInfo.CreateCustomTimeZone("Test/AmbiguousMidnightBaseDelta",
                TimeSpan.FromHours(-5), "Test/AmbiguousMidnightBaseDelta", "Test Standard", "Test Daylight", new[] { rule });

            await WriteSettingAsync(db, tz.Id); // مسار الطرفي يحتاج إعدادًا صالحًا
            var clock = new clsStoreClockService(db, tz, forceHost: false);
            await clock.InitializeAsync();

            // حرس الفرضية: منتصف ليل 2 يناير ملتبس بإزاحتين فعليتين {-3، -4}
            DateTime midnight = new DateTime(2026, 1, 2, 0, 0, 0);
            Assert.True(tz.IsAmbiguousTime(midnight),
                "الفرضية فسدت: منتصف ليل 2 يناير يجب أن يكون ملتبسًا في التوقيت المخصص");
            TimeSpan[] offsets = tz.GetAmbiguousTimeOffsets(midnight);
            Assert.Equal(2, offsets.Length);
            Assert.Contains(TimeSpan.FromHours(-3), offsets); // صيفي (خلال القاعدة: -5 + 1 + 1)
            Assert.Contains(TimeSpan.FromHours(-4), offsets); // قياسي داخل القاعدة (-5 + 1)

            (DateTime s, DateTime e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 1, 2));

            // البداية = أبكر لحظة UTC محسوبة من الإزاحات الفعلية نفسها = 03:00Z
            // (الإعادة اليدوية القديمة كانت ستعطي 04:00Z لأنها تتجاهل BaseUtcOffsetDelta)
            DateTime earliest = offsets.Select(o => new DateTime(midnight.Ticks - o.Ticks, DateTimeKind.Utc)).Min();
            Assert.Equal(earliest, s);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc), s);
            // النهاية = منتصف ليل اليوم التالي بإزاحة القاعدة بعد التأخير (-4) = 04:00Z
            Assert.Equal(new DateTime(2026, 1, 3, 4, 0, 0, DateTimeKind.Utc), e);
            // اليوم الملتبس طوله 25 ساعة (24 + ساعة تأخير الساعة)
            Assert.Equal(TimeSpan.FromHours(25), e - s);

            // الساعة المتكررة بالكامل داخل حدود 2 يناير: 03:30Z (صيفي) و 04:30Z (قياسي)
            // كلاهما يُعاد إلى 00:30 محليًا — لا تُسقط أي ساعة متكررة
            DateTime first = new DateTime(2026, 1, 2, 3, 30, 0, DateTimeKind.Utc);
            Assert.True(first >= s && first < e, $"الساعة المتكررة {first:O} خارج حدود اليوم");
            Assert.Equal(new DateTime(2026, 1, 2, 0, 30, 0), clock.ToStoreLocal(first));
            DateTime second = new DateTime(2026, 1, 2, 4, 30, 0, DateTimeKind.Utc);
            Assert.True(second >= s && second < e, $"الساعة المتكررة {second:O} خارج حدود اليوم");
            Assert.Equal(new DateTime(2026, 1, 2, 0, 30, 0), clock.ToStoreLocal(second));

            // يوم عادي خارج فترة القاعدة يبقى 24 ساعة (sanity على التوقيت نفسه)
            (s, e) = clock.GetStoreDayBoundsUtc(new DateOnly(2026, 2, 1));
            Assert.Equal(TimeSpan.FromHours(24), e - s);
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 17) LocalDayBoundsUtc بالتوقيت الثابت المضبوط (+3 اختبارًا) ══
    // قيم مطابقة للإزاحة المكتوبة يدويًا سابقًا في التقارير/الفواتير (منتصف ليل − 3)
    // — هذا هو أمان الترحيل: اختبارات الخدمات القديمة تمرّ بلا تغيير.
    // Test/FixedPlus3 إزاحة ثابتة بلا DST — نفس حسابات القديم تمامًا.
    [Fact]
    public void ProgramClock_LocalDayBoundsUtc_FixedPlus3TestClock_SameAsLegacyValues()
    {
        try
        {
            TestClockConfig.Configure();

            var (start, end) = Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2026, 7, 1));

            // البداية = منتصف ليل 1 يوليو − 3 (نفس القديم)
            Assert.Equal(new DateTime(2026, 6, 30, 21, 0, 0, DateTimeKind.Utc), start);
            // الإزاحة الثابتة لا تعرف DST — اليوم دائمًا 24 ساعة
            Assert.Equal(TimeSpan.FromHours(24), end - start);

            // شامل البداية وحصري النهاية: اللحظة الأولى داخله ولحظة النهاية نفسها خارجه
            Assert.True(start < end);
            DateTime endProbe = end;
            Assert.False(endProbe < end);        // لحظة النهاية نفسها خارجه (حصري)
            Assert.True(end.AddTicks(-1) < end); // آخر لقطة قبله داخله
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 18) LocalDayBoundsUtc بالتوقيت المركزي المنشور (نيويورك): DST الديناميكي ══
    // نفس حقائق DST المستخدمة في اختبارات حدود الخدمة (7): يوم 23 ساعة عند تقديم
    // الساعة و25 ساعة عند تأخيرها — الخدمة تفوّض لهذا المصدر فالنتيجة واحدة.
    [Fact]
    public void ProgramClock_LocalDayBoundsUtc_NewYork_SpanFollowsDst()
    {
        try
        {
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(ny);

            // تقديم الساعة (2026-03-08): البداية 05:00Z واليوم 23 ساعة
            (DateTime s, DateTime e) = Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2026, 3, 8));
            Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), s);
            Assert.Equal(TimeSpan.FromHours(23), e - s);

            // تأخير الساعة (2026-11-01): البداية 04:00Z (منتصف الليل ما زال EDT) واليوم 25 ساعة
            (s, e) = Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2026, 11, 1));
            Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), s);
            Assert.Equal(TimeSpan.FromHours(25), e - s);
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 19) تكافؤ حد النهاية بعد الترحيل: القديم <= (MaxValue−3h) ≡ الجديد < منتصف ليل اليوم التالي ══
    // datetime2(7) لقطة-دقيق بين 20:59:59.9999999 و21:00:00 لا قيم له — فالاستثناء بالمجموعتين متطابق.
    // التوقيت الثابت +3 (Test/FixedPlus3) يعطي نفس حسابات الإزاحة القديمة تمامًا.
    [Fact]
    public void ProgramClock_LocalDayBoundsUtc_LegacyEndBound_EquivalentToOldMaxValue()
    {
        try
        {
            TestClockConfig.Configure();

            var (_, endEx) = Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2026, 7, 1));
            DateTime oldEnd = new DateOnly(2026, 7, 1).ToDateTime(TimeOnly.MaxValue).AddHours(-3);

            // الفجوة بين الحدّين لقطة واحدة (100ns) — datetime2(7) لا قيم له بينهما
            Assert.Equal(TimeSpan.FromTicks(1), endEx - oldEnd);
            // القديم (شامل: x <= oldEnd) والجديد (حصري: x < endEx) يقبلان ويرفضان
            // المجموعة نفسها على كل لقطة حول الفجوة — التكافؤ الفعلي لا الشكلي
            for (int i = -1; i <= 1; i++)
            {
                DateTime probe = endEx.AddTicks(i);
                Assert.Equal(probe <= oldEnd, probe < endEx);
            }
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 20) مرساة SQL عند الإقلاع: UtcNow المنشورة ≈ ساعة SQL (Kind=Utc) ══
    // الإقلاع يقرأ SYSUTCDATETIME وينشر مرساة SQL — ProgramClock.UtcNow
    // (المرساة + الزمن الرتيب) يجب أن تساوي قراءة ساعة SQL بحدود دقيقتين.
    [Fact]
    public async Task ProgramClock_UtcNow_AfterInit_MatchesSqlClock()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var clock = new clsStoreClockService(db, forceHost: true);
            await clock.InitializeAsync(); // ينشر التوقيت + مرساة ساعة SQL

            DateTime published = Common.Utils.ProgramClock.UtcNow;

            Assert.Equal(DateTimeKind.Utc, published.Kind);
            Assert.True(Math.Abs((published - DateTime.UtcNow).TotalMilliseconds) < TimeSpan.FromMinutes(2).TotalMilliseconds,
                $"الفارق عن ساعة SQL كبير: {published - DateTime.UtcNow}");
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 21) لا احتياطي بعد اليوم: قبل أي نشر UtcNow ترمي الفشل المغلق ══
    // الاستثناء الجديد للاحتياطي الإرثي الملغى (انحراف صفري/ساعة الجهاز):
    // قبل نشر المرساة كل قراءة للوقت استثناء واضح — لا قيمة افتراضية أبدًا.
    [Fact]
    public void ProgramClock_UtcNow_BeforeInit_ThrowsNotInitialized()
    {
        try
        {
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // الحالة قبل الإقلاع

            var ex = Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.UtcNow);
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.Contains("ساعة", ex.Message);
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 22) التقدم الرتيب: مرساة في الماضي + لقطة Stopwatch مطابقة — UtcNow
    // تتقدم بالزمن الرتيب المنقضي من المرساة لا من ساعة الحائط (بلا محاكاة
    // لتغيير ساعة OS — نشر المرساة اختبار محدد حتمي عبر بذرة الاختبارات).
    [Fact]
    public void ProgramClock_UtcNow_MonotonicAdvancesFromAnchor_NotWallClock()
    {
        try
        {
            // مرساة قبل 10 دقائق + لقطة Stopwatch «الآن» — ساعة الحائط تقول الآن
            // لكن الساعة الموحدة مشتقة من المرساة + المنقضي الرتيب فقط
            Common.Utils.ProgramClock.ConfigureForTests(
                TestClockConfig.Tz,
                DateTime.UtcNow.AddMinutes(-10),
                System.Diagnostics.Stopwatch.GetTimestamp());

            DateTime published1 = Common.Utils.ProgramClock.UtcNow;

            Assert.Equal(DateTimeKind.Utc, published1.Kind);
            Assert.True(Math.Abs((published1 - DateTime.UtcNow.AddMinutes(-10)).TotalMilliseconds)
                < TimeSpan.FromSeconds(5).TotalMilliseconds,
                $"الساعة يجب أن تكون على المرساة (قبل 10 دقائق) لا على ساعة الحائط: {published1 - DateTime.UtcNow}");

            // بعد انتظار قصير تتقدم الساعة رتيبًا بمقدار الزمن المنقضي الحقيقي
            System.Threading.Thread.Sleep(50);
            DateTime published2 = Common.Utils.ProgramClock.UtcNow;
            Assert.True(published2 > published1, "الساعة الرتيبة لم تتقدم أبدًا للوراء");
            Assert.True(published2 - published1 < TimeSpan.FromSeconds(5),
                "تقدم الساعة يجب أن يكون بقدر الانتظار الرتيب لا قفزة ساعة حائط");
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 23) النشر مع كل قراءة: GetUtcNowAsync لاحقة تُحدِّث المرساة المنشورة ══
    // مرساة اصطناعية في الماضي ثم قراءة SQL واحدة ترجع الساعة إلى ~الآن
    // (publish-on-every-reading — الإقلاع والقراءات اللاحقة على حد سواء).
    [Fact]
    public async Task GetUtcNowAsync_RepublishesAnchorOnEveryReading()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, TimeZoneInfo.Local.Id); // إعداد صالح لمسار الطرفي
            var clock = new clsStoreClockService(db, forceHost: false);
            await clock.InitializeAsync();

            // إبطال المرساة اصطناعيًا (قبل 10 دقائق + لقطة «الآن»)
            Common.Utils.ProgramClock.ConfigureForTests(
                TestClockConfig.Tz,
                DateTime.UtcNow.AddMinutes(-10),
                System.Diagnostics.Stopwatch.GetTimestamp());
            Assert.True((Common.Utils.ProgramClock.UtcNow - DateTime.UtcNow).TotalMinutes < -9,
                "الفرضية فسدت: المرساة الاصطناعية يجب أن تكون في الماضي");

            // قراءة SQL واحدة ناجحة تنشر المرساة الجديدة — الساعة ترجع إلى ~الآن
            DateTime sqlNow = await clock.GetUtcNowAsync();
            DateTime published = Common.Utils.ProgramClock.UtcNow;

            Assert.True(Math.Abs((published - sqlNow).TotalMilliseconds) < TimeSpan.FromSeconds(2).TotalMilliseconds,
                $"القراءة الناجحة لم تُحدِّث المرساة: {published - sqlNow}");
            Assert.True(Math.Abs((published - DateTime.UtcNow).TotalMilliseconds) < TimeSpan.FromSeconds(5).TotalMilliseconds,
                $"المرساة بعد القراءة بعيدة عن الآن: {published - DateTime.UtcNow}");
        }
        finally
        {
            TestClockConfig.Configure();
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 24) النسخة الواعية بالسياق: GetStoreTodayAsync(db) على سياق المستدعي ══
    // تقرأ من السياق المعطى نفسه وتحوّل بالتوقيت المهيأ — والقيمة تساوي
    // GetStoreToday للقراءة المباشرة نفسها (مصدر واحد).
    [Fact]
    public async Task GetStoreTodayAsync_OnSuppliedContext_MatchesDirectReading()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, TestClockConfig.FixedPlus3Id); // إعداد صالح لمسار الطرفي
            var clock = new clsStoreClockService(db, TestClockConfig.Tz, forceHost: false);
            await clock.InitializeAsync();

            DateOnly todayFromContext = await clock.GetStoreTodayAsync(db);

            DateTime sqlNow = await clock.GetUtcNowAsync(db);
            DateOnly expected = clock.GetStoreToday(sqlNow);

            Assert.Equal(expected, todayFromContext);
            Assert.Equal(DateTimeKind.Utc, sqlNow.Kind);
        }
        finally
        {
            TestClockConfig.Configure();
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 25) الوقت المحلي غير الصالح (ثغرة تقديم الساعة — منتصف ليل مُحذوف):
    // قاعدة انتقال صيفي عند منتصف الليل تحذف 00:00 فلا ينفجر حساب الحدود —
    // تُزَّح اللحظة غير الصالحة بمقدار الدلتا (كما في ProgramClock.ConvertLocalToUtc).
    [Fact]
    public void ProgramClock_LocalDayBoundsUtc_InvalidMidnight_DstAtMidnight_ShiftsByDelta()
    {
        // انتقال صيفي عند 00:00 بالضبط (بلا توقيت صيفي قبله): منتصف ليل 1 يونيو
        // لا وجود له محليًا (IsInvalidTime) — أقصى تعارض محتمل مع حدود اليوم.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date,
            TimeSpan.FromHours(1), // دلتا صيفية +1
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), month: 6, day: 1), // بدء DST: 1 يونيو 00:00
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), month: 9, day: 1)); // نهاية DST: 1 سبتمبر 00:00
        var tz = TimeZoneInfo.CreateCustomTimeZone("Test/MidnightDst",
            TimeSpan.FromHours(2), "Test/MidnightDst", "MidnightDst Standard", "MidnightDst Daylight", new[] { rule });

        try
        {
            Assert.True(tz.IsInvalidTime(new DateTime(2031, 6, 1, 0, 0, 0)),
                "الفرضية فسدت: منتصف ليل 1 يونيو يجب أن يكون وقتًا غير صالح في هذا التوقيت");

            Common.Utils.ProgramClock.ConfigureStoreTimeZone(tz); // نشر التوقيت فقط — المرساة باقية

            var (start, end) = Common.Utils.ProgramClock.LocalDayBoundsUtc(new DateOnly(2031, 6, 1));

            // 00:00 غير الصالحة تُزاح بالدلتا (+1) → 01:00 بتوقيت صيفي +3 → 22:00Z من 31 مايو
            Assert.Equal(new DateTime(2031, 5, 31, 22, 0, 0, DateTimeKind.Utc), start);
            // نهاية اليوم: منتصف ليل 2 يونيو صالح (بعد بدء الصيف) → 21:00Z من 1 يونيو
            Assert.Equal(new DateTime(2031, 6, 1, 21, 0, 0, DateTimeKind.Utc), end);
            // يوم تقديم الساعة: 23 ساعة — الشكل حصري النهاية كما في كل الحدود
            Assert.Equal(TimeSpan.FromHours(23), end - start);
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
        }
    }

    // ══ 26) ذرّية الإقلاع: فشل حفظ StoreTimeZoneId على المضيف لا ينشر شيئًا ══
    // آلية الفشل الحتمية: اعتراض EF Core (SaveChangesInterceptor) يرمي على
    // SavingChangesAsync — بلا تغيير مخطط ولا أقفال جداول. قبل الإصلاح كانت
    // الخطوة 3 تنشر المرساة عبر GetUtcNowAsync قبل الحفظ، فبقي التوقيت/المرساة
    // منشورين رغم فشل الإقلاع. الآن لا يصل التنفيذ إلى النشر أصلًا.
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("فشل حفظ محسوس لأغراض الاختبار (ذرّية إقلاع ساعة المتجر)");
    }

    [Fact]
    public async Task Host_Init_FailedSettingPersistence_PublishesNothing()
    {
        await using var db = _fx.CreateContext();
        try
        {
            // نبدأ من الحالة غير المهيأة (بلا توقيت ولا مرساة) لنعزل نشر هذا الإقلاع
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);
            await WriteSettingAsync(db, "Bogus/Stale_2"); // قيمة قديمة — المضيف سيحاول تحديثها

            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var options = new DbContextOptionsBuilder<SaylDbContext>()
                .UseSqlServer(DatabaseFixture.ConnectionString)
                .AddInterceptors(new ThrowingSaveChangesInterceptor())
                .Options;
            await using var failingDb = new SaylDbContext(options);
            var clock = new clsStoreClockService(failingDb, ny, forceHost: true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => clock.InitializeAsync());

            // الفشل المغلق كاملًا: لا توقيت منشور ولا مرساة — كل عمليات الساعة ترمي
            Assert.Null(Common.Utils.ProgramClock.StoreTimeZone);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.UtcNow);
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.Today);
        }
        finally
        {
            TestClockConfig.Configure();
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 27) حرس القارئ العام: GetUtcNowAsync(db) قبل الإقلاع ترمي ولا تنشر ══
    [Fact]
    public async Task GetUtcNowAsync_OnContext_BeforeInit_Throws_AndPublishesNothing()
    {
        await using var db = _fx.CreateContext();
        try
        {
            // الحالة غير المهيأة (نمط التصفير القائم — الفشل المغلق): بذرة مُنشئ
            // الاختبارات تجد التوقيت null فيبدأ الخدمة غير مهيأة
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);
            var clock = new clsStoreClockService(db, forceHost: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => clock.GetUtcNowAsync(db));

            // البرهان على عدم النشر: المرساة لم تُكتب — UtcNow ما زال فشلًا مغلقًا
            Assert.Throws<Common.Utils.ProgramClock.ClockNotInitializedException>(
                () => Common.Utils.ProgramClock.UtcNow);
        }
        finally { TestClockConfig.Configure(); }
    }

    // ══ 28) نجاح إقلاع المضيف كاملًا ينشر التوقيت والمرساة معًا ══
    [Fact]
    public async Task Host_Init_Success_PublishesTimeZoneAndAnchor()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var clock = new clsStoreClockService(db, ny, forceHost: true);
            await clock.InitializeAsync();

            Assert.Equal("America/New_York", Common.Utils.ProgramClock.StoreTimeZone?.Id);

            // المرساة منشورة: UtcNow ≈ ساعة SQL (Kind=Utc) و«اليوم» يعمل
            DateTime published = Common.Utils.ProgramClock.UtcNow;
            Assert.Equal(DateTimeKind.Utc, published.Kind);
            Assert.True(Math.Abs((published - DateTime.UtcNow).TotalMinutes) < 2,
                $"المرساة بعد الإقلاع الناجح بعيدة عن ساعة SQL: {published:O}");
            Assert.Equal(DateOnly.FromDateTime(Common.Utils.ProgramClock.ToLocal(published)),
                Common.Utils.ProgramClock.Today);
        }
        finally
        {
            TestClockConfig.Configure();
            await WriteSettingAsync(db, null);
        }
    }

    // ══ 29) نجاح إقلاع الطرفي كاملًا ينشر التوقيت المقروء والمرساة معًا ══
    [Fact]
    public async Task Terminal_Init_Success_PublishesTimeZoneAndAnchor()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, "America/New_York"); // الطرفي يقرأ المعرّف ولا يكتب
            var clock = new clsStoreClockService(db, forceHost: false); // بلا توقيت محقون
            await clock.InitializeAsync();

            Assert.Equal("America/New_York", Common.Utils.ProgramClock.StoreTimeZone?.Id);

            DateTime published = Common.Utils.ProgramClock.UtcNow;
            Assert.Equal(DateTimeKind.Utc, published.Kind);
            Assert.True(Math.Abs((published - DateTime.UtcNow).TotalMinutes) < 2,
                $"المرساة بعد الإقلاع الناجح بعيدة عن ساعة SQL: {published:O}");
        }
        finally
        {
            TestClockConfig.Configure();
            await WriteSettingAsync(db, null);
        }
    }
}
