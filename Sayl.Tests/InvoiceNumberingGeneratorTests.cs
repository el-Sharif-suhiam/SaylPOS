using System.Data;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// مولّد الرقم التجاري السنوي INV-YYYY-NNNNNN: أول رقم لسنة جديدة، التسلسل،
/// التسلسل المشترك عبر أنواع الفواتير (بلا وسيط نوع في التوقيع)، استقلال
/// عدّادات السنوات، التوازي الحقيقي (لا فجوات ولا تكرار)، أن الرجوع لا
/// يستهلك رقمًا، إنشاء صف العدّاد الغائب تحت التوازي، ورفض النداء بلا
/// معاملة نشطة. سنوات اصطناعية بعيدة (+100) تُنظَّف في finally —
/// القاعدة المشتركة تبقى نظيفة بين الاختبارات.
/// </summary>
[Collection("database")]
public class InvoiceNumberingGeneratorTests
{
    private readonly DatabaseFixture _fx;
    public InvoiceNumberingGeneratorTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>سنة اصطناعية بعيدة عن سنة الإنتاج — عدّادها لا تلمسه ترقيات/اختبارات أخرى.</summary>
    private static int SynthYear(int offset) => ProgramClock.Today.Year + 100 + offset;

    /// <summary>تخصيص داخل معاملة Serializable مستقلة مع الالتزام/التراجع عنها.</summary>
    private static async Task<Common.Results.Result<string>> AllocateAsync(
        SaylDbContext db, int year, bool commit)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var r = await clsInvoiceNumberGenerator.NextAsync(db, year);
        if (commit && r.IsSuccess)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return r;
    }

    /// <summary>قيمة العدّاد لسنة (0 إن غاب الصف).</summary>
    private static async Task<long> CounterAsync(SaylDbContext db, int year) =>
        await db.InvoiceNumberCounters.AsNoTracking()
            .Where(c => c.NumberYear == year)
            .Select(c => c.LastIssuedNumber)
            .FirstOrDefaultAsync();

    /// <summary>تنظيف صفوف عدّاد سنة اصطناعية (تُستدعى في finally دائمًا).</summary>
    private static async Task CleanupCountersAsync(SaylDbContext db, params int[] years)
    {
        await db.InvoiceNumberCounters
            .Where(c => years.Contains(c.NumberYear))
            .ExecuteDeleteAsync();
    }

    private static string Seq(long n) => n.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);

    // ══ 1) أول رقم لسنة جديدة = 000001 ══
    [Fact]
    public async Task FirstNumber_FreshYear_Is000001()
    {
        int year = SynthYear(0);
        await using var db = _fx.CreateContext();
        try
        {
            var r = await AllocateAsync(db, year, commit: true);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal($"INV-{year}-{Seq(1)}", r.Data);
        }
        finally { await CleanupCountersAsync(db, year); }
    }

    // ══ 2) تخصيصان في نفس السنة: 000001 ثم 000002 ══
    [Fact]
    public async Task SequentialAllocations_SameYear_IncrementByOne()
    {
        int year = SynthYear(1);
        await using var db = _fx.CreateContext();
        try
        {
            var first = await AllocateAsync(db, year, commit: true);
            var second = await AllocateAsync(db, year, commit: true);
            Assert.True(first.IsSuccess, first.Message);
            Assert.True(second.IsSuccess, second.Message);
            Assert.Equal($"INV-{year}-{Seq(1)}", first.Data);
            Assert.Equal($"INV-{year}-{Seq(2)}", second.Data);
        }
        finally { await CleanupCountersAsync(db, year); }
    }

    // ══ 3) تسلسل مشترك عبر أنواع الفواتير: بلا وسيط نوع — تخصيصان متتاليان
    //     يكملان عدّادًا واحدًا، وفاتورتان لأنواع مختلفة داخل المعاملتين
    //     تحملان الرقمين (تكامل مع Invoices نفسها) ══
    [Fact]
    public async Task SharedSequence_AcrossInvoiceTypes_SameCounter()
    {
        int year = SynthYear(2);
        string marker = $"t3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            // فاتورة sale داخل معاملة أولى برقمها المُسند
            await using var tx1 = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var r1 = await clsInvoiceNumberGenerator.NextAsync(db, year);
            Assert.True(r1.IsSuccess, r1.Message);
            db.Invoices.Add(new Invoice
            {
                Type = enInvoicesType.sale,
                Status = enInvoicesStatus.completed,
                PaymentStatus = enPaymentStatus.paid,
                PartyType = 'C',
                PartyId = seed.CustomerId,
                UserId = seed.UserId,
                TotalAmount = 10, DiscountAmount = 0, TaxAmount = 0, NetAmount = 10,
                Notes = marker,
                InvoiceNumber = r1.Data,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await tx1.CommitAsync();

            // فاتورة purchase داخل معاملة ثانية — نفس العدّاد بلا فاصل
            await using var tx2 = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var r2 = await clsInvoiceNumberGenerator.NextAsync(db, year);
            Assert.True(r2.IsSuccess, r2.Message);
            db.Invoices.Add(new Invoice
            {
                Type = enInvoicesType.purchase,
                Status = enInvoicesStatus.completed,
                PaymentStatus = enPaymentStatus.paid,
                PartyType = 'S',
                PartyId = seed.SupplierId,
                UserId = seed.UserId,
                TotalAmount = 20, DiscountAmount = 0, TaxAmount = 0, NetAmount = 20,
                Notes = marker,
                InvoiceNumber = r2.Data,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            await tx2.CommitAsync();

            Assert.NotEqual(r1.Data, r2.Data);
            Assert.Equal($"INV-{year}-{Seq(1)}", r1.Data);
            Assert.Equal($"INV-{year}-{Seq(2)}", r2.Data);
            Assert.Equal(2, await CounterAsync(db, year));

            // الفاتورتان المحفوظتان فعليًا — أنواع مختلفة وأرقام التسلسل نفسه
            var rows = await db.Invoices.AsNoTracking()
                .Where(i => i.Notes == marker)
                .Select(i => new { i.Type, i.InvoiceNumber })
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, r => r.Type == enInvoicesType.sale);
            Assert.Single(rows, r => r.Type == enInvoicesType.purchase);
            Assert.All(rows, r => Assert.NotNull(r.InvoiceNumber));
            Assert.Contains(r1.Data, rows.Select(r => r.InvoiceNumber));
            Assert.Contains(r2.Data, rows.Select(r => r.InvoiceNumber));
        }
        finally
        {
            await db.Invoices.Where(i => i.Notes == marker).ExecuteDeleteAsync();
            await CleanupCountersAsync(db, year);
        }
    }

    // ══ 4) سنوات مختلفة: عدّادات مستقلة — كل سنة تبدأ من 000001 ══
    [Fact]
    public async Task DifferentYears_IndependentCounters()
    {
        int yearA = SynthYear(3);
        int yearB = SynthYear(4);
        await using var db = _fx.CreateContext();
        try
        {
            var a1 = await AllocateAsync(db, yearA, commit: true);
            var a2 = await AllocateAsync(db, yearA, commit: true);
            var b1 = await AllocateAsync(db, yearB, commit: true);
            Assert.True(a1.IsSuccess && a2.IsSuccess && b1.IsSuccess,
                $"{a1.Message} | {a2.Message} | {b1.Message}");
            Assert.Equal($"INV-{yearA}-{Seq(1)}", a1.Data);
            Assert.Equal($"INV-{yearA}-{Seq(2)}", a2.Data);
            Assert.Equal($"INV-{yearB}-{Seq(1)}", b1.Data); // لا 000003 — صف مستقل

            var rows = await db.InvoiceNumberCounters.AsNoTracking()
                .Where(c => c.NumberYear == yearA || c.NumberYear == yearB)
                .OrderBy(c => c.NumberYear).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(yearA, rows[0].NumberYear);
            Assert.Equal(2, rows[0].LastIssuedNumber);
            Assert.Equal(yearB, rows[1].NumberYear);
            Assert.Equal(1, rows[1].LastIssuedNumber);
        }
        finally { await CleanupCountersAsync(db, yearA, yearB); }
    }

    // ══ 5) توازٍ حقيقي: 8 مهام متوازية (سياق/اتصال/معاملة Serializable لكل
    //     واحدة) على نفس السنة — الكل يمر (مهلة القفل سخية)، الأرقام كلها
    //     مميزة، والمجموعة = {base+1..base+8} بلا فجوات ولا تكرار ══
    [Fact]
    public async Task ConcurrentAllocations_SameYear_Unique_NoGaps()
    {
        int year = SynthYear(5);
        const int n = 8;
        await using var setup = _fx.CreateContext();
        try
        {
            long baseValue = await CounterAsync(setup, year); // 0 بعد التنظيف

            var tasks = Enumerable.Range(0, n).Select(async _ =>
            {
                await using var ctx = _fx.CreateContext();
                await using var tx = await ctx.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var r = await clsInvoiceNumberGenerator.NextAsync(ctx, year);
                if (r.IsSuccess)
                    await tx.CommitAsync();
                else
                    await tx.RollbackAsync();
                return r;
            });
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
            var numbers = results.Select(r => r.Data!).ToList();
            Assert.Equal(n, numbers.Distinct().Count());
            var expected = Enumerable.Range(1, n)
                .Select(i => $"INV-{year}-{Seq(baseValue + i)}").ToList();
            Assert.Equal(expected, numbers.OrderBy(x => x));
            Assert.Equal(baseValue + n, await CounterAsync(setup, year));
        }
        finally { await CleanupCountersAsync(setup, year); }
    }

    // ══ 6) الرجوع لا يستهلك رقمًا: تخصيص ملتزم، ثم تخصيص يُتراجع عنه —
    //     التخصيص التالي يعيد الرقم نفسه وقيمة العدّاد بلا تغيّر ══
    [Fact]
    public async Task Rollback_DoesNotConsume_Number()
    {
        int year = SynthYear(6);
        await using var db = _fx.CreateContext();
        try
        {
            var first = await AllocateAsync(db, year, commit: true);
            Assert.True(first.IsSuccess, first.Message);

            // تخصيص داخل معاملة تُتراجع — العدّاد يتراجع معها (تحديثه داخلها)
            var rolledBack = await AllocateAsync(db, year, commit: false);
            Assert.True(rolledBack.IsSuccess, rolledBack.Message);

            // فور التراجع: العدّاد بقى عند آخر تزامن مُلزَم (1) — لم يرتفع للتراجَع
            Assert.Equal(1, await CounterAsync(db, year));

            var next = await AllocateAsync(db, year, commit: true);
            Assert.True(next.IsSuccess, next.Message);
            // الرقم الذي أخذه التخصيص المتراجَع عنه يُعاد من جديد — لا يُستهلك
            Assert.Equal(rolledBack.Data, next.Data);
            Assert.Equal(2, await CounterAsync(db, year)); // التزام واحد فقط بعد الأساس
        }
        finally { await CleanupCountersAsync(db, year); }
    }

    // ══ 7) صف عدّاد غائب تحت التوازي: 4 معاملات متوازية على سنة بلا صف —
    //     أرقام متتالية مميزة من 000001، وصف واحد بالضبط بقيمة N ══
    [Fact]
    public async Task MissingCounterRow_Concurrent_InsertRaceSafe()
    {
        int year = SynthYear(7);
        const int n = 4;
        await using var setup = _fx.CreateContext();
        try
        {
            var tasks = Enumerable.Range(0, n).Select(async _ =>
            {
                await using var ctx = _fx.CreateContext();
                await using var tx = await ctx.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var r = await clsInvoiceNumberGenerator.NextAsync(ctx, year);
                if (r.IsSuccess)
                    await tx.CommitAsync();
                else
                    await tx.RollbackAsync();
                return r;
            });
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
            var numbers = results.Select(r => r.Data!).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(1, n).Select(i => $"INV-{year}-{Seq(i)}"), numbers);

            var rows = await setup.InvoiceNumberCounters.AsNoTracking()
                .Where(c => c.NumberYear == year).ToListAsync();
            Assert.Single(rows);
            Assert.Equal(n, rows[0].LastIssuedNumber);
        }
        finally { await CleanupCountersAsync(setup, year); }
    }

    // ══ 8) بلا معاملة نشطة: رفض صريح (عربي) قبل أي SQL — ولا صف عدّاد ══
    [Fact]
    public async Task NoActiveTransaction_Fails_WithoutCounterRow()
    {
        int year = SynthYear(8);
        await using var db = _fx.CreateContext();
        try
        {
            var r = await clsInvoiceNumberGenerator.NextAsync(db, year);
            Assert.False(r.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
            Assert.Equal(0, await db.InvoiceNumberCounters.AsNoTracking()
                .CountAsync(c => c.NumberYear == year));
        }
        finally { await CleanupCountersAsync(db, year); }
    }

    // ══ 9) سنة العدّاد من يوم المتجر لا من طابع UTC/ساعة الطرفية (Task 3):
    // التوقيت المنشور للرَن +3 ثابت — فاتورة 22:00 UTC من 31 ديسمبر هي محليًا
    // 01:00 من 1 يناير: الاشتقاق كما في مواقع الإنتاج (_storeClock.GetStoreToday(
    // nowUtc).Year) يعطي السنة الجديدة، وسلسلة السنة الجديدة تبدأ من 000001.
    // الكود القديم (احتياطي ProgramClock.Today.Year الساكن) كان يعطي سنة UTC. ══
    [Fact]
    public async Task YearDerivedFromStoreDate_AtPlus3_NewYearSequenceWins()
    {
        int utcYear = SynthYear(9); // سنة 31 ديسمبر بالطابع UTC
        DateTime nowUtc = new(utcYear, 12, 31, 22, 0, 0, DateTimeKind.Utc);
        await using var db = _fx.CreateContext();
        try
        {
            var clock = new clsStoreClockService(db); // يلتقط التوقيت المنشور (+3)

            // نفس الاشتقاق المستخدم في مواقع الإنتاج الثلاثة
            DateOnly storeToday = clock.GetStoreToday(nowUtc);
            Assert.Equal(new DateOnly(utcYear + 1, 1, 1), storeToday); // محليًا 1 يناير
            Assert.Equal(utcYear + 1, storeToday.Year);
            Assert.NotEqual(nowUtc.Year, storeToday.Year); // ليست سنة UTC

            var r = await AllocateAsync(db, storeToday.Year, commit: true);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal($"INV-{utcYear + 1}-{Seq(1)}", r.Data); // سلسلة السنة الجديدة
        }
        finally { await CleanupCountersAsync(db, utcYear + 1); }
    }

    // ══ 10) حدّ رأس السنة: التسلسل يبدأ جديدًا من 000001 للسنة الجديدة —
    // صف عدّاد مستقل لكل سنة (تصفير سنوي لا منطق إعادة تعيين) ══
    [Fact]
    public async Task NewYearBoundary_SequenceStartsFreshAt000001()
    {
        int oldYear = SynthYear(10);
        int newYear = SynthYear(11); // = oldYear + 1 — متتاليتان كما في رأس السنة
        await using var db = _fx.CreateContext();
        try
        {
            var lastOfOldYear = await AllocateAsync(db, oldYear, commit: true);
            Assert.True(lastOfOldYear.IsSuccess, lastOfOldYear.Message);
            Assert.Equal($"INV-{oldYear}-{Seq(1)}", lastOfOldYear.Data);

            var firstOfNewYear = await AllocateAsync(db, newYear, commit: true);
            Assert.True(firstOfNewYear.IsSuccess, firstOfNewYear.Message);
            // السنة الجديدة تفتح عدّادها الخاص من 000001 — لا تكملة للقديمة
            Assert.Equal($"INV-{newYear}-{Seq(1)}", firstOfNewYear.Data);
            Assert.Equal(1, await CounterAsync(db, newYear));
            Assert.Equal(1, await CounterAsync(db, oldYear));
        }
        finally { await CleanupCountersAsync(db, oldYear, newYear); }
    }
}
