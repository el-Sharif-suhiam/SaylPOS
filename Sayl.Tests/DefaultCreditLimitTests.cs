using Common.DTOs;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Task-1: حد الائتمان الافتراضي القابل للضبط (مفتاح DefaultCreditLimit في
/// Settings، افتراضيته 1000 عند غياب الصف) وترحيل العملاء القائمين لمرة واحدة
/// (0/NULL -> 1000) المحروس بصف Settings مفتاح CreditLimitMigrationV1.
/// (قاعدة مشتركة — كل تغيير على الإعداد يُنظَّف في finally كي لا يتسرب
/// إلى بقية الاختبارات، والعلامة تُعاد موجودةً دائمًا.)
/// </summary>
[Collection("database")]
public class DefaultCreditLimitTests
{
    private readonly DatabaseFixture _fx;
    public DefaultCreditLimitTests(DatabaseFixture fx) => _fx = fx;

    private const string MarkerKey = "CreditLimitMigrationV1";
    private const string CreditKey = "DefaultCreditLimit";

    // ══ أدوات الحالة المشتركة: الإعداد والعلامة ══

    private static async Task<bool> SettingExistsAsync(SaylDbContext db, string key)
        => (await db.Database.SqlQuery<bool>($@"
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dbo.Settings WHERE SettingKey = {key}) THEN 1 ELSE 0 END AS BIT) AS Value")
            .ToListAsync()).Single();

    private static async Task DeleteSettingAsync(SaylDbContext db, string key)
        => await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM dbo.Settings WHERE SettingKey = {0}", key);

    /// <summary>إعادة الحالة المشتركة مهما كانت النتيجة: صف الإعداد يُحذف
    /// (فالافتراضية 1000 تسري على من بعده) والعلامة تُضمن موجودة (كي لا
    /// يعاد الترحيل فوق أصفار متعمدة في اختبارات لاحقة).</summary>
    private static async Task RestoreSharedStateAsync(SaylDbContext db)
    {
        await DeleteSettingAsync(db, CreditKey);
        if (!await SettingExistsAsync(db, MarkerKey))
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.Settings (SettingKey, SettingValue, UpdatedAt) VALUES ({0}, N'1', SYSUTCDATETIME())", MarkerKey);
    }

    /// <summary>بذر عميل مباشرًا (بلا خدمة) بحد ائتمان محدد — قيد الاختبار.</summary>
    private static async Task<int> SeedCustomerAsync(SaylDbContext db, string name, decimal? creditLimit)
    {
        var party = new Party { PartyType = 'C', Name = name, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(party);
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { PartyId = party.PartyId, Balance = 0, CreditLimit = creditLimit });
        await db.SaveChangesAsync();
        return party.PartyId;
    }

    private static async Task<decimal?> ReadCreditLimitAsync(SaylDbContext db, int partyId)
        => (await db.Database.SqlQuery<decimal?>($@"
            SELECT CreditLimit AS Value FROM dbo.Customers WHERE PartyId = {partyId}")
            .ToListAsync()).Single();

    // ══ 1) الافتراضية الابتدائية: 1000 عند غياب صف الإعداد ══
    [Fact]
    public async Task GetDefaultCreditLimit_NoSettingRow_Returns1000()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await DeleteSettingAsync(db, CreditKey);
            var settings = new clsSettingsService(db);
            Assert.Equal(1000m, await settings.GetDefaultCreditLimitAsync());
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 2) عميل جديد يأخذ الافتراضية (غياب الصف -> 1000) ══
    [Fact]
    public async Task NewCustomer_ReceivesDefaultLimit_WhenNoSettingRow()
    {
        await using var db = _fx.CreateContext();
        int newPartyId = 0;
        try
        {
            await DeleteSettingAsync(db, CreditKey);
            string marker = $"dcl2-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);

            var result = await new clsPartyService(db).AddNewPartyWithRoleAsync(
                new DtoParty { PartyType = 'C', Name = $"زبون-{marker}" }, actorUserId: seed.UserId);
            Assert.True(result.IsSuccess, result.Message);
            newPartyId = result.Data;

            Assert.Equal(1000m, await ReadCreditLimitAsync(db, newPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 3) تغيير الافتراضية يسري على العملاء اللاحقين ══
    [Fact]
    public async Task ChangedDefault_AppliesToSubsequentlyCreatedCustomers()
    {
        await using var db = _fx.CreateContext();
        int newPartyId = 0;
        try
        {
            string marker = $"dcl3-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            var settings = new clsSettingsService(db);

            var set = await settings.SetDefaultCreditLimitAsync(2500m);
            Assert.True(set.IsSuccess, set.Message);
            Assert.Equal(2500m, await settings.GetDefaultCreditLimitAsync());

            var result = await new clsPartyService(db).AddNewPartyWithRoleAsync(
                new DtoParty { PartyType = 'C', Name = $"زبون-{marker}" }, actorUserId: seed.UserId);
            Assert.True(result.IsSuccess, result.Message);
            newPartyId = result.Data;

            Assert.Equal(2500m, await ReadCreditLimitAsync(db, newPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 4) تغيير الافتراضية لا يمسّ العملاء القائمين ══
    [Fact]
    public async Task ChangedDefault_DoesNotModifyExistingCustomers()
    {
        await using var db = _fx.CreateContext();
        int existingPartyId = 0;
        try
        {
            string marker = $"dcl4-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            existingPartyId = await SeedCustomerAsync(db, $"قديم-{marker}", 500m);

            var settings = new clsSettingsService(db);
            var set = await settings.SetDefaultCreditLimitAsync(2500m);
            Assert.True(set.IsSuccess, set.Message);

            Assert.Equal(500m, await ReadCreditLimitAsync(db, existingPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 5) الترحيل: الأصفار وNULL تصير 1000 (مسار الإقلاع الفعلي) ══
    [Fact]
    public async Task Migration_ZeroAndNullCreditLimits_Become1000()
    {
        await using var db = _fx.CreateContext();
        int zeroPartyId = 0, nullPartyId = 0;
        try
        {
            string marker = $"dcl5-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            zeroPartyId = await SeedCustomerAsync(db, $"صفر-{marker}", 0m);
            nullPartyId = await SeedCustomerAsync(db, $"فارغ-{marker}", null);

            // محاكاة قاعدة قبل الترحيل: حذف العلامة ثم مسار الإقلاع الفعلي
            await DeleteSettingAsync(db, MarkerKey);
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();

            Assert.Equal(1000m, await ReadCreditLimitAsync(db, zeroPartyId));
            Assert.Equal(1000m, await ReadCreditLimitAsync(db, nullPartyId));
            Assert.True(await SettingExistsAsync(db, MarkerKey)); // العلامة وُضعت
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 6) الترحيل: الحدود غير الصفرية تُحفظ كما هي ══
    [Fact]
    public async Task Migration_NonZeroCreditLimits_ArePreserved()
    {
        await using var db = _fx.CreateContext();
        int keptPartyId = 0;
        try
        {
            string marker = $"dcl6-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            keptPartyId = await SeedCustomerAsync(db, $"مضبوط-{marker}", 777.50m);

            await DeleteSettingAsync(db, MarkerKey);
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();

            Assert.Equal(777.50m, await ReadCreditLimitAsync(db, keptPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 7) الترحيل مرة واحدة: العلامة تمنع رفع أصفار متعمدة لاحقة ══
    [Fact]
    public async Task Migration_RunsOnce_MarkerPreventsReBumpingDeliberateZeros()
    {
        await using var db = _fx.CreateContext();
        int migratedPartyId = 0, deliberatePartyId = 0;
        try
        {
            string marker = $"dcl7-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            migratedPartyId = await SeedCustomerAsync(db, $"قديم-{marker}", 0m);

            // الإقلاع الأول: العلامة غائبة — الأصفار تُرفع
            await DeleteSettingAsync(db, MarkerKey);
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();
            Assert.Equal(1000m, await ReadCreditLimitAsync(db, migratedPartyId));

            // عميل يضبطه المالك على 0 بعد الترحيل (منع الآجل عليه)
            deliberatePartyId = await SeedCustomerAsync(db, $"متعمد-{marker}", 0m);

            // إقلاع ثانٍ + تشغيل السكربت اليدوي: كلاهما بلا أثر على الصفر المتعمد
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();
            Assert.Equal(0m, await ReadCreditLimitAsync(db, deliberatePartyId));

            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_16_DefaultCreditLimit.sql")));
            Assert.Equal(0m, await ReadCreditLimitAsync(db, deliberatePartyId));
            // والعميل المُرحَّل الأول لم يُمس أيضًا
            Assert.Equal(1000m, await ReadCreditLimitAsync(db, migratedPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 8) الخدمة ترفض القيم السالبة ══
    [Fact]
    public async Task SetDefaultCreditLimit_Negative_IsRejected()
    {
        await using var db = _fx.CreateContext();
        try
        {
            await DeleteSettingAsync(db, CreditKey);
            var settings = new clsSettingsService(db);

            var result = await settings.SetDefaultCreditLimitAsync(-5m);
            Assert.False(result.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(result.Message)); // رسالة عربية للمستخدم
            Assert.False(await SettingExistsAsync(db, CreditKey));   // لم يُخزَّن شيء
            Assert.Equal(1000m, await settings.GetDefaultCreditLimitAsync()); // الافتراضية سارية
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 9) الصفر قيمة مقبولة (عميل جديد بلا آجل) ══
    [Fact]
    public async Task SetDefaultCreditLimit_Zero_IsAccepted()
    {
        await using var db = _fx.CreateContext();
        try
        {
            var settings = new clsSettingsService(db);
            var result = await settings.SetDefaultCreditLimitAsync(0m);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0m, await settings.GetDefaultCreditLimitAsync());
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 10) إعادة تشغيل مسار الإقلاع لا تصفّر الإعداد المضبوط ولا تلمس العملاء ══
    [Fact]
    public async Task StartupUpgradeRerun_KeepsConfiguredSetting_AndCustomerLimits()
    {
        await using var db = _fx.CreateContext();
        int customerPartyId = 0;
        try
        {
            string marker = $"dcl10-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            var settings = new clsSettingsService(db);

            customerPartyId = await SeedCustomerAsync(db, $"مضبوط-{marker}", 450m);

            var set = await settings.SetDefaultCreditLimitAsync(2500m);
            Assert.True(set.IsSuccess, set.Message);

            // «إعادة تشغيل»: مسار الإقلاع الفعلي + السكربت اليدوي — الإعداد يبقى
            await new clsUsersService(db).EnsureSchemaUpgradesAsync();
            DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_16_DefaultCreditLimit.sql")));

            Assert.Equal(2500m, await settings.GetDefaultCreditLimitAsync());
            Assert.Equal(450m, await ReadCreditLimitAsync(db, customerPartyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }

    // ══ 11) تعديل العميل لا يستبدل حده الائتماني بصمت (التحديث الجزئي) ══
    [Fact]
    public async Task EditCustomer_DoesNotOverwriteExistingCreditLimit()
    {
        await using var db = _fx.CreateContext();
        try
        {
            string marker = $"dcl11-{Guid.NewGuid():N}"[..12];
            var seed = await TestSeed.SeedAsync(db, marker);
            int partyId = await SeedCustomerAsync(db, $"عميل-{marker}", 333.25m);

            // تعديل تحديثي جزئي: حقل الاسم فقط — DtoParty لا يحمل حد ائتمان أصلًا
            var result = await new clsPartyService(db).UpdatePartyAsync(
                new DtoParty { PartyId = partyId, PartyType = 'C', Name = $"معدّل-{marker}" },
                actorUserId: seed.UserId);
            Assert.True(result.IsSuccess, result.Message);

            Assert.Equal(333.25m, await ReadCreditLimitAsync(db, partyId));
        }
        finally
        {
            await RestoreSharedStateAsync(db);
        }
    }
}
