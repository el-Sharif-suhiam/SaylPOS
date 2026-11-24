using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Task 4 — التعامل مع تاريخ المتجر المحلي في التقارير:
/// كل الطوابع المخزَّنة UTC تُحوَّل إلى يوم المتجر (التوقيت المنشور) قبل
/// التجميع اليومي/الأسبوعي/الشهري وحساب العمر — لا تاريخ UTC الخام.
/// التوقيت المحقون Test/FixedPlus3 (+3 بلا DST) وتوقيتات مخصصة ثابتة
/// التواريخ (23/25 ساعة) — بلا أي اعتماد على توقيت جهاز CI.
/// قاعدة مشتركة [Collection("database")].
/// </summary>
[Collection("database")]
public class ReportStoreLocalDateTests
{
    private readonly DatabaseFixture _fx;
    public ReportStoreLocalDateTests(DatabaseFixture fx) => _fx = fx;

    private static string Marker(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..12];

    /// <summary>فاتورة بيع مكتملة بطابع تاريخي ثابت (UTC) — بذر مباشر بلا تدفق
    /// بيع (عزل تام: التقرير يقرأ CreatedAt وType وقيم المال فقط).</summary>
    private static Invoice MakeSale(int userId, int productId, DateTime createdAtUtc,
        decimal total = 20m, int? partyId = null)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = partyId,
            PartyType = partyId == null ? null : 'C',
            UserId = userId,
            TotalAmount = total,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = total,
            InvoiceNumber = TestSeed.NewInvoiceNumber(),
            CreatedAt = createdAtUtc
        };
        return invoice;
    }

    // ══ 1+2) قبل/بعد منتصف ليل المتجر: نفس التاريخ UTC يومان مختلفان بمعنى المتجر ══
    // فاتورتان في 30 يونيو 2031 UTC (نافذة سنوات لا يستخدمها اختبار آخر —
    // قاعدة الاختبارات مشتركة): 20:00Z = 23:00 يوم 30-06 بالمتجر (+3) و
    // 22:00Z = 01:00 يوم 1 يوليو بالمتجر — التجميع اليومي بمعنى المتجر يفصلهما،
    // والتجميع القديم بتاريخ UTC الخام كان يدمجهما تحت يوم 30-06.
    [Fact]
    public async Task SalesTrend_GroupsByStoreLocalDay_AroundMidnight()
    {
        string marker = Marker("slrd1");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 6, 30, 20, 0, 0, DateTimeKind.Utc)));
        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 6, 30, 22, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ساعة محقونة +3 تُمرَّر صراحةً (لا الافتراضي المبذور) — والحجم ضيق:
        // التجميع بمعنى المتجر: 20:00Z → يوم 2031-06-30 و22:00Z → يوم 2031-07-01
        var reports = new clsReportService(db, new clsStoreClockService(db, TestClockConfig.Tz));
        var result = await reports.GetSalesTrendAsync(
            new DateOnly(2031, 6, 30), new DateOnly(2031, 7, 1), enTrendGroupBy.Day);

        Assert.True(result.IsSuccess, result.Message);
        var points = result.Data!.OrderBy(p => p.PeriodStart).ToList();

        Assert.Equal(2, points.Count); // القديم: نقطة واحدة (كلتاهما تحت 30-06 UTC)
        Assert.Equal(new DateOnly(2031, 6, 30), points[0].PeriodStart);
        Assert.Equal(new DateOnly(2031, 6, 30), points[0].PeriodEnd);
        Assert.Equal(1, points[0].InvoiceCount);
        Assert.Equal(20m, points[0].NetSales);
        // 22:00Z يوم 30 يونيو UTC → تندرج تحت يوم 1 يوليو المحلي
        Assert.Equal(new DateOnly(2031, 7, 1), points[1].PeriodStart);
        Assert.Equal(new DateOnly(2031, 7, 1), points[1].PeriodEnd);
        Assert.Equal(1, points[1].InvoiceCount);
        Assert.Equal(20m, points[1].NetSales);
    }

    // ══ 3) الاتساق الأسبوعي/الشهري بعد التحويل: الدلو يُبنى على يوم المتجر ══
    // أيام المتجر (نافذة سنوات لا يستخدمها اختبار آخر): 2031-12-29 (إثنين)
    // و2032-01-01 (فاتورة 31-12 22:00Z) و2032-01-05 (إثنين).
    // أسبوعيًا (بداية الإثنين): [2031-12-29 يضم 29-12 و1-1] و[2032-01-05].
    // شهريًا: [2031-12-01 يضم 29-12] و[2032-01-01 يضم 1-1 و5-1].
    // فاتورة 31-12 22:00Z (يوم متجر 1-1) كانت بالقديم تدخل دلو ديسمبر
    // بالتاريخ UTC الخام.
    [Fact]
    public async Task SalesTrend_WeeklyAndMonthly_BucketsOnStoreLocalDays()
    {
        string marker = Marker("slrd3");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 12, 29, 10, 0, 0, DateTimeKind.Utc)));
        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 12, 31, 22, 0, 0, DateTimeKind.Utc))); // يوم متجر 2032-01-01
        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2032, 1, 5, 10, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reports = new clsReportService(db, new clsStoreClockService(db, TestClockConfig.Tz));

        var weekly = await reports.GetSalesTrendAsync(
            new DateOnly(2031, 12, 29), new DateOnly(2032, 1, 5), enTrendGroupBy.Week);
        Assert.True(weekly.IsSuccess, weekly.Message);
        var w = weekly.Data!.OrderBy(p => p.PeriodStart).ToList();
        Assert.Equal(2, w.Count);
        Assert.Equal(new DateOnly(2031, 12, 29), w[0].PeriodStart); // أسبوع الإثنين 29-12 يضم فاتورة 22:00Z
        Assert.Equal(new DateOnly(2032, 1, 1), w[0].PeriodEnd); // آخر يوم متجر في الأسبوع (لا 31-12 UTC)
        Assert.Equal(40m, w[0].NetSales);
        Assert.Equal(new DateOnly(2032, 1, 5), w[1].PeriodStart);
        Assert.Equal(20m, w[1].NetSales);

        var monthly = await reports.GetSalesTrendAsync(
            new DateOnly(2031, 12, 29), new DateOnly(2032, 1, 5), enTrendGroupBy.Month);
        Assert.True(monthly.IsSuccess, monthly.Message);
        var m = monthly.Data!.OrderBy(p => p.PeriodStart).ToList();
        Assert.Equal(2, m.Count);
        Assert.Equal(new DateOnly(2031, 12, 1), m[0].PeriodStart); // دلو ديسمبر يضم 29-12 وحده
        Assert.Equal(20m, m[0].NetSales);
        Assert.Equal(new DateOnly(2032, 1, 1), m[1].PeriodStart); // 22:00Z دخل يناير المحلي
        Assert.Equal(40m, m[1].NetSales);
    }

    // ══ 4) الدفعات قريبة الانتهاء: «اليوم» من ساعة SQL عبر الخدمة لا ساعة العميل ══
    // حافة النافذة تحسب من اليوم بمعنى المتجر المشتق من لقطة SQL — حتمي
    // بلا اعتماد على توقيت CI: الدفعة المنتهية أمس = IsExpired، واليوم = 0
    // غير منتهية، وحد النافذة الشامل today+5 وحصري today+6.
    [Fact]
    public async Task ExpiringBatches_TodayFromSqlWindowEdgesCorrect()
    {
        string marker = Marker("slrd4");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // «اليوم» المحتمل نفسه الذي تُشتقه الخدمة: لقطة SQL + التوقيت المحقون
        var clock = new clsStoreClockService(db, TestClockConfig.Tz);
        DateOnly expectedToday = await clock.GetStoreTodayAsync(db);

        DateOnly[] expiries =
        {
            expectedToday.AddDays(-1), // منتهية
            expectedToday,             // تنتهي اليوم — غير منتهية بعد
            expectedToday.AddDays(5),  // آخر يوم داخل نافذة 5
            expectedToday.AddDays(6)   // خارج نافذة 5
        };
        for (int i = 0; i < expiries.Length; i++)
        {
            db.Batches.Add(new Batch
            {
                ProductId = seed.ProductId,
                BatchNumber = $"SLRD4-{i}-{marker}"[..20],
                ExpiryDate = expiries[i],
                BatchQuantity = 3,
                UnitCost = 10m
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reports = new clsReportService(db, new clsStoreClockService(db, TestClockConfig.Tz));
        var result = await reports.GetExpiringBatchesAsync(withinDays: 5);

        Assert.True(result.IsSuccess, result.Message);
        var rows = result.Data!;
        var expired = rows.Single(b => b.BatchNumber == $"SLRD4-0-{marker}"[..20]);
        Assert.True(expired.IsExpired);
        Assert.Equal(-1, expired.DaysUntilExpiry);

        var todayBatch = rows.Single(b => b.BatchNumber == $"SLRD4-1-{marker}"[..20]);
        Assert.False(todayBatch.IsExpired);
        Assert.Equal(0, todayBatch.DaysUntilExpiry);

        var edge = rows.Single(b => b.BatchNumber == $"SLRD4-2-{marker}"[..20]);
        Assert.False(edge.IsExpired);
        Assert.Equal(5, edge.DaysUntilExpiry);

        Assert.DoesNotContain(rows, b => b.BatchNumber == $"SLRD4-3-{marker}"[..20]); // حصري بعد +5
    }

    // ══ 5) أعمار الديون: عمر الدين من يوم المتجر المحلي لا تاريخ UTC الخام ══
    // فاتورة آجلة 100 غير مسددة بطابع 2032-02-28 22:00Z = يوم متجر 2032-02-29
    // (سنة كبيسة). asOf = 2032-03-30: العمر الصحيح 30 يومًا (شريحة 0-30) —
    // القديم (تاريخ UTC الخام 2032-02-28) كان يعطي 31 يومًا (شريحة 31-60).
    // نافذة سنوات مستقلة عن اختبارات التجميع (قاعدة الاختبارات مشتركة).
    [Fact]
    public async Task DebtAgeing_AgeUsesStoreLocalDay_WhenUtcDateDiffers()
    {
        string marker = Marker("slrd5");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var invoice = MakeSale(seed.UserId, seed.ProductId,
            new DateTime(2032, 2, 28, 22, 0, 0, DateTimeKind.Utc),
            total: 100m, partyId: seed.CustomerId);
        db.Invoices.Add(invoice);
        await db.Customers
            .Where(c => c.PartyId == seed.CustomerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reports = new clsReportService(db, new clsStoreClockService(db, TestClockConfig.Tz));
        var result = await reports.GetDebtAgeingReportAsync(new DateOnly(2032, 3, 30));

        Assert.True(result.IsSuccess, result.Message);
        var row = result.Data!.Single(a => a.CustomerId == seed.CustomerId);
        Assert.Equal(100m, row.Balance);
        Assert.Equal(100m, row.Age0to30); // العمر 30 يومًا باليوم المحلي
        Assert.Equal(0m, row.Age31to60);  // القديم كان يضع الدين هنا (عمر 31 بالتاريخ UTC)
        Assert.Equal(0m, row.Age61to90);
        Assert.Equal(0m, row.Age90plus);
    }

    // ══ 6) أيام DST بطول 23 و25 ساعة (توقيت مخصص ثابت التواريخ) ══
    // أساس +1، دلتا صيفية +1: بدء DST 31 مارس 02:00 (اليوم 23 ساعة) ونهايته
    // 27 أكتوبر 03:00 (اليوم 25 ساعة). حدود اليوم تُحسب بالقواعد الديناميكية
    // والتجميع اليومي يبقي فاتورتي اليوم الطويل (00:30 و22:30 محليًا) تحت
    // نقطة يوم واحدة، ويُسقط فاتورة ما بعد منتصف ليل اليوم القصير في اليوم التالي.
    [Fact]
    public async Task SalesTrend_DstTwentyThreeAndTwentyFiveHourDays()
    {
        string marker = Marker("slrd6");
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date,
            TimeSpan.FromHours(1), // دلتا صيفية +1
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), month: 3, day: 31), // بدء DST: 31 مارس 02:00
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), month: 10, day: 27)); // نهاية DST: 27 أكتوبر 03:00
        var tz = TimeZoneInfo.CreateCustomTimeZone("Test/DstFixedDates",
            TimeSpan.FromHours(1), "Test/DstFixedDates", "Dst Fixed Standard", "Dst Fixed Daylight", new[] { rule });

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            Common.Utils.ProgramClock.ConfigureStoreTimeZone(tz); // ProgramDay يقرأ المنشور

            db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 3, 30, 23, 30, 0, DateTimeKind.Utc))); // 00:30 محلي 31 مارس
            db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 3, 31, 22, 30, 0, DateTimeKind.Utc))); // 00:30 محلي 1 أبريل
            db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 10, 26, 22, 30, 0, DateTimeKind.Utc))); // 00:30 محلي 27 أكتوبر
            db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, new DateTime(2031, 10, 27, 21, 30, 0, DateTimeKind.Utc))); // 22:30 محلي 27 أكتوبر
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var reports = new clsReportService(db, new clsStoreClockService(db, tz));

            // اليوم القصير 23 ساعة: [2031-03-30 23:00Z، 2031-04-01 22:00Z)
            var spring = await reports.GetSalesTrendAsync(
                new DateOnly(2031, 3, 31), new DateOnly(2031, 4, 1), enTrendGroupBy.Day);
            Assert.True(spring.IsSuccess, spring.Message);
            var s = spring.Data!.OrderBy(p => p.PeriodStart).ToList();
            Assert.Equal(2, s.Count);
            Assert.Equal(new DateOnly(2031, 3, 31), s[0].PeriodStart); // 23:30Z يوم 30 مارس UTC → محليًا 31 مارس
            Assert.Equal(new DateOnly(2031, 4, 1), s[1].PeriodStart);  // 22:30Z يوم 31 مارس UTC → محليًا 1 أبريل

            // اليوم الطويل 25 ساعة: [2031-10-26 22:00Z، 2031-10-27 23:00Z)
            var autumn = await reports.GetSalesTrendAsync(
                new DateOnly(2031, 10, 27), new DateOnly(2031, 10, 27), enTrendGroupBy.Day);
            Assert.True(autumn.IsSuccess, autumn.Message);
            var a = autumn.Data!;
            Assert.Single(a); // فاتورتا اليوم الطويل تحت نقطة يوم واحد (القديم: نقطتان بتاريخي UTC)
            Assert.Equal(new DateOnly(2031, 10, 27), a[0].PeriodStart);
            Assert.Equal(2, a[0].InvoiceCount);
            Assert.Equal(40m, a[0].NetSales);
        }
        finally
        {
            TestClockConfig.Configure(); // النشر الجانبي للساعات الموحدة لا يتسرب لاختبارات أخرى
        }
    }

    // ══ 7) إثبات الفلترة: شامل البداية وحصري النهاية (حدود يوم المتجر بمعنى UTC) ══
    // يوم 2026-06-10 بتوقيت +3: البداية 2026-06-09 21:00Z والنهاية 2026-06-10 21:00Z.
    // فاتورة على البداية بالضبط تدخل وفاتورة على النهاية بالضبط (منتصف ليل اليوم
    // التالي محليًا) تخرج — لا تدخل ولا تخرج بالتاريخ UTC الخام.
    [Fact]
    public async Task SalesTrend_InclusiveStartExclusiveEnd_Bounds()
    {
        string marker = Marker("slrd7");
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        DateOnly day = new(2026, 6, 10);
        (DateTime startUtc, DateTime endUtc) =
            Common.Utils.ProgramClock.LocalDayBoundsUtc(day); // التوقيت المنشور Test/FixedPlus3
        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, startUtc)); // على الحد — يدخل
        db.Invoices.Add(MakeSale(seed.UserId, seed.ProductId, endUtc));   // على الحد — يخرج
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reports = new clsReportService(db, new clsStoreClockService(db, TestClockConfig.Tz));
        var result = await reports.GetSalesTrendAsync(day, day, enTrendGroupBy.Day);

        Assert.True(result.IsSuccess, result.Message);
        var points = result.Data!;
        Assert.Single(points); // فاتورة النهاية خارج النطاق الحصري لا تُحمَّل أصلًا
        Assert.Equal(day, points[0].PeriodStart);
        Assert.Equal(1, points[0].InvoiceCount);
        Assert.Equal(20m, points[0].NetSales);
    }
}
