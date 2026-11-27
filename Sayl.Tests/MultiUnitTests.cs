using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// المرحلة 5 — تدفقات الوحدات المتعددة: البيع والتعليق والشراء والاستلام
/// والإرجاع بوحدة إدخال (2 كيس × 5 كجم) مع بقاء الحقيقة المخزنة بالوحدة
/// الأساس (DECIMAL(10,3)). القواعد المثبّتة:
/// التحويل مرة واحدة لكل سطر عند حد الخدمة (|الأساس − المُرسل| &lt; 0.0005)؛
/// اللقطة (الاسم/المعامل/المُدخل) تُكتب مرة عند الإنشاء ولا تُعاد كتابتها؛
/// الوحدات المختلطة لنفس المنتج تُجمَّع بالأساس (حجز وخصم)؛ الإرجاع بوحدة
/// لقطة السطر الأصلي فقط؛ والإرثي (NULL) يمر بلا اشتقاق.
/// </summary>
[Collection("database")]
public class MultiUnitTests
{
    private readonly DatabaseFixture _fx;
    public MultiUnitTests(DatabaseFixture fx) => _fx = fx;

    // ───────────────── أدوات مساعدة (سياق جديد لكل عملية — كما في الإنتاج) ─────────────────

    private async Task<TestSeed> SeedAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        return await TestSeed.SeedAsync(db, marker);
    }

    /// <summary>إضافة وحدة إدخال غير أساسية لمنتج — عبر النموذج (نفس مسار
    /// الإنتاج). صف أساس المنتج (IsBase=1) تُنشئه الترحيلات ولا يلزمها
    /// مسارات الخدمة هنا (الأساس = UnitName فارغ يتجاوز بلا لقطة).</summary>
    private async Task<int> AddUnitAsync(TestSeed seed, string name, decimal factor,
        bool isActive = true)
    {
        await using var db = _fx.CreateContext();
        var unit = new ProductUnit
        {
            ProductId = seed.ProductId,
            Name = name,
            FactorToBase = factor,
            IsBase = false,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        };
        db.ProductUnits.Add(unit);
        await db.SaveChangesAsync();
        return unit.ProductUnitId;
    }

    /// <summary>استلام مخزون عبر clsStockService.ReceiveStockAsync (بوابة الصلاحية بمستخدم البذر).</summary>
    private async Task ReceiveAsync(TestSeed seed, int productId, decimal qty, string batch,
        decimal unitCost = 10m, DateOnly? expiry = null)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
        {
            ProductId = productId,
            BatchNumber = batch,
            Quantity = qty,
            UnitCost = unitCost,
            ExpiryDate = expiry
        }, seed.UserId);
        Assert.True(r.IsSuccess, r.Message);
    }

    /// <summary>بيع عادي (كاش) — السعر من المنتج (20) لا من الواجهة.</summary>
    private async Task<int> SaleAsync(TestSeed seed, int productId, decimal qty)
    {
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = qty } },
            Payment = new DtoPaymentInfo { Amount = 20m * qty, Method = "cash" }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>بيع بوحدة إدخال: الكمية المُرسلة بالأساس + اللقطة المُرسلة
    /// من الواجهة (الخدمة تُعيد الحساب وتُطابق). الدفع = 20 × الأساس.</summary>
    private async Task<int> SaleWithUnitAsync(TestSeed seed, int productId,
        decimal enteredQty, string unitName, decimal factor)
    {
        decimal baseQty = Math.Round(enteredQty * factor, 3, MidpointRounding.AwayFromZero);
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
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
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>مرتجع عميل بوحدة لقطة السطر الأصلي (الكمية المُرسلة بالأساس).</summary>
    private async Task<Common.Results.Result<int>> ReturnWithUnitAsync(TestSeed seed,
        int saleId, int productId, decimal enteredQty, string unitName, decimal factor)
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

    private async Task<decimal> BatchQuantityAsync(int batchId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Batches.AsNoTracking().SingleAsync(b => b.BatchId == batchId)).BatchQuantity;
    }

    private async Task<decimal> ReservedAsync(int productId)
    {
        await using var db = _fx.CreateContext();
        return (await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId)).QuantityReserved ?? 0m;
    }

    /// <summary>منتج مخزني جديد مع تسمية وحدة الأساس (Product.Unit).</summary>
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
        return product.ProductId;
    }

    // ══ 1) بيع بوحدة إدخال: 2 كيس × 5 = 10 أساس — اللقطة على السطر والخصم بالأساس ══

    [Fact]
    public async Task SaleInEnteredUnit_SnapshotAndBaseQuantityPersisted()
    {
        string marker = $"mu1-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");

        int saleId = await SaleWithUnitAsync(seed, seed.ProductId, 2m, "كيس", 5m);

        await using var db = _fx.CreateContext();
        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == saleId);
        var line = Assert.Single(invoice.InvoiceItems);

        // الحقيقة المخزنة بالأساس + اللقطة كما أُدخلت
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(2m, line.EnteredQuantity);
        Assert.Equal("كيس", line.UnitName);
        Assert.Equal(5m, line.UnitFactorToBase);
        Assert.Equal(20m, line.ItemPrice); // سعر الأساس المخزن لا سعر الوحدة المُدخلة

        // الخصم والحركة بالوحدة الأساس حصرًا
        Assert.Equal(10m, await AvailableAsync(seed.ProductId));
        var movement = await db.StockMovements.AsNoTracking()
            .SingleAsync(m => m.SourceType == "Invoice" && m.SourceId == saleId);
        Assert.Equal(-10m, movement.Quantity);
    }

    // ══ 2) وحدتان مختلفتان لنفس المنتج في سلة واحدة — الحجز والخصم تجميع بالأساس ══

    [Fact]
    public async Task MixedUnitsSameProduct_OneCart_AggregatesReservationAndDeductionInBase()
    {
        string marker = $"mu2-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");

        // سلة: 1 كرتون (12 أساس) + 3 حبات (أساس) — منتج واحد وحدتان
        int suspendedId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items =
                {
                    new DtoCheckoutItem
                    {
                        ProductId = seed.ProductId, ProductName = "x", Quantity = 12m,
                        EnteredQuantity = 1m, UnitName = "كرتون", UnitFactorToBase = 12m
                    },
                    new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 3m }
                },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(r.IsSuccess, r.Message);
            suspendedId = r.Data;
        }

        // التعليق يحجز بالتجميع الأساسي: 12 + 3 = 15
        Assert.Equal(15m, await ReservedAsync(seed.ProductId));

        await using (var db = _fx.CreateContext())
        {
            var complete = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
                suspendedId,
                new List<DtoPaymentInfo> { new() { Amount = 300m, Method = "cash" } },
                seed.UserId);
            Assert.True(complete.IsSuccess, complete.Message);
        }

        // الخصم النهائي = 15 واللقطات صحيحة لكل سطر (سطر الكرتون فقط يحمل لقطة)
        Assert.Equal(0m, await ReservedAsync(seed.ProductId));
        Assert.Equal(5m, await AvailableAsync(seed.ProductId));

        await using var verify = _fx.CreateContext();
        var invoice = await verify.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == suspendedId);
        Assert.Equal(2, invoice.InvoiceItems.Count);

        var carton = invoice.InvoiceItems.Single(l => l.UnitName == "كرتون");
        Assert.Equal(12m, carton.Quantity);
        Assert.Equal(1m, carton.EnteredQuantity);
        Assert.Equal(12m, carton.UnitFactorToBase);

        var baseLine = invoice.InvoiceItems.Single(l => l.UnitName == null);
        Assert.Equal(3m, baseLine.Quantity);
        Assert.Null(baseLine.EnteredQuantity);
        Assert.Null(baseLine.UnitFactorToBase);

        // حركة لكل سطر بالأساس: −12 ثم −3
        var movements = await verify.StockMovements.AsNoTracking()
            .Where(m => m.SourceType == "Invoice" && m.SourceId == suspendedId)
            .OrderBy(m => m.MovementId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(-12m, movements[0].Quantity);
        Assert.Equal(-3m, movements[1].Quantity);
    }

    // ══ 3) شراء بوحدة الإدخال: 1 كرتون @ 120 بمعامل 12 → دفعة +12 بتكلفة 10.00 ══

    [Fact]
    public async Task PurchaseInEnteredUnit_BatchBaseQuantity_CostPerBase_Total()
    {
        string marker = $"mu3-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);

        int purchaseId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = seed.SupplierId,
                UserId = seed.UserId,
                Items =
                {
                    new DtoPurchaseItem
                    {
                        ProductId = seed.ProductId,
                        BatchNumber = $"P-{marker}",
                        Quantity = 12m,               // الأساس المُرسل من الواجهة
                        UnitCost = 120m,              // تكلفة وحدة الإدخال (الكرتون)
                        EnteredQuantity = 1m,
                        UnitName = "كرتون",
                        UnitFactorToBase = 12m
                    }
                }
            });
            Assert.True(r.IsSuccess, r.Message);
            purchaseId = r.Data;
        }

        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.AsNoTracking().SingleAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(12m, batch.BatchQuantity);   // الدفعة بالأساس
        Assert.Equal(10.00m, batch.UnitCost);     // 120 ÷ 12 = 10.00 تكلفة الأساس

        var purchase = await verify.Invoices.Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == purchaseId);
        var line = Assert.Single(purchase.InvoiceItems);
        Assert.Equal(12m, line.Quantity);         // سطر الفاتورة بالأساس
        Assert.Equal(10.00m, line.ItemCost);      // تكلفة الأساس على السطر
        Assert.Equal(120m, purchase.TotalAmount); // الإجمالي 120 (نفس ما دُفع للكرتون)
        Assert.Equal(1m, line.EnteredQuantity);   // لقطة الشراء محفوظة
        Assert.Equal("كرتون", line.UnitName);
        Assert.Equal(12m, line.UnitFactorToBase);
    }

    // ══ 4) استلام مخزون بوحدة الإدخال: 3 أكياس × 5 = 15 بالكمية والتكلفة ══

    [Fact]
    public async Task ReceiveStockInEnteredUnit_ConvertsQuantityAndCostToBase()
    {
        string marker = $"mu4-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m);

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsStockService(db).ReceiveStockAsync(new DtoStockReceiveRequest
            {
                ProductId = seed.ProductId,
                BatchNumber = $"R-{marker}",
                Quantity = 15m,               // الأساس المُرسل
                UnitCost = 25m,               // تكلفة الكيس الواحد (بالوحدة المُدخلة)
                EnteredQuantity = 3m,
                UnitName = "كيس",
                UnitFactorToBase = 5m
            }, seed.UserId);
            Assert.True(r.IsSuccess, r.Message);
        }

        // الدفعة والمخزون والحركة كلها بالأساس (15) والتكلفة بالأساس (25 ÷ 5 = 5.00)
        Assert.Equal(15m, await AvailableAsync(seed.ProductId));
        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.AsNoTracking().SingleAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(15m, batch.BatchQuantity);
        Assert.Equal(5.00m, batch.UnitCost);
        var movement = await verify.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == seed.ProductId);
        Assert.Equal(15m, movement.Quantity);
    }

    // ══ 5) تعليق ثم إكمال: اللقطة تُقرأ من الصفوف المخزنة ولا تُعاد كتابتها ══

    [Fact]
    public async Task SuspendedInvoice_RoundTripsUnitSnapshot_ThroughCompletion()
    {
        string marker = $"mu5-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");

        int suspendedId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items =
                {
                    new DtoCheckoutItem
                    {
                        ProductId = seed.ProductId, ProductName = "x", Quantity = 10m,
                        EnteredQuantity = 2m, UnitName = "كيس", UnitFactorToBase = 5m
                    }
                },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(r.IsSuccess, r.Message);
            suspendedId = r.Data;
        }

        // التعليق يكتب اللقطة فورًا على الصفوف المخزنة
        await using (var db = _fx.CreateContext())
        {
            var suspended = await db.Invoices.Include(i => i.InvoiceItems)
                .FirstAsync(i => i.InvoiceId == suspendedId);
            var line = Assert.Single(suspended.InvoiceItems);
            Assert.Equal(enInvoicesStatus.suspended, suspended.Status);
            Assert.Equal(2m, line.EnteredQuantity);
            Assert.Equal("كيس", line.UnitName);
            Assert.Equal(5m, line.UnitFactorToBase);
        }

        await using (var db = _fx.CreateContext())
        {
            var complete = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
                suspendedId,
                new List<DtoPaymentInfo> { new() { Amount = 200m, Method = "cash" } },
                seed.UserId);
            Assert.True(complete.IsSuccess, complete.Message);
        }

        // الإكمال يخصم بالأساس من الصفوف المخزنة — اللقطة كما هي بلا إعادة كتابة
        await using var verify = _fx.CreateContext();
        var invoice = await verify.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == suspendedId);
        Assert.Equal(enInvoicesStatus.completed, invoice.Status);
        var done = Assert.Single(invoice.InvoiceItems);
        Assert.Equal(10m, done.Quantity);
        Assert.Equal(2m, done.EnteredQuantity);
        Assert.Equal("كيس", done.UnitName);
        Assert.Equal(5m, done.UnitFactorToBase);

        Assert.Equal(0m, await ReservedAsync(seed.ProductId));
        Assert.Equal(10m, await AvailableAsync(seed.ProductId));
    }

    // ══ 6) مرتجع جزئي بوحدة اللقطة الأصلية: حد/استرداد/إرجاع مخزون كلها بالأساس ══

    [Fact]
    public async Task PartialCustomerReturn_InOriginalSnapshotUnit_LimitRefundRestockInBase()
    {
        string marker = $"mu6-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}", unitCost: 10m);
        int saleId = await SaleWithUnitAsync(seed, seed.ProductId, 2m, "كيس", 5m); // 10 أساس × 20 = 200

        // إرجاع 1 كيس من 2 (5 أساس) — بنفس وحدة اللقطة الأصلية
        var r = await ReturnWithUnitAsync(seed, saleId, seed.ProductId, 1m, "كيس", 5m);
        Assert.True(r.IsSuccess, r.Message);

        await using var db = _fx.CreateContext();
        var ret = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == r.Data);

        // الاسترداد متناسب بالأساس: 5 × 20 = 100 (سالب = مبلغ مردود)
        Assert.Equal(-100.00m, ret.NetAmount);

        // سطر المرتجع يحمل لقطة السطر الأصلي نفسها (الجزئي = المدخل المُقسَّم)
        var line = Assert.Single(ret.InvoiceItems);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(1m, line.EnteredQuantity);
        Assert.Equal("كيس", line.UnitName);
        Assert.Equal(5m, line.UnitFactorToBase);
        Assert.Equal(10.00m, line.ItemCost); // تكلفة الدفعة الأصلية تتبع الأساس

        // المخزون يعود بالأساس: 20 − 10 + 5
        Assert.Equal(15m, await AvailableAsync(seed.ProductId));
        var batch = await db.Batches.AsNoTracking().SingleAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(15m, batch.BatchQuantity);

        // الإرجاع بوحدة الأساس على فاتورة لقطة = رفض (وحدة اللقطة هي الوحيدة)
        await using (var db2 = _fx.CreateContext())
        {
            var baseUnitTry = await new clsReturnService(db2).CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = saleId,
                UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 5m } }
            });
            Assert.False(baseUnitTry.IsSuccess);
        }

        // ثاني كيس يُرتجع كاملًا (الحد المتبقي بالأساس = 5)
        var second = await ReturnWithUnitAsync(seed, saleId, seed.ProductId, 1m, "كيس", 5m);
        Assert.True(second.IsSuccess, second.Message);
        Assert.Equal(20m, await AvailableAsync(seed.ProductId));

        // والثالث يُرفض: الحد المتبقي صفر بالأساس
        var third = await ReturnWithUnitAsync(seed, saleId, seed.ProductId, 1m, "كيس", 5m);
        Assert.False(third.IsSuccess);
        Assert.Equal(20m, await AvailableAsync(seed.ProductId));
    }

    // ══ 7) ثبات اللقطة: تعديل/تعطيل الوحدة بعد البيع لا يلمس التاريخ ══

    [Fact]
    public async Task SnapshotImmutability_UnitRenamedOrDeactivated_HistoryUnchanged()
    {
        string marker = $"mu7-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int unitId = await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 24m, $"B-{marker}");
        int saleId = await SaleWithUnitAsync(seed, seed.ProductId, 1m, "كرتون", 12m);

        // تعديل جذري بعد البيع: إعادة تسمية + تعطيل
        await using (var db = _fx.CreateContext())
        {
            await db.ProductUnits.Where(u => u.ProductUnitId == unitId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(p => p.Name, "كرتون-معدل")
                    .SetProperty(p => p.IsActive, false));
        }

        // اللقطة المخزنة كما بِيعت — الاسم القديم والعامل والمُدخل
        await using var verify = _fx.CreateContext();
        var vm = (await new clsInvoiceService(verify).GetInvoiceByIdAsync(saleId)).Data!;
        var line = Assert.Single(vm.Items);
        Assert.Equal(12m, line.Quantity);
        Assert.Equal(1m, line.EnteredQuantity);
        Assert.Equal("كرتون", line.UnitName);
        Assert.Equal(12m, line.UnitFactorToBase);
    }

    // ══ 8) رفض الخادم لوحدة غير معروفة — بلا فاتورة وبلا مسّ مخزون ══

    [Fact]
    public async Task Checkout_UnknownUnit_IsRejected_NoSideEffects()
    {
        string marker = $"mu8-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await ReceiveAsync(seed, seed.ProductId, 50m, $"B-{marker}");

        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = seed.ProductId, ProductName = "x", Quantity = 3m,
                    EnteredQuantity = 3m, UnitName = "صندوق", UnitFactorToBase = 1m
                }
            },
            Payment = new DtoPaymentInfo { Amount = 60m, Method = "cash" }
        });
        Assert.False(r.IsSuccess);

        // لا فاتورة ولا مسّ مخزون
        Assert.False(await db.Invoices.AsNoTracking().AnyAsync(
            i => i.PartyId == seed.CustomerId && i.Type == enInvoicesType.sale));
        Assert.Equal(50m, await AvailableAsync(seed.ProductId));
    }

    // ══ 9) رفض الخادم لوحدة معطلة — تكوين نشط فقط يُقبل ══

    [Fact]
    public async Task Checkout_InactiveUnit_IsRejected()
    {
        string marker = $"mu9-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m, isActive: false);
        await ReceiveAsync(seed, seed.ProductId, 50m, $"B-{marker}");

        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = seed.ProductId, ProductName = "x", Quantity = 5m,
                    EnteredQuantity = 1m, UnitName = "كيس", UnitFactorToBase = 5m
                }
            },
            Payment = new DtoPaymentInfo { Amount = 100m, Method = "cash" }
        });
        Assert.False(r.IsSuccess);
        Assert.Equal(50m, await AvailableAsync(seed.ProductId));
    }

    // ══ 10) رفض الخادم لمعامل مخالف لتكوين الوحدة (الواجهة ليست حد الثقة) ══

    [Fact]
    public async Task Checkout_FactorMismatch_IsRejected()
    {
        string marker = $"mu10-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كرتون", 12m);
        await ReceiveAsync(seed, seed.ProductId, 50m, $"B-{marker}");

        // الواجهة أرسلت معاملًا خاطئًا (10 بدل 12) — الخادم يُعيد الحساب من
        // التكوين (3 × 12 = 36) ويجده لا يطابق المُرسل (30) ⇒ رفض
        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items =
            {
                new DtoCheckoutItem
                {
                    ProductId = seed.ProductId, ProductName = "x", Quantity = 30m,
                    EnteredQuantity = 3m, UnitName = "كرتون", UnitFactorToBase = 10m
                }
            },
            Payment = new DtoPaymentInfo { Amount = 600m, Method = "cash" }
        });
        Assert.False(r.IsSuccess);
        Assert.Equal(50m, await AvailableAsync(seed.ProductId));
    }

    // ══ 11) رفض الخادم لكمية مُدخلة صفر أو سالبة ══

    [Fact]
    public async Task Checkout_NonPositiveEnteredQuantity_IsRejected()
    {
        string marker = $"mu11-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await AddUnitAsync(seed, "كيس", 5m);
        await ReceiveAsync(seed, seed.ProductId, 50m, $"B-{marker}");

        // صفر مُدخل مع لقطة وحدة
        await using (var db = _fx.CreateContext())
        {
            var zero = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items =
                {
                    new DtoCheckoutItem
                    {
                        ProductId = seed.ProductId, ProductName = "x", Quantity = 0m,
                        EnteredQuantity = 0m, UnitName = "كيس", UnitFactorToBase = 5m
                    }
                },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            });
            Assert.False(zero.IsSuccess);
        }

        // سالبة مُدخلة مع لقطة وحدة
        await using (var db = _fx.CreateContext())
        {
            var negative = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items =
                {
                    new DtoCheckoutItem
                    {
                        ProductId = seed.ProductId, ProductName = "x", Quantity = 5m,
                        EnteredQuantity = -1m, UnitName = "كيس", UnitFactorToBase = 5m
                    }
                },
                Payment = new DtoPaymentInfo { Amount = 100m, Method = "cash" }
            });
            Assert.False(negative.IsSuccess);
        }

        Assert.Equal(50m, await AvailableAsync(seed.ProductId));
    }

    // ══ 12) الإرثي (NULL) يمر عبر تعيين الفاتورة بلا اشتقاق — العرض يرجع لوحدة المنتج ══

    [Fact]
    public async Task LegacyNullSnapshot_PassesThroughVmMapping_NoDerivation()
    {
        string marker = $"mu12-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);

        // فاتورة وسطر مبنيان مباشرةً (سطر إرثي: أعمدة اللقطة NULL كلها)
        int invoiceId;
        await using (var db = _fx.CreateContext())
        {
            var invoice = new Invoice
            {
                Type = enInvoicesType.sale,
                Status = enInvoicesStatus.completed,
                PaymentStatus = enPaymentStatus.paid,
                PartyType = 'C',
                PartyId = seed.CustomerId,
                UserId = seed.UserId,
                TotalAmount = 40m,
                DiscountAmount = 0m,
                TaxAmount = 0m,
                NetAmount = 40m,
                InvoiceNumber = TestSeed.NewInvoiceNumber(),
                CreatedAt = DateTime.UtcNow
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = invoice.InvoiceId,
                ProductId = seed.ProductId,
                Quantity = 2,
                ItemPrice = 20m
            });
            await db.SaveChangesAsync();
            invoiceId = invoice.InvoiceId;
        }

        // التعيين يمرر null كما هو — لا وحدة مُختلقة من ProductUnits ولا من Product
        await using var verify = _fx.CreateContext();
        var vm = (await new clsInvoiceService(verify).GetInvoiceByIdAsync(invoiceId)).Data!;
        var line = Assert.Single(vm.Items);
        Assert.Null(line.UnitName);
        Assert.Null(line.UnitFactorToBase);
        Assert.Null(line.EnteredQuantity);
        Assert.Equal(2m, line.Quantity);

        // ومقابل ذلك: سطر بلقطة يُمرر لقطة النص نفسها لا شتقاقًا حيًا
        await AddUnitAsync(seed, "كيس", 5m);
        await ReceiveAsync(seed, seed.ProductId, 20m, $"B-{marker}");
        int saleId = await SaleWithUnitAsync(seed, seed.ProductId, 2m, "كيس", 5m);
        var withUnit = (await new clsInvoiceService(verify).GetInvoiceByIdAsync(saleId)).Data!;
        var unitLine = Assert.Single(withUnit.Items);
        Assert.Equal("كيس", unitLine.UnitName);
        Assert.Equal(2m, unitLine.EnteredQuantity);
        Assert.Equal(5m, unitLine.UnitFactorToBase);
    }

    // ══ 13) وحدات المنتج للعرض: الأساس أولًا، النشطة حصرًا، سعر الوحدة مشتق للعرض ══

    [Fact]
    public async Task GetProductUnits_BaseFirst_ActiveOnly_DisplayPriceDerived()
    {
        string marker = $"mu13-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        int productId = await SeedProductWithUnitAsync(seed, "عرض", "حبة");

        await using (var db = _fx.CreateContext())
        {
            // مسار الإقلاع: صف أساس مرآة Product.Unit
            await new clsProductService(db).EnsureProductUnitsSchemaAsync();
        }
        await using (var db = _fx.CreateContext())
        {
            db.ProductUnits.AddRange(
                new ProductUnit { ProductId = productId, Name = "كرتون", FactorToBase = 12m, IsBase = false, IsActive = true, CreatedAt = DateTime.UtcNow },
                new ProductUnit { ProductId = productId, Name = "علبة", FactorToBase = 6m, IsBase = false, IsActive = false, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var verify = _fx.CreateContext();
        var units = await new clsProductService(verify).GetProductUnitsAsync(productId);

        // الأساس أولًا ثم النشطة بالاسم — المعطلة محجوبة
        Assert.Equal(2, units.Count);
        Assert.True(units[0].IsBase);
        Assert.Equal("حبة", units[0].Name);
        Assert.Equal(1m, units[0].FactorToBase);
        Assert.Equal("كرتون", units[1].Name);
        Assert.False(units[1].IsBase);

        // سعر وحدة الإدخال للعرض فقط = سعر الأساس × المعامل (20 × 12 = 240.00)
        Assert.Equal(240.00m, units[1].PricePerUnit);

        // منتج غير موجود = قائمة فارغة (الواجهة تسقط إلى الأساس)
        var none = await new clsProductService(verify).GetProductUnitsAsync(0);
        Assert.Empty(none);
    }

    // ══ 14) الجرد يدخل بالوحدة الأساس: سطر الجلسة يحمل وحدة المنتج الحية ══

    private async Task<bool> HasOpenSessionAsync()
    {
        await using var db = _fx.CreateContext();
        return await new clsStockCountService(db).HasOpenSessionAsync();
    }

    /// <summary>حاجز ضد تسرب جلسة جرد مفتوحة من اختبار سابق أو إليها.</summary>
    private async Task CloseAnyOpenSessionAsync(int userId)
    {
        while (await HasOpenSessionAsync())
        {
            await using var db = _fx.CreateContext();
            var svc = new clsStockCountService(db);
            foreach (var l in await svc.GetOpenSessionAsync())
                Assert.True((await svc.SetCountedAsync(l.CountId, l.BookQuantity, userId)).IsSuccess);
            Assert.True((await svc.PostSessionAsync(userId)).IsSuccess);
        }
    }

    [Fact]
    public async Task StockCount_LinesCarryLiveBaseUnitLabel()
    {
        string marker = $"mu14-{Guid.NewGuid():N}"[..12];
        var seed = await SeedAsync(marker);
        await CloseAnyOpenSessionAsync(seed.UserId);
        int productId = await SeedProductWithUnitAsync(seed, "جرد", "كجم");

        try
        {
            await using (var db = _fx.CreateContext())
            {
                var svc = new clsStockCountService(db);
                var start = await svc.StartSessionAsync(seed.UserId);
                Assert.True(start.IsSuccess, start.Message);

                var line = (await svc.GetOpenSessionAsync()).Single(l => l.ProductId == productId);
                Assert.Equal("كجم", line.UnitName); // دخول الجرد بوحدة الأساس حصرًا
            }
        }
        finally
        {
            // إغلاق الجلسة بعدّ كل شيء بالكتيب — بلا حركات ولا تسرب للجلسات
            await CloseAnyOpenSessionAsync(seed.UserId);
        }
        Assert.False(await HasOpenSessionAsync());
    }

    // ══ 15) حافة الخانتين الموثقة: 115 كرتون ÷ 12 = 9.58 — والعرض × المعامل ══

    [Fact]
    public void UnitConversion_CostAndDisplayEdges_RoundHalfAwayFromZero_TwoDecimals()
    {
        // تكلفة الأساس = تكلفة الإدخال ÷ المعامل (خانتان، AwayFromZero):
        // 115 ÷ 12 = 9.5833… → 9.58 (خانتان — الفرق الموثق من 9.5833 يبقى أسعاريًا)
        Assert.Equal(9.58m, UnitConversion.ConvertCostToBase(115m, 12m));
        Assert.Equal(10.00m, UnitConversion.ConvertCostToBase(120m, 12m));

        // سعر وحدة الإدخال للعرض = سعر الأساس × المعامل (خانتان، AwayFromZero)
        Assert.Equal(240.00m, UnitConversion.DisplayPricePerUnit(20m, 12m));
        Assert.Equal(100.00m, UnitConversion.DisplayPricePerUnit(20m, 5m));
    }
}
