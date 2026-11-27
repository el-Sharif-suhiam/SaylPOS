using Common.DTOs.RequestModels;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// بوابات صلاحيات خدمة المخزون (clsStockService) عند حدود التنفيذ الموثوق:
/// الواجهة تخفي الأزرار تجميلًا — الاستدعاء المباشر لطريقة عامة يجب ألا يتجاوز الصلاحية.
/// استلام/تسوية/نقل الدفعات تتطلب بت شاشة المخزون (AppPermissions.Inventory = 2):
///   • هوية مفقودة (≤ 0) / مستخدم غير موجود / مستخدم غير نشط / دور غير نشط → رفض
///     (GetUserRoleValueAsync تعيد -1 — لا يملك أي بت ولا هوية كاشير).
///   • كل رفض صلاحية يُدقَّق (StockReceiveDenied / StockAdjustmentDenied / StockTransferDenied)
///     على اتصال مستقل ينجو من تراجع المعاملة.
///   • الرفض يسبق المعاملة وأي إنشاء دفعة — لا أثر على Batches/Inventories/StockMovements.
/// ملاحظة FK: تدقيق رفض بهوية غير موجودة في Users (0 / سالبة / مجهولة) لا يُكتب —
/// قيد SystemLogs.UserId ← Users يرفضه، وLogRejectedAsync أفضل-جهد يبتلع الفشل
/// (حد معروف مشترك مع بوابة الاستلام القائمة — موثق في تقرير المهمة).
/// SellStockAsync ليست هنا: نقطة خصم للاختبار حصرًا — البيع الحقيقي مقيّد في clsCheckoutService.
/// كل الاختبارات تستدعي الخدمة مباشرة — بلا واجهة WinForms.
/// </summary>
[Collection("database")]
public class InventoryAuthorizationTests
{
    private readonly DatabaseFixture _fx;
    public InventoryAuthorizationTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>مستخدم بدور محدد (قيمة بت) — نشط افتراضيًا. الدور مُعاد استخدامه من
    /// TestSeed.GetOrCreateRoleAsync (RoleId tinyint — لا دور جديد لكل اختبار).</summary>
    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue, bool isActive = true)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>مستخدم بدور غير نشط — الدور صف خاص بهذا الاختبار (لا يُعاد استخدامه
    /// ولا يُعطَّل دور مشترك) باسم فريد يتفادى تصادم GetOrCreateRoleAsync.</summary>
    private static async Task<int> SeedUserWithInactiveRoleAsync(SaylDbContext db, string marker)
    {
        var role = new Role
        {
            Name = $"inactive-role-{Guid.NewGuid():N}"[..40],
            RoleValue = AppPermissions.Inventory,
            IsActive = false
        };
        db.Roles.Add(role);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>منتج مخزني بدفتين: مصدر بكمية وتكلفة، وهدف اختياري موجود —
    /// على المنتج المُبذَر في الاختبار نفسه (seed.ProductId) لا منتج منفصل.</summary>
    private async Task<(int SourceBatchId, int? TargetBatchId)> SeedBatchesAsync(
        int productId, string marker, int sourceQty = 6, decimal? sourceCost = 10m,
        string? targetNumber = null, int targetQty = 0, decimal? targetCost = null)
    {
        await using var db = _fx.CreateContext();
        var source = new Batch
        {
            ProductId = productId, BatchNumber = $"IA-SRC-{marker}",
            ExpiryDate = new DateOnly(2027, 3, 15), BatchQuantity = sourceQty,
            UnitCost = sourceCost, CreatedAt = DateTime.UtcNow
        };
        db.Batches.Add(source);
        int? targetId = null;
        if (targetNumber != null)
        {
            var target = new Batch
            {
                ProductId = productId, BatchNumber = targetNumber,
                ExpiryDate = new DateOnly(2027, 3, 15), BatchQuantity = targetQty,
                UnitCost = targetCost, CreatedAt = DateTime.UtcNow
            };
            db.Batches.Add(target);
            await db.SaveChangesAsync();
            targetId = target.BatchId;
        }
        else
        {
            await db.SaveChangesAsync();
        }
        return (source.BatchId, targetId);
    }

    private static DtoBatchTransferRequest TransferRequest(int userId, int sourceBatchId, string targetNumber, int qty) =>
        new() { SourceBatchId = sourceBatchId, TargetBatchNumber = targetNumber, Quantity = qty, UserId = userId };

    /// <summary>لقطة كاملة لحالة المنتج المخزنية: الدفعات + المخزون + الحركات —
    /// تُقارن قبل/بعد المحاولة غير المصرَّحة لتثبت «لا أثر إطلاقًا».</summary>
    private sealed record ProductSnapshot(
        List<(int BatchId, string BatchNumber, decimal Quantity, decimal? UnitCost, DateOnly? Expiry, bool Quarantined)> Batches,
        (decimal? Available, decimal? Reserved)? Inventory,
        List<(string MovementType, decimal Quantity, string SourceType, int? BatchId)> Movements);

    private async Task<ProductSnapshot> SnapshotAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        var batches = (await db.Batches.AsNoTracking()
            .Where(b => b.ProductId == productId)
            .OrderBy(b => b.BatchId)
            .ToListAsync())
            .Select(b => (b.BatchId, b.BatchNumber, b.BatchQuantity, b.UnitCost, b.ExpiryDate, b.IsQuarantined))
            .ToList();
        var inv = await db.Inventories.AsNoTracking().FirstOrDefaultAsync(i => i.ProductId == productId);
        var movements = (await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderBy(m => m.MovementId)
            .ToListAsync())
            .Select(m => (m.MovementType.ToString(), m.Quantity, m.SourceType, m.BatchId))
            .ToList();
        return new ProductSnapshot(
            batches,
            inv == null ? null : (inv.QuantityAvailable, inv.QuantityReserved),
            movements);
    }
    // ══ 1) دور المخزون (بت 2): النقل ينجح — مسار النجاح سليم ══
    [Fact]
    public async Task InventoryRole_CanTransfer_Success()
    {
        string marker = $"ia1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-inv", AppPermissions.Inventory); // بت 2 وحده
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(userId, sourceId, $"IA-NEW-{marker}", 6));
        Assert.True(r.IsSuccess, r.Message);

        await using var verify = _fx.CreateContext();
        var created = await verify.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == $"IA-NEW-{marker}");
        Assert.Equal(6, created.BatchQuantity);
    }

    // ══ 2) دور بلا بت المخزون (فواتير بيع فقط): رفض + تدقيق + لا أثر ══
    [Fact]
    public async Task SalesInvoicesOnlyRole_Transfer_Denied_Audited_NoChange()
    {
        string marker = $"ia2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-si", AppPermissions.SalesInvoices); // بت 16 — لا مخزون
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(userId, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAndDeniedAsync(seed.ProductId, userId, before);
    }

    // ══ 3) الكاشير (دور 0 — بلا بت): رفض + تدقيق + لا أثر ══
    [Fact]
    public async Task Cashier_Transfer_Denied_Audited_NoChange()
    {
        string marker = $"ia3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-cash", 0); // كاشير — لا يُمنح مخزونًا
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(userId, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAndDeniedAsync(seed.ProductId, userId, before);
    }

    // ══ 4) هوية مفقودة: UserId = 0 → رفض فوري بلا أي أثر ══
    [Fact]
    public async Task UserIdZero_Transfer_Rejected_NoChange()
    {
        string marker = $"ia4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(0, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAsync(seed.ProductId, before); // تدقيق الرفض لا يُكتب (FK) — حد موثق
    }

    // ══ 5) هوية سالبة → رفض فوري بلا أي أثر ══
    [Fact]
    public async Task NegativeUserId_Transfer_Rejected_NoChange()
    {
        string marker = $"ia5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(-7, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAsync(seed.ProductId, before);
    }

    // ══ 6) مستخدم غير موجود → رفض فوري بلا أي أثر ══
    [Fact]
    public async Task NonexistentUser_Transfer_Rejected_NoChange()
    {
        string marker = $"ia6-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(999_999_999, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAsync(seed.ProductId, before);
    }

    // ══ 7) مستخدم غير نشط → رفض + تدقيق بالفاعل + لا أثر ══
    [Fact]
    public async Task InactiveUser_Transfer_Denied_Audited_NoChange()
    {
        string marker = $"ia7-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-off", AppPermissions.Inventory, isActive: false);
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(userId, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAndDeniedAsync(seed.ProductId, userId, before);
    }

    // ══ 8) دور غير نشط → رفض + تدقيق بالفاعل + لا أثر ══
    [Fact]
    public async Task InactiveRole_Transfer_Denied_Audited_NoChange()
    {
        string marker = $"ia8-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserWithInactiveRoleAsync(db, marker + "-roff");
        var (sourceId, _) = await SeedBatchesAsync(seed.ProductId, $"{marker}-b");
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext())
            .TransferBatchAsync(TransferRequest(userId, sourceId, $"IA-NEW-{marker}", 6));

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAndDeniedAsync(seed.ProductId, userId, before);
    }

    /// <summary>لا أثر على بيانات المنتج + سجل رفض واحد بالفاعل الصحيح.</summary>
    private async Task AssertNoChangeAndDeniedAsync(int productId, int userId, ProductSnapshot before)
    {
        await AssertNoChangeAsync(productId, before);
        await using var verify = _fx.CreateContext();
        var log = Assert.Single(await verify.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "StockTransferDenied" && l.UserId == userId).ToListAsync());
        Assert.Contains("المخزون", log.Details);
        Assert.True((DateTime.UtcNow - (log.CreatedAt ?? DateTime.MinValue)).TotalMinutes < 5);
    }

    /// <summary>لا أثر: الدفعات/المخزون/الحركات مطابقة تمامًا للقطة قبل المحاولة
    /// (مقارنة بنيوية جزءًا جزءًا — قوائم السجل تقارن بالمرجع لا بالقيمة).</summary>
    private async Task AssertNoChangeAsync(int productId, ProductSnapshot before)
    {
        var after = await SnapshotAsync(productId);
        Assert.Equal(before.Batches, after.Batches);
        Assert.Equal(before.Inventory, after.Inventory);
        Assert.Equal(before.Movements, after.Movements);
    }
    // ══ 9) الاستلام: مصرَّح (بت 2) ينجح — البوابة القائمة سليمة ══
    [Fact]
    public async Task ReceiveStock_AuthorizedInventory_Passes()
    {
        string marker = $"ia9-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-inv", AppPermissions.Inventory);

        var r = await new clsStockService(_fx.CreateContext()).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = $"IA-RCV-{marker}",
            Quantity = 5,
            UnitCost = 8m
        }, userId);

        Assert.True(r.IsSuccess, r.Message);
        await using var verify = _fx.CreateContext();
        Assert.Equal(5, (await verify.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == $"IA-RCV-{marker}")).BatchQuantity);
    }

    // ══ 10) الاستلام: بلا بت المخزون → رفض + تدقيق StockReceiveDenied + لا أثر ══
    [Fact]
    public async Task ReceiveStock_Unauthorized_Rejected_Audited_NoChange()
    {
        string marker = $"ia10-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-si", AppPermissions.SalesInvoices);
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext()).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = seed.ProductId,
            BatchNumber = $"IA-RCV-{marker}",
            Quantity = 5
        }, userId);

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAsync(seed.ProductId, before);
        await using var verify = _fx.CreateContext();
        Assert.Single(await verify.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "StockReceiveDenied" && l.UserId == userId).ToListAsync());
    }

    // ══ 11) التسوية: مصرَّح (بت 2) ينجح — البوابة القائمة سليمة ══
    [Fact]
    public async Task AdjustStock_AuthorizedInventory_Passes()
    {
        string marker = $"ia11-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-inv", AppPermissions.Inventory);

        var r = await new clsStockService(_fx.CreateContext()).AdjustStockAsync(new DtoStockAdjustmentRequest
        {
            ProductId = seed.ProductId,
            Quantity = 3,
            IsIncrease = true,
            Reason = "ia-authorized",
            BatchNumber = $"IA-ADJ-{marker}",
            UserId = userId
        });

        Assert.True(r.IsSuccess, r.Message);
        await using var verify = _fx.CreateContext();
        Assert.Equal(3, (await verify.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber == $"IA-ADJ-{marker}")).BatchQuantity);
    }

    // ══ 12) التسوية: بلا بت المخزون → رفض + تدقيق StockAdjustmentDenied + لا أثر ══
    [Fact]
    public async Task AdjustStock_Unauthorized_Rejected_Audited_NoChange()
    {
        string marker = $"ia12-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-si", AppPermissions.SalesInvoices);
        var before = await SnapshotAsync(seed.ProductId);

        var r = await new clsStockService(_fx.CreateContext()).AdjustStockAsync(new DtoStockAdjustmentRequest
        {
            ProductId = seed.ProductId,
            Quantity = 3,
            IsIncrease = true,
            Reason = "ia-denied",
            UserId = userId
        });

        Assert.False(r.IsSuccess);
        Assert.Contains("صلاحية", r.Message);
        await AssertNoChangeAsync(seed.ProductId, before);
        await using var verify = _fx.CreateContext();
        Assert.Single(await verify.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "StockAdjustmentDenied" && l.UserId == userId).ToListAsync());
    }
}
