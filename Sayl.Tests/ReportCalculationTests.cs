using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// حسابات التقارير المالية بمجموعات بيانات بنتائج معلومة:
/// مرتجع المورد ليس خصمًا من تكلفة المبيعات، خصم الفاتورة يوزع على بنود
/// ربحية المنتجات، مرتجع العميل يقلب الإيراد والتكلفة معًا، والمتبقي
/// اليومي/الدوري بتخصيص الدفعات على الفواتير (لا طرح تحصيل اليوم من مبيعاته).
/// </summary>
[Collection("database")]
public class ReportCalculationTests
{
    private readonly DatabaseFixture _fx;
    public ReportCalculationTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>اليوم البرمجي (UTC+3) — مطابق لحدود أيام التقارير.</summary>
    private static DateOnly ProgramToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static DateTime LocalDayUtc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).AddHours(-3);

    /// <summary>نقل CreatedAt لفاتورة إلى يوم برنامجي ثابت — عزل تواريخ كل اختبار
    /// (الخدمات تكتب DateTime.UtcNow، والاختبارات تشترك في قاعدة واحدة).</summary>
    private static async Task MoveInvoiceDayAsync(SaylDbContext db, int invoiceId, DateOnly day, int hour = 12)
        => await db.Invoices
            .Where(i => i.InvoiceId == invoiceId)
            .ExecuteUpdateAsync(u => u.SetProperty(
                i => i.CreatedAt, LocalDayUtc(day).AddHours(hour)));

    /// <summary>شراء مخزون ببند واحد — يعيد معرف فاتورة الشراء.</summary>
    private static async Task<int> PurchaseAsync(SaylDbContext db, int supplierId, int userId,
        int productId, int qty, decimal unitCost)
    {
        var result = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = qty, UnitCost = unitCost } }
        });
        Assert.True(result.IsSuccess, result.Message);
        return result.Data;
    }

    /// <summary>بيع نقدي/آجل بسيط — يعيد معرف فاتورة البيع.</summary>
    private static async Task<int> SaleAsync(SaylDbContext db, int userId, int? customerId,
        (int ProductId, int Qty, decimal UnitPrice)[] items,
        decimal discountAmount = 0m, decimal taxPercent = 0m, decimal paid = 0m, DateOnly? paymentDate = null)
    {
        var request = new DtoCheckoutRequest
        {
            CustomerId = customerId,
            UserId = userId,
            DiscountAmount = discountAmount,
            TaxPercent = taxPercent,
            Payment = new DtoPaymentInfo { Amount = paid, Method = "cash", PaymentDate = paymentDate ?? ProgramToday }
        };
        foreach (var (pid, qty, price) in items)
            request.Items.Add(new DtoCheckoutItem { ProductId = pid, ProductName = "x", Quantity = qty, UnitPrice = price });

        var result = await new clsCheckoutService(db).ProcessCheckoutAsync(request);
        Assert.True(result.IsSuccess, result.Message);
        return result.Data;
    }

    // ══ 1) مرتجع المورد ليس خصمًا من تكلفة مبيعات العملاء ══
    // شراء 10 @10، بيع 4 @25 (إيراد 100، COGS 40)، ثم مرتجع مورد بوحدتين (استرداد 20).
    // الصحيح: COGS يبقى 40 والربح 60 — الكود القديم كان يخصم 20 فيُظهر ربح 80 وهميًا.
    [Fact]
    public async Task ProfitSummary_SupplierReturn_DoesNotReduceSalesCogs()
    {
        string marker = $"rp1-{Guid.NewGuid():N}"[..12];
        DateOnly day = new(2026, 1, 10);

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int purchaseId = await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m);

        await using var saleDb = _fx.CreateContext();
        int saleId = await SaleAsync(saleDb, seed.UserId, null, new[] { (seed.ProductId, 4, 25m) },
            paid: 100m, paymentDate: day);
        await MoveInvoiceDayAsync(saleDb, saleId, day);

        await using var returnDb = _fx.CreateContext();
        var ret = await new clsReturnService(returnDb).CreateSupplierReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = purchaseId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
        });
        Assert.True(ret.IsSuccess, ret.Message);
        await MoveInvoiceDayAsync(returnDb, ret.Data, day);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var profit = await reports.GetProfitSummaryAsync(day, day);
        Assert.True(profit.IsSuccess, profit.Message);
        Assert.Equal(100m, profit.Data!.SalesRevenue);
        Assert.Equal(0m, profit.Data.ReturnsAmount);           // لا مرتجع عملاء
        Assert.Equal(40m, profit.Data.COGS);                   // لا يُخفَّض بتكلفة مرتجع المورد (20)
        Assert.Equal(60m, profit.Data.GrossProfit);            // 100 − 40 (لا 80)
        Assert.Equal(60m, profit.Data.NetProfit);

        var dashboard = await reports.GetDashboardSummaryAsync(day);
        Assert.True(dashboard.IsSuccess, dashboard.Message);
        Assert.Equal(100m, dashboard.Data!.MonthNetSales);
        Assert.Equal(60m, dashboard.Data.MonthGrossProfit);    // اتساق مع تقرير الأرباح
    }

    // ══ 2) ربحية المنتجات تخصم نصيبها من خصم الفاتورة (وتتطابق مع تقرير الأرباح) ══
    // فاتورة بمنتجين: P1 = 10×20 = 200، P2 = 5×10 = 50، الإجمالي 250، خصم 25 → صافي 225.
    // نسبة الصافي 0.9: إيراد P1 = 180 (لا 200) وإيراد P2 = 45 (لا 50).
    // التكلفة: P1 = 10×10 = 100، P2 = 5×4 = 20.
    // الربح: P1 = 80، P2 = 25 — ومجموعه 105 = الربح الإجمالي في تقرير الأرباح.
    [Fact]
    public async Task ProductProfit_AllocatesInvoiceDiscount_AndReconcilesWithProfitSummary()
    {
        string marker = $"rp2-{Guid.NewGuid():N}"[..12];
        DateOnly day = new(2026, 2, 10);

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);

        // منتج ثانٍ (سعر 10 / تكلفة شراء 4)
        var party2 = new Sayl.Core.Models.Party { PartyType = 'C', Name = $"عميل2-{marker}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(party2);
        var p2 = new Sayl.Core.Models.Product
        {
            Name = $"منتج2-{marker}",
            Type = "stock",
            BasePrice = 10m,
            CostPrice = 4m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(p2);
        await db.SaveChangesAsync();

        await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 15, 10m);
        await PurchaseAsync(db, seed.SupplierId, seed.UserId, p2.ProductId, 5, 4m);

        await using var saleDb = _fx.CreateContext();
        int saleId = await SaleAsync(saleDb, seed.UserId, null,
            new[] { (seed.ProductId, 10, 20m), (p2.ProductId, 5, 10m) },
            discountAmount: 25m, paid: 225m, paymentDate: day);
        await MoveInvoiceDayAsync(saleDb, saleId, day);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var products = await reports.GetProductProfitReportAsync(day, day);
        Assert.True(products.IsSuccess, products.Message);
        Assert.NotNull(products.Data);

        var p1Row = products.Data!.Single(x => x.ProductId == seed.ProductId);
        var p2Row = products.Data.Single(x => x.ProductId == p2.ProductId);

        Assert.Equal(10, p1Row.Quantity);
        Assert.Equal(180m, p1Row.Revenue);   // 200 × (225/250) — الخصم مخصم من الإيراد
        Assert.Equal(100m, p1Row.Cost);
        Assert.Equal(80m, p1Row.Profit);

        Assert.Equal(5, p2Row.Quantity);
        Assert.Equal(45m, p2Row.Revenue);    // 50 × 0.9
        Assert.Equal(20m, p2Row.Cost);
        Assert.Equal(25m, p2Row.Profit);

        // اتساق: مجموع ربحية المنتجات = إجمالي الربح في تقرير الأرباح
        var profit = await reports.GetProfitSummaryAsync(day, day);
        Assert.True(profit.IsSuccess, profit.Message);
        Assert.Equal(225m, profit.Data!.NetRevenue);
        Assert.Equal(120m, profit.Data.COGS);
        Assert.Equal(105m, profit.Data.GrossProfit);
        Assert.Equal(105m, products.Data.Sum(x => x.Profit));
    }

    // ══ 3) مرتجع العميل يقلب الإيراد والتكلفة معًا — وثلاثة تقارير متطابقة ══
    // بيع آجل لعميل: 10×20 = 200، خصم 20، ضريبة 10% (18) → صافي 198 (سياسة قائمة:
    // الإيراد = NetAmount والضريبة جزء منه — لا تُصنف مصروفًا ولا تُقتطع منه).
    // مرتجع وحدتين: الاسترداد الفعلي = 2×20×(198/200) = 39.60، وتكلفته 2×10 = 20.
    // الصحيح: إيراد صافٍ 158.40 وتكلفة صافية 80 وربح 78.40 في التقارير الثلاثة.
    // (الكود القديم كان يُبقي التكلفة كاملة 100 → ربح العميل 58.40 خاطئًا.)
    [Fact]
    public async Task CustomerReturn_ReversesRevenueAndCost_AllProfitReportsReconcile()
    {
        string marker = $"rp3-{Guid.NewGuid():N}"[..12];
        DateOnly day = new(2026, 3, 10);

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m);

        await using var saleDb = _fx.CreateContext();
        int saleId = await SaleAsync(saleDb, seed.UserId, seed.CustomerId,
            new[] { (seed.ProductId, 10, 20m) },
            discountAmount: 20m, taxPercent: 10m, paid: 198m, paymentDate: day);
        await MoveInvoiceDayAsync(saleDb, saleId, day);

        await using var returnDb = _fx.CreateContext();
        var ret = await new clsReturnService(returnDb).CreateCustomerReturnAsync(new DtoReturnRequest
        {
            OriginalInvoiceId = saleId,
            UserId = seed.UserId,
            Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 2 } }
        });
        Assert.True(ret.IsSuccess, ret.Message);
        await MoveInvoiceDayAsync(returnDb, ret.Data, day);

        // فاتورة المرتجع: استرداد نسبي بالصافي — ضريبة وخصم معكوسان نسبيًا
        var returnNet = await returnDb.Invoices.Where(i => i.InvoiceId == ret.Data).Select(i => i.NetAmount).SingleAsync();
        Assert.Equal(-39.60m, returnNet);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        var profit = await reports.GetProfitSummaryAsync(day, day);
        Assert.True(profit.IsSuccess, profit.Message);
        Assert.NotNull(profit.Data);
        Assert.Equal(198m, profit.Data!.SalesRevenue);     // الضريبة داخل الإيراد (سياسة الصافي القائمة)
        Assert.Equal(-39.60m, profit.Data.ReturnsAmount);
        Assert.Equal(158.40m, profit.Data.NetRevenue);
        Assert.Equal(80m, profit.Data.COGS);               // 100 − 20 (تكلفة المرتجع تُقلب)
        Assert.Equal(78.40m, profit.Data.GrossProfit);

        var products = await reports.GetProductProfitReportAsync(day, day);
        Assert.True(products.IsSuccess, products.Message);
        Assert.NotNull(products.Data);
        var row = Assert.Single(products.Data!);
        Assert.Equal(8, row.Quantity);
        Assert.Equal(158.40m, row.Revenue);                // 198 − 39.60 (بنفس نسبة الصافي)
        Assert.Equal(80m, row.Cost);
        Assert.Equal(78.40m, row.Profit);

        var customers = await reports.GetTopCustomersReportAsync(day, day);
        Assert.True(customers.IsSuccess, customers.Message);
        var cust = Assert.Single(customers.Data!);
        Assert.Equal(1, cust.InvoiceCount);
        Assert.Equal(158.40m, cust.NetRevenue);
        Assert.Equal(78.40m, cust.Profit);                 // التكلفة المرتجعة مقلوبة (لا 58.40)

        // اتساق كامل بين التقارير الثلاثة
        Assert.Equal(profit.Data.GrossProfit, products.Data.Sum(x => x.Profit));
        Assert.Equal(profit.Data.GrossProfit, customers.Data!.Sum(x => x.Profit));
    }

    // ══ 4) المتبقي بتخصيص الدفعات: تحصيل اليوم قد يسدد دَين يوم أمس ══
    // أمس: بيع آجل 100 (بلا سداد) → دين 100. اليوم: بيع آجل 50 ثم تحصيل عام 120
    // (التخصيص الأقدم أولًا: 100 يسدَّد بالكامل و20 من فاتورة اليوم).
    // الصحيح: متبقي اليوم 30 — الكود القديم (50 − 120 = −70) كان يجعل تحصيل
    // ديون قديمة يقلص متبقي مبيعات اليوم أو يقلبه سالبًا.
    [Fact]
    public async Task DailyAndPeriodicOutstanding_AllocatePaymentsAcrossInvoiceAges()
    {
        string marker = $"rp4-{Guid.NewGuid():N}"[..12];
        DateOnly dayB = new(2026, 4, 11); // "اليوم" في الاختبار
        DateOnly dayA = dayB.AddDays(-1); // "أمس"

        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        await PurchaseAsync(db, seed.SupplierId, seed.UserId, seed.ProductId, 10, 10m);

        // أمس: بيع آجل 5×20 = 100 بلا سداد
        await using var sale1Db = _fx.CreateContext();
        int invoiceYesterday = await SaleAsync(sale1Db, seed.UserId, seed.CustomerId,
            new[] { (seed.ProductId, 5, 20m) });
        await MoveInvoiceDayAsync(sale1Db, invoiceYesterday, dayA);

        // اليوم: بيع آجل 5×10 = 50 بلا سداد ثم تحصيل عام 120
        await using var sale2Db = _fx.CreateContext();
        int invoiceToday = await SaleAsync(sale2Db, seed.UserId, seed.CustomerId,
            new[] { (seed.ProductId, 5, 10m) });
        await MoveInvoiceDayAsync(sale2Db, invoiceToday, dayB);

        await using var payDb = _fx.CreateContext();
        var pay = await new clsPaymentService(payDb).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
        {
            PartyId = seed.CustomerId,
            Amount = 120m,
            PaymentDate = dayB,
            Method = "cash",
            UserId = seed.UserId // الفاعل: مستخدم مصرّح له بالتحصيل (بت شاشة العملاء)
        });
        Assert.True(pay.IsSuccess, pay.Message);

        await using var reportDb = _fx.CreateContext();
        var reports = new clsReportService(reportDb);

        // اليومي: تحصيل اليوم 120 يظهر محصَّلًا، لكن المتبقي على فواتير اليوم 30 فقط
        var daily = await reports.GetDailySalesAsync(dayB);
        Assert.True(daily.IsSuccess, daily.Message);
        Assert.Equal(50m, daily.Data!.TotalNet);
        Assert.Equal(120m, daily.Data.TotalCollected);
        Assert.Equal(30m, daily.Data.TotalOutstanding);

        // الدوري (من أمس): فواتيره صافيها 150 ومحصله 120 → متبقي 30،
        // والربح الإجمالي 50 مطابق لتقرير الأرباح لنفس النطاق (اتساق)
        var periodic = await reports.GetPeriodicReportAsync(dayA, dayB, enPeriodReportType.Quarterly);
        Assert.True(periodic.IsSuccess, periodic.Message);
        Assert.Equal(150m, periodic.Data!.TotalNetRevenue);
        Assert.Equal(120m, periodic.Data.TotalCollected);
        Assert.Equal(30m, periodic.Data.TotalOutstanding);
        Assert.Equal(50m, periodic.Data.TotalGrossProfit);

        // نطاق يوم اليوم وحده: فاتورته 50 وسدد منها التخصيص 20 → متبقي 30
        // (الكود القديم كان يعطي 50 − 120 = −70)
        var periodicToday = await reports.GetPeriodicReportAsync(dayB, dayB, enPeriodReportType.Quarterly);
        Assert.True(periodicToday.IsSuccess, periodicToday.Message);
        Assert.Equal(120m, periodicToday.Data!.TotalCollected);
        Assert.Equal(30m, periodicToday.Data.TotalOutstanding);

        var profit = await reports.GetProfitSummaryAsync(dayA, dayB);
        Assert.True(profit.IsSuccess, profit.Message);
        Assert.Equal(150m, profit.Data!.NetRevenue);
        Assert.Equal(50m, profit.Data.GrossProfit);
        Assert.Equal(periodic.Data.TotalGrossProfit, profit.Data.GrossProfit);
    }
}
