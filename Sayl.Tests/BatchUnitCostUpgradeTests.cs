using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// ترقية Upgrade_11: عمود Batches.UnitCost يُنشأ، والدفعات القائمة تُرحَّل
/// إلى تكلفة أحدث بند شراء مرتبط بها — بيانات تاريخية تُحفظ لا تُمس.
/// (الـfixture نفسه يبني القاعدة بلا العمود ثم يشغل السكربت — فالاختبار
/// يثبت أنه أنشأه وترحّل على حالة ترقية حقيقية.)
/// </summary>
[Collection("database")]
public class BatchUnitCostUpgradeTests
{
    private readonly DatabaseFixture _fx;
    public BatchUnitCostUpgradeTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task ColumnExists_AfterUpgrade()
    {
        await using var db = _fx.CreateContext();
        var columns = await db.Database
            .SqlQuery<string>($@"SELECT name AS Value FROM sys.columns
                                 WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'UnitCost'")
            .ToListAsync();
        Assert.NotEmpty(columns); // الـfixture شغل Upgrade_11 على قاعدة بُنيت بلا العمود
    }

    [Fact]
    public async Task Backfill_LegacyBatch_FromLatestPurchaseItem()
    {
        await using var db = _fx.CreateContext();
        string marker = $"up-{Guid.NewGuid():N}"[..12];
        var seed = await TestSeed.SeedAsync(db, marker);

        // حالة قديمة: دفعة بلا تكلفة مخزنة + بند شراء يحمل تكلفة
        var batch = new Batch { ProductId = seed.ProductId, BatchNumber = "B-LEGACY", BatchQuantity = 7, CreatedAt = DateTime.UtcNow };
        db.Batches.Add(batch);

        var product = await db.Products.FirstAsync(p => p.ProductId == seed.ProductId);
        product.CostPrice = 10m;

        var invoice = new Invoice
        {
            Type = enInvoicesType.purchase,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.paid,
            PartyType = 'S',
            PartyId = seed.SupplierId,
            UserId = seed.UserId,
            TotalAmount = 50m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 50m,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.InvoiceItems.Add(new InvoiceItem
        {
            InvoiceId = invoice.InvoiceId,
            ProductId = seed.ProductId,
            Quantity = 5,
            BatchId = batch.BatchId,
            ItemPrice = 10m,
            ItemCost = 10m
        });
        await db.SaveChangesAsync();

        // إعادة تشغيل سكربت الترقية (idempotent) — الترحيل يملأ الدفة القديمة
        DatabaseFixture.ExecuteBatches(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Upgrade_11_BatchUnitCost.sql")));

        await db.Entry(batch).ReloadAsync();
        Assert.Equal(10.00m, batch.UnitCost);
    }
}
