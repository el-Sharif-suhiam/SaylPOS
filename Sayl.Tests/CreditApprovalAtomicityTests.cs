using Common.DTOs;
using Common.Utils;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ذرّية الاعتماد وتدقيقه (Task 5): سجل CreditApprovals وسجل SystemLogs
/// (CreditApprovalGranted) يُلتزمان داخل معاملة صريحة واحدة — فشل حفظ أيٍّ
/// منهما يتراجع المعاملة كلها فلا يُترك اعتماد بلا تدقيق ولا تدقيق بلا اعتماد.
/// الحقن الحتمي لفشل الحفظ: اعتراض أوامر EF (DbCommandInterceptor) يرمي استثناءً
/// على أمر INSERT الهدف فقط (SystemLogs أو CreditApprovals) — بلا أي تعديل
/// على كود الإنتاج.
/// </summary>
[Collection("database")]
public class CreditApprovalAtomicityTests
{
    private readonly DatabaseFixture _fx;
    public CreditApprovalAtomicityTests(DatabaseFixture fx) => _fx = fx;

    private const string ManagerPassword = "Manager@123";
    private const string Reason = "ذرّية الاعتماد - تفويض استثنائي لمرة واحدة";

    /// <summary>اعتراض اختباري: يرمي استثناء حتميًا على أمر INSERT الهدف.
    /// سجل الدخول الناجح (LoginAsync) يكتب سجل SystemLogs أيضًا — لذا حقن
    /// فشل التدقيق يصيب المنادى الثاني (تدقيق المنح) لا سجل الدخول.</summary>
    private sealed class InjectedFailureInterceptor : DbCommandInterceptor
    {
        private readonly Func<string, bool> _shouldThrow;
        public InjectedFailureInterceptor(Func<string, bool> shouldThrow) => _shouldThrow = shouldThrow;

        private static void Check(DbCommand command, Func<string, bool> shouldThrow)
        {
            if (shouldThrow(command.CommandText))
                throw new InvalidOperationException("حقن فشل اختباري على أمر INSERT الهدف.");
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Check(command, _shouldThrow); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Check(command, _shouldThrow); return ValueTask.FromResult(result); }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Check(command, _shouldThrow); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { Check(command, _shouldThrow); return ValueTask.FromResult(result); }
    }

    private static bool IsSystemLogsInsert(string sql)
        => sql.Contains("SystemLogs", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase);

    private static bool IsCreditApprovalsInsert(string sql)
        => sql.Contains("CreditApprovals", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase);

    /// <summary>عدّاد أوامر INSERT على جدول محدد — لحقن المنادى الثاني حصرًا
    /// (الأول = سجل الدخول الناجح، الثاني = تدقيق المنح).</summary>
    private sealed class CountingInsertInterceptor : DbCommandInterceptor
    {
        private readonly string _table;
        private readonly int _failOn;
        private int _seen;
        public CountingInsertInterceptor(string table, int failOn) { _table = table; _failOn = failOn; }

        private void Check(DbCommand command)
        {
            string sql = command.CommandText;
            if (sql.Contains(_table, StringComparison.OrdinalIgnoreCase)
                && sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && ++_seen == _failOn)
                throw new InvalidOperationException($"حقن فشل اختباري على INSERT {_table} رقم {_seen}.");
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Check(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Check(command); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }

    /// <summary>بذرة عميل (حد 100، رصيد 0) + مدير بنظام بكلمة مرور حقيقية.</summary>
    private async Task<(int CustomerId, int ManagerUserId, string ManagerUsername)> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
        string username = $"at-{marker}-{Guid.NewGuid():N}";
        var user = new User
        {
            Username = username.Length > 50 ? username[..50] : username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(ManagerPassword),
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        var party = new Party { PartyType = 'C', Name = $"عميل-{marker}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(party);
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { PartyId = party.PartyId, Balance = 0, CreditLimit = 100m });
        await db.SaveChangesAsync();
        return (party.PartyId, user.UserId, user.Username);
    }

    private static CreditApprovalRequiredInfo OverLimitInfo(int customerId) => new()
    {
        CustomerId = customerId,
        CurrentOutstanding = 0m,
        CreditLimit = 100m,
        NewCreditAmount = 150m,
        ProjectedOutstanding = 150m,
        ExcessAmount = 50m
    };

    private static clsCreditApprovalService Service(SaylDbContext db)
        => new(db, new clsUsersService(db));

    /// <summary>سياق الاعتماد مع الاعتراض — سياق التحقق منفصل بلا اعتراض.</summary>
    private static SaylDbContext InterceptedContext(DatabaseFixture fx, DbCommandInterceptor interceptor)
        => new(new DbContextOptionsBuilder<SaylDbContext>()
            .UseSqlServer(DatabaseFixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options);

    // ══ 1) نجاح الاعتماد: صف الاعتماد وسجل تدقيقه يُحفظان معًا ══
    [Fact]
    public async Task SuccessfulApproval_PersistsBothApprovalAndAuditRecords()
    {
        var (customerId, managerId, managerUsername) = await SeedAsync($"at1-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        var grant = await Service(db).ApproveCreditSaleAsync(OverLimitInfo(customerId), managerUsername, ManagerPassword, Reason);

        Assert.True(grant.IsSuccess, grant.Message);
        var row = await db.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == grant.Data);
        Assert.Equal("granted", row.Status);
        Assert.Equal(customerId, row.CustomerId);
        Assert.Equal(managerId, row.ManagerUserId);
        // سجل تدقيق المنح: واحد بالضبط مرتبط بالاعتماد نفسه
        Assert.Equal(1, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalGranted"
            && l.UserId == managerId
            && l.Details!.Contains($"#{grant.Data} ")));
    }

    // ══ 2) فشل حفظ التدقيق: لا اعتماد ولا تدقيق — تراجع كامل ══
    // (المنادى الثاني على SystemLogs = تدقيق المنح؛ الأول سجل الدخول الناجح)
    [Fact]
    public async Task FailureWhileSavingAudit_PersistsNeitherRecord()
    {
        var (customerId, managerId, managerUsername) = await SeedAsync($"at2-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();
        await using var approvalDb = InterceptedContext(_fx, new CountingInsertInterceptor("SystemLogs", 2));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Service(approvalDb).ApproveCreditSaleAsync(OverLimitInfo(customerId), managerUsername, ManagerPassword, Reason));

        // لا صف اعتماد بلا سجل تدقيق: الاثنان تراجعا معًا (محصورًا بنطاق الاختبار —
        // قاعدة الرَن مشتركة بين الاختبارات)
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalGranted" && l.UserId == managerId));
    }

    // ══ 3) فشل حفظ الاعتماد نفسه: لا اعتماد ولا تدقيق — تراجع كامل ══
    [Fact]
    public async Task FailureWhileSavingApproval_PersistsNeitherRecord()
    {
        var (customerId, managerId, managerUsername) = await SeedAsync($"at3-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();
        await using var approvalDb = InterceptedContext(_fx, new CountingInsertInterceptor("CreditApprovals", 1));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Service(approvalDb).ApproveCreditSaleAsync(OverLimitInfo(customerId), managerUsername, ManagerPassword, Reason));

        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalGranted" && l.UserId == managerId));
    }

    // ══ 4) فشل عام (أمر INSERT المطابق فقط): الرفضات القائمة لم تتأثر ══
    // حقن أمر SystemLogs بأكمله (من المحاولة الأولى): سجل الدخول الناجح
    // نفسه تحصين best-effort لا يمنع الدخول، وفشل التدقيق داخل المعاملة يتراجع.
    [Fact]
    public async Task InjectedSystemLogsFailure_NeverLeavesApprovalWithoutAudit()
    {
        var (customerId, managerId, managerUsername) = await SeedAsync($"at4-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();
        await using var approvalDb = InterceptedContext(_fx, new InjectedFailureInterceptor(IsSystemLogsInsert));

        // الدخول الناجح يكتب سجله عبر السياق المعترض — التحصين يمرر الدخول،
        // وتدقيق المنح داخل المعاملة يفشل: تراجع كامل ثم إعادة إلقاء.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Service(approvalDb).ApproveCreditSaleAsync(OverLimitInfo(customerId), managerUsername, ManagerPassword, Reason));

        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalGranted" && l.UserId == managerId));
    }
}
