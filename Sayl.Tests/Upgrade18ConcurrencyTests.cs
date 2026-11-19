using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Common.Utils;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// سلامة Upgrade_18 تحت تزامن الإقلاع (Task 5): الترقية تُسلسَل بين تطبيقات
/// متوازية بقفل تطبيقي مسمّى (sp_getapplock Exclusive بمالك المعاملة)، والحرق
/// والتدقيق وإنشاء القيد داخل معاملة واحدة — إعادة التشغيل idempotent بلا إحراق
/// ثانٍ، والتنافس الحقيقي من اتصالين مستقلين يمر مرة واحدة بالضبط، وفشل ترقية
/// حقيقي يتراجع كليًا (لا حرق نصف مُرتحل)، وانتهاء مهلة القفل تخطٍّ غير فادح.
/// </summary>
[Collection("database")]
public class Upgrade18ConcurrencyTests
{
    private readonly DatabaseFixture _fx;
    public Upgrade18ConcurrencyTests(DatabaseFixture fx) => _fx = fx;

    private const string ConstraintName = "CK_CreditApprovals_GrantPolicy";

    /// <summary>بذرة عميل + مدير — لصفوف SQL الخام.</summary>
    private async Task<(int CustomerId, int ManagerUserId)> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
        string username = $"u18-{marker}";
        if (username.Length > 50) username = username[..50];
        var user = new User
        {
            Username = username,
            PasswordHash = "test-hash",
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        var party = new Party { PartyType = 'C', Name = $"عميل-{marker}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(party);
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { PartyId = party.PartyId, Balance = 0, CreditLimit = 100m });
        await db.SaveChangesAsync();
        return (party.PartyId, user.UserId);
    }

    /// <summary>صف granted مخالف للسياسة (حد صفري — كان يُقبل قبل Upgrade_18).</summary>
    private async Task<int> InsertViolatingRowAsync(SaylDbContext db, int customerId, int managerUserId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.CreditApprovals
    (CustomerId, ManagerUserId, CurrentOutstanding, CreditLimit, NewCreditAmount,
     ProjectedOutstanding, ExcessAmount, Reason, Status, ApprovedAtUtc)
VALUES ({customerId}, {managerUserId}, {0m}, {0m}, {5m}, {5m}, {5m},
        {("صف مخالف تاريخي " + Guid.NewGuid().ToString("N"))}, {"granted"}, SYSUTCDATETIME())");
        return await db.CreditApprovals.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.ApprovalId)
            .Select(a => a.ApprovalId).FirstAsync();
    }

    /// <summary>صف granted صالح (تجاوز حقيقي لحد موجب) + استهلاك تاريخي.</summary>
    private async Task<int> InsertValidConsumedRowAsync(SaylDbContext db, int customerId, int managerUserId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.CreditApprovals
    (CustomerId, ManagerUserId, ConsumedByInvoiceId, CurrentOutstanding, CreditLimit,
     NewCreditAmount, ProjectedOutstanding, ExcessAmount, Reason, Status, ApprovedAtUtc)
VALUES ({customerId}, {managerUserId}, {(int?)null}, {0m}, {100m}, {150m}, {150m}, {50m},
        {("صف صالح مستهلك " + Guid.NewGuid().ToString("N"))}, {"consumed"}, SYSUTCDATETIME())");
        return await db.CreditApprovals.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.ApprovalId)
            .Select(a => a.ApprovalId).FirstAsync();
    }

    /// <summary>إسقاط القيد — محاكاة قاعدة ما قبل Upgrade_18.</summary>
    private static async Task DropConstraintAsync(SaylDbContext db)
        => await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE dbo.CreditApprovals DROP CONSTRAINT {ConstraintName}");

    private async Task<bool> ConstraintExistsAsync(SaylDbContext db)
        => (await db.Database.SqlQuery<bool>($@"
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE name = {ConstraintName}
                  AND parent_object_id = OBJECT_ID(N'dbo.CreditApprovals')) THEN 1 ELSE 0 END AS BIT) AS Value")
            .ToListAsync()).Single();

    private async Task<int> ConstraintCountAsync(SaylDbContext db)
        => (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM sys.check_constraints
            WHERE name = {ConstraintName}
              AND parent_object_id = OBJECT_ID(N'dbo.CreditApprovals')")
            .ToListAsync()).Single();

    private async Task<int> BurnedLogCountAsync(SaylDbContext db, int approvalId)
        => await db.SystemLogs.AsNoTracking().CountAsync(l =>
            l.Action == "CreditApprovalBurned"
            && l.Details!.Contains($"#{approvalId} ")
            && l.Details.Contains("Upgrade_18"));

    /// <summary>حارس نهائي: القيد يُضمن موجودًا مهما كانت النتيجة.</summary>
    private async Task RestoreConstraintAsync()
    {
        await using var db = _fx.CreateContext();
        if (!await ConstraintExistsAsync(db))
        {
            DatabaseFixture.ExecuteBatches(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                    "Upgrade_18_CreditApprovalGrantPolicy.sql")));
        }
    }

    // ══ 1) إعادة تشغيل الترقية (الإقلاع + السكربت المستقل): idempotent —
    //       لا إحراق ثانٍ ولا قيد مزدوج والتاريخ الصالح لم يُمس ══
    [Fact]
    public async Task Upgrade18_Rerun_IsIdempotent_NoReBurn_NoDuplicateConstraint()
    {
        var (customerId, managerId) = await SeedAsync($"u18a-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        await DropConstraintAsync(db);
        int violatingId = await InsertViolatingRowAsync(db, customerId, managerId);
        int validConsumedId = await InsertValidConsumedRowAsync(db, customerId, managerId);

        // التشغيل الأول (مسار الإقلاع): يحرق المخالف ويضيف القيد
        await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        Assert.Equal("burned", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == violatingId)).Status);
        Assert.True(await ConstraintExistsAsync(db));
        int burnsAfterFirst = await BurnedLogCountAsync(db, violatingId);
        int rowsAfterFirst = await db.CreditApprovals.CountAsync();
        Assert.Equal(1, burnsAfterFirst);

        // إعادة التشغيل (مسار الإقلاع + السكربت المستقل): لا تفعل شيئًا
        await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        DatabaseFixture.ExecuteBatches(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "Upgrade_18_CreditApprovalGrantPolicy.sql")));

        Assert.Equal(1, await BurnedLogCountAsync(db, violatingId));      // لا إحراق ثانٍ
        Assert.Equal(rowsAfterFirst, await db.CreditApprovals.CountAsync()); // لا صفوف جديدة
        Assert.Equal(1, await ConstraintCountAsync(db));                  // لا قيد مزدوج
        Assert.True(await ConstraintExistsAsync(db));
        // التاريخ الصالح المستهلك لم يُمس: consumed كما هو
        Assert.Equal("consumed", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == validConsumedId)).Status);
    }

    // ══ 2) إقلاع متوازٍ حقيقي من اتصالين مستقلين: كلاهما ينجح، القيد مرة واحدة،
    //       الحرق مرة واحدة — لا سباق قيد مزدوج ولا فشل إقلاع ══
    [Fact]
    public async Task ConcurrentStartup_FromTwoConnections_BothSucceed_SingleConstraint_SingleBurn()
    {
        var (customerId, managerId) = await SeedAsync($"u18b-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        await DropConstraintAsync(db);
        int violatingId = await InsertViolatingRowAsync(db, customerId, managerId);

        // سياقان واتصالان مستقلان تمامًا — تنافس حقيقي على SQL Server
        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();

        Exception?[] failures = await Task.WhenAll(
            Task.Run(async () => await Record.ExceptionAsync(() => new clsUsersService(db1).EnsureSchemaUpgradesAsync())),
            Task.Run(async () => await Record.ExceptionAsync(() => new clsUsersService(db2).EnsureSchemaUpgradesAsync())));

        Assert.Null(failures[0]); // كلا الإقلاعين نجح
        Assert.Null(failures[1]);

        Assert.Equal(1, await ConstraintCountAsync(db)); // قيد واحد بالضبط
        Assert.True(await ConstraintExistsAsync(db));
        Assert.Equal("burned", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == violatingId)).Status);
        Assert.Equal(1, await BurnedLogCountAsync(db, violatingId)); // حرق مرة واحدة بالضبط
    }

    // ══ 3) فشل ترقية حقيقي (THROW محقون بعد الحرق داخل المعاملة نفسها):
    //       تراجع كامل — الصف لم يُحرق ولا تدقيق ولا قيد ══
    [Fact]
    public async Task MigrationFailure_AfterBurn_RollsBackDataAndConstraint()
    {
        var (customerId, managerId) = await SeedAsync($"u18c-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        await DropConstraintAsync(db);
        int violatingId = await InsertViolatingRowAsync(db, customerId, managerId);

        // جسم الترقية الحقيقي نفسه + THROW محقون بعد الحرق وقبل إنشاء القيد
        string batch = clsUsersService.EnsureSchemaSql.Replace(
            "ALTER TABLE dbo.CreditApprovals ADD CONSTRAINT CK_CreditApprovals_GrantPolicy CHECK (",
            "THROW 51000, 'injected migration failure after burn', 1; " +
            "ALTER TABLE dbo.CreditApprovals ADD CONSTRAINT CK_CreditApprovals_GrantPolicy CHECK (");
        Assert.NotEqual(clsUsersService.EnsureSchemaSql, batch); // الحقن فعّال فعلًا

        var ex = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(batch));
        Assert.NotNull(ex); // الفشل يُرمى — الإقلاع لا يكتمل بصمت
        // الخطأ الخطي 51000 (THROW المحقون) يصل كـSqlException — التراجع تم في CATCH
        var sql = Assert.IsType<SqlException>(ex);
        Assert.Equal(51000, sql.Number);

        // تراجع كامل: الصف المخالف عاد granted (الحرق تراجع)، بلا تدقيق، والقيد غائب
        Assert.Equal("granted", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == violatingId)).Status);
        Assert.Equal(0, await BurnedLogCountAsync(db, violatingId));
        Assert.False(await ConstraintExistsAsync(db));

        await RestoreConstraintAsync(); // حماية الاختبارات التالية
    }

    // ══ 4) مهلة القفل (طرفية أخرى ممسكة بالقفل): تخطٍّ غير فادح —
    //       لا فشل إقلاع ولا ترحيل جزئي، والإقلاع اللاحق يطبق الترقية ══
    [Fact]
    public async Task LockTimeout_SkipsGracefully_NextStartupApplies()
    {
        var (customerId, managerId) = await SeedAsync($"u18d-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        await DropConstraintAsync(db);
        int violatingId = await InsertViolatingRowAsync(db, customerId, managerId);

        // مهلة قصيرة في نسخة اختبارية من دفعة الإقلاع (النص نفسه — المهلة وحدها)
        string batch = clsUsersService.EnsureSchemaSql.Replace("@LockTimeout = 10000", "@LockTimeout = 500");
        Assert.NotEqual(clsUsersService.EnsureSchemaSql, batch);

        // طرفية أخرى ممسكة بقفل الترقية (مالك معاملة مفتوحة)
        var csb = new SqlConnectionStringBuilder(DatabaseFixture.ConnectionString);
        await using var holder = new SqlConnection(csb.ConnectionString);
        await holder.OpenAsync();
        SqlTransaction holderTran = (SqlTransaction)await holder.BeginTransactionAsync();
        int lockResult;
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = holderTran;
            cmd.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Upgrade18GrantPolicy', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r";
            lockResult = (int)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.True(lockResult >= 0, "test setup: external lock must be acquired");

        // إقلاع الطرفية المنافسة: مهلة القفل تنتهي — تخطٍّ غير فادح (بلا استثناء)
        Exception? failure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(batch));
        Assert.Null(failure);

        // لا ترحيل جزئي: الصف لم يُحرق ولا تدقيق ولا قيد
        Assert.Equal("granted", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == violatingId)).Status);
        Assert.Equal(0, await BurnedLogCountAsync(db, violatingId));
        Assert.False(await ConstraintExistsAsync(db));

        // فك القفل — الإقلاع اللاحق يطبق الترقية كاملة
        await holderTran.RollbackAsync();
        await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        Assert.True(await ConstraintExistsAsync(db));
        Assert.Equal("burned", (await db.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == violatingId)).Status);
        Assert.Equal(1, await BurnedLogCountAsync(db, violatingId));
    }
}
