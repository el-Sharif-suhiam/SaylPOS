using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// الفاتورة المختلطة الوحدات (سلة: 1 كرتون معامل 12 + 3 حبات = سطران لنفس
/// المنتج): إرجاع وحدة الأساس مسموح على الجزء الإرثي فقط — السعة = مجموع
/// السطور بلا لقطة ناقص المرتجع الأساسي السابق، وسطر المرتجع يُخزَّن بلا
/// لقطة. إرجاع اللقطة يسحب من سطور وحدتها. الفاتورة بلقطات فقط ترفض
/// إرجاع الأساس كما كان.
/// </summary>
[Collection("database")]
public class MixedUnitReturnTests
{
    private readonly DatabaseFixture _fx;
    public MixedUnitReturnTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة (نمط MultiUnitTests — سياق جديد لكل عملية) ─────────────────

    private async Task<TestSeed> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, marker);
    }

    private async Task AddUnitAsync(TestSeed seed, string name, decimal factor)
    {
        await using var db = _fx.CreateContext();
        db.ProductUnits.Add(new ProductUnit
        {
            ProductId = seed.ProductId,
            Name = name,
            FactorToBase = factor,
            IsBase = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
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

    /// <summary>بيع مختلط: 1 كرتون (12 أساس) + 3 حبات (أساس) — سطران بنفس السعر
    /// الأساس 20. الإجمالي 300 نقدًا.</summary>
    private async Task<int> MixedSaleAsync(TestSeed seed, string unitName, decimal factor)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = seed.ProductId, ProductName = "x", Quantity = factor,
                    EnteredQuantity = 1m, UnitName = unitName, UnitFactorToBase = factor
                },
                new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 3m }
            },
            Payment = new DtoPaymentInfo { Amount = 20m * (factor + 3m), Method = "cash" }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>بيع بلقطة فقط (كرتون واحد) — بلا سطر إرثي.</summary>
    private async Task<int> SnapshotOnlySaleAsync(TestSeed seed, string unitName, decimal factor)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = seed.ProductId, ProductName = "x", Quantity = factor,
                    EnteredQuantity = 1m, UnitName = unitName, UnitFactorToBase = factor
                }
            },
            Payment = new DtoPaymentInfo { Amount = 20m * factor, Method = "cash" }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>مرتجع بوحدة الأساس (بلا لقطة) — الكمية المُرسلة بالأساس.</summary>
    private async Task<Common.Results.Result<int>> ReturnBaseAsync(
        TestSeed seed, int saleId, int productId, decimal qty)
    {
        await using var db = _fx.CreateContext();
        return await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = productId, Quantity = qty } }
        });
    }

    /// <summary>مرتجع بوحدة لقطة السطر الأصلي.</summary>
    private async Task<Common.Results.Result<int>> ReturnWithUnitAsync(
        TestSeed seed, int saleId, int productId, decimal enteredQty, string unitName, decimal factor)
    {
        decimal baseQty = Math.Round(enteredQty * factor, 3, MidpointRounding.AwayFromZero);
        await using var db = _fx.CreateContext();
        return await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleId,
            UserId = seed.UserId,
            Items =
            {
                new DtoReturnItem
                {
                    ProductId = productId,
                    Quantity = baseQty,
                    EnteredQuantity = enteredQty,
                    UnitName = unitName,
                    UnitFactorToBase = factor
                }
            }
        });
    }

    private async Task<decimal> AvailableAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityAvailable ?? 0m;
    }

    // ══ 1) إرجاع الأساس على فاتورة مختلطة: السعة = الجزء الإرثي فقط وسطر المرتجع بلا لقطة ══

    [Fact]
    public async Task MixedInvoice_BaseReturn_LimitedToLegacyLines_StoredWithoutSnapshot()
    {
        string marker = $"mix1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");
        int saleId = await MixedSaleAsync(seed, "كرتون", 12m); // 12 + 3 = 15 أساس

        // طلب 4 أساس على سعة إرثية 3 → رفض والكمية المتبقية مذكورة
        var over = await ReturnBaseAsync(seed, saleId, seed.ProductId, 4m);
        Assert.False(over.IsSuccess);
        Assert.Contains("3", over.Message);

        // إرجاع 2 أساس من الجزء الإرثي → نجاح واسترداد 2 × 20 = 40
        var two = await ReturnBaseAsync(seed, saleId, seed.ProductId, 2m);
        Assert.True(two.IsSuccess, two.Message);
        Assert.Equal(7m, await AvailableAsync(seed.ProductId)); // 5 + 2

        await using (var db = _fx.CreateContext())
        {
            var ret = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == two.Data);
            Assert.Equal(-40.00m, ret.NetAmount);
            var line = Assert.Single(ret.InvoiceItems);
            Assert.Equal(2m, line.Quantity);
            Assert.Null(line.UnitName);          // سطر المرتجع بلا لقطة — إرثي
            Assert.Null(line.UnitFactorToBase);
            Assert.Null(line.EnteredQuantity);
        }

        // 2 أخرى فوق المتبقي (سعة 3 − 2 = 1) → رفض
        var overAgain = await ReturnBaseAsync(seed, saleId, seed.ProductId, 2m);
        Assert.False(overAgain.IsSuccess);
        Assert.Contains("1", overAgain.Message);

        // آخر أساس إرثي يُرتجع → الجزء الأساس مكتمل
        var last = await ReturnBaseAsync(seed, saleId, seed.ProductId, 1m);
        Assert.True(last.IsSuccess, last.Message);
        Assert.Equal(8m, await AvailableAsync(seed.ProductId));

        // إرجاع أساس إضافي بعد استنفاد السعة الإرثية → رفض
        var exhausted = await ReturnBaseAsync(seed, saleId, seed.ProductId, 1m);
        Assert.False(exhausted.IsSuccess);

        // والجزء المُسمّى ما زال يُرتجع بوحدته (كرتون كامل = 12 أساس)
        var carton = await ReturnWithUnitAsync(seed, saleId, seed.ProductId, 1m, "كرتون", 12m);
        Assert.True(carton.IsSuccess, carton.Message);
        Assert.Equal(20m, await AvailableAsync(seed.ProductId)); // 20 − 15 + 15
    }

    // ══ 2) طلب واحد مختلط (أساس + كرتون) لنفس المنتج: كل مجموعة على سطورها ══

    [Fact]
    public async Task MixedInvoice_BaseAndNamed_ReturnedInOneRequest_AllocatesPerLineType()
    {
        string marker = $"mix2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");
        int saleId = await MixedSaleAsync(seed, "كرتون", 12m); // 15 أساس × 20 = 300

        await using var db = _fx.CreateContext();
        var r = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleId,
            UserId = seed.UserId,
            Items =
            {
                new DtoReturnItem { ProductId = seed.ProductId, Quantity = 3m },                       // الأساس كاملًا
                new DtoReturnItem
                {
                    ProductId = seed.ProductId, Quantity = 12m,
                    EnteredQuantity = 1m, UnitName = "كرتون", UnitFactorToBase = 12m
                }
            }
        });
        Assert.True(r.IsSuccess, r.Message);

        var ret = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == r.Data);
        Assert.Equal(2, ret.InvoiceItems.Count);

        var baseRow = ret.InvoiceItems.Single(l => l.UnitName == null);
        Assert.Equal(3m, baseRow.Quantity);

        var cartonRow = ret.InvoiceItems.Single(l => l.UnitName == "كرتون");
        Assert.Equal(12m, cartonRow.Quantity);
        Assert.Equal(1m, cartonRow.EnteredQuantity);
        Assert.Equal(12m, cartonRow.UnitFactorToBase);

        // الاسترداد = (3 + 12) × 20 = 300 — كل مجموعة بسعر سطرها الأصلي
        Assert.Equal(-300.00m, ret.NetAmount);
        Assert.Equal(20m, await AvailableAsync(seed.ProductId));
    }

    // ══ 3) فاتورة بلقطة فقط: إرجاع الأساس يظل مرفوضًا ══

    [Fact]
    public async Task SnapshotOnlyInvoice_BaseReturn_StillRejected()
    {
        string marker = $"mix3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");
        int saleId = await SnapshotOnlySaleAsync(seed, "كرتون", 12m);

        var r = await ReturnBaseAsync(seed, saleId, seed.ProductId, 5m);
        Assert.False(r.IsSuccess);
        Assert.Equal(8m, await AvailableAsync(seed.ProductId)); // 20 − 12 بلا تغيير
    }
}
