using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// B5 — سلوك المرتجع على دفعة منتهية الصلاحية (النص المصحّح للإصلاح الفعلي):
/// - كمية المرتجع تعود إلى دفعة المصدر المنتهية نفسها ← تبقى محجوبة عن البيع
///   (البيع يستبعد الدفعات المنتهية) — لا تُصنع دفعة جديدة قابلة للبيع.
/// - دفعة الحجر RET- تُنشأ فقط لمصادر بلا دفعة (بنود قديمة بلا BatchId)
///   أو دفعة أجنبية (لك منتج آخر) — وتُولد معزولة (IsQuarantined) لا قابلة للبيع.
/// - المتاح الصافي يستثني المنتهي والمحجوب.
///
/// النص القديم في QA_REPORT كان يدّعي أن المرتجع المنتهي يُنشئ دفعة جديدة
/// قابلة للبيع (ExpiryDate=null) — هذا عكس السلوك الحالي الفعلي. الاختبارات
/// هنا تثبت السلوك الحقيقي داخل مجموعة xunit (كانت تغطيته فقط في هارنس
/// الكونسول InventoryBatchConsistencyCycle §5/§7 الذي يُشغَّل يدويًا).
/// المصدر: clsReturnService.RestockProductAsync.
/// </summary>
[Collection("database")]
public class ExpiredBatchRestockTests
{
    private readonly DatabaseFixture _fx;
    public ExpiredBatchRestockTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>يوم البرنامج (UTC+3) — نفس ساعة الخدمة (ProgramClock).</summary>
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>شراء 10 @10 ثم بيع 3 (FEFO من الدفعة الوحيدة) — يعيد (منتج، مستخدم، دفعة المصدر، فاتورة البيع).</summary>
    private async Task<(int ProductId, int UserId, int SourceBatchId, int SaleInvoiceId)> SeedSaleAsync(string marker)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);

        var purchaseResult = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"EB-{marker}", Quantity = 10, UnitCost = 10m, ExpiryDate = Today.AddDays(30) } }
        });
        Assert.True(purchaseResult.IsSuccess, purchaseResult.Message);

        // سياق جديد للبيع — كما في الإنتاج (سياق لكل طلب)
        await using var saleDb = _fx.CreateContext();
        // BasePrice 20 × 3 = 60 — دفع كامل (البيع الجزئي يتطلب عميلًا)
        var sale = await new clsCheckoutService(saleDb).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 3 } },
            Payment = new DtoPaymentInfo { Amount = 60m, Method = "cash" }
        });
        Assert.True(sale.IsSuccess, sale.Message);

        int sourceBatchId = await db.InvoiceItems.AsNoTracking()
            .Where(ii => ii.InvoiceId == sale.Data)
            .Select(ii => ii.BatchId!.Value)
            .SingleAsync();

        return (seed.ProductId, seed.UserId, sourceBatchId, sale.Data);
    }

    // ══ 1) المرتجع على دفعة منتهية: الكمية تعود للدفعة المنتهية نفسها — لا دفعة قابلة للبيع ══
    [Fact]
    public async Task Return_OfExpiredBatch_GoesBackIntoTheSameExpiredBatch_StaysBlocked()
    {
        string marker = $"eb1-{Guid.NewGuid():N}"[..12];
        var (productId, userId, sourceBatchId, saleInvoiceId) = await SeedSaleAsync(marker);

        await using var db = _fx.CreateContext();

        // تفقيط الدفعة المصدر (انتهت صلاحيتها أمس — يوم البرنامج)
        await db.Batches.Where(b => b.BatchId == sourceBatchId)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.ExpiryDate, Today.AddDays(-1)));

        int batchQtyBefore = await db.Batches.AsNoTracking()
            .Where(b => b.BatchId == sourceBatchId).Select(b => b.BatchQuantity).SingleAsync();

        var result = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleInvoiceId,
            UserId = userId,
            Items = { new DtoReturnItem { ProductId = productId, Quantity = 2 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        // الكمية عادت إلى الدفعة المنتهية نفسها (7) — لا دفعة جديدة
        var batch = await db.Batches.AsNoTracking().FirstAsync(b => b.BatchId == sourceBatchId);
        Assert.Equal(batchQtyBefore + 2, batch.BatchQuantity);
        Assert.Equal(Today.AddDays(-1), batch.ExpiryDate); // ما زالت منتهية — لم تُلمس

        // لم تُصنع أي دفعة RET- قابلة للبيع لهذا المنتج
        bool fabricatedSellable = await db.Batches.AsNoTracking()
            .AnyAsync(b => b.ProductId == productId
                        && b.BatchNumber.StartsWith("RET-")
                        && !b.IsQuarantined);
        Assert.False(fabricatedSellable, "expired return must not fabricate a sellable RET- batch");

        // البضاعة المعادة محجوبة فعليًا: البيع من الدفعة المنتهية مرفوض
        var blocked = await new clsStockService(db).SellStockAsync(productId, 1);
        Assert.False(blocked.IsSuccess, "expired restocked stock must remain unsellable");

        // المتاح الصافي يستثني المنتهي: كل المخزون (9) داخل دفعة منتهية ← صافي 0
        int net = await new clsProductService(db).GetNetAvailableQuantityAsync(productId);
        Assert.Equal(0, net);
    }

    // ══ 2) مرتجع بلا دفعة مصدر (بند قديم): دفعة حجر RET- معزولة لا تُباع ══
    [Fact]
    public async Task Return_OfLegacyNoBatchLine_CreatesQuarantinedRetBatch_NotSellable()
    {
        string marker = $"eb2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // فاتورة بيع قديمة الشكل (بند بلا دفعة) — تحاكي بيانات ما قبل الدفعات
        var manual = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.paid,
            PartyType = 'C',
            PartyId = seed.CustomerId,
            UserId = seed.UserId,
            TotalAmount = 10m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 10m,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(manual);
        await db.SaveChangesAsync();
        db.InvoiceItems.Add(new InvoiceItem
        { InvoiceId = manual.InvoiceId, ProductId = seed.ProductId, Quantity = 1, BatchId = null, ItemPrice = 10m, ItemCost = 5m });
        await db.SaveChangesAsync();

        var result = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = manual.InvoiceId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
        });
        Assert.True(result.IsSuccess, result.Message);

        // دفعة الحجر وُلدت معزولة (IsQuarantined) بلا صلاحية
        var retBatch = await db.Batches.AsNoTracking()
            .SingleAsync(b => b.ProductId == seed.ProductId && b.BatchNumber.StartsWith("RET-"));
        Assert.True(retBatch.IsQuarantined);
        Assert.Null(retBatch.ExpiryDate);
        Assert.Equal(1, retBatch.BatchQuantity);

        // المخزون الكلي زاد لكن المتاح الصافي يستثني المحجوب
        int inventory = await db.Inventories.AsNoTracking()
            .Where(i => i.ProductId == seed.ProductId).Select(i => i.QuantityAvailable ?? 0).SingleAsync();
        Assert.Equal(1, inventory);
        int net = await new clsProductService(db).GetNetAvailableQuantityAsync(seed.ProductId);
        Assert.Equal(0, net);

        // والبيع من المخزون المحجوب مرفوض
        var blocked = await new clsStockService(db).SellStockAsync(seed.ProductId, 1);
        Assert.False(blocked.IsSuccess, "quarantined restocked stock must remain unsellable");
    }
}
