using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ترقية Upgrade_19 — الترقيم التجاري السنوي INV-YYYY-NNNNNN (الحتمية النهائية):
/// مخطط التركيب الجديد (عمود NOT NULL + فهرس فريد عادي + عدّاد)،
/// مسار القاعدة القديمة (ترحيل الصفوف NULL بالسنة البرمجية UTC+3
/// وحدودها، تسلسل مشترك عبر الأنواع، الحفاظ على المُرقَّم سلفًا،
/// العدّاد = الأقصى، ثم شدّ NOT NULL وإعادة الفهرس عاديًّا ورفض أي
/// صف بقي بلا رقم)، Idempotency (إعادة التشغيل لا تعيد الإسناد)،
/// حاجز الفهرس الفريد على التكرار، ومراجع المثبّت.
/// </summary>
[Collection("database")]
public class InvoiceNumberingUpgradeTests
{
    private readonly DatabaseFixture _fx;
    public InvoiceNumberingUpgradeTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>مخطط التركيب الجديد: SaylDB.sql النهائية تنشئ العمود NOT NULL
    /// والفهرس الفريد العادي والعدّاد — والـfixture شغّل Upgrade_19 فوقها
    /// فيجب أن يمر بلا فشل (لا-op حقيقي: الحالة النهائية تُكتشف).</summary>
    [Fact]
    public async Task FreshInstallSchema_HasNotNullColumn_NormalUniqueIndex_AndCounterTable()
    {
        await using var db = _fx.CreateContext();
        await new clsUsersService(db).EnsureSchemaUpgradesAsync(); // noop على المخطط الكامل

        var col = (await db.Database.SqlQuery<ColumnRow>($@"
            SELECT name AS Name, max_length AS MaxLength, is_nullable AS IsNullable
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber'").ToListAsync()).Single();
        // max_length بوحدات البايت — NVARCHAR(32) = 64
        Assert.Equal(64, col.MaxLength);
        Assert.False(col.IsNullable); // الحتمية النهائية: NOT NULL — لا إدراج بلا رقم

        var idx = (await db.Database.SqlQuery<IndexRow>($@"
            SELECT is_unique AS IsUnique, has_filter AS HasFilter
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber'").ToListAsync()).Single();
        Assert.True(idx.IsUnique);
        Assert.False(idx.HasFilter); // فهرس عادي بلا مرشِّح — كل صف مميَّز برقم

        var countersTable = await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM sys.tables WHERE name = N'InvoiceNumberCounters'").ToListAsync();
        Assert.Equal(1, countersTable.Single());

        // سكربت الترقية نفسه (الذي يشغّله المثبّت يدويًا) يمر بلا فشل على
        // مخطط التركيب الجديد الكامل — لا-op حقيقي (idempotency عبر المسارات)
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_19_AnnualInvoiceNumbering.sql")));
    }

    /// <summary>مسار القاعدة القديمة: إسقاط العمود/العدّاد/الفهرس ثم بذر فواتير
    /// legacy (سنتان، حد UTC+3 السنوي 2025-12-31T22:00Z → 2026، صف بلا
    /// CreatedAt، صف مُرقَّم سلفًا محفوظ) → EnsureSchemaUpgradesAsync →
    /// ترقيم كامل بتسلسل مشترك حتمي وعدّادات = الأقصى.</summary>
    [Fact]
    public async Task EnsureSchemaUpgradesAsync_OnLegacyDatabase_Backfills_WithYearRules_AndPreservesExisting()
    {
        await using var db = _fx.CreateContext();

        // ── محاكاة قاعدة قديمة بلا عمود إطلاقًا: إسقاط الفهرس والعدّاد والعمود ──
        // + DBCC FREEPROCCACHE وجوبيًا: خطة مخزّنة لنفس النص (من اختبار سابق في
        //   نفس الرَن على القاعدة نفسها) تجعل SQL Server يتخطى الترجمة فيخفي
        //   أي عيب مرجعٍ ثابت للعمود في الدفعة — كنا نُثبت ذلك تجريبيًا.
        //   التنظيف يجعل هذا الاختبار محاكاة أمينة لأول إقلاع حقيقي دائمًا.
        await db.Database.ExecuteSqlRawAsync(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber')
    DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;
IF OBJECT_ID(N'dbo.InvoiceNumberCounters', N'U') IS NOT NULL DROP TABLE dbo.InvoiceNumberCounters;
IF COL_LENGTH(N'dbo.Invoices', N'InvoiceNumber') IS NOT NULL ALTER TABLE dbo.Invoices DROP COLUMN InvoiceNumber;
DBCC FREEPROCCACHE;");
        try
        {
            // المرحلة 1 — مسار إنشاء العمود على قاعدة قديمة: كل الصفوف القائمة تُرقَّم
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();

            // المرحلة 2 — قاعدة قديمة بترقيم جزئي سابق: بلا فهرس ولا عدّاد، والعمود
            // مُخفَّف إلى nullable كي يُقبل إدراج صفوف بلا رقم (المسار القديم
            // الفعلي: عمود nullable من ترقية سابقة لم تُعمِّد بعد)، وقيم محفوظة
            await db.Database.ExecuteSqlRawAsync(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber')
    DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;
IF OBJECT_ID(N'dbo.InvoiceNumberCounters', N'U') IS NOT NULL DROP TABLE dbo.InvoiceNumberCounters;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber' AND is_nullable = 0)
    ALTER TABLE dbo.Invoices ALTER COLUMN InvoiceNumber NVARCHAR(32) NULL;");

            string marker = $"u19-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            int uid = seed.UserId, cust = seed.CustomerId, sup = seed.SupplierId;

            // فواتير legacy بأنواع مختلفة (إثبات التسلسل المشترك) + صف محفوظ
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt)
VALUES (N'sale', N'completed', N'paid', N'C', {cust}, {uid}, 10, 0, 0, 10, '2025-06-15T10:00:00');   -- 2025
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt)
VALUES (N'purchase', N'completed', N'paid', N'S', {sup}, {uid}, 20, 0, 0, 20, '2025-12-31T22:00:00'); -- UTC+3 → 2026
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt)
VALUES (N'customer_return', N'completed', N'paid', N'C', {cust}, {uid}, 5, 0, 0, 5, NULL);            -- بلا CreatedAt → السنة الحالية
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, InvoiceNumber, CreatedAt)
VALUES (N'supplier_return', N'completed', N'paid', N'S', {sup}, {uid}, 7, 0, 0, 7, N'INV-2025-000042', '2025-07-01T09:00:00'); -- محفوظ");

            // القاعدة المتوقعة تُحسب من حالة القاعدة نفسها قبل الترقية (معزول عن بيانات اختبارات أخرى):
            // أعلى لاحقة محفوظة بتنسيق INV-<digits>-<digits> لكل سنة
            int base2025 = await MaxPreservedSuffixAsync(db, 2025);
            int base2026 = await MaxPreservedSuffixAsync(db, 2026);
            Assert.True(base2025 >= 42); // صفنا المحفوظ يدخل الحساب

            // مسار الإقلاع الفعلي — لا سكربت خارجي. صفوفنا الثلاثة NULL هي
            // الوحيدة بلا رقم، فالترتيب داخل كل سنة حتمي تمامًا.
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();

            var rows = await db.Database.SqlQuery<NumberedRow>($@"
            SELECT i.InvoiceId AS InvoiceId, i.Type AS Type, i.InvoiceNumber AS InvoiceNumber,
                   COALESCE(c.LastIssuedNumber, -1) AS LastIssuedNumber,
                   CASE WHEN i.CreatedAt IS NULL THEN YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME()))
                        ELSE YEAR(DATEADD(HOUR, 3, i.CreatedAt)) END AS ProgramYear
            FROM dbo.Invoices i
            OUTER APPLY (SELECT c.LastIssuedNumber
                         FROM dbo.InvoiceNumberCounters c
                         WHERE c.NumberYear = CASE WHEN i.CreatedAt IS NULL
                                                   THEN YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME()))
                                                   ELSE YEAR(DATEADD(HOUR, 3, i.CreatedAt)) END) c
            WHERE i.UserId = {uid}").ToListAsync();

            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.Matches(@"^INV-\d{4}-\d{6,}$", r.InvoiceNumber!));

            var sale2025 = rows.Single(r => r.Type == "sale");
            var purchase2026 = rows.Single(r => r.Type == "purchase");
            var custReturn = rows.Single(r => r.Type == "customer_return");
            var supReturn = rows.Single(r => r.Type == "supplier_return");

            // سنة 2025: العدّاد يبدأ فوق اللاحقة المحفوظة — لا ROW_NUMBER من 1 (تصادم ممنوع)
            Assert.Equal(2025, sale2025.ProgramYear);
            Assert.Equal($"INV-2025-{base2025 + 1:D6}", sale2025.InvoiceNumber);
            // حد UTC+3: 2025-12-31T22:00Z → 2026-01-01T01:00 بالتوقيت البرمجي
            Assert.Equal(2026, purchase2026.ProgramYear);
            Assert.Equal($"INV-2026-{base2026 + 1:D6}", purchase2026.InvoiceNumber);
            // بلا CreatedAt → السنة البرمجية الحالية (مُوثَّق) وأخيرًا في ترتيب السنة
            Assert.Equal(2026, custReturn.ProgramYear);
            Assert.Equal($"INV-2026-{base2026 + 2:D6}", custReturn.InvoiceNumber);
            // المُرقَّم سلفًا محفوظ حرفيًا — لا يُعاد إسناده
            Assert.Equal("INV-2025-000042", supReturn.InvoiceNumber);

            // العدّادات = الأقصى المُسند لكل سنة (لا يُخفَّض أبدًا)
            var counters = await db.Database.SqlQuery<CounterRow>($@"
            SELECT NumberYear AS NumberYear, LastIssuedNumber AS LastIssuedNumber
            FROM dbo.InvoiceNumberCounters WHERE NumberYear IN (2025, 2026)").ToListAsync();
            Assert.Equal(base2025 + 1, counters.Single(c => c.NumberYear == 2025).LastIssuedNumber);
            Assert.Equal(base2026 + 2, counters.Single(c => c.NumberYear == 2026).LastIssuedNumber);

            // حاجز المحرك: تكرار رقم محفوظ يُرفض بالفهرس الفريد المُرشَّح (2601)
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, InvoiceNumber, CreatedAt) " +
                $"VALUES (N'sale', N'completed', N'paid', N'C', {cust}, {uid}, 1, 0, 0, 1, N'INV-2025-000042', SYSUTCDATETIME())"));

            // لا NULL بقيت في صفوف هذا الاختبار
            Assert.DoesNotContain(rows, r => r.InvoiceNumber == null);

            // الحتمية النهائية بعد الترحيل على القاعدة القديمة: العمود NOT NULL
            // والفهرس فريد عادي بلا مرشِّح — أي إدراج بلا رقم يُرفض هيكليًا
            var finalCol = (await db.Database.SqlQuery<ColumnRow>($@"
                SELECT name AS Name, max_length AS MaxLength, is_nullable AS IsNullable
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber'").ToListAsync()).Single();
            Assert.False(finalCol.IsNullable);
            var finalIdx = (await db.Database.SqlQuery<IndexRow>($@"
                SELECT is_unique AS IsUnique, has_filter AS HasFilter
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber'").ToListAsync()).Single();
            Assert.True(finalIdx.IsUnique);
            Assert.False(finalIdx.HasFilter);

            // الإدراج بلا رقم مستحيل الآن — رفض NOT NULL هيكليًا (وليس فهرسًا)
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt) " +
                $"VALUES (N'sale', N'completed', N'paid', N'C', {cust}, {uid}, 1, 0, 0, 1, SYSUTCDATETIME())"));
        }
        finally
        {
            // إعادة الحالة المشتركة للـfixture: إعادة تشغيل الترقية تعيد إنشاء كل شيء وترقّم ما زال NULL
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        }
    }

    /// <summary>Idempotency: ثلاث إقلاعات متتالية — بلا أخطاء، لا إسناد مزدوج،
    /// لا تتغير الأرقام ولا العدّادات (الصفوف المُرقَّمة لا تُمس أبدًا). أول
    /// إقلاع يرقّم صفًا NULL محاكاته قاعدة قديمة (تخفيف العمود + هدم الفهرس)،
    /// والإقلاعان التاليان على الحالة النهائية no-op حقيقي.</summary>
    [Fact]
    public async Task EnsureSchemaUpgradesAsync_IsIdempotent_ThreeRuns_NoReassignment()
    {
        await using var db = _fx.CreateContext();
        string marker = $"u19i-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        // محاكاة قاعدة قديمة بصف بلا رقم: هدم الفهرس + تخفيف العمود كي يُقبل إدراج NULL
        await db.Database.ExecuteSqlRawAsync(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber')
    DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber' AND is_nullable = 0)
    ALTER TABLE dbo.Invoices ALTER COLUMN InvoiceNumber NVARCHAR(32) NULL;");
        await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.Invoices (Type, Status, PaymentStatus, PartyType, PartyId, UserId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, CreatedAt)
VALUES (N'sale', N'completed', N'paid', N'C', {seed.CustomerId}, {seed.UserId}, 3, 0, 0, 3, SYSUTCDATETIME())");

        await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        var before = await SnapshotAsync(db, seed.UserId);

        for (int i = 0; i < 2; i++) // مجموع ثلاثة إقلاعات
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();

        var after = await SnapshotAsync(db, seed.UserId);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].InvoiceNumber, after[i].InvoiceNumber);
            Assert.Equal(before[i].LastIssuedNumber, after[i].LastIssuedNumber);
        }
        Assert.All(after, r => Assert.NotNull(r.InvoiceNumber));

        // الحتمية النهائية باقية بعد الإقلاعات الثلاثة: NOT NULL + فهرس عادي فريد
        var col = (await db.Database.SqlQuery<ColumnRow>($@"
            SELECT name AS Name, max_length AS MaxLength, is_nullable AS IsNullable
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber'").ToListAsync()).Single();
        Assert.False(col.IsNullable);
        var idx = (await db.Database.SqlQuery<IndexRow>($@"
            SELECT is_unique AS IsUnique, has_filter AS HasFilter
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber'").ToListAsync()).Single();
        Assert.True(idx.IsUnique);
        Assert.False(idx.HasFilter);
    }

    /// <summary>التغليف: Upgrade_19 يجب أن يُنسخ إلى حزمة المثبّت
    /// (قائمة المصادر في build-installer.ps1 وقسم [Files] في setup.iss).</summary>
    [Fact]
    public async Task Installer_Includes_Upgrade19_Migration()
    {
        string repoRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
        string ps1 = await File.ReadAllTextAsync(Path.Combine(repoRoot, "installer", "build-installer.ps1"));
        string iss = await File.ReadAllTextAsync(Path.Combine(repoRoot, "installer", "setup.iss"));

        Assert.Contains("Upgrade_19_AnnualInvoiceNumbering.sql", ps1);
        Assert.Contains("Upgrade_19_AnnualInvoiceNumbering.sql", iss);
    }

    /// <summary>أعلى لاحقة رقمية مُحلَّلة من الأرقام المحفوظة ذات تنسيقنا
    /// INV-<digits>-<digits> في سنة معيّنة (نفس منطق التحليل في الترقية) —
    /// أو 0 إن لا شيء.</summary>
    private static async Task<int> MaxPreservedSuffixAsync(SaylDbContext db, int year) =>
        (await db.Database.SqlQuery<int?>($@"
            SELECT MAX(CAST(SUBSTRING(InvoiceNumber, 10, 100) AS INT)) AS Value
            FROM dbo.Invoices
            WHERE InvoiceNumber IS NOT NULL
              AND SUBSTRING(InvoiceNumber, 1, 4) = N'INV-'
              AND SUBSTRING(InvoiceNumber, 5, 4) = {year:D4}
              AND SUBSTRING(InvoiceNumber, 9, 1) = N'-'
              AND SUBSTRING(InvoiceNumber, 10, 100) <> N''
              AND SUBSTRING(InvoiceNumber, 10, 100) NOT LIKE N'%[^0-9]%'").ToListAsync()).FirstOrDefault() ?? 0;

    private static async Task<List<NumberedRow>> SnapshotAsync(SaylDbContext db, int userId) =>
        await db.Database.SqlQuery<NumberedRow>($@"
            SELECT i.InvoiceId AS InvoiceId, i.Type AS Type, i.InvoiceNumber AS InvoiceNumber,
                   COALESCE(c.LastIssuedNumber, -1) AS LastIssuedNumber,
                   0 AS ProgramYear
            FROM dbo.Invoices i
            OUTER APPLY (SELECT c.LastIssuedNumber
                         FROM dbo.InvoiceNumberCounters c
                         WHERE c.NumberYear = CASE WHEN i.CreatedAt IS NULL
                                                   THEN YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME()))
                                                   ELSE YEAR(DATEADD(HOUR, 3, i.CreatedAt)) END) c
            WHERE i.UserId = {userId}").ToListAsync();

    private sealed class ColumnRow
    {
        public string Name { get; set; } = "";
        public short MaxLength { get; set; }
        public bool IsNullable { get; set; }
    }

    private sealed class IndexRow
    {
        public bool IsUnique { get; set; }
        public bool HasFilter { get; set; }
    }

    private sealed class NumberedRow
    {
        public int InvoiceId { get; set; }
        public string Type { get; set; } = "";
        public string? InvoiceNumber { get; set; }
        public int ProgramYear { get; set; }
        public long LastIssuedNumber { get; set; }
    }

    private sealed class CounterRow
    {
        public int NumberYear { get; set; }
        public long LastIssuedNumber { get; set; }
    }
}
