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
        finally { await WriteSettingAsync(db, null); }
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
        finally { await WriteSettingAsync(db, null); }
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
        finally { await WriteSettingAsync(db, null); }
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
        finally { await WriteSettingAsync(db, null); }
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
        finally { await WriteSettingAsync(db, null); }
    }
}
