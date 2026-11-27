using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// المرحلة 6 — إدارة وحدات المنتج من الخدمة (محرر المنتج): إضافة وحدات بديلة
/// بتحقق كامل (الاسم ≤ 20 والمفرد للمنتج، المعامل أكبر من صفر بـ 6 خانات
/// كحد أقصى، السعر ≥ 0)، وحماية التاريخ: تغيير معامل وحدة مستخدمة في فواتير
/// يُرفض (اللقطة النصية تجعل إعادة التسمية آمنة فتُسمح)، والتعطيل مسار الحياة
/// الآمن للوحدة المستخدمة، والحذف الصارم للغير مرجعية حصرًا، والوحدة الأساس
/// لا تُعطَّل ولا تُحذف ولا تُعدل من هنا. ووحدة الأساس إلزامية للمنتج المخزني.
/// </summary>
[Collection("database")]
public class ProductUnitManagementTests
{
    private readonly DatabaseFixture _fx;
    public ProductUnitManagementTests(DatabaseFixture fx) => _fx = fx;

    private async Task<TestSeed> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, marker);
    }

    /// <summary>منتج مخزني بوحدة أساس مسماة + ضمان صف الأساس عبر مسار الإقلاع
    /// (EnsureProductUnitsSchemaAsync — نفس سلوك الإنتاج).</summary>
    private async Task<int> SeedProductWithUnitAsync(TestSeed seed, string name, string unit)
    {
        await using var db = _fx.CreateContext();
        var product = new Product
        {
            Name = $"{name}-{Guid.NewGuid():N}"[..20],
            Type = "stock",
            Unit = unit,
            BasePrice = 20m,
            CostPrice = 10m,
            ReorderLevel = 0m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        await new clsProductService(db).EnsureProductUnitsSchemaAsync();
        return product.ProductId;
    }

    private async Task ReceiveAsync(TestSeed seed, int productId, decimal qty, string batch)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = productId,
            BatchNumber = batch,
            Quantity = qty,
            UnitCost = 10m
        }, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);
    }

    /// <summary>بيع بوحدة إدخال — يعيد النتيجة بلا تأكيد (الرفض مقصود في بعض الاختبارات).</summary>
    private static async Task<Common.Results.Result<int>> TrySaleWithUnitAsync(
        SaylDbContext db, TestSeed seed, int productId, decimal enteredQty, string unitName, decimal factor)
    {
        decimal baseQty = Math.Round(enteredQty * factor, 3, MidpointRounding.AwayFromZero);
        return await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = productId,
                    ProductName = "x",
                    Quantity = baseQty,
                    EnteredQuantity = enteredQty,
                    UnitName = unitName,
                    UnitFactorToBase = factor
                }
            },
            Payment = new DtoPaymentInfo { Amount = 20m * baseQty, Method = "cash" }
        });
    }

    private async Task<int> SaleWithUnitAsync(TestSeed seed, int productId,
        decimal enteredQty, string unitName, decimal factor)
    {
        await using var db = _fx.CreateContext();
        var r = await TrySaleWithUnitAsync(db, seed, productId, enteredQty, unitName, factor);
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private async Task<decimal> AvailableAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityAvailable ?? 0m;
    }

    // ══ 1) إضافة وحدتين بديلتين (Pack=6, Carton=24) — الأساس أولًا في العرض ══

    [Fact]
    public async Task AddAlternateUnits_AppearInGetProductUnits_BaseFirst()
    {
        string marker = $"pu1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "عرض", "حبة");

        await using (var db = _fx.CreateContext())
        {
            var svc = new clsProductService(db);
            var pack = await svc.AddProductUnitAsync(productId, "Pack", 6m, pricePerUnit: null, actorUserId: seed.UserId);
            Assert.True(pack.IsSuccess, pack.Message);
            var carton = await svc.AddProductUnitAsync(productId, "Carton", 24m, pricePerUnit: 480m, actorUserId: seed.UserId);
            Assert.True(carton.IsSuccess, carton.Message);
        }

        await using var verify = _fx.CreateContext();
        var units = await new clsProductService(verify).GetProductUnitsAsync(productId);

        // الأساس أولًا ثم البدائل بالاسم — والنشطة جميعها
        Assert.Equal(3, units.Count);
        Assert.True(units[0].IsBase);
        Assert.Equal("حبة", units[0].Name);
        Assert.Equal(1m, units[0].FactorToBase);
        Assert.Equal("Carton", units[1].Name);
        Assert.Equal(24m, units[1].FactorToBase);
        Assert.Equal("Pack", units[2].Name);
        Assert.Equal(6m, units[2].FactorToBase);
        Assert.All(units, u => Assert.True(u.IsActive));

        // السعر المخزن للوحدة يُعاد كما هو في وضع التحرير (includeInactive)
        var editorUnits = await new clsProductService(verify).GetProductUnitsAsync(productId, includeInactive: true);
        Assert.Equal(480m, editorUnits.Single(u => u.Name == "Carton").PricePerUnit);
    }

    // ══ 2) تكرار الاسم لنفس المنتج مرفوض — والاسم نفسه لمنتج آخر مسموح ══

    [Fact]
    public async Task DuplicateName_SameProductRejected_DifferentProductAllowed()
    {
        string marker = $"pu2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productA = await SeedProductWithUnitAsync(seed, "منتج-أ", "حبة");
        int productB = await SeedProductWithUnitAsync(seed, "منتج-ب", "قطعة");

        await using var db = _fx.CreateContext();
        var svc = new clsProductService(db);

        var first = await svc.AddProductUnitAsync(productA, "كرتون", 12m, actorUserId: seed.UserId);
        Assert.True(first.IsSuccess, first.Message);

        // التكرار لنفس المنتج مرفوض — واسم صف الأساس («حبة») محجوز أيضًا
        var dup = await svc.AddProductUnitAsync(productA, "كرتون", 6m, actorUserId: seed.UserId);
        Assert.False(dup.IsSuccess);
        var baseDup = await svc.AddProductUnitAsync(productA, "حبة", 2m, actorUserId: seed.UserId);
        Assert.False(baseDup.IsSuccess);

        // الاسم نفسه لمنتج آخر مسموح (الوحدات خاصة بالمنتج — لا كتالوج عام)
        var other = await svc.AddProductUnitAsync(productB, "كرتون", 10m, actorUserId: seed.UserId);
        Assert.True(other.IsSuccess, other.Message);
    }

    // ══ 3) التحقق: اسم فارغ/أطول من 20، معامل صفر/سالب/7 خانات، سعر سالب ══

    [Fact]
    public async Task Validation_EmptyName_LongName_ZeroOrNegativeFactor_7dpFactor_NegativePrice_Rejected()
    {
        string marker = $"pu3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "تحقق", "حبة");

        await using var db = _fx.CreateContext();
        var svc = new clsProductService(db);

        Assert.False((await svc.AddProductUnitAsync(productId, "", 2m, actorUserId: seed.UserId)).IsSuccess);
        Assert.False((await svc.AddProductUnitAsync(productId, "   ", 2m, actorUserId: seed.UserId)).IsSuccess);
        Assert.False((await svc.AddProductUnitAsync(productId, "اسم-وحدة-طويل-جداً-يتجاوز", 2m, actorUserId: seed.UserId)).IsSuccess); // > 20
        Assert.False((await svc.AddProductUnitAsync(productId, "صفر", 0m, actorUserId: seed.UserId)).IsSuccess);
        Assert.False((await svc.AddProductUnitAsync(productId, "سالب", -1m, actorUserId: seed.UserId)).IsSuccess);
        // دقة مفرطة: 7 منازل عشرية تُرفض لا تُقرَّب
        Assert.False((await svc.AddProductUnitAsync(productId, "دقة", 0.1234567m, actorUserId: seed.UserId)).IsSuccess);
        Assert.False((await svc.AddProductUnitAsync(productId, "سعر", 2m, pricePerUnit: -0.5m, actorUserId: seed.UserId)).IsSuccess);

        // المقابل: 6 خانات مقبولة
        var ok = await svc.AddProductUnitAsync(productId, "رطل", 0.453592m, actorUserId: seed.UserId);
        Assert.True(ok.IsSuccess, ok.Message);
    }

    // ══ 4) المعامل: وحدة غير مستخدمة تتقبل التغيير — ومستخدمة تُرفض،
    //        وإعادة التسمية لوحدة مستخدمة تنجح واللقطة القديمة كما هي ══

    [Fact]
    public async Task UpdateFactor_UnusedSucceeds_UsedRejected_RenameAllowed_SnapshotUnchanged()
    {
        string marker = $"pu4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "معامل", "حبة");

        int unitId;
        await using (var db = _fx.CreateContext())
        {
            var added = await new clsProductService(db).AddProductUnitAsync(productId, "كيس", 5m, actorUserId: seed.UserId);
            Assert.True(added.IsSuccess, added.Message);
            unitId = added.Data;
        }

        // غير مستخدمة: تغيير المعامل ينحفظ
        await using (var db = _fx.CreateContext())
        {
            var upd = await new clsProductService(db).UpdateProductUnitAsync(unitId, "كيس", 6m, actorUserId: seed.UserId);
            Assert.True(upd.IsSuccess, upd.Message);
        }

        await ReceiveAsync(seed, productId, 30m, $"B-{marker}");
        int saleId = await SaleWithUnitAsync(seed, productId, 2m, "كيس", 6m); // 12 أساس

        // لقطة السطر قبل التعديل — الفهارس المرجعية (ProductUnitId) تبقى NULL
        // في مسار الإقلاع الحالي (اللقطة النصية هي المحمَّل — المرحلة 5)،
        // لذا كشف الاستخدام عند الخدمة يتم بمطابقة الاسم النصي
        await using (var db = _fx.CreateContext())
        {
            var line = await db.InvoiceItems.AsNoTracking().SingleAsync(i => i.InvoiceId == saleId);
            Assert.Equal("كيس", line.UnitName);
            Assert.Equal(6m, line.UnitFactorToBase);
            Assert.Null(line.ProductUnitId);
        }

        // مستخدمة: تغيير المعامل مرفوض برسالة توضح أن التاريخ سيتغير معناه
        await using (var db = _fx.CreateContext())
        {
            var factorTry = await new clsProductService(db).UpdateProductUnitAsync(unitId, "كيس", 7m, actorUserId: seed.UserId);
            Assert.False(factorTry.IsSuccess);
            Assert.Contains("فواتير", factorTry.Message);
        }

        // إعادة التسمية لوحدة مستخدمة مسموحة (اللقطات نصية — الصفوف القديمة تبقى)
        await using (var db = _fx.CreateContext())
        {
            var rename = await new clsProductService(db).UpdateProductUnitAsync(unitId, "كيس-كبير", 6m, actorUserId: seed.UserId);
            Assert.True(rename.IsSuccess, rename.Message);
        }

        // اللقطة المخزنة بِيعت بالاسم القديم والمعامل القديم — لم تُعاد كتابتها
        await using (var verify = _fx.CreateContext())
        {
            var line = await verify.InvoiceItems.AsNoTracking().SingleAsync(i => i.InvoiceId == saleId);
            Assert.Equal("كيس", line.UnitName);
            Assert.Equal(6m, line.UnitFactorToBase);
            Assert.Equal(2m, line.EnteredQuantity);
        }
    }

    // ══ 5) تعطيل وحدة مستخدمة ينجح — والبيع بها بعده يرفضه الخادم ══

    [Fact]
    public async Task DeactivateUsedUnit_Succeeds_ThenCheckoutWithItRejected()
    {
        string marker = $"pu5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "تعطيل", "حبة");

        int unitId;
        await using (var db = _fx.CreateContext())
        {
            var added = await new clsProductService(db).AddProductUnitAsync(productId, "كرتون", 12m, actorUserId: seed.UserId);
            Assert.True(added.IsSuccess, added.Message);
            unitId = added.Data;
        }

        await ReceiveAsync(seed, productId, 60m, $"B-{marker}");
        await SaleWithUnitAsync(seed, productId, 1m, "كرتون", 12m); // وحدة مستخدمة

        await using (var db = _fx.CreateContext())
        {
            var deact = await new clsProductService(db).DeactivateProductUnitAsync(unitId, seed.UserId);
            Assert.True(deact.IsSuccess, deact.Message);
        }

        // الوحدة المعطلة لا تُقبل في معاملات جديدة (ResolveUnitAsync خادميًا)
        await using (var db = _fx.CreateContext())
        {
            var sale = await ProductUnitManagementTests.TrySaleWithUnitAsync(db, seed, productId, 1m, "كرتون", 12m);
            Assert.False(sale.IsSuccess);
        }

        // لا مسّ للمخزون بعد الرفض: 60 − 12 = 48
        Assert.Equal(48m, await AvailableAsync(productId));
    }

    // ══ 6) الوحدة الأساس: تعطيلها مرفوض وحذفها مرفوض وتعديلها من هنا مرفوض ══

    [Fact]
    public async Task BaseRow_DeactivateDeleteAndUpdate_Refused()
    {
        string marker = $"pu6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "أساس", "حبة");

        await using var db = _fx.CreateContext();
        int baseId = await db.ProductUnits.AsNoTracking()
            .Where(u => u.ProductId == productId && u.IsBase)
            .Select(u => u.ProductUnitId).SingleAsync();
        var svc = new clsProductService(db);

        Assert.False((await svc.DeactivateProductUnitAsync(baseId, seed.UserId)).IsSuccess);
        Assert.False((await svc.DeleteProductUnitAsync(baseId, seed.UserId)).IsSuccess);
        Assert.False((await svc.UpdateProductUnitAsync(baseId, "حبة-جديدة", 5m, actorUserId: seed.UserId)).IsSuccess);

        // بقيت سليمة
        var baseRow = await db.ProductUnits.AsNoTracking().SingleAsync(u => u.ProductUnitId == baseId);
        Assert.True(baseRow.IsActive);
        Assert.Equal(1m, baseRow.FactorToBase);
        Assert.Equal("حبة", baseRow.Name);
    }

    // ══ 7) الحذف: غير مرجعية يُحذف فعليًا — ومرجعية يُرفض ويُوجَّه للتعطيل ══

    [Fact]
    public async Task Delete_UnreferencedSucceeds_ReferencedRefused_PointedToDeactivation()
    {
        string marker = $"pu7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "حذف", "حبة");

        int unusedId, usedId;
        await using (var db = _fx.CreateContext())
        {
            var svc = new clsProductService(db);
            var u1 = await svc.AddProductUnitAsync(productId, "علبة", 6m, actorUserId: seed.UserId);
            var u2 = await svc.AddProductUnitAsync(productId, "كرتون", 12m, actorUserId: seed.UserId);
            Assert.True(u1.IsSuccess && u2.IsSuccess);
            unusedId = u1.Data;
            usedId = u2.Data;
        }

        await ReceiveAsync(seed, productId, 60m, $"B-{marker}");
        await SaleWithUnitAsync(seed, productId, 1m, "كرتون", 12m); // «كرتون» صارت مرجعية

        // غير المرجعية: حذف صارم ينجح
        await using (var db = _fx.CreateContext())
        {
            var del = await new clsProductService(db).DeleteProductUnitAsync(unusedId, seed.UserId);
            Assert.True(del.IsSuccess, del.Message);
        }

        // المرجعية: الحذف مرفوض والرسالة توجّه للتعطيل
        await using (var db = _fx.CreateContext())
        {
            var del = await new clsProductService(db).DeleteProductUnitAsync(usedId, seed.UserId);
            Assert.False(del.IsSuccess);
            Assert.Contains("عطّل", del.Message);
        }

        await using var verify = _fx.CreateContext();
        Assert.False(await verify.ProductUnits.AsNoTracking().AnyAsync(u => u.ProductUnitId == unusedId));
        Assert.True(await verify.ProductUnits.AsNoTracking().AnyAsync(u => u.ProductUnitId == usedId));
    }

    // ══ 8) وحدة الأساس إلزامية للمنتج المخزني — والأنواع الأخرى حرة النص ══

    [Fact]
    public async Task EmptyBaseUnit_StockRejected_ServiceAllowed()
    {
        string marker = $"pu8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);

        await using var db = _fx.CreateContext();
        var svc = new clsProductService(db);

        var stockNoUnit = await svc.AddNewProductAsync(new Common.DTOs.DtoProduct
        {
            Name = $"مخزني-{marker}",
            Type = "stock",
            Unit = null,
            BasePrice = 20m,
            CostPrice = 10m
        }, seed.UserId);
        Assert.False(stockNoUnit.IsSuccess);

        // مسافة بيضاء حصرًا = فارغ أيضًا
        var stockWhitespace = await svc.AddNewProductAsync(new Common.DTOs.DtoProduct
        {
            Name = $"مخزني2-{marker}",
            Type = "stock",
            Unit = "   ",
            BasePrice = 20m,
            CostPrice = 10m
        }, seed.UserId);
        Assert.False(stockWhitespace.IsSuccess);

        // النوع الخدمي حر النص (السلوك القائم)
        var serviceNoUnit = await svc.AddNewProductAsync(new Common.DTOs.DtoProduct
        {
            Name = $"خدمي-{marker}",
            Type = "service",
            Unit = null,
            BasePrice = 50m,
            CostPrice = 0m
        }, seed.UserId);
        Assert.True(serviceNoUnit.IsSuccess, serviceNoUnit.Message);
    }
}
