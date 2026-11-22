using System.Text.RegularExpressions;
using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// البحث بالرقم التجاري (INV-YYYY-NNNNNN): المطابقة التامة فقط بلا تفسير رقمي
/// ولا تجريد تنسيق، وتنظيف الفراغات المحيط، والرفض النظيف لغير الموجود/المشوّه،
/// وحرسا نوع الفاتورة في مسارات المرتجعين الحقيقية، وصدق الرقم التجاري مقابل
/// المعرّف الداخلي (رسائل النجاح تعرض الرقم التجاري لا المعرّف أبدًا).
/// </summary>
[Collection("database")]
public class InvoiceNumberLookupTests
{
    private readonly DatabaseFixture _fx;
    public InvoiceNumberLookupTests(DatabaseFixture fx) => _fx = fx;

    private static int Year => ProgramClock.Today.Year;

    /// <summary>شراء حقيقي يورد الدفعات (أساس أي بيع/مرتجع) — يعيد معرّف الفاتورة.</summary>
    private static async Task<int> PurchaseStockAsync(SaylDbContext db, TestSeed seed,
        string batch, int qty, decimal unitCost = 10m)
    {
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = batch, Quantity = qty, UnitCost = unitCost } }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>بيع نقدي كامل (مسار الكاشير الحقيقي).</summary>
    private static async Task<int> CashSaleAsync(SaylDbContext db, TestSeed seed, int qty, decimal unitPrice = 20m)
    {
        var checkout = new clsCheckoutService(db);
        var sale = new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Payment = new DtoPaymentInfo { Amount = qty * unitPrice, Method = "cash" }
        };
        sale.Items.Add(new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = qty, UnitPrice = unitPrice });
        var r = await checkout.ProcessCheckoutAsync(sale);
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    /// <summary>سطر الرقم التجاري المخزَّن لفاتورة بالمعرف الداخلي.</summary>
    private static async Task<string> NumberByIdAsync(SaylDbContext db, int invoiceId) =>
        await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId)
            .Select(i => i.InvoiceNumber)
            .SingleAsync();

    // ══ 1) المطابقة التامة عبر المسارات الحقيقية: شراء ثم بيع، وكلٌّ يُجلب
    //       برقمه التجاري المخزَّن فيعود المعرّف الداخلي الصحيح. ══
    [Fact]
    public async Task GetInvoiceIdByNumberAsync_ExactMatch_ReturnsInternalId()
    {
        string marker = $"il1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                purchaseId = await PurchaseStockAsync(pdb, seed, $"L1-{marker[..8]}", 5);
            }
            int saleId;
            {
                await using var sdb = _fx.CreateContext();
                saleId = await CashSaleAsync(sdb, seed, 1);
            }

            string purchaseNumber = await NumberByIdAsync(db, purchaseId);
            string saleNumber = await NumberByIdAsync(db, saleId);

            var svc = new clsInvoiceService(db);

            var byPurchase = await svc.GetInvoiceIdByNumberAsync(purchaseNumber);
            Assert.True(byPurchase.IsSuccess, byPurchase.Message);
            Assert.Equal(purchaseId, byPurchase.Data);

            var bySale = await svc.GetInvoiceIdByNumberAsync(saleNumber);
            Assert.True(bySale.IsSuccess, bySale.Message);
            Assert.Equal(saleId, bySale.Data);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 2) الفراغات المحيطة تُنظَّف (مسافات/جدولة/سطر جديد) ولا تمنع المطابقة. ══
    [Fact]
    public async Task GetInvoiceIdByNumberAsync_TrimsSurroundingWhitespace()
    {
        string marker = $"il2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                purchaseId = await PurchaseStockAsync(pdb, seed, $"L2-{marker[..8]}", 5);
            }
            string number = await NumberByIdAsync(db, purchaseId);

            var svc = new clsInvoiceService(db);

            var spaced = await svc.GetInvoiceIdByNumberAsync($"  {number}  ");
            Assert.True(spaced.IsSuccess, spaced.Message);
            Assert.Equal(purchaseId, spaced.Data);

            var mixed = await svc.GetInvoiceIdByNumberAsync($"\t{number}\n");
            Assert.True(mixed.IsSuccess, mixed.Message);
            Assert.Equal(purchaseId, mixed.Data);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 3) غير الموجود والمشوّه والفارغ: فشل منظم بلا استثناء — ولا تفسير رقمي
    //       (‎"123" ليس معرّفًا داخليًا ولا لاحقة رقم تجاري). ══
    [Fact]
    public async Task GetInvoiceIdByNumberAsync_Unknown_Or_Malformed_FailsCleanly()
    {
        string marker = $"il3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"L3-{marker[..8]}", 5);
            }

            var svc = new clsInvoiceService(db);

            foreach (var input in new[] { $"INV-{Year}-999999", "abc", "123", "" })
            {
                var r = await svc.GetInvoiceIdByNumberAsync(input);
                Assert.False(r.IsSuccess);
            }
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 4) حرس نوع الفاتورة في مسار مرتجع العميل الحقيقي: أصلٌ شراء (ولو مكتملًا)
    //       يُرفض على مستوى الاستعلام نفسه الذي يحرسه النموذج. ══
    [Fact]
    public async Task CustomerReturn_ServicePath_RejectsNonSaleOriginal()
    {
        string marker = $"il4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                purchaseId = await PurchaseStockAsync(pdb, seed, $"L4-{marker[..8]}", 5);
            }

            var r = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = purchaseId, UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
            });
            Assert.False(r.IsSuccess);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 5) المرآة: مسار مرتجع المورد يرفض أصلًا بيعًا (ولو مكتملًا). ══
    [Fact]
    public async Task SupplierReturn_ServicePath_RejectsNonPurchaseOriginal()
    {
        string marker = $"il5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"L5-{marker[..8]}", 5);
            }
            int saleId;
            {
                await using var sdb = _fx.CreateContext();
                saleId = await CashSaleAsync(sdb, seed, 1);
            }

            var r = await new clsReturnService(db).CreateSupplierReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = saleId, UserId = seed.UserId,
                Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
            });
            Assert.False(r.IsSuccess);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 6) التحميل بالمعرف الداخلي (مسار CreateForInvoice التلقائي) يعيد الرقم
    //       التجاري — ما يُعرض في مربع الإدخال بعد التحميل التلقائي. ══
    [Fact]
    public async Task GetInvoiceByIdAsync_ByInternalId_ResolvesCommercialNumber()
    {
        string marker = $"il6-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                purchaseId = await PurchaseStockAsync(pdb, seed, $"L6-{marker[..8]}", 5);
            }
            int saleId;
            {
                await using var sdb = _fx.CreateContext();
                saleId = await CashSaleAsync(sdb, seed, 1);
            }

            var svc = new clsInvoiceService(db);

            foreach (var id in new[] { purchaseId, saleId })
            {
                var byId = await svc.GetInvoiceByIdAsync(id);
                Assert.True(byId.IsSuccess, byId.Message);
                Assert.NotNull(byId.Data);
                Assert.False(string.IsNullOrWhiteSpace(byId.Data!.InvoiceNumber));
                Assert.StartsWith("INV-", byId.Data.InvoiceNumber);
            }
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 7) GetInvoiceNumberAsync يعيد الرقم التجاري لا المعرّف الداخلي —
    //       بصيغة INV-YYYY-NNNNNN المطابقة للعدّاد السنوي. ══
    [Fact]
    public async Task GetInvoiceNumberAsync_ReturnsCommercialNumber_NotInternalId()
    {
        string marker = $"il7-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                purchaseId = await PurchaseStockAsync(pdb, seed, $"L7-{marker[..8]}", 5);
            }

            var svc = new clsInvoiceService(db);
            var r = await svc.GetInvoiceNumberAsync(purchaseId);
            Assert.True(r.IsSuccess, r.Message);
            Assert.NotNull(r.Data);

            string number = r.Data!;
            Assert.NotEqual(purchaseId.ToString(), number);
            Assert.Matches($@"^INV-\d{{4}}-\d{{6}}$", number);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    /// <summary>تنظيف صفوف الاختبار (فواتير بذرته) — نفس نهج اختبارات الترقيم:
    /// العدّاد يبقى (الأرقام تتصاعد دائمًا وقاعدة الـfixture عشوائية لكل رَن).</summary>
    private static async Task CleanupAsync(SaylDbContext db, string marker)
    {
        var ids = await db.Invoices
            .Where(i => i.UserId == db.Users.Where(u => u.Username.Contains(marker)).Select(u => u.UserId).FirstOrDefault())
            .Select(i => i.InvoiceId).ToListAsync();
        if (ids.Count == 0) return;

        await db.Payments.Where(p => p.InvoiceId != null && ids.Contains(p.InvoiceId.Value)).ExecuteDeleteAsync();
        await db.InvoiceItems.Where(ii => ids.Contains(ii.InvoiceId)).ExecuteDeleteAsync();
        await db.StockMovements.Where(m => m.SourceType == "Invoice" && m.SourceId != null && ids.Contains(m.SourceId.Value)).ExecuteDeleteAsync();
        await db.StockReservations.Where(r => ids.Contains(r.InvoiceId)).ExecuteDeleteAsync();
        await db.Invoices.Where(i => ids.Contains(i.InvoiceId)).ExecuteDeleteAsync();
    }
}
