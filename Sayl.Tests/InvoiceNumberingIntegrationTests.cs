using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// تكامل الترقيم التجاري السنوي مع مسارات الإنتاج الحقيقية: كل نوع من
/// الأنواع الأربعة (بيع/شراء/مرتجع عميل/مرتجع مورد) يحصل على رقم عبر
/// الخدمة الفعلية لا بذرًا مباشرًا، والتسلسل واحد مشترك بين الأنواع
/// (صف عدّاد واحد لكل سنة)، والتعليق ثم الإكمال يحتفظ برقمه الأصلي
/// بلا استهلاك رقم جديد، وفشل العملية بعد الإسناد يتراجع بالعدّاد
/// معها (الإسناد داخل معاملة العملية)، ولا مسار يُدرج فاتورة بلا رقم
/// (IsRequired + NOT NULL + فهرس فريد عادي — إثبات مخططي).
/// </summary>
[Collection("database")]
public class InvoiceNumberingIntegrationTests
{
    private readonly DatabaseFixture _fx;
    public InvoiceNumberingIntegrationTests(DatabaseFixture fx) => _fx = fx;

    private static int Year => ProgramClock.Today.Year;

    /// <summary>قيمة عدّاد السنة البرمجية الحالية (0 إن غاب الصف).</summary>
    private static async Task<long> CurrentYearCounterAsync(SaylDbContext db) =>
        await db.InvoiceNumberCounters.AsNoTracking()
            .Where(c => c.NumberYear == Year)
            .Select(c => c.LastIssuedNumber)
            .FirstOrDefaultAsync();

    /// <summary>شراء حقيقي يورد الدفعات (أساس أي بيع/مرتجع).</summary>
    private static async Task PurchaseStockAsync(SaylDbContext db, TestSeed seed,
        string batch, int qty, decimal unitCost = 10m) =>
        Assert.True((await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = batch, Quantity = qty, UnitCost = unitCost } }
        })).IsSuccess);

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

    // ══ 1) الأنواع الأربعة عبر المسارات الحقيقية — تسلسل واحد مشترك:
    //       بيع → شراء → مرتجع عميل → مرتجع مورد تتداخل أرقامها من
    //       عدّاد واحد لكل السنة (صف واحد بقيمة base+4). ══
    [Fact]
    public async Task AllFourTypes_ViaRealServicePaths_ShareOneSequence()
    {
        string marker = $"in4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            long baseCounter = await CurrentYearCounterAsync(db);

            // 1) بيع نقدي (يتضمن شراءً داخليًا يورد الدفعات — يستهلك رقم شراء أولًا)
            int saleId;
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"N4-{marker[..8]}", 5);
            }
            {
                await using var sdb = _fx.CreateContext();
                saleId = await CashSaleAsync(sdb, seed, 1);
            }

            // 2) شراء مستقل
            int purchaseId;
            {
                await using var pdb = _fx.CreateContext();
                var r = await new clsPurchaseService(pdb).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
                {
                    SupplierId = seed.SupplierId,
                    UserId = seed.UserId,
                    Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"N4b-{marker[..8]}", Quantity = 3, UnitCost = 10m } }
                });
                Assert.True(r.IsSuccess, r.Message);
                purchaseId = r.Data;
            }

            // 3) مرتجع عميل على البيع
            int customerReturnId;
            {
                await using var rdb = _fx.CreateContext();
                var r = await new clsReturnService(rdb).CreateCustomerReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = saleId, UserId = seed.UserId,
                    Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
                });
                Assert.True(r.IsSuccess, r.Message);
                customerReturnId = r.Data;
            }

            // 4) مرتجع مورد على الشراء المستقل
            int supplierReturnId;
            {
                await using var rdb = _fx.CreateContext();
                var r = await new clsReturnService(rdb).CreateSupplierReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = purchaseId, UserId = seed.UserId,
                    Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
                });
                Assert.True(r.IsSuccess, r.Message);
                supplierReturnId = r.Data;
            }

            // ترتيب الإسناد الفعلي: شراء التوريد الداخلي، ثم البيع، ثم الشراء
            // المستقل، ثم المرتجعان — كلها من عدّاد واحد بلا فجوات.
            var ids = new[] { saleId, purchaseId, customerReturnId, supplierReturnId };
            var rows = await db.Invoices.AsNoTracking()
                .Where(i => ids.Contains(i.InvoiceId))
                .ToDictionaryAsync(i => i.InvoiceId, i => new { i.Type, i.InvoiceNumber });

            Assert.All(ids, id => Assert.True(!string.IsNullOrWhiteSpace(rows[id].InvoiceNumber), $"invoice {id} has no number"));
            Assert.Equal(4, rows.Values.Select(r => r.InvoiceNumber).Distinct().Count());

            // صف عدّاد واحد فقط للسنة بقيمة base+4 — التسلسل المشترك عبر الأنواع
            var counterRows = await db.InvoiceNumberCounters.AsNoTracking()
                .Where(c => c.NumberYear == Year).ToListAsync();
            Assert.Single(counterRows);
            Assert.Equal(baseCounter + 5, counterRows[0].LastIssuedNumber); // +1 شراء التوريد +4 المسارات أعلاه

            // الأرقام الأربعة هي base+2..base+5 (base+1 لشراء التوريد الداخلي)
            var numbers = ids.Select(id => rows[id].InvoiceNumber).OrderBy(x => x).ToList();
            var expected = Enumerable.Range(2, 4)
                .Select(i => $"INV-{Year}-{(baseCounter + i).ToString("000000")}").OrderBy(x => x).ToList();
            Assert.Equal(expected, numbers);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 2) تعليق ثم إكمال: الرقم الأصلي يبقى، والعدّاد لا يتقدم بالإكمال ══
    [Fact]
    public async Task SuspendedThenCompleted_RetainsOriginalNumber_CounterNotAdvanced()
    {
        string marker = $"ins-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"NS-{marker[..8]}", 5);
            }

            long afterPurchase = await CurrentYearCounterAsync(db);

            // تعليق: سياق جديد نظيف (كميات حية من القاعدة)
            int suspendedId;
            string suspendedNumber;
            {
                await using var sdb = _fx.CreateContext();
                var susp = await new clsCheckoutService(sdb).ProcessCheckoutAsync(new DtoCheckoutRequest
                {
                    CustomerId = seed.CustomerId,
                    UserId = seed.UserId,
                    Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 50m } },
                    Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
                }, suspend: true);
                Assert.True(susp.IsSuccess, susp.Message);
                suspendedId = susp.Data;
                suspendedNumber = await sdb.Invoices.AsNoTracking()
                    .Where(i => i.InvoiceId == suspendedId)
                    .Select(i => i.InvoiceNumber)
                    .SingleAsync();
            }
            Assert.Equal(afterPurchase + 1, await CurrentYearCounterAsync(db));

            // إكمال: لا صف جديد — الرقم الأصلي يبقى والعدّاد لا يتقدم
            {
                await using var cdb = _fx.CreateContext();
                var done = await new clsCheckoutService(cdb).CompleteSuspendedInvoiceAsync(
                    suspendedId, new List<DtoPaymentInfo> { new() { Amount = 50m, Method = "cash" } }, seed.UserId);
                Assert.True(done.IsSuccess, done.Message);
            }

            var completed = await db.Invoices.AsNoTracking()
                .SingleAsync(i => i.InvoiceId == suspendedId);
            Assert.Equal(enInvoicesStatus.completed, completed.Status);
            Assert.Equal(suspendedNumber, completed.InvoiceNumber);
            Assert.Equal(afterPurchase + 1, await CurrentYearCounterAsync(db)); // لا رقم جديد عند الإكمال

            // صفوف الفاتورة لنفس المستخدم: صف واحد فقط بهذا الرقم (لم يُضف صفّ ثانٍ)
            Assert.Equal(1, await db.Invoices.AsNoTracking().CountAsync(i => i.InvoiceNumber == suspendedNumber));
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 3) فشل العملية بعد الإسناد يتراجع بالعدّاد معها: بيع بكمية تفوق
    //       المخزون — الرأس يُنشأ ورقمه يُسند داخل المعاملة ثم يفشل الخصم
    //       (حارس المخزون) فتراجع المعاملة كلها بالعدّاد والفاتورة. ══
    [Fact]
    public async Task FailedCheckout_InsufficientStock_RollsBackCounter()
    {
        string marker = $"inf-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"NF-{marker[..8]}", 5);
            }

            long before = await CurrentYearCounterAsync(db);

            await using var sdb = _fx.CreateContext();
            var checkout = new clsCheckoutService(sdb);
            var sale = new DtoCheckoutRequest
            {
                UserId = seed.UserId,
                Payment = new DtoPaymentInfo { Amount = 990m, Method = "cash" }
            };
            sale.Items.Add(new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 99 });
            var failed = await checkout.ProcessCheckoutAsync(sale);
            Assert.False(failed.IsSuccess);

            // العدّاد لم يتقدم — الإسناد تراجع مع المعاملة الفاشلة (تحديثه داخلها)
            Assert.Equal(before, await CurrentYearCounterAsync(db));
            // ولا فاتورة بهذا العلامة بقيت
            Assert.Equal(0, await db.Invoices.AsNoTracking().CountAsync(i => i.UserId == seed.UserId && i.Type == enInvoicesType.sale));
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    // ══ 4) لا مسار يُدرج فاتورة بلا رقم — إثبات هيكلي: EF metadata
    //       (IsRequired) + العمود NOT NULL + الفهرس فريد عادي بلا مرشِّح ══
    [Fact]
    public async Task NoPathInsertsWithoutNumber_SchemaLevelProof()
    {
        await using var db = _fx.CreateContext();

        // EF: الخاصية غير فارغة — أي مسار يرسل NULL يفشل قبل القاعدة
        var prop = db.Model.FindEntityType(typeof(Invoice))!
            .FindProperty(nameof(Invoice.InvoiceNumber))!;
        Assert.False(prop.IsNullable);

        var col = (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'InvoiceNumber'
              AND is_nullable = 0").ToListAsync()).Single();
        Assert.Equal(1, col);

        var idx = (await db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS Value FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'UQ_Invoices_InvoiceNumber'
              AND is_unique = 1 AND has_filter = 0").ToListAsync()).Single();
        Assert.Equal(1, idx);
    }

    // ══ 5) إسقاطات العرض تحمل الرقم التجاري: بالمعرف/للطباعة/القائمة/الصفحة
    //       — والمرتجع يحمل رقم الفاتورة الأصلية (OriginalInvoiceNumber)
    //       بينما غير المرتجع يتركه null. ══
    [Fact]
    public async Task Projections_CarryInvoiceNumber_AndReturnCarriesOriginalNumber()
    {
        string marker = $"inp-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        try
        {
            {
                await using var pdb = _fx.CreateContext();
                await PurchaseStockAsync(pdb, seed, $"NP-{marker[..8]}", 5);
            }

            int saleId;
            {
                await using var sdb = _fx.CreateContext();
                saleId = await CashSaleAsync(sdb, seed, 2);
            }

            // مرتجع عميل على البيع — المسار الحقيقي
            int returnId;
            {
                await using var rdb = _fx.CreateContext();
                var r = await new clsReturnService(rdb).CreateCustomerReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = saleId, UserId = seed.UserId,
                    Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } }
                });
                Assert.True(r.IsSuccess, r.Message);
                returnId = r.Data;
            }

            string saleNumber = await db.Invoices.AsNoTracking()
                .Where(i => i.InvoiceId == saleId)
                .Select(i => i.InvoiceNumber)
                .SingleAsync();
            Assert.False(string.IsNullOrWhiteSpace(saleNumber));

            var invoiceService = new clsInvoiceService(db);

            // بالمعرّف: البيع يحمل رقمه ولا مرجع أصلي له
            var byId = await invoiceService.GetInvoiceByIdAsync(saleId);
            Assert.True(byId.IsSuccess, byId.Message);
            Assert.NotNull(byId.Data);
            Assert.Equal(saleNumber, byId.Data!.InvoiceNumber);
            Assert.Null(byId.Data.OriginalInvoiceNumber);

            // للطباعة: المرتجع يحمل رقم الفاتورة الأصلية تجاريًا
            var forPrint = await invoiceService.GetInvoiceForPrintAsync(returnId);
            Assert.True(forPrint.IsSuccess, forPrint.Message);
            Assert.NotNull(forPrint.Data);
            Assert.False(string.IsNullOrWhiteSpace(forPrint.Data!.InvoiceNumber));
            Assert.Equal(saleNumber, forPrint.Data.OriginalInvoiceNumber);

            // القائمة الكاملة + الصفحة: صف المرتجع يحمل رقمه ورقم الأصل
            var list = await invoiceService.GetInvoicesListAsync(userId: seed.UserId);
            Assert.True(list.IsSuccess, list.Message);
            var listSale = list.Data!.Single(i => i.InvoiceId == saleId);
            var listReturn = list.Data!.Single(i => i.InvoiceId == returnId);
            Assert.Equal(saleNumber, listSale.InvoiceNumber);
            Assert.Null(listSale.OriginalInvoiceNumber);
            // المرتجع رقم تجاري خاص به (جديد) — والأصل مرجعًا برقمه
            Assert.False(string.IsNullOrWhiteSpace(listReturn.InvoiceNumber));
            Assert.NotEqual(saleNumber, listReturn.InvoiceNumber);
            Assert.Equal(saleNumber, listReturn.OriginalInvoiceNumber);

            var paged = await invoiceService.GetInvoicesPagedAsync(userId: seed.UserId, page: 1, pageSize: 50);
            Assert.True(paged.IsSuccess, paged.Message);
            var pagedReturn = paged.Data!.Items.Single(i => i.InvoiceId == returnId);
            Assert.False(string.IsNullOrWhiteSpace(pagedReturn.InvoiceNumber));
            Assert.Equal(saleNumber, pagedReturn.OriginalInvoiceNumber);
        }
        finally
        {
            await CleanupAsync(db, marker);
        }
    }

    /// <summary>تنظيف صفوف الاختبار (فواتير بذرته) — العدّاد يبقى (لن يُعاد
    /// إسناده: الأرقام تتصاعد دائمًا، وقاعدة الـfixture عشوائية لكل رَن).</summary>
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
