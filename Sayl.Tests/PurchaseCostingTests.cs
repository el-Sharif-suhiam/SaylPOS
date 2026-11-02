using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>فواتير الشراء: تخصيص الخصم على البنود وحفظ التكلفة الفعلية على الدفعات.</summary>
[Collection("database")]
public class PurchaseCostingTests
{
    private readonly DatabaseFixture _fx;
    public PurchaseCostingTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task Purchase_Discount_AllocatedAcrossItems_ReconcilesExactly()
    {
        string marker = $"pd-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        var service = new clsPurchaseService(db);
        var request = new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            DiscountAmount = 30m,
            Items =
            {
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-A", Quantity = 10, UnitCost = 10m },
                new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-B", Quantity = 10, UnitCost = 20m },
            }
        };

        var result = await service.CreatePurchaseInvoiceAsync(request);
        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices
            .Include(i => i.InvoiceItems)
            .FirstAsync(i => i.InvoiceId == result.Data);

        // رأس الفاتورة كما هو: إجمالي − خصم = صافي
        Assert.Equal(300m, invoice.TotalAmount);
        Assert.Equal(30m, invoice.DiscountAmount);
        Assert.Equal(270m, invoice.NetAmount);

        // البنود: التكلفة المخزنة صافية بعد نصيب كل بند من الخصم
        var items = invoice.InvoiceItems.OrderBy(ii => ii.ItemCost).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(9.00m, items[0].ItemCost);  // (100 − 10) / 10
        Assert.Equal(18.00m, items[1].ItemCost); // (200 − 20) / 10
        Assert.Equal(10m, items[0].ItemPrice);   // السعر الخام يبقى تاريخيًا
        Assert.Equal(20m, items[1].ItemPrice);

        // المخصصات على مستوى البنود تساوي خصم الفاتورة بالضبط
        decimal observedDiscount = items.Sum(ii => ii.ItemPrice * ii.Quantity - (ii.ItemCost ?? 0m) * ii.Quantity);
        Assert.Equal(invoice.DiscountAmount, observedDiscount);

        // الدفعات تحمل التكلفة التاريخية الفعلية
        var batches = await db.Batches.Where(b => b.ProductId == seed.ProductId).ToListAsync();
        Assert.All(batches, b => Assert.NotNull(b.UnitCost));
        Assert.Contains(batches, b => b.UnitCost == 9.00m && b.BatchQuantity == 10);
        Assert.Contains(batches, b => b.UnitCost == 18.00m && b.BatchQuantity == 10);
    }

    [Fact]
    public async Task Purchase_DiscountRounding_LargestRemainder_SumsToInvoiceDiscount()
    {
        // نصيب البنود غير قابل للقسمة: 1.00 على قيم 9.99 و10.01 و5.00
        var allocations = clsPurchaseService.AllocateDiscountAcrossItems(
            new List<DtoPurchaseItem>
            {
                new() { ProductId = 1, BatchNumber = "x", Quantity = 3, UnitCost = 3.33m },  // 9.99
                new() { ProductId = 2, BatchNumber = "x", Quantity = 1, UnitCost = 10.01m }, // 10.01
                new() { ProductId = 3, BatchNumber = "x", Quantity = 100, UnitCost = 0.05m } // 5.00
            }, 1.00m);

        decimal totalAllocated = allocations.Values.Sum(a => a.AllocatedDiscount);
        Assert.Equal(1.00m, totalAllocated); // قرش قرشًا — لا فرق تقريب
        Assert.All(allocations.Values, a => Assert.True(a.AllocatedDiscount >= 0m));
    }

    [Fact]
    public async Task Purchase_IntoExistingBatch_KeepsWeightedAverageCost()
    {
        string marker = $"pw-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var service = new clsPurchaseService(db);

        // شراء أول: 5 @ 10.00
        Assert.True((await service.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-1", Quantity = 5, UnitCost = 10m } }
        })).IsSuccess);

        // شراء ثانٍ على نفس رقم الدفعة: 5 @ 20.00 → متوسط مرجح 15.00
        Assert.True((await service.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = "B-1", Quantity = 5, UnitCost = 20m } }
        })).IsSuccess);

        // قراءة من سياق جديد — القيمة المخزنة في القاعدة هي المعتمدة
        await using var verify = _fx.CreateContext();
        var batch = await verify.Batches.FirstAsync(b => b.ProductId == seed.ProductId);
        Assert.Equal(15.00m, batch.UnitCost);
        Assert.Equal(10, batch.BatchQuantity);
    }
}
