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
/// بوابة صلاحية الإعدادات + ذرّية تدقيق الإعدادين الضريبيين (Task 6):
/// • SetTaxPercentAsync / SetVatNumberAsync: فاعل ≤ 0 / دور -1 / بلا بت الإعدادات (2048)
///   → رفض بلا أي سجل تدقيق (العملية المرفوضة لا تُدقَّق — متطلب المهمة صريح).
///   ثغر -1 (متممة الاثنين) مُغلق بـ Has المصحّحة والبوابة الصريحة.
/// • التغيير + سجل التدقيق (TaxPercentChanged / VatNumberChanged) داخل معاملة واحدة —
///   فشل التدقيق يتراجع عن الاثنين.
/// • لا-تغيير: بلا كتابة وبلا تدقيق (الصف محدَّث UpdatedAt كما هو).
/// • تعديل الدور بلا تغيير فعلي → نجاح بلا سجل UpdateRole زائف.
/// الإعدادات تُضبط وتُصفَّر في finally — قاعدة الاختبار مشتركة.
/// </summary>
[Collection("database")]
public class TaxSettingsAuditTests
{
    private readonly DatabaseFixture _fx;
    public TaxSettingsAuditTests(DatabaseFixture fx) => _fx = fx;

    private const decimal TestTaxPercent = 15m;

    /// <summary>كتابة صف الضريبة مباشرة (بذر اختبار لا عملية مستخدم — نمط TaxPermissionTests).</summary>
    private static async Task SetTaxRowAsync(SaylDbContext db, string? value)
    {
        var row = await db.Settings.FindAsync(clsSettingsService.KeyTaxPercent);
        if (row == null && value != null)
        {
            db.Settings.Add(new Setting
            {
                SettingKey = clsSettingsService.KeyTaxPercent,
                SettingValue = value,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else if (row != null)
        {
            row.SettingValue = value;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // القاعدة مشتركة — لا قيم متتبعة قديمة بين الخطوات
    }

    private static async Task<string?> GetTaxRowAsync(SaylDbContext db)
        => (await db.Settings.AsNoTracking()
            .Where(s => s.SettingKey == clsSettingsService.KeyTaxPercent)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync());

    /// <summary>كتابة صف الرقم الضريبي مباشرة (بذر اختبار) — القيمة null تحذف الصف.</summary>
    private static async Task SetVatRowAsync(SaylDbContext db, string? value)
    {
        var row = await db.Settings.FindAsync(clsSettingsService.KeyVatNumber);
        if (row == null && value != null)
        {
            db.Settings.Add(new Setting
            {
                SettingKey = clsSettingsService.KeyVatNumber,
                SettingValue = value,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else if (row != null)
        {
            if (value == null)
                db.Settings.Remove(row); // حذف فعلي — اختبار حالة «الصف غير موجود»
            else
            {
                row.SettingValue = value;
                row.UpdatedAt = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<string?> GetVatRowAsync(SaylDbContext db)
        => (await db.Settings.AsNoTracking()
            .Where(s => s.SettingKey == clsSettingsService.KeyVatNumber)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync());

    /// <summary>مستخدم بدور محدد — الدور مُعاد استخدامه (RoleId tinyint، قراءة فقط هنا).</summary>
    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue, bool isActive = true)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>اعتراض اختبار: يرمي حتميًا على INSERT SystemLogs (نمط Task-5 —
    /// الأوامر الأربعة: DML المجمّع مع SELECT @@ROWCOUNT يُنفَّذ عبر مسار القارئ).</summary>
    private sealed class InjectedFailureInterceptor : DbCommandInterceptor
    {
        private static void Check(DbCommand command)
        {
            string sql = command.CommandText;
            if (sql.Contains("SystemLogs", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("حقن فشل اختباري على INSERT SystemLogs.");
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

    private static SaylDbContext InterceptedContext(DatabaseFixture fx, DbCommandInterceptor interceptor)
        => new(new DbContextOptionsBuilder<SaylDbContext>()
            .UseSqlServer(DatabaseFixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options);

    // ══ 1) فاعل مصرَّح (بت 2048) يغيّر الضريبة → نجاح + الصف محدَّث + تدقيق واحد بالضبط ══
    [Fact]
    public async Task AuthorizedActor_ChangesTaxPercent_Success_RowUpdated_SingleAudit()
    {
        string m = $"ts1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings); // بت 2048

        try
        {
            await SetTaxRowAsync(db, "0"); // نقطة انطلاق معروفة
            await using var svc = _fx.CreateContext();

            var r = await new clsSettingsService(svc).SetTaxPercentAsync(TestTaxPercent, actorId);

            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("15", await GetTaxRowAsync(db)); // "0.##" بثقافة الجهاز
            Assert.Equal(1, await db.SystemLogs.CountAsync(l =>
                l.Action == "TaxPercentChanged" && l.UserId == actorId));
        }
        finally
        {
            await SetTaxRowAsync(db, "0"); // تصفير — القاعدة مشتركة
        }
    }

    // ══ 2) فاعل بلا بت الإعدادات (بت 16، وكاشير 0) → رفض + الصف لم يُلمس + بلا أي تدقيق ══
    [Fact]
    public async Task UnauthorizedActor_Rejected_RowUntouched_NoAudit()
    {
        string m = $"ts2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int salesOnlyId = await SeedUserAsync(db, m + "-s", AppPermissions.SalesInvoices); // بت 16 فقط
        int cashierId = await SeedUserAsync(db, m + "-c", AppPermissions.CashierRoleValue); // كاشير 0

        try
        {
            await SetTaxRowAsync(db, "15");

            foreach (int actorId in new[] { salesOnlyId, cashierId })
            {
                await using var svc = _fx.CreateContext();
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId);

                Assert.False(r.IsSuccess);
                Assert.Contains("الإعدادات", r.Message);
                Assert.Equal("15", await GetTaxRowAsync(db)); // الصف لم يُلمس
                // العملية المرفوضة بلا أي سجل تدقيق — لا رفض ولا تغيير (متطلب المهمة صريح)
                Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
                    (l.Action == "TaxPercentChanged" || l.Action == "TaxSettingsDenied") && l.UserId == actorId));
            }
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 3) فاعل 0 / غير موجود / غير نشط → رفض (ثغر -1 مُغلق عند البوابة) بلا أي تدقيق ══
    [Fact]
    public async Task Zero_Nonexistent_InactiveActor_Rejected()
    {
        string m = $"ts3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int inactiveId = await SeedUserAsync(db, m, AppPermissions.Settings, isActive: false);

        try
        {
            await SetTaxRowAsync(db, "15");

            foreach (int actorId in new[] { 0, int.MaxValue, inactiveId })
            {
                await using var svc = _fx.CreateContext();
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId);

                Assert.False(r.IsSuccess, $"actor {actorId}");
                Assert.Equal("15", await GetTaxRowAsync(db));
                // العملية المرفوضة بلا أي سجل تدقيق — حتى الفاعل غير الموثق لا يُدقَّق رفضه
                Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
                    (l.Action == "TaxPercentChanged" || l.Action == "TaxSettingsDenied") &&
                    (l.UserId == actorId || l.UserId == null)));
            }
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 4) صحة القديم/الجديد في التدقيق: قيمة موجبة، وغياب الصف = (غير محددة) ══
    [Fact]
    public async Task AuditDetails_RecordsOldAndNew_IncludingNullOld()
    {
        string m = $"ts4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings);

        try
        {
            // 4أ) صف قديم "15" → تعيين 20: التفاصيل تحوي القيمتين
            await SetTaxRowAsync(db, "15");
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            var log = await db.SystemLogs
                .Where(l => l.Action == "TaxPercentChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId)
                .FirstAsync();
            Assert.Contains("15", log.Details);
            Assert.Contains("20", log.Details);

            // 4ب) بلا صف إطلاقًا → القديمة (غير محددة) — لا 0 مضلل
            // (الحذف بسياق جديد — لا يبقي في db كيانًا بحالة Deleted يشوّش التصفير)
            await using (var del = _fx.CreateContext())
            {
                del.Settings.Remove((await del.Settings.FindAsync(clsSettingsService.KeyTaxPercent))!);
                await del.SaveChangesAsync();
            }
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            var logNull = await db.SystemLogs
                .Where(l => l.Action == "TaxPercentChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId)
                .FirstAsync();
            Assert.Contains("(غير محددة)", logNull.Details);
            Assert.Contains("20", logNull.Details);
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 5+6) هوية الفاعل في التدقيق + الطابع ضمن تفاوت UTC ══
    [Fact]
    public async Task AuditRow_AttributesAuthenticatedActor_AndUtcTimestamp()
    {
        string m = $"ts5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings);
        int otherId = await SeedUserAsync(db, m + "-o", AppPermissions.Settings); // مستخدم آخر

        try
        {
            await SetTaxRowAsync(db, "0");
            DateTime before = DateTime.UtcNow;
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            DateTime after = DateTime.UtcNow;

            var log = await db.SystemLogs
                .Where(l => l.Action == "TaxPercentChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId)
                .FirstAsync();
            Assert.Equal(actorId, log.UserId); // هوية الفاعل لا مستخدم آخر
            Assert.NotNull(log.CreatedAt);
            Assert.InRange(log.CreatedAt.Value, before.AddSeconds(-1), after.AddSeconds(1));
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 7) لا-تغيير: نفس القيمة → نجاح بلا تدقيق جديد و UpdatedAt كما هو ══
    [Fact]
    public async Task NoOp_SameValue_NoNewAudit_UpdatedAtUntouched()
    {
        string m = $"ts6-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings);

        try
        {
            await SetTaxRowAsync(db, "15");
            DateTime updatedAtBefore = (await db.Settings.FindAsync(clsSettingsService.KeyTaxPercent))!.UpdatedAt!.Value;
            int auditsBefore = await db.SystemLogs.CountAsync(l =>
                l.Action == "TaxPercentChanged" && l.UserId == actorId);

            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetTaxPercentAsync(15m, actorId);
                Assert.True(r.IsSuccess, r.Message); // نجاح برسالة لا-تغيير
            }

            var row = await db.Settings.FindAsync(clsSettingsService.KeyTaxPercent);
            Assert.Equal(updatedAtBefore, row!.UpdatedAt); // UpdatedAt لم يُلمس
            Assert.Equal("15", row.SettingValue);
            Assert.Equal(auditsBefore, await db.SystemLogs.CountAsync(l =>
                l.Action == "TaxPercentChanged" && l.UserId == actorId)); // بلا تدقيق جديد
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 8) فشل حفظ التدقيق → تراجع: الصف لم يتغيّر ولا حالة نصفية ══
    [Fact]
    public async Task AuditFailure_RollsBack_SettingNotChanged()
    {
        string m = $"ts7-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings);

        try
        {
            await SetTaxRowAsync(db, "15");
            await using var svc = InterceptedContext(_fx, new InjectedFailureInterceptor());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                new clsSettingsService(svc).SetTaxPercentAsync(20m, actorId));

            Assert.Equal("15", await GetTaxRowAsync(db)); // لا تغيير بلا تدقيق
            Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
                l.Action == "TaxPercentChanged" && l.UserId == actorId));
        }
        finally
        {
            await SetTaxRowAsync(db, "0");
        }
    }

    // ══ 9) فحص Has المصحّح: كل القيم السلبية تُرفض لكل البتات، 0 يُرفض، المدير يمرّ ══
    [Fact]
    public void Has_NegativeOne_FalseForAllBits_ZeroFalse_AdminTrue()
    {
        int[] bits =
        {
            AppPermissions.Products, AppPermissions.Inventory, AppPermissions.Customers,
            AppPermissions.Suppliers, AppPermissions.SalesInvoices, AppPermissions.PurchaseInvoices,
            AppPermissions.Expenses, AppPermissions.Reports, AppPermissions.Employees,
            AppPermissions.Users, AppPermissions.Roles, AppPermissions.Settings
        };
        // قيم سلبية تمثيلية: -1 (كل البتات بمتممة الاثنين)، -2 (بت المنتجات فقط)،
        // -2048 (بت الإعدادات فقط!)، int.MinValue (أقصى سالب) — كلها يجب أن تُرفض.
        int[] negativeRoles = { -1, -2, -2048, int.MinValue };

        foreach (int bit in bits)
        {
            foreach (int negativeRole in negativeRoles)
                Assert.False(AppPermissions.Has(negativeRole, bit)); // ثغر متممة الاثنين مُغلق
            Assert.False(AppPermissions.Has(0, bit));  // الكاشير بلا بت أصلًا
            Assert.True(AppPermissions.Has(AppPermissions.AdminRoleValue, bit)); // 4095 يملك الكل
        }
        // سلوك الأدوار الصالحة لم يتغير: من يملك البت يمرّ، ومن لا يملكه يُرفض
        Assert.True(AppPermissions.Has(AppPermissions.Settings, AppPermissions.Settings));
        Assert.True(AppPermissions.Has(AppPermissions.SalesInvoices, AppPermissions.SalesInvoices));
        Assert.False(AppPermissions.Has(AppPermissions.AccountantRoleValue, AppPermissions.Settings)); // محاسب 508 بلا بت الإعدادات
        Assert.False(AppPermissions.Has(AppPermissions.Customers, AppPermissions.SalesInvoices)); // بت 4 لا يملك بت 16
    }

    // ══ 10) تعديل الدور بلا تغيير → بلا سجل UpdateRole زائف؛ التغيير الفعلي يُدقَّق ══
    [Fact]
    public async Task RoleUpdate_NoOp_NoAuditRealChange_AuditedOldNew()
    {
        string m = $"ts8-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();

        // دور مخصص حصري للاختبار (يُعدَّل — لا إعادة استخدام أدوار TestSeed)
        var role = new Role { Name = $"role-{m}", RoleValue = AppPermissions.Customers, IsActive = true };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        int actorId = await SeedUserAsync(db, m + "-a", AppPermissions.AdminRoleValue); // مدير (قراءة الدور فقط)
        int auditsBefore = await db.SystemLogs.CountAsync(l =>
            l.Action == "UpdateRole" && l.Details!.Contains(role.Name));

        // لا-تغيير: نفس القيمة ونفس الاسم → نجاح بلا حفظ وبلا تدقيق
        await using (var svc = _fx.CreateContext())
        {
            var noOp = await new clsRoleService(svc).UpdateRoleAsync(
                new DtoRole { RoleId = role.RoleId, Name = role.Name, RoleValue = role.RoleValue }, actorId);
            Assert.True(noOp.IsSuccess, noOp.Message);
        }
        Assert.Equal(auditsBefore, await db.SystemLogs.CountAsync(l =>
            l.Action == "UpdateRole" && l.Details!.Contains(role.Name)));

        await using (var verify = _fx.CreateContext())
            Assert.Equal(AppPermissions.Customers, (await verify.Roles.FindAsync(role.RoleId))!.RoleValue);

        // تغيير فعلي: القيمة تتغير → تدقيق «من 4 إلى 8»
        await using (var svc = _fx.CreateContext())
        {
            var changed = await new clsRoleService(svc).UpdateRoleAsync(
                new DtoRole { RoleId = role.RoleId, Name = role.Name, RoleValue = AppPermissions.Suppliers }, actorId);
            Assert.True(changed.IsSuccess, changed.Message);
        }
        await using (var verify = _fx.CreateContext())
            Assert.Equal(AppPermissions.Suppliers, (await verify.Roles.FindAsync(role.RoleId))!.RoleValue);

        var audit = await db.SystemLogs
            .Where(l => l.Action == "UpdateRole" && l.Details!.Contains(role.Name))
            .OrderByDescending(l => l.LogId)
            .FirstAsync();
        Assert.Contains($"{AppPermissions.Customers}", audit.Details);
        Assert.Contains($"{AppPermissions.Suppliers}", audit.Details);
    }

    // ══ 11) الرقم الضريبي (VatNumber): بوابة + تدقيق ذرّي + لا-op — نفس سياسة الضريبة ══
    [Fact]
    public async Task VatNumber_Gate_AuditAtomicity_NoOp_Unauthorized()
    {
        string m = $"ts9-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m + "-p", AppPermissions.Settings);      // مصرَّح
        int cashierId = await SeedUserAsync(db, m + "-c", AppPermissions.CashierRoleValue); // مرفوض

        try
        {
            // (أ) فاعل مصرَّح يغيّر من "3001122334" إلى "3009988776" → تدقيق بالقيمتين
            await SetVatRowAsync(db, "3001122334");
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetVatNumberAsync("3009988776", actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            Assert.Equal("3009988776", await GetVatRowAsync(db));
            var audit = await db.SystemLogs.AsNoTracking()
                .Where(l => l.Action == "VatNumberChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId).FirstAsync();
            Assert.Contains("3001122334", audit.Details); // القديمة
            Assert.Contains("3009988776", audit.Details); // الجديدة

            // (ب) لا-تغيير: نفس القيمة → نجاح بلا كتابة وبلا تدقيق جديد
            int auditsBefore = await db.SystemLogs.CountAsync(l =>
                l.Action == "VatNumberChanged" && l.UserId == actorId);
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetVatNumberAsync("3009988776", actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            Assert.Equal(auditsBefore, await db.SystemLogs.CountAsync(l =>
                l.Action == "VatNumberChanged" && l.UserId == actorId));

            // (ج) فاعل مرفوض (كاشير) → فشل + الصف لم يُلمس + بلا أي تدقيق
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetVatNumberAsync("X", cashierId);
                Assert.False(r.IsSuccess);
                Assert.Contains("الإعدادات", r.Message);
            }
            Assert.Equal("3009988776", await GetVatRowAsync(db));
            Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
                (l.Action == "VatNumberChanged" || l.Action == "VatSettingsDenied") && l.UserId == cashierId));

            // (د) التفريغ إلى "" صف موجود → تدقيق يُميّز «(فارغ)» عن «(غير محددة)»
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetVatNumberAsync("", actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            Assert.Equal("", await GetVatRowAsync(db));
            var emptyAudit = await db.SystemLogs.AsNoTracking()
                .Where(l => l.Action == "VatNumberChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId).FirstAsync();
            Assert.Contains("(فارغ)", emptyAudit.Details);
            Assert.Contains("3009988776", emptyAudit.Details); // القديمة قبل التفريغ

            // (هـ) غياب الصف كليًا → القديمة «(غير محددة)» — تمثيل بلا غموض
            await SetVatRowAsync(db, null); // حذف فعلي
            await using (var svc = _fx.CreateContext())
            {
                var r = await new clsSettingsService(svc).SetVatNumberAsync("3005555555", actorId);
                Assert.True(r.IsSuccess, r.Message);
            }
            Assert.Equal("3005555555", await GetVatRowAsync(db));
            var missingAudit = await db.SystemLogs.AsNoTracking()
                .Where(l => l.Action == "VatNumberChanged" && l.UserId == actorId)
                .OrderByDescending(l => l.LogId).FirstAsync();
            Assert.Contains("(غير محددة)", missingAudit.Details);
            Assert.Contains("3005555555", missingAudit.Details);
        }
        finally
        {
            await SetVatRowAsync(db, ""); // تصفير — قاعدة الاختبار مشتركة
        }
    }

    // ══ 12) فشل حفظ تدقيق VatNumber → تراجع: الصف لم يتغيّر ولا حالة نصفية ══
    [Fact]
    public async Task VatNumber_AuditFailure_RollsBack_SettingNotChanged()
    {
        string m = $"ts10-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        int actorId = await SeedUserAsync(db, m, AppPermissions.Settings);

        try
        {
            await SetVatRowAsync(db, "3000000001");
            await using var svc = InterceptedContext(_fx, new InjectedFailureInterceptor());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                new clsSettingsService(svc).SetVatNumberAsync("3000000002", actorId));

            Assert.Equal("3000000001", await GetVatRowAsync(db)); // لا تغيير بلا تدقيق
            Assert.Equal(0, await db.SystemLogs.CountAsync(l =>
                l.Action == "VatNumberChanged" && l.UserId == actorId));
        }
        finally
        {
            await SetVatRowAsync(db, "");
        }
    }
}
