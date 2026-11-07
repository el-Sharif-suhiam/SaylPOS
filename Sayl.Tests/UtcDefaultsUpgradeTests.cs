using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ترقية Upgrade_15: افتراضيات الطوابع الزمنية التقنية (CreatedAt/UpdatedAt/StartedAt)
/// تُعاد تعريفها بـ SYSUTCDATETIME() بدل GETDATE() (توقيت الخادم المحلي).
/// - الافتراضية فقط تتغير — لا إزاحة لأي قيمة مخزنة (Upgrade_08 رحّل البيانات التاريخية مسبقًا).
/// - أعمدة تواريخ الأعمال (PaymentDate، ExpiryDate، ExpenseDate...) DATE بلا افتراضيات — لا تُمس.
/// - الـfixture نفسه يشغّل Upgrade_15 بعد Upgrade_14 على قاعدة بُنيت من SaylDB.sql
///   (التي أصبحت بافتراضيات UTC — فيثبت السكربت أنه no-op آمن على قاعدة محدثة).
/// </summary>
[Collection("database")]
public class UtcDefaultsUpgradeTests
{
    private readonly DatabaseFixture _fx;
    public UtcDefaultsUpgradeTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>(الجدول، العمود) للطوابع التقنية التي يجب أن تكون افتراضيتها UTC.
    /// تطابق قائمة Upgrade_15 — أعمدة تواريخ الأعمال (DATE) عمدًا غير موجودة هنا.</summary>
    public static readonly (string Table, string Column)[] TechnicalTimestampColumns =
    {
        ("Parties", "CreatedAt"),
        ("Employees", "CreatedAt"),
        ("Users", "CreatedAt"),
        ("Products", "CreatedAt"),
        ("Batches", "CreatedAt"),
        ("Inventory", "UpdatedAt"),
        ("Invoices", "CreatedAt"),
        ("StockReservations", "CreatedAt"),
        ("Payments", "CreatedAt"),
        ("StockMovements", "CreatedAt"),
        ("Expenses", "CreatedAt"),
        ("SystemLogs", "CreatedAt"),
        ("POSSessions", "StartedAt"),
        ("Settings", "UpdatedAt"),
        ("CashMovements", "CreatedAt"),
        ("PayrollRecords", "CreatedAt"),
        ("StockCounts", "CreatedAt"),
    };

    private static string RepoRoot =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");

    private static string Upgrade15Path => Path.Combine(RepoRoot, "Upgrade_15_UtcDefaults.sql");

    /// <summary>كل عمود تقني موجود فعليًا لديه افتراضية واحدة بتعريف UTC (SYSUTCDATETIME/GETUTCDATE) —
    /// ولا افتراضية واحدة تبقى بتعريف GETDATE المحلي. نفس أسلوب فحص sys.default_constraints
    /// في BatchUnitCostUpgradeTests لترقية Upgrade_11.</summary>
    [Fact]
    public async Task AllTechnicalTimestampDefaults_AreUtcBased()
    {
        await using var db = _fx.CreateContext();
        int checkedCount = 0;

        foreach (var (table, column) in TechnicalTimestampColumns)
        {
            var defaults = await db.Database
                .SqlQuery<UtcDefaultRow>($@"
                    SELECT dc.name AS ConstraintName, LOWER(dc.definition) AS Definition
                    FROM sys.default_constraints dc
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME({table}))
                      AND dc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME({table})), {column}, 'ColumnId')")
                .ToListAsync();

            Assert.True(defaults.Count == 1,
                $"{table}.{column}: expected exactly 1 default constraint, found {defaults.Count}");
            var d = defaults[0];
            Assert.True(d.Definition.Contains("utcdatetime"),
                $"{table}.{column}: default '{d.ConstraintName}' is not UTC-based: {d.Definition}");
            Assert.True(!d.Definition.Contains("getdate"),
                $"{table}.{column}: default '{d.ConstraintName}' still uses local-time GETDATE: {d.Definition}");
            checkedCount++;
        }

        Assert.Equal(TechnicalTimestampColumns.Length, checkedCount);
    }

    /// <summary>الأسماء المتعارف عليها بقيت كما هي: القيود المسمّاة في SaylDB.sql
    /// (DF_SystemLogs_CreatedAt، DF_Settings_UpdatedAt، DF_CashMovements_CreatedAt)
    /// تُستعاد بنفس الاسم بعد إسقاط الافتراضية المحلية. StockReservations في
    /// SaylDB.sql يُنشأ بلا اسم — مساره المسمّى (DF_StockReservations_CreatedAt)
    /// يُختبر عبر محاكاة القاعدة القديمة: إعادة افتراضية محلية ثم تشغيل الترقية.</summary>
    [Fact]
    public async Task NamedConstraints_KeepTheirNames_AfterUtcRedefinition()
    {
        await using var db = _fx.CreateContext();
        foreach (var (table, column, expectedName) in new[]
                 {
                     ("SystemLogs", "CreatedAt", "DF_SystemLogs_CreatedAt"),
                     ("Settings", "UpdatedAt", "DF_Settings_UpdatedAt"),
                     ("CashMovements", "CreatedAt", "DF_CashMovements_CreatedAt"),
                 })
        {
            var names = await db.Database
                .SqlQuery<string>($@"
                    SELECT dc.name AS Value
                    FROM sys.default_constraints dc
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME({table}))
                      AND dc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME({table})), {column}, 'ColumnId')")
                .ToListAsync();
            Assert.True(names.Any(n => n == expectedName),
                $"{table}.{column}: expected default constraint named '{expectedName}', found [{string.Join(", ", names)}]");
        }

        // محاكاة قاعدة قديمة: StockReservations.CreatedAt بافتراضية محلية مسمّاة
        // (كما كانت EnsureReservationSchemaSql قبل Upgrade_15) — ثم الترقية
        // تُسقطها وتُعاد بنفس الاسم بتعريف UTC.
        await db.Database.ExecuteSqlRawAsync(@"
DECLARE @c sysname, @sql NVARCHAR(MAX);
SELECT @c = name FROM sys.default_constraints
 WHERE parent_object_id = OBJECT_ID(N'dbo.StockReservations')
   AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.StockReservations'), N'CreatedAt', 'ColumnId');
IF @c IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE dbo.StockReservations DROP CONSTRAINT ' + QUOTENAME(@c) + N';';
    EXEC(@sql);
END;
ALTER TABLE dbo.StockReservations ADD CONSTRAINT DF_StockReservations_CreatedAt
    DEFAULT (GETDATE()) FOR CreatedAt;");
        try
        {
            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Upgrade15Path));

            var restored = await db.Database
                .SqlQuery<UtcDefaultRow>($@"
                    SELECT dc.name AS ConstraintName, LOWER(dc.definition) AS Definition
                    FROM sys.default_constraints dc
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.StockReservations')
                      AND dc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.StockReservations'), N'CreatedAt', 'ColumnId')")
                .ToListAsync();
            var d = Assert.Single(restored);
            Assert.Equal("DF_StockReservations_CreatedAt", d.ConstraintName);
            Assert.True(d.Definition.Contains("utcdatetime"),
                $"StockReservations.CreatedAt: legacy local default not redefined to UTC: {d.Definition}");
        }
        finally
        {
            // إعادة الحالة المشتركة للـfixture: افتراضية UTC باسمها المعياري
            await db.Database.ExecuteSqlRawAsync(@"
DECLARE @c sysname, @sql NVARCHAR(MAX);
SELECT @c = name FROM sys.default_constraints
 WHERE parent_object_id = OBJECT_ID(N'dbo.StockReservations')
   AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.StockReservations'), N'CreatedAt', 'ColumnId');
IF @c IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE dbo.StockReservations DROP CONSTRAINT ' + QUOTENAME(@c) + N';';
    EXEC(@sql);
END;
ALTER TABLE dbo.StockReservations ADD CONSTRAINT DF_StockReservations_CreatedAt
    DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;");
        }
    }

    /// <summary>INSERT يُغفل عمود الطابع الزمني → القاعدة تملأه UTC (لا توقيت خادم محلي):
    /// مقارنة القيمة الناتجة بـSYSUTCDATETIME داخل نافذة تسامح.</summary>
    [Fact]
    public async Task Insert_OmittingTimestampColumn_GetsUtcValue()
    {
        await using var db = _fx.CreateContext();
        string key = $"utc15-{Guid.NewGuid():N}";
        try
        {
            // Settings: الإدراج يغفل UpdatedAt فتُطبَّق الافتراضية (DATETIME)
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO dbo.Settings (SettingKey) VALUES (N'{key}')");

            int? diffSeconds = (await db.Database.SqlQuery<int?>($@"
                SELECT DATEDIFF(SECOND, s.UpdatedAt, SYSUTCDATETIME()) AS Value
                FROM dbo.Settings s WHERE s.SettingKey = {key}")
                .ToListAsync()).Single();

            Assert.NotNull(diffSeconds);
            Assert.InRange(diffSeconds.Value, -5, 300);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.Settings WHERE SettingKey = N'{key}'");
        }

        string action = $"utc15-{Guid.NewGuid():N}";
        try
        {
            // SystemLogs: الإدراج يغفل CreatedAt فتُطبَّق الافتراضية (DATETIME2، قيد مسمّى)
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO dbo.SystemLogs (Action) VALUES (N'{action}')");

            int? diffSeconds = (await db.Database.SqlQuery<int?>($@"
                SELECT DATEDIFF(SECOND, sl.CreatedAt, SYSUTCDATETIME()) AS Value
                FROM dbo.SystemLogs sl WHERE sl.Action = {action}")
                .ToListAsync()).Single();

            Assert.NotNull(diffSeconds);
            Assert.InRange(diffSeconds.Value, -5, 300);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.SystemLogs WHERE Action = N'{action}'");
        }
    }

    /// <summary>السكربت defaults-only: قيمة تاريخية مخزنة صراحة لا تُزحزح بإعادة تشغيل الترقية —
    /// (الإزاحة التاريخية كانت مهمة Upgrade_08 المنفّذة سلفًا؛ Upgrade_15 لا يمس القيم).</summary>
    [Fact]
    public async Task Migration_IsDefaultsOnly_ExplicitStoredValuesNotShifted()
    {
        await using var db = _fx.CreateContext();
        string name = $"utc15-{Guid.NewGuid():N}";
        try
        {
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.Employees (Name, Position, CreatedAt)
VALUES (N'{name}', N'tester', '2020-01-01T10:00:00.000');");

            // إعادة تشغيل الترقية (idempotent) — ثم القيمة المخزنة كما هي بالضبط
            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Upgrade15Path));

            var createdAt = (await db.Database.SqlQuery<DateTime?>($@"
                SELECT CAST(e.CreatedAt AS datetime2(3)) AS Value
                FROM dbo.Employees e WHERE e.Name = {name}")
                .ToListAsync()).Single();

            Assert.NotNull(createdAt);
            Assert.Equal(new DateTime(2020, 1, 1, 10, 0, 0), createdAt.Value);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.Employees WHERE Name = N'{name}'");
        }
    }

    /// <summary>أعمدة تواريخ الأعمال (DATE) بلا أي افتراضية زمنية — السياسة: معنى تاريخ
    /// المحل محلي، والكاتب يمرر القيمة صراحة. تُفحص في القاعدة الفعلية.</summary>
    [Fact]
    public async Task BusinessDateColumns_HaveNoTimestampDefaults()
    {
        await using var db = _fx.CreateContext();
        foreach (var (table, column) in new[]
                 {
                     ("Payments", "PaymentDate"),
                     ("Batches", "ExpiryDate"),
                     ("Expenses", "ExpenseDate"),
                     ("PayrollRecords", "PeriodFrom"),
                     ("PayrollRecords", "PeriodTo"),
                     ("Employees", "HireDate"),
                     ("OfficialHolidays", "HolidayDate"),
                 })
        {
            var rows = await db.Database
                .SqlQuery<BusinessDateRow>($@"
                    SELECT TYPE_NAME(c.system_type_id) AS TypeName,
                           (SELECT COUNT_BIG(*) FROM sys.default_constraints dc
                            WHERE dc.parent_object_id = c.object_id
                              AND dc.parent_column_id = c.column_id) AS DefaultCount
                    FROM sys.columns c
                    WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME({table}))
                      AND c.name = {column}")
                .ToListAsync();

            var r = Assert.Single(rows);
            Assert.Equal("date", r.TypeName);
            Assert.Equal(0L, r.DefaultCount);
        }
    }

    /// <summary>إعادة التشغيل بلا أثر: بعد تشغيل ثانٍ تبقى كل افتراضية واحدة بالعمود وتعريفها UTC.</summary>
    [Fact]
    public async Task Upgrade15_Rerun_IsIdempotent()
    {
        // تشغيل إضافي فوق تشغيل الـfixture — ثم نفس فحص تعريفات UTC
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(Upgrade15Path));

        await using var db = _fx.CreateContext();
        int checkedCount = 0;
        foreach (var (table, column) in TechnicalTimestampColumns)
        {
            var defaults = await db.Database
                .SqlQuery<UtcDefaultRow>($@"
                    SELECT dc.name AS ConstraintName, LOWER(dc.definition) AS Definition
                    FROM sys.default_constraints dc
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME({table}))
                      AND dc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME({table})), {column}, 'ColumnId')")
                .ToListAsync();

            Assert.True(defaults.Count == 1,
                $"{table}.{column}: rerun produced {defaults.Count} default constraints");
            Assert.True(defaults[0].Definition.Contains("utcdatetime"),
                $"{table}.{column}: rerun default not UTC-based: {defaults[0].Definition}");
            checkedCount++;
        }
        Assert.Equal(TechnicalTimestampColumns.Length, checkedCount);
    }

    /// <summary>التغليف: سكربت Upgrade_15 يجب أن يُنسخ إلى حزمة المثبّت
    /// (قائمة المصادر في build-installer.ps1 وقسم [Files] في setup.iss) —
    /// نفس نمط Installer_Includes_Upgrade11_Migration.</summary>
    [Fact]
    public async Task Installer_Includes_Upgrade15_Migration()
    {
        string ps1 = await File.ReadAllTextAsync(Path.Combine(RepoRoot, "installer", "build-installer.ps1"));
        string iss = await File.ReadAllTextAsync(Path.Combine(RepoRoot, "installer", "setup.iss"));

        Assert.Contains("Upgrade_15_UtcDefaults.sql", ps1);
        Assert.Contains("Upgrade_15_UtcDefaults.sql", iss);
    }

    private sealed class UtcDefaultRow
    {
        public string ConstraintName { get; set; } = "";
        public string Definition { get; set; } = "";
    }

    private sealed class BusinessDateRow
    {
        public string TypeName { get; set; } = "";
        public long DefaultCount { get; set; }
    }
}
