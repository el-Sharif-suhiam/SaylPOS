using Microsoft.EntityFrameworkCore;
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
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
        finally { Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); }
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

    // ══ 11) فشل الإقلاع ينشر شيئًا — الساعات تبقى على الاحتياطي الإرثي ══
    [Fact]
    public async Task FailedInit_DoesNotPublishStoreTimeZone()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await WriteSettingAsync(db, null);
            var clock = new clsStoreClockService(db, forceHost: false);

            await Assert.ThrowsAsync<clsStoreClockService.StoreClockInitializationException>(
                () => clock.InitializeAsync());

            Assert.Null(Common.Utils.ProgramClock.StoreTimeZone);

            // التقوية: بعد فشل الإقلاع تبقى ToLocal على الاحتياطي الإرثي (+3) —
            // نفس الإزاحة التي يجب ألا يعتمد عليها إقلاع الإنتاج أبدًا (fail-closed
            // في Sayl.UI/Program.cs يمنع العمل عليها)
            DateTime utcProbe = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(utcProbe.AddHours(Common.Utils.ProgramClock.UtcOffsetHours),
                Common.Utils.ProgramClock.ToLocal(utcProbe));
        }
        finally { Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); }
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
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
        finally { Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); }
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
        finally { Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); }
    }

    // ══ 14) الاحتياطي الإرثي بعد التصفير: +3 ثابتة ══
    [Fact]
    public void ProgramClock_AfterReset_LegacyFixedOffsetFallback()
    {
        try
        {
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null);

            // NowLocal ضمن دقيقة من UTC + 3 (الاحتياطي الإرثي)
            Assert.True(Math.Abs((Common.Utils.ProgramClock.NowLocal - DateTime.UtcNow.AddHours(3)).TotalMinutes) < 1,
                "الاحتياطي الإرثي يجب أن يكون UTC+3");

            // FromUtc بإزاحة +3 الثابتة: 2026-03-08 21:00Z = منتصف ليل 2026-03-09 بمعنى +3
            Assert.Equal(new DateOnly(2026, 3, 9),
                Common.Utils.ProgramClock.FromUtc(new DateTime(2026, 3, 8, 21, 0, 0, DateTimeKind.Utc)));

            // ToLocal بالإزاحة الثابتة نفسها
            DateTime utc = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(utc.AddHours(3), Common.Utils.ProgramClock.ToLocal(utc));
        }
        finally { Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); }
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
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(null); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
            await WriteSettingAsync(db, null);
        }
    }
}
