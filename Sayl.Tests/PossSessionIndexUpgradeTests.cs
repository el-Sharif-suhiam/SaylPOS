using Common.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// VULN-21: فهرس `UX_POSSessions_OneOpenPerUser` (فريد مُرشّح — جلسة درج مفتوحة
/// واحدة لكل مستخدم) كان في SaylDB.sql فقط؛ أي قاعدة قديمة بُنيت بلا السكربت
/// كانت بلا الحماية (TOCTOU في فحص-ثم-إدراج). يغطي هنا: الإنشاء عند الغياب عبر
/// مسار الإقلاع الفعلي (EnsureSchemaUpgradesAsync)، وأمان التشغيل المتكرر،
/// ومنع القاعدة لجلستين مفتوحتين، وسلوك OpenSessionAsync القائم عند الانتهاك،
/// والفشل الواضح بلا مساس بالبيانات إن وُجد تكرار قائم.
/// (قاعدة مشتركة — كل إسقاط يُعاد بناؤه في finally.)
/// </summary>
[Collection("database")]
public class PossSessionIndexUpgradeTests
{
    private readonly DatabaseFixture _fx;
    public PossSessionIndexUpgradeTests(DatabaseFixture fx) => _fx = fx;

    private const string DropIndexSql =
        "IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.POSSessions') AND name = N'UX_POSSessions_OneOpenPerUser') DROP INDEX UX_POSSessions_OneOpenPerUser ON dbo.POSSessions;";

    private static async Task<bool> IndexExistsAsync(SaylDbContext db)
        => (await db.Database.SqlQuery<bool>($@"
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.POSSessions')
                  AND name = N'UX_POSSessions_OneOpenPerUser'
                  AND is_unique = 1 AND has_filter = 1
            ) THEN 1 ELSE 0 END AS BIT) AS Value").ToListAsync()).Single();

    // ══ 1) الفهرس يُنشأ عند غيابه، والتشغيل المتكرر آمن ══
    [Fact]
    public async Task EnsureSchema_CreatesMissingIndex_And_IsIdempotent()
    {
        await using var db = _fx.CreateContext();
        try
        {
            // محاكاة قاعدة قديمة بُنيت بلا SaylDB.sql الحديث
            await db.Database.ExecuteSqlRawAsync(DropIndexSql);
            Assert.False(await IndexExistsAsync(db));

            var svc = new clsUsersService(db);
            await svc.EnsureSchemaUpgradesAsync();   // الإقلاع الأول: يُنشئ الفهرس
            Assert.True(await IndexExistsAsync(db));

            await svc.EnsureSchemaUpgradesAsync();   // الإقلاع الثاني: noop بلا أخطاء
            await svc.EnsureSchemaUpgradesAsync();   // والثالث
            Assert.True(await IndexExistsAsync(db));

            // نفس تعريف SaylDB.sql: فريد + مُرشّح على Status = 'open'
            var filter = (await db.Database.SqlQuery<string>($@"
                SELECT filter_definition AS Value FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.POSSessions')
                  AND name = N'UX_POSSessions_OneOpenPerUser'").ToListAsync()).Single();
            Assert.Contains("Status", filter);
            Assert.Contains("open", filter);
        }
        finally
        {
            // إعادة الحالة المشتركة: إن فشل المسار أعلاه يبقى الفهرس مفقودًا — نعيده
            await db.Database.ExecuteSqlRawAsync(clsUsersService.EnsureSchemaSql);
        }
    }

    // ══ 2) القاعدة نفسها تمنع جلستين مفتوحتين لمستخدم واحد ══
    [Fact]
    public async Task DatabaseConstraint_PreventsDuplicateOpenSessions()
    {
        string marker = $"psi2-{Guid.NewGuid():N}"[..12];
        int userId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
        }

        try
        {
            await using var db = _fx.CreateContext();
            db.PosSessions.Add(new PosSession
            {
                UserId = userId, OpeningAmount = 0m, StartedAt = DateTime.UtcNow,
                Status = enPosSessionStatusType.open
            });
            await db.SaveChangesAsync(); // الأولى تمر

            db.PosSessions.Add(new PosSession
            {
                UserId = userId, OpeningAmount = 0m, StartedAt = DateTime.UtcNow,
                Status = enPosSessionStatusType.open
            });
            // الثانية: القيد الفريد المُرشّح في القاعدة يرفضها — على مستوى المحرك لا التطبيق
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UX_POSSessions_OneOpenPerUser",
                ex.GetBaseException().Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(1, await db.PosSessions.CountAsync(s => s.UserId == userId && s.Status == enPosSessionStatusType.open));
        }
        finally
        {
            await using var db = _fx.CreateContext();
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM dbo.POSSessions WHERE UserId = {userId};");
        }
    }

    // ══ 3) OpenSessionAsync يحافظ على نتيجة الفشل القائمة عند انتهاك القيد ══
    //     فحص-ثم-إدراج بطلبين متوازيين: أحدهما يمر، والآخر يفشل — عبر الفحص
    //     المسبق أو عبر انتهاك القيد الفريد — وكلا المسارين يُرجع الرسالة نفسها.
    [Fact]
    public async Task OpenSessionAsync_ConcurrentOpens_OneSucceeds_OtherFails()
    {
        string marker = $"psi3-{Guid.NewGuid():N}"[..12];
        int userId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
        }

        try
        {
            await using var db1 = _fx.CreateContext();
            await using var db2 = _fx.CreateContext();
            var t1 = new clsPosSessionService(db1).OpenSessionAsync(userId, 0m);
            var t2 = new clsPosSessionService(db2).OpenSessionAsync(userId, 0m);
            var results = await Task.WhenAll(t1, t2);

            Assert.Equal(1, results.Count(r => r.IsSuccess));                 // واحدة فقط نجحت
            Assert.All(results, r => { if (!r.IsSuccess)
                Assert.Equal("يوجد لدى المستخدم جلسة مفتوحة بالفعل.", r.Message); });
            Assert.Equal(1, await db1.PosSessions.AsNoTracking()
                .CountAsync(s => s.UserId == userId && s.Status == enPosSessionStatusType.open));
        }
        finally
        {
            await using var db = _fx.CreateContext();
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM dbo.POSSessions WHERE UserId = {userId};");
        }
    }

    // ══ 4) تكرار قائم في البيانات: فشل واضح برسالة تحمل المعرّفات — ولا جلسة تُمسّ ══
    [Fact]
    public async Task EnsureSchema_FailsClearly_WhenDuplicateOpenSessionsExist()
    {
        string marker = $"psi4-{Guid.NewGuid():N}"[..12];
        int userId;
        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
        }

        try
        {
            await using var db = _fx.CreateContext();
            // تكرار قائم: يُزرع عبر SQL مباشرة (الفهرس أُسقط أولًا كي يمر الإدراج)
            await db.Database.ExecuteSqlRawAsync(DropIndexSql);
            await db.Database.ExecuteSqlRawAsync($@"
INSERT INTO dbo.POSSessions (UserId, OpeningAmount, StartedAt, Status)
VALUES ({userId}, 0, GETUTCDATE(), N'open'), ({userId}, 0, GETUTCDATE(), N'open');");

            var svc = new clsUsersService(db);
            var ex = await Assert.ThrowsAsync<SqlException>(() => svc.EnsureSchemaUpgradesAsync());

            // فشل واضح: رسالة تحمل معرّف المستخدم المتعارض ولا تلقائية في العلاج
            Assert.Equal(50001, ex.Number);
            Assert.Contains("مكررة", ex.Message);
            Assert.Contains(userId.ToString(), ex.Message);

            // لا بيانات مُعدَّلة: الجلستان المكررتان كما هما، والفهرس لم يُنشأ
            Assert.Equal(2, await db.PosSessions.CountAsync(s => s.UserId == userId && s.Status == enPosSessionStatusType.open));
            Assert.False(await IndexExistsAsync(db));

            // بعد معالجة المالك (حذف الزائد يدويًا هنا) تعيد الترقية إنشاء الفهرس
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM dbo.POSSessions WHERE UserId = {userId} AND SessionId NOT IN (SELECT MIN(SessionId) FROM dbo.POSSessions WHERE UserId = {userId});");
            await svc.EnsureSchemaUpgradesAsync();
            Assert.True(await IndexExistsAsync(db));
        }
        finally
        {
            // إعادة الحالة المشتركة: إزالة جلسات الاختبار + ضمان وجود الفهرس
            await using var db = _fx.CreateContext();
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.POSSessions WHERE UserId = {userId};");
            await db.Database.ExecuteSqlRawAsync(clsUsersService.EnsureSchemaSql);
        }
    }
}
