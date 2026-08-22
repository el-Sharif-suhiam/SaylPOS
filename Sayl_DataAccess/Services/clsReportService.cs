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
    }
}
