using Common.DTOs.RequestModels;
using Common.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اختبارات سياسة المنح الصارمة على مستوى قاعدة البيانات (Task 4 — البنود 28-29).
/// قيد CK_CreditApprovals_GrantPolicy (Upgrade_18): أي صف granted جديد يجب أن
/// يمثل تجاوزًا فعليًا لحدٍّ موجب — الصفر منع مطلق لا يعتمده مدير، والسالب
/// بيانات فاسدة، وما لا يتجاوز الحد ليست عملية تجاوز أصلًا. القيد مشروط
/// بالحالة (granted حصرًا) حتى لا يُجبر على تعديل سجل التدقيق التاريخي
/// (consumed/burned) — والترحيل يحوّل صفوف granted التاريخية المخالفة إلى
/// burned مع تدقيق، بلا حذف أي سجل.
/// </summary>
[Collection("database")]
public class CreditApprovalGrantPolicyTests
{
    private readonly DatabaseFixture _fx;
    public CreditApprovalGrantPolicyTests(DatabaseFixture fx) => _fx = fx;

    private const string ConstraintName = "CK_CreditApprovals_GrantPolicy";

    /// <summary>بذرة عميل + مستخدم مدير مباشرة (بلا خدمة) — لصفوف SQL الخام.</summary>
    private async Task<(int CustomerId, int ManagerUserId)> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
        var user = new User
        {
            Username = $"gp-{marker}-{Guid.NewGuid():N}".Length > 50 ? $"gp-{marker}" : $"gp-{marker}-{Guid.NewGuid():N}",
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

    /// <summary>إدراج صف اعتماد خام بالقيم المحددة — يعيد معرف الصف.</summary>
    private async Task<int> InsertApprovalRowAsync(SaylDbContext db, int customerId, int managerUserId,
        decimal current, decimal limit, decimal newCredit, decimal projected, decimal excess, string status = "granted")
    {
        // معرف فريد بالسبب لتفادي تصادم LEN>=2 — قيد Reason محقق دائمًا
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO dbo.CreditApprovals
    (CustomerId, ManagerUserId, CurrentOutstanding, CreditLimit, NewCreditAmount,
     ProjectedOutstanding, ExcessAmount, Reason, Status, ApprovedAtUtc)
VALUES ({customerId}, {managerUserId}, {current}, {limit}, {newCredit}, {projected}, {excess},
        {("إدراج اختبار سياسة المنح " + Guid.NewGuid().ToString("N"))}, {status}, SYSUTCDATETIME())");
        return await db.CreditApprovals.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.ApprovalId)
            .Select(a => a.ApprovalId).FirstAsync();
    }

    private static bool IsCheckViolation(Exception? ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is SqlException sql && sql.Number == 547)
                return true;
        return false;
    }

    private async Task<bool> ConstraintExistsAsync(SaylDbContext db)
        => (await db.Database.SqlQuery<bool>($@"
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.check_constraints
                WHERE name = {ConstraintName}
                  AND parent_object_id = OBJECT_ID(N'dbo.CreditApprovals')) THEN 1 ELSE 0 END AS BIT) AS Value")
            .ToListAsync()).Single();

    // ══ 28) قيد قاعدة البيانات يرفض صفوف granted الجديدة المخالفة ══
    [Fact]
    public async Task GrantPolicyConstraint_RejectsInvalidGrantedRows_AtDatabaseLevel()
    {
        var (customerId, managerId) = await SeedAsync($"gp28-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        // حارس: القيد موجود على قاعدة الاختبار (ثبّته الترقية عند الإقلاع)
        Assert.True(await ConstraintExistsAsync(db), $"{ConstraintName} missing on test database");

        // حد صفري: رفض قيدي (لا اعتماد لمنع مطلق)
        var ex1 = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 0m, newCredit: 1m, projected: 1m, excess: 1m));
        Assert.True(IsCheckViolation(ex1), $"expected CHECK violation, got: {ex1.Message}");

        // حد سالب: رفض قيدي (بيانات فاسدة)
        var ex2 = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: -5m, newCredit: 10m, projected: 10m, excess: 15m));
        Assert.True(IsCheckViolation(ex2), $"expected CHECK violation, got: {ex2.Message}");

        // مشروع لا يتجاوز الحد: رفض قيدي (ليست عملية تجاوز)
        var ex3 = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 100m, newCredit: 50m, projected: 50m, excess: -50m));
        Assert.True(IsCheckViolation(ex3), $"expected CHECK violation, got: {ex3.Message}");

        // فائض محسوب خطأ: رفض قيدي
        var ex4 = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 100m, newCredit: 150m, projected: 150m, excess: 40m));
        Assert.True(IsCheckViolation(ex4), $"expected CHECK violation, got: {ex4.Message}");

        // مبلغ غير موجب: رفض قيدي
        var ex5 = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 100m, newCredit: 0m, projected: 0m, excess: -100m));
        Assert.True(IsCheckViolation(ex5), $"expected CHECK violation, got: {ex5.Message}");

        // صف granted صالح (تجاوز حقيقي لحد موجب): يقبل — عنصر تحكم
        int validId = await InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 100m, newCredit: 150m, projected: 150m, excess: 50m);
        var valid = await db.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == validId);
        Assert.Equal("granted", valid.Status);

        // لا صفوف مخالفة تسربت (العميل يحمل الصف الصالح وحده)
        Assert.Equal(1, await db.CreditApprovals.CountAsync(a => a.CustomerId == customerId));
    }

    // ══ 29) الترحيل يرحل صفوف granted التاريخية المخالفة إلى burned بلا حذف ══
    [Fact]
    public async Task Upgrade18_TransitionsLegacyViolatingGrantedRows_ToBurned_WithoutDeletingHistory()
    {
        var (customerId, managerId) = await SeedAsync($"gp29-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        // محاكاة قاعدة ما قبل Upgrade_18: إسقاط القيد ثم إدراج صفوف بأسلوب القديم
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE dbo.CreditApprovals DROP CONSTRAINT {ConstraintName}");
        try
        {
            // صف مخالف تاريخي: حد صفري (كان يُقبل تحت قيد Upgrade_17 الأضعف)
            int legacyViolating = await InsertApprovalRowAsync(
                db, customerId, managerId, current: 0m, limit: 0m, newCredit: 5m, projected: 5m, excess: 5m);
            // صف صالح تاريخي: يبقى granted كما هو (لا يمس الترحيل التاريخ الصالح)
            int legacyValid = await InsertApprovalRowAsync(
                db, customerId, managerId, current: 0m, limit: 100m, newCredit: 150m, projected: 150m, excess: 50m);

            // تشغيل سكربت الترقية نفسه (المسار المستقل) على هذه الحالة
            DatabaseFixture.ExecuteBatches(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                    "Upgrade_18_CreditApprovalGrantPolicy.sql")));

            // الصف المخالف: انتقال granted -> burned — سجل تدقيق باقٍ لا محذوف
            var violating = await db.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == legacyViolating);
            Assert.Equal("burned", violating.Status);
            Assert.Equal(0m, violating.CreditLimit);          // القيم التاريخية كما كانت
            Assert.Null(violating.ConsumedByInvoiceId);       // لم يُستهلك قط
            Assert.Null(violating.ConsumedAtUtc);

            // سجل تدقيق الترحيل مرتبط بالاعتماد المُحرَق حصرًا
            Assert.Equal(1, await db.SystemLogs.CountAsync(l =>
                l.Action == "CreditApprovalBurned"
                && l.Details!.Contains($"#{legacyViolating} ")
                && l.Details.Contains("Upgrade_18")));

            // الصف الصالح: لم يُمس إطلاقًا (لا حذف ولا تعديل)
            var validRow = await db.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == legacyValid);
            Assert.Equal("granted", validRow.Status);
            Assert.Equal(150m, validRow.ProjectedOutstanding);

            // القيد أُعيد إنشاؤه بعد الترحيل — الإدراج المخالف مرفوض من جديد
            Assert.True(await ConstraintExistsAsync(db), $"{ConstraintName} not re-created by migration");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
                db, customerId, managerId, current: 0m, limit: 0m, newCredit: 1m, projected: 1m, excess: 1m));
            Assert.True(IsCheckViolation(ex), $"expected CHECK violation, got: {ex.Message}");

            // سجل التاريخ محفوظ بالكامل: صفان (محروق + صالح) بلا حذف
            Assert.Equal(2, await db.CreditApprovals.CountAsync(a => a.CustomerId == customerId));
        }
        finally
        {
            // حماية الاختبارات التالية: القيد يُضمن موجودًا مهما كانت النتيجة
            if (!await ConstraintExistsAsync(db))
                DatabaseFixture.ExecuteBatches(
                    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                        "Upgrade_18_CreditApprovalGrantPolicy.sql")));
        }
    }

    // ══ 9-ملحق) مسار الإقلاع (EnsureSchemaSql) والسكربت المستقل متطابقا النتيجة ══
    // إعادة تشغيل مسار الإقلاع على قاعدة مُرقّاة: لا يفعل شيئًا (idempotent)
    // ولا يمس أي صف — والقيد يبقى موجودًا وساريًا.
    [Fact]
    public async Task StartupPath_Rerun_IsIdempotent_ConstraintStaysEnforced()
    {
        var (customerId, managerId) = await SeedAsync($"gp30-{Guid.NewGuid():N}"[..10]);
        await using var db = _fx.CreateContext();

        int rowsBefore = await db.CreditApprovals.CountAsync();

        // إعادة تشغيل مسار الإقلاع الفعلي (نفس ما ينفذه التطبيق عند الإقلاع)
        await new clsUsersService(db).EnsureSchemaUpgradesAsync();
        DatabaseFixture.ExecuteBatches(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "Upgrade_18_CreditApprovalGrantPolicy.sql")));

        // لا صف تغيّر ولا صف أُنشئ بلا اعتماد حقيقي
        Assert.Equal(rowsBefore, await db.CreditApprovals.CountAsync());
        Assert.True(await ConstraintExistsAsync(db), $"{ConstraintName} missing after startup rerun");

        // القيد ساري: الإدراج المخالف مرفوض، والصالح يقبل
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InsertApprovalRowAsync(
            db, customerId, managerId, current: 0m, limit: 0m, newCredit: 1m, projected: 1m, excess: 1m));
        Assert.True(IsCheckViolation(ex), $"expected CHECK violation, got: {ex.Message}");
    }
}
