using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    public class clsReportService
    {
        private readonly SaylDbContext _dbContext;

        public clsReportService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // تقرير المبيعات اليومية
        // ─────────────────────────────────────────────────
        public async Task<Result<DailySalesVM>> GetDailySalesAsync(DateOnly date)
        {
            DateTime dayStart = date.ToDateTime(TimeOnly.MinValue);
            DateTime dayEnd = dayStart.AddDays(1);

            var invoicesQuery = _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= dayStart && i.CreatedAt < dayEnd);

            var sales = await invoicesQuery
                .GroupBy(i => 1)
                .Select(g => new
                {
                    Count = g.Count(),
                    Gross = g.Sum(i => i.TotalAmount),
                    Discount = g.Sum(i => i.DiscountAmount),
                    Tax = g.Sum(i => i.TaxAmount),
                    Net = g.Sum(i => i.NetAmount)
                })
                .FirstOrDefaultAsync();

            decimal collected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PaymentDate == date)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal returnsNet = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= dayStart && i.CreatedAt < dayEnd)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m;

            decimal net = sales?.Net ?? 0m;
            decimal netAfterReturns = net + returnsNet; // المرتجعات قيمتها سالبة

            var vm = new DailySalesVM
            {
                Date = date,
                InvoiceCount = sales?.Count ?? 0,
                TotalGross = sales?.Gross ?? 0m,
                TotalDiscount = sales?.Discount ?? 0m,
                TotalTax = sales?.Tax ?? 0m,
                TotalNet = netAfterReturns,
                TotalCollected = collected,
                TotalOutstanding = netAfterReturns - collected
            };

            return Result<DailySalesVM>.Ok(vm);
        }

        // ─────────────────────────────────────────────────
        // تقرير حالة المخزون
        // ─────────────────────────────────────────────────
        public async Task<Result<List<InventoryStatusVM>>> GetInventoryStatusAsync(bool lowStockOnly = false)
        {
            var query = from p in _dbContext.Products
                        join inv in _dbContext.Inventories on p.ProductId equals inv.ProductId
                        where p.Type == "stock" && p.IsActive
                        select new InventoryStatusVM
                        {
                            ProductId = p.ProductId,
                            ProductName = p.Name,
                            Unit = p.Unit,
                            QuantityAvailable = inv.QuantityAvailable ?? 0,
                            QuantityReserved = inv.QuantityReserved ?? 0,
                            ReorderLevel = p.ReorderLevel ?? 0,
                            IsLowStock = (inv.QuantityAvailable ?? 0) <= (p.ReorderLevel ?? 0),
                            LastCountedAt = inv.LastCountedAt
                        };

            if (lowStockOnly)
                query = query.Where(x => x.IsLowStock);

            var result = await query.OrderBy(x => x.ProductName).ToListAsync();
            return Result<List<InventoryStatusVM>>.Ok(result);
        }

        // ─────────────────────────────────────────────────
        // ملخص الأرباح (COGS من تكلفة البنود المسجلة)
        // ─────────────────────────────────────────────────
        public async Task<Result<ProfitSummaryVM>> GetProfitSummaryAsync(DateOnly fromDate, DateOnly toDate)
        {
            if (fromDate > toDate)
                return Result<ProfitSummaryVM>.Fail("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue);

            decimal salesRevenue = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= start && i.CreatedAt <= end)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m;

            decimal returnsAmount = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= start && i.CreatedAt <= end)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m; // قيمة سالبة

            decimal netRevenue = salesRevenue + returnsAmount;

            var saleInvoiceIds = _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= start && i.CreatedAt <= end)
                .Select(i => i.InvoiceId);

            decimal cogs = await _dbContext.InvoiceItems
                .Where(ii => saleInvoiceIds.Contains(ii.InvoiceId))
                .SumAsync(ii => (decimal?)(ii.ItemCost ?? 0m) * ii.Quantity) ?? 0m;

            decimal expenses = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate <= toDate)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            decimal grossProfit = netRevenue - cogs;
            decimal netProfit = grossProfit - expenses;

            var vm = new ProfitSummaryVM
            {
                FromDate = fromDate,
                ToDate = toDate,
                SalesRevenue = salesRevenue,
                ReturnsAmount = returnsAmount,
                NetRevenue = netRevenue,
                COGS = cogs,
                GrossProfit = grossProfit,
                Expenses = expenses,
                NetProfit = netProfit
            };

            return Result<ProfitSummaryVM>.Ok(vm);
        }

        // ─────────────────────────────────────────────────
        // المنتجات الأكثر مبيعاً
        // ─────────────────────────────────────────────────
        public async Task<Result<List<TopSellingProductVM>>> GetTopSellingProductsAsync(DateOnly fromDate, DateOnly toDate, int limit = 10)
        {
            if (fromDate > toDate)
                return Result<List<TopSellingProductVM>>.Fail("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            if (limit <= 0)
                limit = 10;

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue);

            var saleInvoiceIds = _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= start && i.CreatedAt <= end)
                .Select(i => i.InvoiceId);

            var result = await _dbContext.InvoiceItems
                .Where(ii => saleInvoiceIds.Contains(ii.InvoiceId))
                .GroupBy(ii => new { ii.ProductId, ii.Product.Name })
                .Select(g => new TopSellingProductVM
                {
                    ProductId = g.Key.ProductId,
                    ProductName = g.Key.Name,
                    TotalQuantitySold = g.Sum(ii => ii.Quantity),
                    TotalRevenue = g.Sum(ii => ii.ItemPrice * ii.Quantity)
                })
                .OrderByDescending(x => x.TotalQuantitySold)
                .Take(limit)
                .ToListAsync();

            return Result<List<TopSellingProductVM>>.Ok(result);
        }

        // ─────────────────────────────────────────────────
        // ملخص لوحة التحكم (اليوم + الشهر حتى تاريخه + مخزون)
        // ─────────────────────────────────────────────────
        public async Task<Result<DashboardSummaryVM>> GetDashboardSummaryAsync(DateOnly date)
        {
            DateTime dayStart = date.ToDateTime(TimeOnly.MinValue);
            DateTime dayEnd = dayStart.AddDays(1);

            DateOnly monthStartDate = new DateOnly(date.Year, date.Month, 1);
            DateTime mtdStart = monthStartDate.ToDateTime(TimeOnly.MinValue);

            // ── اليوم ──
            var todaySales = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= dayStart && i.CreatedAt < dayEnd)
                .GroupBy(i => 1)
                .Select(g => new { Count = g.Count(), Net = g.Sum(i => i.NetAmount) })
                .FirstOrDefaultAsync();

            decimal todayReturns = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= dayStart && i.CreatedAt < dayEnd)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m; // قيمة سالبة

            decimal todayCollected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PaymentDate == date)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal todayExpenses = await _dbContext.Expenses
                .Where(e => e.ExpenseDate == date)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            // ── الشهر حتى تاريخه ──
            decimal monthSalesNet = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= mtdStart && i.CreatedAt < dayEnd)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m;

            decimal monthReturnsNet = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= mtdStart && i.CreatedAt < dayEnd)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m; // قيمة سالبة

            decimal monthNetSales = monthSalesNet + monthReturnsNet;

            var mtdSaleInvoiceIds = _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= mtdStart && i.CreatedAt < dayEnd)
                .Select(i => i.InvoiceId);

            decimal cogs = await _dbContext.InvoiceItems
                .Where(ii => mtdSaleInvoiceIds.Contains(ii.InvoiceId))
                .SumAsync(ii => (decimal?)(ii.ItemCost ?? 0m) * ii.Quantity) ?? 0m;

            decimal monthExpenses = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= monthStartDate && e.ExpenseDate <= date)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            decimal monthGrossProfit = monthNetSales - cogs;
            decimal monthNetProfit = monthGrossProfit - monthExpenses;

            // ── المخزون والمنتجات ──
            int totalActiveProducts = await _dbContext.Products
                .CountAsync(p => p.IsActive);

            decimal totalStockValue = await (from p in _dbContext.Products
                                             join inv in _dbContext.Inventories on p.ProductId equals inv.ProductId
                                             where p.Type == "stock" && p.IsActive
                                             select (decimal)(inv.QuantityAvailable ?? 0) * p.CostPrice)
                                            .SumAsync();

            var lowStockQuery = from p in _dbContext.Products
                                join inv in _dbContext.Inventories on p.ProductId equals inv.ProductId
                                where p.Type == "stock" && p.IsActive
                                   && (inv.QuantityAvailable ?? 0) <= (p.ReorderLevel ?? 0)
                                select new InventoryStatusVM
                                {
                                    ProductId = p.ProductId,
                                    ProductName = p.Name,
                                    Unit = p.Unit,
                                    QuantityAvailable = inv.QuantityAvailable ?? 0,
                                    QuantityReserved = inv.QuantityReserved ?? 0,
                                    ReorderLevel = p.ReorderLevel ?? 0,
                                    IsLowStock = true,
                                    LastCountedAt = inv.LastCountedAt
                                };

            int lowStockCount = await lowStockQuery.CountAsync();

            var lowStockItems = await lowStockQuery
                .OrderBy(x => x.QuantityAvailable)
                .Take(10)
                .ToListAsync();

            // ── أفضل المنتجات (آخر 30 يوماً شاملة اليوم) ──
            var topProductsResult = await GetTopSellingProductsAsync(date.AddDays(-29), date, 5);
            var topProducts = topProductsResult.IsSuccess ? topProductsResult.Data! : new List<TopSellingProductVM>();

            var vm = new DashboardSummaryVM
            {
                Date = date,
                TodayInvoiceCount = todaySales?.Count ?? 0,
                TodayNetSales = (todaySales?.Net ?? 0m) + todayReturns,
                TodayCollected = todayCollected,
                TodayReturns = todayReturns,
                TodayExpenses = todayExpenses,
                MonthNetSales = monthNetSales,
                MonthExpenses = monthExpenses,
                MonthGrossProfit = monthGrossProfit,
                MonthNetProfit = monthNetProfit,
                LowStockCount = lowStockCount,
                TotalActiveProducts = totalActiveProducts,
                TotalStockValue = totalStockValue,
                TopProducts = topProducts,
                LowStockItems = lowStockItems
            };

            return Result<DashboardSummaryVM>.Ok(vm);
        }

        // ─────────────────────────────────────────────────
        // سلسلة المبيعات الزمنية (يومي / أسبوعي / شهري)
        // ─────────────────────────────────────────────────
        public async Task<Result<List<SalesTrendPointVM>>> GetSalesTrendAsync(
            DateOnly fromDate,
            DateOnly toDate,
            enTrendGroupBy groupBy = enTrendGroupBy.Day)
        {
            if (fromDate > toDate)
                return Result<List<SalesTrendPointVM>>.Fail("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue);

            // تجميع يومي داخل قاعدة البيانات ثم دمج الفترات (أسبوع/شهر) في الذاكرة
            var rows = await _dbContext.Invoices
                .Where(i => i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= start && i.CreatedAt <= end
                         && (i.Type == enInvoicesType.sale || i.Type == enInvoicesType.customer_return))
                .GroupBy(i => new { Day = i.CreatedAt!.Value.Date, i.Type })
                .Select(g => new
                {
                    g.Key.Day,
                    g.Key.Type,
                    Count = g.Count(),
                    Gross = g.Sum(i => i.TotalAmount),
                    Net = g.Sum(i => i.NetAmount)
                })
                .ToListAsync();

            var dailyPoints = rows
                .GroupBy(r => r.Day.Date)
                .Select(g =>
                {
                    decimal salesNet = g.Where(x => x.Type == enInvoicesType.sale).Sum(x => x.Net);
                    decimal returnsNet = g.Where(x => x.Type == enInvoicesType.customer_return).Sum(x => x.Net); // قيمتها سالبة

                    return new SalesTrendPointVM
                    {
                        PeriodStart = DateOnly.FromDateTime(g.Key),
                        PeriodEnd = DateOnly.FromDateTime(g.Key),
                        InvoiceCount = g.Where(x => x.Type == enInvoicesType.sale).Sum(x => x.Count),
                        GrossSales = g.Where(x => x.Type == enInvoicesType.sale).Sum(x => x.Gross),
                        ReturnsAmount = returnsNet,
                        NetSales = salesNet + returnsNet
                    };
                })
                .ToList();

            List<SalesTrendPointVM> result;

            if (groupBy == enTrendGroupBy.Day)
            {
                result = dailyPoints.OrderBy(p => p.PeriodStart).ToList();
            }
            else
            {
                result = dailyPoints
                    .GroupBy(p => groupBy == enTrendGroupBy.Week
                        ? GetWeekStart(p.PeriodStart)
                        : new DateOnly(p.PeriodStart.Year, p.PeriodStart.Month, 1))
                    .Select(g => new SalesTrendPointVM
                    {
                        PeriodStart = g.Key,
                        PeriodEnd = g.Max(x => x.PeriodStart),
                        InvoiceCount = g.Sum(x => x.InvoiceCount),
                        GrossSales = g.Sum(x => x.GrossSales),
                        ReturnsAmount = g.Sum(x => x.ReturnsAmount),
                        NetSales = g.Sum(x => x.NetSales)
                    })
                    .OrderBy(p => p.PeriodStart)
                    .ToList();
            }

            return Result<List<SalesTrendPointVM>>.Ok(result);
        }

        private static DateOnly GetWeekStart(DateOnly date)
        {
            // بداية الأسبوع: الإثنين
            int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return date.AddDays(-diff);
        }

        // ─────────────────────────────────────────────────
        // الدفعات القريبة من انتهاء الصلاحية (تنبيهات 30/60/90 يوم)
        // ─────────────────────────────────────────────────
        public async Task<Result<List<ExpiringBatchVM>>> GetExpiringBatchesAsync(int withinDays = 90)
        {
            if (withinDays <= 0)
                withinDays = 90;

            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly until = today.AddDays(withinDays);

            var result = await _dbContext.Batches
                .Include(b => b.Product)
                .Where(b => b.BatchQuantity > 0
                         && b.ExpiryDate != null
                         && b.ExpiryDate <= until)
                .OrderBy(b => b.ExpiryDate)
                .Select(b => new ExpiringBatchVM
                {
                    BatchId = b.BatchId,
                    ProductId = b.ProductId,
                    ProductName = b.Product.Name,
                    BatchNumber = b.BatchNumber,
                    ExpiryDate = b.ExpiryDate!.Value,
                    Quantity = b.BatchQuantity,
                    DaysUntilExpiry = b.ExpiryDate!.Value.DayNumber - today.DayNumber,
                    IsExpired = b.ExpiryDate < today
                })
                .ToListAsync();

            return Result<List<ExpiringBatchVM>>.Ok(result);
        }
    }
}
