using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// تدقيق المحاولات المرفوضة (Audit Log — denied attempts):
/// الرفض (صلاحية/دخول فاشل/استعادة مرفوضة/تحقق أعمال) يجب أن يُدقَّق في
/// SystemLogs، لا أن يمر بلا أثر. كل اختبار يتحقق من:
///   1) وجود سجل الرفض بالحدث والفاعل الصحيحين.
///   2) عدم الازدواجية: رفض واحد يمر عبر أكثر من مكوّن = صفّ واحد.
///   3) بقاء سجل التدقيق رغم تراجع العملية المرفوضة (كتابة على اتصال مستقل).
///   4) لا كلمات مرور/أسرار داخل تفاصيل السجل.
/// الطوابع الزمنية للتدقيق تقنية (UTC) — عكس يوم البرنامج (UTC+3).
/// </summary>
[Collection("database")]
public class RejectionAuditTests
{
    private readonly DatabaseFixture _fx;
    public RejectionAuditTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>فاتورة بيع آجلة مكتملة (بلا أي سداد) بقيمة محددة.</summary>
    private static async Task<int> SeedCreditSaleAsync(SaylDbContext db, TestSeed seed, decimal net)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = net,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = net,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    private static DtoPaymentRequest Pay(int? invoiceId, int? partyId, decimal amount, int userId, int? sessionId = null) =>
        new() { InvoiceId = invoiceId, PartyId = partyId, Amount = amount, PaymentDate = Today, Method = "cash", UserId = userId, SessionId = sessionId };

    /// <summary>
    /// سطور الرفض لنوع حدث معين — القاعدة مشتركة بين كل اختبارات الرَن
    /// (SystemLogs لا تُمسح)، لذا تُنَشّط دائمًا بالفاعل/العلامة الفريدة للاختبار.
    /// </summary>
    private static async Task<SystemLog[]> RejectionLogsAsync(SaylDbContext db, string action) =>
        await db.SystemLogs.AsNoTracking().Where(l => l.Action == action).ToArrayAsync();

    // ══ 1) رفض صلاحية الجرد: يُدقَّق مرة واحدة بالفاعل الصحيح ══
    [Fact]
    public async Task StockCountPermissionDenial_IsAuditedOnce_WithActor()
    {
        string m = $"ra1-{Guid.NewGuid():N}"[..12];
        int outsiderId;

        await using (var db = _fx.CreateContext())
        {
            await TestSeed.SeedAsync(db, m);
            var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.Products); // بت 1 فقط — لا مخزون
            var user = new User { Username = $"user-{m}", PasswordHash = "x", Role = role, IsActive = true };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            outsiderId = user.UserId;
        }

        await using (var db = _fx.CreateContext())
        {
            var start = await new clsStockCountService(db).StartSessionAsync(outsiderId);
            Assert.False(start.IsSuccess);
            Assert.Contains("صلاحية", start.Message);
        }

        await using (var verify = _fx.CreateContext())
        {
            // ننَشّط بالفاعل الفريد — القاعدة مشتركة بين اختبارات الرَن
            var log = Assert.Single(await RejectionLogsAsync(verify, "StockCountDenied"),
                l => l.UserId == outsiderId);
            Assert.Equal(outsiderId, log.UserId);
            Assert.Contains("جرد", log.Details);
            // الطابع الزمني تقني (UTC) — قريب من الآن
            Assert.True((DateTime.UtcNow - (log.CreatedAt ?? DateTime.MinValue)).TotalMinutes < 5);
        }
    }

    // ══ 2) دخول فاشل باسم مستخدم غير معروف: يُدقَّق بلا فاعل (UserId = null) ══
    [Fact]
    public async Task FailedLogin_UnknownUsername_AuditedWithoutActor()
    {
        string username = $"ghost-{Guid.NewGuid():N}"[..20];

        var r = await new clsUsersService(_fx.CreateContext()).LoginAsync(username, "WrongPass123");
        Assert.False(r.IsSuccess);

        await using var verify = _fx.CreateContext();
        // ننَشّط باسم المستخدم الفريد — LoginFailed تُكتب أيضًا باختبارات أخرى في الرَن
        var log = Assert.Single(await RejectionLogsAsync(verify, "LoginFailed"),
            l => l.Details!.Contains(username));
        Assert.Null(log.UserId); // Upgrade_14: محاولة بلا هوية تُدقَّق بلا فاعل
    }

    // ══ 3) دخول فاشل بكلمة مرور خاطئة: يُدقَّق بالفاعل — وبلا كلمة المرور ══
    [Fact]
    public async Task FailedLogin_WrongPassword_AuditedWithActor_NoPasswordInDetails()
    {
        string m = $"ra3-{Guid.NewGuid():N}"[..12];
        const string realPassword = "CorrectPass1";
        int userId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, m);
            var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
            var user = new User
            {
                Username = $"user-{m}",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(realPassword),
                Role = role,
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.UserId;
        }

        var r = await new clsUsersService(_fx.CreateContext()).LoginAsync($"user-{m}", "WrongPass123");
        Assert.False(r.IsSuccess);

        await using (var verify = _fx.CreateContext())
        {
            var log = Assert.Single(await RejectionLogsAsync(verify, "LoginFailed"),
                l => l.UserId == userId);
            // لا كلمات مرور (الصحيحة ولا الخاطئة) في السجل أبدًا
            Assert.DoesNotContain(realPassword, log.Details);
            Assert.DoesNotContain("WrongPass123", log.Details);
        }
    }

    // ══ 4) استعادة مرفوضة (بلا صلاحية): يُدقَّق بالفاعل ══
    [Fact]
    public async Task RejectedRestore_MissingFile_IsAudited()
    {
        string m = $"ra4-{Guid.NewGuid():N}"[..12];
        int actorId;

        await using (var db = _fx.CreateContext())
        {
            var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.Settings);
            var user = new User { Username = $"user-{m}", PasswordHash = "x", Role = role, IsActive = true };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            actorId = user.UserId;
        }

        // فاعل بصلاحية الإعدادات + ملف غير موجود → يصل بوابة الملف ويُرفض
        string missingFile = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.bak");
        var r = await new clsBackupService(_fx.CreateContext()).RestoreAsync(missingFile, actorUserId: actorId);
        Assert.False(r.IsSuccess);
        Assert.Contains("غير موجود", r.Message);

        await using var verify = _fx.CreateContext();
        var logs = await RejectionLogsAsync(verify, "RestoreDenied");
        Assert.Contains(logs, l => l.UserId == actorId && l.Details!.Contains("استعادة"));
    }

    // ══ 5) رفض تحقق أعمال (دفع زائد): صف واحد — بلا ازدواجية ══
    [Fact]
    public async Task OverpaymentRejection_AuditedOnce_NoDuplicates()
    {
        string m = $"ra5-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, m);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, customerId, 150m, userId));
            Assert.False(r.IsSuccess);
            Assert.Contains("أكبر من المتبقي", r.Message);
        }

        await using (var verify = _fx.CreateContext())
        {
            // ننَشّط بمعرّف الفاتورة الفريد (والفاعل) — القاعدة مشتركة بين اختبارات الرَن
            var log = Assert.Single(await RejectionLogsAsync(verify, "PaymentRejected"),
                l => l.Details!.Contains(invoiceId.ToString()));
            Assert.Equal(userId, log.UserId);

            // رفض واحد = صف واحد: لا صفوف رفض صلاحية لهذا الفاعل لنفس العملية
            Assert.DoesNotContain(await RejectionLogsAsync(verify, "PaymentDenied"),
                l => l.UserId == userId && l.Details!.Contains(invoiceId.ToString()));

            // العملية المرفوضة بلا أثر، وسجل التدقيق باقٍ
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId));
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            Assert.Equal(enPaymentStatus.unpaid, invoice.PaymentStatus);
        }
    }

    // ══ 6) الازدواجية عبر المكوّنات: رفض جلسة الدرج داخل السداد = صف واحد ══
    [Fact]
    public async Task DrawerSessionRejectionThroughPayment_LogsSingleRow_NoDoubleAudit()
    {
        string m = $"ra6-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, m);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        // مدير (يجتاز بوابة الصلاحية) + جلسة درج غير موجودة → الرفض من
        // ValidateDrawerSessionAsync وحدها: صف "DrawerSessionDenied" واحد
        // ولا صفوف رفض آخر لنفس المحاولة.
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(
                Pay(invoiceId, customerId, 50m, userId, sessionId: 99_999_999));
            Assert.False(r.IsSuccess);
            Assert.Contains("جلسة الدرج غير موجودة", r.Message);
        }

        await using (var verify = _fx.CreateContext())
        {
            // ننَشّط بالفاعل الفريد — اختبارات أخرى قد تُولّد رفض جلسة درج لفواعلهم هم
            var log = Assert.Single(await RejectionLogsAsync(verify, "DrawerSessionDenied"),
                l => l.UserId == userId);
            Assert.Equal(userId, log.UserId);

            Assert.DoesNotContain(await RejectionLogsAsync(verify, "PaymentRejected"),
                l => l.UserId == userId);
            Assert.DoesNotContain(await RejectionLogsAsync(verify, "PaymentDenied"),
                l => l.UserId == userId && l.Details!.Contains(invoiceId.ToString()));
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        }
    }

    // ══ 7) بقاء سجل التدقيق رغم تراجع العملية (اتصال مستقل) ══
    [Fact]
    public async Task AuditRow_WrittenOnSeparateConnection_SurvivesBusinessRollback()
    {
        string m = $"ra7-{Guid.NewGuid():N}"[..12];
        int userId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, m);
            userId = seed.UserId;
        }

        string productName = $"rollback-probe-{m}";
        await using (var db = _fx.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            // كتابة أعمال غير مُلزَمة (سيناريو الرفض داخل معاملة ثم تراجعها)
            db.Products.Add(new Product
            {
                Name = productName, Type = "stock", BasePrice = 1m, CostPrice = 1m,
                IsActive = true, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            // سجل التدقيق يُكتب على اتصال مستقل — لا ينتظر التزام هذه المعاملة
            await clsAudit.LogRejectedAsync(db, userId, "RollbackProbeRejected", $"رفض تجريبي {m}");

            await transaction.RollbackAsync();
        }

        await using (var verify = _fx.CreateContext())
        {
            // سجل التدقيق نجا من التراجع
            var logs = await RejectionLogsAsync(verify, "RollbackProbeRejected");
            var log = Assert.Single(logs);
            Assert.Equal(userId, log.UserId);

            // كتابة الأعمال المرفوضة تراجعت فعلًا
            Assert.False(await verify.Products.AnyAsync(p => p.Name == productName));
        }
    }

    // ══ 8) مخطط Upgrade_14: UserId يقبل NULL والـ FK ما زال مفعَّلًا ══
    [Fact]
    public async Task SystemLogs_UserIdNullable_FkStillEnforced()
    {
        await using var db = _fx.CreateContext();

        // سجل بلا فاعل يُقبل (محاولة دخول باسم غير معروف)
        db.SystemLogs.Add(new SystemLog { UserId = null, Action = "SchemaProbeNull", Details = "probe", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // FK ما زال قائمًا: هوية غير موجودة تُرفض من قاعدة البيانات
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            db.SystemLogs.Add(new SystemLog { UserId = 99_999_999, Action = "SchemaProbeFk", Details = "probe", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
    }

    // ══ 9) شكل التدقيق القائم لم يتغير: clsAudit.Log يبقى مؤجَّلًا مع العملية ══
    [Fact]
    public async Task ExistingAuditShape_LogStillDeferredUntilSaveChanges()
    {
        string m = $"ra8-{Guid.NewGuid():N}"[..12];

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, m);

        clsAudit.Log(db, seed.UserId, "ShapeProbe", $"اختبار الشكل {m}");
        // لم يُحفظ بعد: مخزَّن في سياق العملية (يُلتزم معها) — لا صف في القاعدة
        Assert.Equal(EntityState.Added, db.Entry(db.SystemLogs.Local.Single(l => l.Action == "ShapeProbe")).State);

        await db.SaveChangesAsync();
        await using var verify = _fx.CreateContext();
        var log = Assert.Single(await RejectionLogsAsync(verify, "ShapeProbe"),
            l => l.UserId == seed.UserId);
        Assert.Equal(seed.UserId, log.UserId);
        Assert.Contains(m, log.Details);
    }
}
