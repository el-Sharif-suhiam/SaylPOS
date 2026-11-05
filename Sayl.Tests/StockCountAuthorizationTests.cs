using Common.DTOs.ViewModel;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Stock count authorization gates at the trusted execution boundary (the service):
/// UI hides buttons by role — but a direct call to a public service method must not
/// bypass the required permission. Every operation that creates, modifies or posts a
/// stock count session (StartSessionAsync / SetCountedAsync / PostSessionAsync)
/// requires the Inventory screen bit (AppPermissions.Inventory = 2) — the same
/// policy clsStockService.AdjustStockAsync enforces internally (indirect calls
/// therefore use a consistent policy).
///
/// The gate reuses the established mechanism (clsPermissions.GetUserRoleValueAsync):
///   - userId ≤ 0 (missing identity) → denied
///   - non-existent user → denied
///   - inactive user or inactive role → denied
///   - role without the Inventory bit → denied
///
/// Zero-difference sessions still modify session state (lines get IsPosted=true),
/// so posting them without authorization is denied too.
///
/// All tests here call the service directly — no WinForms UI involved.
/// Every denied operation is verified to leave the database unchanged.
/// </summary>
[Collection("database")]
public class StockCountAuthorizationTests
{
    private readonly DatabaseFixture _fx;
    public StockCountAuthorizationTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── helpers (fresh context per step) ─────────────────

    private async Task<TestSeed> SeedAsync(string m)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, m);
    }

    /// <summary>مستخدم بدور محدد (قيمة بت) — نشط افتراضيًا.</summary>
    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue, bool isActive = true)
    {
        var role = new Role { Name = $"role-{marker}", RoleValue = roleValue, IsActive = true };
        db.Roles.Add(role);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>مخزون مبدئي لمنتج بلا دفعات (نظام يقرأ QuantityAvailable مباشرة).</summary>
    private static async Task SeedInventoryAsync(SaylDbContext db, int productId, int qty)
    {
        db.Inventories.Add(new Inventory { ProductId = productId, QuantityAvailable = qty, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>توريد مخزون بفاتورة شراء (ينشئ دفعة) — لازم لعجز الترحيل:
    /// نقصان التسوية يُخصم عبر الدفعات FEFO حصرًا (بلا دفعة = رفض «المخزون غير كافٍ»).</summary>
    private async Task SeedStockAsync(TestSeed seed, int productId, int qty, string batch)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new Common.DTOs.RequestModels.DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new Common.DTOs.RequestModels.DtoPurchaseItem { ProductId = productId, BatchNumber = batch, Quantity = qty, UnitCost = 10m } }
        });
        Assert.True(r.IsSuccess, r.Message);
    }

    private static async Task<int> SeedProductAsync(SaylDbContext db, string marker, string name)
    {
        var product = new Product
        {
            Name = $"{name}-{marker}",
            Type = "stock",
            BasePrice = 25m,
            CostPrice = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    /// <summary>فتح جلسة (مصرّح) — يفشل الاختبار إن رُفضت.</summary>
    private static async Task<(int sessionId, List<StockCountVM> lines)> StartAsync(SaylDbContext db, int userId)
    {
        var svc = new clsStockCountService(db);
        var start = await svc.StartSessionAsync(userId);
        Assert.True(start.IsSuccess, start.Message);
        return (start.Data, await svc.GetOpenSessionAsync());
    }

    /// <summary>عدّ كل سطور الجلسة المفتوحة (بقيمة معينة أو بالكتيب — فرق صفر).</summary>
    private static async Task CountAllLinesAsync(SaylDbContext db, int userId, Dictionary<int, int>? countedByProduct = null)
    {
        var svc = new clsStockCountService(db);
        foreach (var line in await svc.GetOpenSessionAsync())
        {
            int counted = countedByProduct != null && countedByProduct.TryGetValue(line.ProductId, out int v)
                ? v : line.BookQuantity;
            var r = await svc.SetCountedAsync(line.CountId, counted, userId);
            Assert.True(r.IsSuccess, r.Message);
        }
    }

    /// <summary>إغلاق أي جلسة مفتوحة (عدّ بالكتيب + ترحيل) — حاجز ضد تسرب جلسة بين الاختبارات.</summary>
    private async Task CloseOpenSessionAsync(int authorizedUserId)
    {
        await using var db = _fx.CreateContext();
        var svc = new clsStockCountService(db);
        if (!await svc.HasOpenSessionAsync())
            return;
        await CountAllLinesAsync(db, authorizedUserId);
        var post = await svc.PostSessionAsync(authorizedUserId);
        Assert.True(post.IsSuccess, post.Message);
    }

    // ══ 1) المدير (كل البتات بما فيها المخزون): فتح/عدّ/ترحيل كامل بنجاح ══
    [Fact]
    public async Task Admin_FullCycle_Succeeds()
    {
        string m = $"sca1-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int sessionId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m); // مدير: 4095 يشمل بت المخزون (2)
            await SeedInventoryAsync(db, seed.ProductId, 10);
            (sessionId, _) = await StartAsync(db, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);
            // عدّ منتج الاختبار = 12 وبقية سطور الجلسة (منتجات اختبارات سابقة) بالكتيب
            await CountAllLinesAsync(db, seed.UserId, new() { [seed.ProductId] = 12 });
        }

        await using (var db = _fx.CreateContext())
        {
            var post = await new clsStockCountService(db).PostSessionAsync(seed.UserId);
            Assert.True(post.IsSuccess, post.Message);
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId);
            Assert.Equal(12, inv.QuantityAvailable); // فائض +2 مُرحّل
        }
    }

    // ══ 2) موظف ببت المخزون (2) فقط: الدورة الكاملة بنجاح ══
    [Fact]
    public async Task EmployeeWithInventoryBit_FullCycle_Succeeds()
    {
        string m = $"sca2-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int clerkId, sessionId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedStockAsync(seed, seed.ProductId, 10, $"B-{m}"); // توريد بدفعة — عجز الترحيل يُخصم عبر الدفعات
            clerkId = await SeedUserAsync(db, m + "-clerk", AppPermissions.Inventory); // بت 2 فقط
            (sessionId, _) = await StartAsync(db, clerkId);
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);
            // عدّ منتج الاختبار = 8 وبقية سطور الجلسة بالكتيب (فرق صفري)
            await CountAllLinesAsync(db, clerkId, new() { [seed.ProductId] = 8 });
        }

        await using (var db = _fx.CreateContext())
        {
            var post = await new clsStockCountService(db).PostSessionAsync(clerkId);
            Assert.True(post.IsSuccess, post.Message);
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId);
            Assert.Equal(8, inv.QuantityAvailable); // عجز −2 مُرحّل
        }
    }

    // ══ 3) إنشاء جلسة بلا بت المخزون — رفض وقاعدة بلا أثر ══
    [Fact]
    public async Task StartSession_WithoutInventoryBit_Denied_DatabaseUnchanged()
    {
        string m = $"sca3-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int outsiderId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            outsiderId = await SeedUserAsync(db, m + "-out", AppPermissions.Products); // بت 1 فقط — لا مخزون
        }

        await using (var db = _fx.CreateContext())
        {
            int baseline = await db.StockCounts.AsNoTracking().CountAsync();

            var start = await new clsStockCountService(db).StartSessionAsync(outsiderId);
            Assert.False(start.IsSuccess);
            Assert.Contains("صلاحية", start.Message);

            // قاعدة بلا أثر: لا سطور جرد جديدة
            Assert.Equal(baseline, await db.StockCounts.AsNoTracking().CountAsync());
        }
    }

    // ══ 4) تحديث كمية معدودة بلا بت المخزون — رفض والسطر بلا تغيير ══
    [Fact]
    public async Task SetCounted_WithoutInventoryBit_Denied_DatabaseUnchanged()
    {
        string m = $"sca4-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int outsiderId, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            outsiderId = await SeedUserAsync(db, m + "-out", AppPermissions.Products);
            (_, var lines) = await StartAsync(db, seed.UserId); // جلسة بمصرّح
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsStockCountService(db).SetCountedAsync(lineId, 5, outsiderId);
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);

            // السطر لم يُلمس: بلا معدود وبلا لحظة عد
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.Null(line.CountedQuantity);
            Assert.Null(line.CountedAt);
            Assert.Null(line.BookQuantityAtCount);
        }

        // تنظيف: لا تتسرب جلسة مفتوحة لاختبار لاحق
        await CloseOpenSessionAsync(seed.UserId);
    }

    // ══ 5) ترحيل بلا بت المخزون — رفض والجلسة والرصيد كما كانا ══
    [Fact]
    public async Task PostSession_WithoutInventoryBit_Denied_DatabaseUnchanged()
    {
        string m = $"sca5-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int outsiderId, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            outsiderId = await SeedUserAsync(db, m + "-out", AppPermissions.Products);
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        // عدّ مصرّح بفرق حقيقي (+2)
        await using (var db = _fx.CreateContext())
        {
            Assert.True((await new clsStockCountService(db).SetCountedAsync(lineId, 12, seed.UserId)).IsSuccess);
        }

        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsStockCountService(db).PostSessionAsync(outsiderId);
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);

            // القاعدة بلا تغيير: السطر غير مرحّل والرصيد كما كان
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.False(line.IsPosted);
            Assert.Null(line.PostedMovementId);
            Assert.Equal(0, await db.StockMovements.AsNoTracking()
                .CountAsync(mv => mv.SourceType == "ManualAdjustment" && mv.SourceId == lineId));
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId);
            Assert.Equal(10, inv.QuantityAvailable);
        }

        // نفس الفاعل غير المصرّح محاولته مرة أخرى — لا فرق؛ ثم ترحيل مصرّح يعمل
        await using (var db = _fx.CreateContext())
        {
            var stillDenied = await new clsStockCountService(db).PostSessionAsync(outsiderId);
            Assert.False(stillDenied.IsSuccess);
        }

        await using (var db = _fx.CreateContext())
        {
            // عدّ بقية سطور الجلسة بالكتيب (فرق صفري) قبل الترحيل المصرّح
            await CountAllLinesAsync(db, seed.UserId, new() { [seed.ProductId] = 12 });
            var ok = await new clsStockCountService(db).PostSessionAsync(seed.UserId);
            Assert.True(ok.IsSuccess, ok.Message);
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId);
            Assert.Equal(12, inv.QuantityAvailable);
        }
    }

    // ══ 6) موظف غير نشط (حتى ببت المخزون) — رفض في كل العمليات ══
    [Fact]
    public async Task InactiveEmployee_Denied()
    {
        string m = $"sca6-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int inactiveId, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            inactiveId = await SeedUserAsync(db, m + "-inact", AppPermissions.Inventory, isActive: false);
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);

            var start = await svc.StartSessionAsync(inactiveId);
            Assert.False(start.IsSuccess);
            Assert.Contains("صلاحية", start.Message);

            var set = await svc.SetCountedAsync(lineId, 5, inactiveId);
            Assert.False(set.IsSuccess);

            var post = await svc.PostSessionAsync(inactiveId);
            Assert.False(post.IsSuccess);

            // لا أثر: السطر بلا معدود وغير مرحّل
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.Null(line.CountedQuantity);
            Assert.False(line.IsPosted);
        }

        await CloseOpenSessionAsync(seed.UserId);
    }

    // ══ 7) فاعل غير موجود (معرف مستخدم لا يوجد) — رفض ══
    [Fact]
    public async Task NonExistentActor_Denied()
    {
        string m = $"sca7-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int ghostId = 999999, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);

            var start = await svc.StartSessionAsync(ghostId);
            Assert.False(start.IsSuccess);

            var set = await svc.SetCountedAsync(lineId, 5, ghostId);
            Assert.False(set.IsSuccess);

            var post = await svc.PostSessionAsync(ghostId);
            Assert.False(post.IsSuccess);

            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.Null(line.CountedQuantity);
            Assert.False(line.IsPosted);
        }

        await CloseOpenSessionAsync(seed.UserId);
    }

    // ══ 8) هوية فاعل مفقودة (0 أو سالبة) — رفض ══
    [Fact]
    public async Task MissingActorIdentity_Denied()
    {
        string m = $"sca8-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);

            foreach (int missing in new[] { 0, -1 })
            {
                var start = await svc.StartSessionAsync(missing);
                Assert.False(start.IsSuccess);

                var set = await svc.SetCountedAsync(lineId, 5, missing);
                Assert.False(set.IsSuccess);

                var post = await svc.PostSessionAsync(missing);
                Assert.False(post.IsSuccess);
            }

            // لا أثر بعد كل المحاولات
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.Null(line.CountedQuantity);
            Assert.False(line.IsPosted);
        }

        await CloseOpenSessionAsync(seed.UserId);
    }

    // ══ 9) جلسة بفرق صفري: ترحيل غير مصرّح يُرفض — والمصرّح يرحّل بنجاح ══
    // (الفرق الصفري يُقفل السطور IsPosted=true — تعديل حالة يستوجب التصريح)
    [Fact]
    public async Task ZeroDifferenceSession_UnauthorizedPost_Denied_AuthorizedPost_Succeeds()
    {
        string m = $"sca9-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int outsiderId, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            outsiderId = await SeedUserAsync(db, m + "-out", AppPermissions.Products);
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        // عدّ بالكتيب الحي — فرق صفري لكل السطور
        await using (var db = _fx.CreateContext())
            await CountAllLinesAsync(db, seed.UserId);

        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsStockCountService(db).PostSessionAsync(outsiderId);
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);

            // السطور ما زالت مفتوحة (غير مرحّلة) — لا قفل بلا تصريح
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.False(line.IsPosted);
        }

        await using (var db = _fx.CreateContext())
        {
            var ok = await new clsStockCountService(db).PostSessionAsync(seed.UserId);
            Assert.True(ok.IsSuccess, ok.Message);

            // فرق صفري: سطر مرحّل بلا أي حركة تسوية
            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.True(line.IsPosted);
            Assert.Null(line.PostedMovementId);
            Assert.Equal(0, await db.StockMovements.AsNoTracking()
                .CountAsync(mv => mv.SourceType == "ManualAdjustment" && mv.SourceId == lineId));
            var inv = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId);
            Assert.Equal(10, inv.QuantityAvailable);
        }
    }

    // ══ 10) مباشر بلا واجهة: موظف ببت المنتجات (1) لا يستطيع ولا عدًّا ولا ترحيلًا ══
    // (كل اختبارات هذا الصف استدعاء مباشر للخدمة — هذه تحديدًا تُثبت أن نداء
    //  الخدمة مباشرة من كود/إضافة لا يتجاوز بوابة الصلاحية)
    [Fact]
    public async Task DirectServiceCall_ProductsBitOnly_CannotCountOrPost()
    {
        string m = $"sca10-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int clerkId, lineId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, m);
            await SeedInventoryAsync(db, seed.ProductId, 10);
            clerkId = await SeedUserAsync(db, m + "-clerk", AppPermissions.Products); // بت 1 فقط
            (_, var lines) = await StartAsync(db, seed.UserId);
            lineId = lines.Single(l => l.ProductId == seed.ProductId).CountId;
        }

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsStockCountService(db);

            var set = await svc.SetCountedAsync(lineId, 99, clerkId);
            Assert.False(set.IsSuccess);

            var post = await svc.PostSessionAsync(clerkId);
            Assert.False(post.IsSuccess);

            var line = await db.StockCounts.AsNoTracking().SingleAsync(c => c.CountId == lineId);
            Assert.Null(line.CountedQuantity);
            Assert.False(line.IsPosted);
            Assert.Equal(10, (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seed.ProductId)).QuantityAvailable);
        }

        await CloseOpenSessionAsync(seed.UserId);
    }
}
