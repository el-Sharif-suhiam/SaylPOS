using Common.DTOs;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Authorization gates for party CRUD with Combined Parties ('B'):
///   - Update/Deactivate/Activate of a combined party touches BOTH the customer
///     and supplier records → both screen bits (Customers 4 + Suppliers 8) required.
///   - Single-role parties ('C'/'S') keep requiring only their own screen bit —
///     no unnecessary permission is demanded.
///   - Conversion to 'B' creates the missing record on the other side → both bits.
///   - Converting 'B' back down to 'C'/'S' stays forbidden (existing rule).
///   - User resolution follows the shared clsPermissions helper: inactive users
///     (or users with inactive roles) resolve to -1 and pass no gate.
/// Fresh context per step (same pattern as ServiceAuthorizationTests).
/// </summary>
[Collection("database")]
public class PartyPermissionTests
{
    private readonly DatabaseFixture _fx;
    public PartyPermissionTests(DatabaseFixture fx) => _fx = fx;

    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue, bool isActive = true)
    {
        // الدور مُعاد استخدامه من TestSeed.GetOrCreateRoleAsync (RoleId tinyint — لا دور جديد لكل اختبار)
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>يحوّل طرفًا قائمًا إلى مشترك ('B') بفاعل مدير — نقطة انطلاق اختبارات 'B'.</summary>
    private static async Task ConvertToCombinedAsync(SaylDbContext db, int partyId, int adminId)
    {
        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = partyId, PartyType = 'B' }, actorUserId: adminId);
        Assert.True(r.IsSuccess, r.Message);
    }

    // ══ 1) تحديث عميل: بت العملاء يكفي — وصاحب بت الموردين يُرفض ══
    [Fact]
    public async Task UpdateCustomer_WithCustomersBit_Succeeds()
    {
        string marker = $"pu1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int clerkId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers); // بت 4 فقط

        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'C', Name = "upd-c" }, actorUserId: clerkId);

        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("upd-c", (await db.Parties.FindAsync(seed.CustomerId))!.Name);
    }

    [Fact]
    public async Task UpdateCustomer_WithoutCustomersBit_Denied()
    {
        string marker = $"pu2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int clerkId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers); // بت 8 فقط

        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'C', Name = "hacked" }, actorUserId: clerkId);

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        Assert.NotEqual("hacked", (await db.Parties.FindAsync(seed.CustomerId))!.Name);
    }

    // ══ 2) تحديث مورد: بت الموردين يكفي — وصاحب بت العملاء يُرفض ══
    [Fact]
    public async Task UpdateSupplier_WithSuppliersBit_Succeeds()
    {
        string marker = $"pu3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int clerkId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers); // بت 8 فقط

        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.SupplierId, PartyType = 'S', Name = "upd-s" }, actorUserId: clerkId);

        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal("upd-s", (await db.Parties.FindAsync(seed.SupplierId))!.Name);
    }

    [Fact]
    public async Task UpdateSupplier_WithoutSuppliersBit_Denied()
    {
        string marker = $"pu4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int clerkId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers); // بت 4 فقط

        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.SupplierId, PartyType = 'S', Name = "hacked" }, actorUserId: clerkId);

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        Assert.NotEqual("hacked", (await db.Parties.FindAsync(seed.SupplierId))!.Name);
    }

    // ══ 3) تحديث طرف مشترك: الفجوة السابقة — كان يمرّ بلا أي فحص ══
    [Fact]
    public async Task UpdateCombinedParty_RequiresBothBits()
    {
        string marker = $"pu5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await ConvertToCombinedAsync(db, seed.CustomerId, seed.UserId);

        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        int suppliersOnlyId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers);

        // صاحب بت العملاء وحده: يُرفض (التعديل يمسّ سجل المورد أيضًا)
        var deniedC = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'B', Name = "hacked-c" }, actorUserId: customersOnlyId);
        Assert.False(deniedC.IsSuccess);
        Assert.Contains("صلاحية", deniedC.Message);

        // صاحب بت الموردين وحده: يُرفض (التعديل يمسّ سجل العميل أيضًا)
        var deniedS = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'B', Name = "hacked-s" }, actorUserId: suppliersOnlyId);
        Assert.False(deniedS.IsSuccess);
        Assert.Contains("صلاحية", deniedS.Message);

        // صاحب البتّين معًا: يُسمح
        int bothId = await SeedUserAsync(db, marker + "-b", AppPermissions.Customers | AppPermissions.Suppliers);
        var ok = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'B', Name = "upd-b" }, actorUserId: bothId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.Equal("upd-b", (await db.Parties.FindAsync(seed.CustomerId))!.Name);
    }

    // ══ 4) تعطيل عميل/مورد: بت شاشته فقط — لا صلاحية زائدة تُطلب ══
    [Fact]
    public async Task DeactivateCustomer_RequiresCustomersBit_SupplierBitInsufficient()
    {
        string marker = $"pu6-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        int suppliersOnlyId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers);

        var denied = await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: suppliersOnlyId);
        Assert.False(denied.IsSuccess);
        Assert.True((await db.Parties.FindAsync(seed.CustomerId))!.IsActive);

        var ok = await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: customersOnlyId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.False((await db.Parties.FindAsync(seed.CustomerId))!.IsActive);
    }

    [Fact]
    public async Task DeactivateSupplier_RequiresSuppliersBit_CustomerBitInsufficient()
    {
        string marker = $"pu7-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        int suppliersOnlyId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers);

        var denied = await new clsPartyService(db).DeactivatePartyAsync(seed.SupplierId, actorUserId: customersOnlyId);
        Assert.False(denied.IsSuccess);
        Assert.True((await db.Parties.FindAsync(seed.SupplierId))!.IsActive);

        var ok = await new clsPartyService(db).DeactivatePartyAsync(seed.SupplierId, actorUserId: suppliersOnlyId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.False((await db.Parties.FindAsync(seed.SupplierId))!.IsActive);
    }

    // ══ 5) تعطيل طرف مشترك: الفجوة الثانية — كان يُقاس ببت العملاء وحده ══
    [Fact]
    public async Task DeactivateCombinedParty_RequiresBothBits()
    {
        string marker = $"pu8-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await ConvertToCombinedAsync(db, seed.CustomerId, seed.UserId);

        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        int suppliersOnlyId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers);

        // صاحب بت العملاء وحده: يُرفض — التعطيل يخفي سجل المورد من شاشته أيضًا
        var deniedC = await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: customersOnlyId);
        Assert.False(deniedC.IsSuccess);
        Assert.Contains("صلاحية", deniedC.Message);

        // صاحب بت الموردين وحده: يُرفض — التعطيل يخفي سجل العميل من شاشته أيضًا
        var deniedS = await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: suppliersOnlyId);
        Assert.False(deniedS.IsSuccess);
        Assert.Contains("صلاحية", deniedS.Message);

        // صاحب البتّين معًا (أرصدة صفرية): يُسمح
        int bothId = await SeedUserAsync(db, marker + "-b", AppPermissions.Customers | AppPermissions.Suppliers);
        var ok = await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: bothId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.False((await db.Parties.FindAsync(seed.CustomerId))!.IsActive);
    }

    // ══ 6) إعادة تفعيل الطرف المشترك: نفس بوابة التعطيل ══
    [Fact]
    public async Task ActivateCombinedParty_RequiresBothBits()
    {
        string marker = $"pu9-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await ConvertToCombinedAsync(db, seed.CustomerId, seed.UserId);
        Assert.True((await new clsPartyService(db).DeactivatePartyAsync(seed.CustomerId, actorUserId: seed.UserId)).IsSuccess);

        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        var denied = await new clsPartyService(db).ActivatePartyAsync(seed.CustomerId, actorUserId: customersOnlyId);
        Assert.False(denied.IsSuccess);
        Assert.False((await db.Parties.FindAsync(seed.CustomerId))!.IsActive);

        int bothId = await SeedUserAsync(db, marker + "-b", AppPermissions.Customers | AppPermissions.Suppliers);
        var ok = await new clsPartyService(db).ActivatePartyAsync(seed.CustomerId, actorUserId: bothId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.True((await db.Parties.FindAsync(seed.CustomerId))!.IsActive);
    }

    // ══ 7) التحويل إلى مشترك: يُنشئ السجل الناقص — كلا البتّين مطلوب ══
    [Fact]
    public async Task ConvertCustomerToCombined_RequiresSuppliersBitToo()
    {
        string marker = $"pu10-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);

        var denied = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'B' }, actorUserId: customersOnlyId);

        Assert.False(denied.IsSuccess);
        Assert.Contains("صلاحية", denied.Message);
        // لم يحدث التحويل: النوع قائم والسجل الناقص لم يُنشأ
        Assert.Equal('C', (await db.Parties.FindAsync(seed.CustomerId))!.PartyType);
        Assert.Null(await db.Suppliers.FindAsync(seed.CustomerId));
    }

    [Fact]
    public async Task ConvertSupplierToCombined_RequiresCustomersBitToo()
    {
        string marker = $"pu11-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int suppliersOnlyId = await SeedUserAsync(db, marker + "-s", AppPermissions.Suppliers);

        var denied = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.SupplierId, PartyType = 'B' }, actorUserId: suppliersOnlyId);

        Assert.False(denied.IsSuccess);
        Assert.Contains("صلاحية", denied.Message);
        Assert.Equal('S', (await db.Parties.FindAsync(seed.SupplierId))!.PartyType);
        Assert.Null(await db.Customers.FindAsync(seed.SupplierId));
    }

    // ══ 8) إضافة طرف مشترك: كلا البتّين — والعميل السريع من POS غير متأثر ══
    [Fact]
    public async Task AddCombinedParty_RequiresBothBits()
    {
        string marker = $"pu12-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int customersOnlyId = await SeedUserAsync(db, marker + "-c", AppPermissions.Customers);
        int bothId = await SeedUserAsync(db, marker + "-b", AppPermissions.Customers | AppPermissions.Suppliers);
        var svc = new clsPartyService(db);

        var denied = await svc.AddNewPartyWithRoleAsync(
            new DtoParty { PartyType = 'B', Name = $"combo-{marker}" }, actorUserId: customersOnlyId);
        Assert.False(denied.IsSuccess);
        Assert.Contains("صلاحية", denied.Message);
        Assert.False(await db.Parties.AnyAsync(p => p.Name == $"combo-{marker}"));

        var ok = await svc.AddNewPartyWithRoleAsync(
            new DtoParty { PartyType = 'B', Name = $"combo-{marker}" }, actorUserId: bothId);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.NotNull(await db.Customers.FindAsync(ok.Data));
        Assert.NotNull(await db.Suppliers.FindAsync(ok.Data));
    }

    [Fact]
    public async Task PosQuickAddCustomer_StillAllowedWithoutPermission()
    {
        string marker = $"pu13-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int cashierId = await SeedUserAsync(db, marker + "-k", AppPermissions.CashierRoleValue);

        var r = await new clsPartyService(db).AddNewPartyWithRoleAsync(
            new DtoParty { PartyType = 'C', Name = $"walkin-{marker}" },
            actorUserId: cashierId, fromPosQuickAdd: true);

        Assert.True(r.IsSuccess, r.Message);
        Assert.NotNull(await db.Customers.FindAsync(r.Data));
    }

    // ══ 9) مستخدم موقوف بدور مدير: لا يمرّ من أي بوابة (المساعد المشترك) ══
    [Fact]
    public async Task InactiveUserWithAdminRole_PassesNoGate()
    {
        string marker = $"pu14-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int inactiveId = await SeedUserAsync(db, marker + "-x", AppPermissions.AdminRoleValue, isActive: false);

        var r = await new clsPartyService(db).UpdatePartyAsync(
            new DtoParty { PartyId = seed.CustomerId, PartyType = 'C', Name = "hacked" }, actorUserId: inactiveId);

        Assert.False(r.IsSuccess);
        Assert.NotEqual("hacked", (await db.Parties.FindAsync(seed.CustomerId))!.Name);
    }
}
