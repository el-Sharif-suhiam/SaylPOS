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
        // التقرير الدوري (ربع سنوي / نصف سنوي / سنوي) — فواتير مفصلة ودقيقة
        // ─────────────────────────────────────────────────
        public async Task<Result<PeriodicReportVM>> GetPeriodicReportAsync(
            DateOnly fromDate,
            DateOnly toDate,
            enPeriodReportType reportType,
            bool includeInvoiceDetails = true)
        {
            if (fromDate > toDate)
                return Result<PeriodicReportVM>.Fail("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            var periodStarts = BuildPeriodStarts(fromDate, toDate, reportType);

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue);

            // ── لفّ واحد على الفواتير المكتملة (بيع + مرتجع بيع) في النطاق ──
            var invoices = await _dbContext.Invoices
                .Where(i => i.CreatedAt >= start && i.CreatedAt <= end
                         && i.Status == enInvoicesStatus.completed
                         && (i.Type == enInvoicesType.sale || i.Type == enInvoicesType.customer_return))
                .Select(i => new
                {
                    i.InvoiceId,
                    i.Type,
                    i.NetAmount,
                    Day = i.CreatedAt!.Value.Date,
                    i.PartyId,
                    i.PartyType,
                    i.TotalAmount,
                    i.DiscountAmount,
                    i.TaxAmount,
                    UserName = i.User.Username,
                    PartyName = i.Party != null ? i.Party.Name : null
                })
                .ToListAsync();

            var invoiceIds = invoices.Select(i => i.InvoiceId).ToList();

            // ── بنود الفواتير المرتبطة (لحساب COGS الصافي) ──
            var itemRows = await (from ii in _dbContext.InvoiceItems
                                  where invoiceIds.Contains(ii.InvoiceId)
                                  select new
                                  {
                                      ii.InvoiceId,
                                      ItemCost = ii.ItemCost ?? 0m,
                                      ii.Quantity
                                  }).ToListAsync();

            var cogsByInvoice = itemRows
                .GroupBy(x => x.InvoiceId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ItemCost * x.Quantity));

            // ── المصروفات في النطاق ──
            var expensesByDay = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate <= toDate)
                .GroupBy(e => new { e.ExpenseDate })
                .Select(g => new { g.Key.ExpenseDate, Total = g.Sum(e => e.Amount) })
                .ToListAsync();

            var expensesByDate = expensesByDay.ToDictionary(x => x.ExpenseDate, x => x.Total);

            // ── الدفعات (المحصل على مدى النطاق) ──
            decimal collected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // المحصل مجمّعاً باليوم لدعم حساب كل فترة من الذاكرة (بدل استعلام per-period)
            var collectedByDate = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .GroupBy(p => p.PaymentDate)
                .Select(g => new { Date = g.Key, Total = g.Sum(p => p.Amount) })
                .ToDictionaryAsync(x => x.Date, x => x.Total);

            // ── تجميع الفترات ──
            var periods = new List<PeriodicPeriodVM>();
            var invoiceDetails = new List<InvoicePrintVM>();

            DateOnly? fromFilled = null;
            DateOnly? toFilled = null;

            for (int idx = 0; idx < periodStarts.Count; idx++)
            {
                DateOnly pFrom = periodStarts[idx];
                DateOnly pTo = idx + 1 < periodStarts.Count
                    ? periodStarts[idx + 1].AddDays(-1)
                    : toDate;

                DateTime pStartD = pFrom.ToDateTime(TimeOnly.MinValue);
                DateTime pEndD = pTo.ToDateTime(TimeOnly.MaxValue);

                var periodInvoices = invoices.Where(i => i.Day >= pStartD && i.Day <= pEndD).ToList();

                decimal salesRev = periodInvoices.Where(i => i.Type == enInvoicesType.sale).Sum(i => i.NetAmount);
                decimal returnsRev = periodInvoices.Where(i => i.Type == enInvoicesType.customer_return).Sum(i => i.NetAmount);
                decimal cogsSales = periodInvoices
                    .Where(i => i.Type == enInvoicesType.sale)
                    .Sum(i => cogsByInvoice.GetValueOrDefault(i.InvoiceId, 0m));
                decimal cogsReturns = periodInvoices
                    .Where(i => i.Type == enInvoicesType.customer_return)
                    .Sum(i => cogsByInvoice.GetValueOrDefault(i.InvoiceId, 0m));

                decimal netRev = salesRev + returnsRev;
                decimal cogsNet = cogsSales - cogsReturns;
                decimal gross = netRev - cogsNet;

                decimal exp = expensesByDate
                    .Where(kv => kv.Key >= pFrom && kv.Key <= pTo)
                    .Sum(kv => kv.Value);

                decimal periodCollected = collectedByDate
                    .Where(kv => kv.Key >= pFrom && kv.Key <= pTo)
                    .Sum(kv => kv.Value);

                periods.Add(new PeriodicPeriodVM
                {
                    PeriodLabel = $"{pFrom:yyyy-MM-dd} ← {pTo:yyyy-MM-dd}",
                    FromDate = pFrom,
                    ToDate = pTo,
                    InvoiceCount = periodInvoices.Count(i => i.Type == enInvoicesType.sale),
                    SalesRevenue = salesRev,
                    ReturnsAmount = returnsRev,
                    NetRevenue = netRev,
                    CogsSales = cogsSales,
                    CogsReturns = cogsReturns,
                    CogsNet = cogsNet,
                    GrossProfit = gross,
                    Expenses = exp,
                    NetProfit = gross - exp,
                    Collected = periodCollected,
                    Outstanding = netRev - periodCollected
                });

                if (fromFilled == null) fromFilled = pFrom;
                toFilled = pTo;

                // تفاصيل فواتير الفترة (اختياري)
                if (includeInvoiceDetails)
                {
                    foreach (var inv in periodInvoices.OrderBy(x => x.Day))
                    {
                        var detail = new InvoicePrintVM
                        {
                            InvoiceId = inv.InvoiceId,
                            Type = inv.Type.ToString(),
                            Status = "completed",
                            PaymentStatus = "",
                            PartyId = inv.PartyId,
                            PartyName = inv.PartyName,
                            CashierName = inv.UserName ?? "",
                            TotalAmount = inv.TotalAmount,
                            DiscountAmount = inv.DiscountAmount,
                            TaxAmount = inv.TaxAmount,
                            NetAmount = inv.NetAmount,
                            CreatedAt = inv.Day
                        };
                        invoiceDetails.Add(detail);
                    }
                }
            }

            // ── تجميع الإيرادات اليومية (بدل سرد كل فاتورة على حدة) ──
            var dailyLines = invoices
                .GroupBy(i => DateOnly.FromDateTime(i.Day))
                .Select(g => new PeriodDailyLineVM
                {
                    Date = g.Key,
                    InvoiceCount = g.Count(x => x.Type == enInvoicesType.sale),
                    GrossSales = g.Where(x => x.Type == enInvoicesType.sale).Sum(x => x.TotalAmount),
                    ReturnsAmount = g.Where(x => x.Type == enInvoicesType.customer_return).Sum(x => x.NetAmount),
                    NetSales = g.Sum(x => x.NetAmount),
                    CogsNet = g.Where(x => x.Type == enInvoicesType.sale).Sum(x => cogsByInvoice.GetValueOrDefault(x.InvoiceId, 0m))
                             - g.Where(x => x.Type == enInvoicesType.customer_return).Sum(x => cogsByInvoice.GetValueOrDefault(x.InvoiceId, 0m))
                })
                .OrderBy(x => x.Date)
                .ToList();
            foreach (var d in dailyLines)
                d.GrossProfit = d.NetSales - d.CogsNet;

            // ── ملخص الجلسات المكتملة داخل المدة (نظير تقرير Z) ──
            // نجمع كل البيانات دفعة واحدة ثم نبني السطور في الذاكرة (تجنّب N+1)
            var sessions = await _dbContext.PosSessions
                .Include(s => s.User)
                .Where(s => s.Status == enPosSessionStatusType.closed
                         && s.ClosedAt >= start && s.ClosedAt <= end)
                .OrderBy(s => s.ClosedAt)
                .ToListAsync();

            var sessionIds = sessions.Select(s => (int?)s.SessionId).ToList();

            // فواتير الجلسات المكتملة (مرة واحدة لكل الجلسات)
            var sessionInvoiceRows = await _dbContext.Invoices
                .Where(i => sessionIds.Contains(i.SessionId) && i.Status == enInvoicesStatus.completed)
                .Select(i => new { i.SessionId, i.InvoiceId, i.Type, i.NetAmount })
                .ToListAsync();

            var saleBySession = sessionInvoiceRows
                .Where(x => x.Type == enInvoicesType.sale)
                .GroupBy(x => x.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sessionInvoiceIds = sessionInvoiceRows.Select(x => x.InvoiceId).ToList();
            var invoiceSessionMap = sessionInvoiceRows
                .GroupBy(x => x.InvoiceId)
                .ToDictionary(g => g.Key, g => g.First().SessionId);

            // دفعات الجلسات (نقدي income/outcome) دفعة واحدة
            var sessionPayments = await _dbContext.Payments
                .Where(p => p.InvoiceId != null && sessionInvoiceIds.Contains(p.InvoiceId.Value))
                .Select(p => new { p.InvoiceId, p.PaymentMethod, p.Type, p.Amount })
                .ToListAsync();

            var payBySession = sessionPayments
                .Where(p => p.InvoiceId!.HasValue && invoiceSessionMap.ContainsKey(p.InvoiceId!.Value))
                .GroupBy(p => invoiceSessionMap[p.InvoiceId!.Value])
                .ToDictionary(g => g.Key, g => g.ToList());

            // حركات النقد (قبض/صرف) لكل الجلسات دفعة واحدة
            var cashMoves = await _dbContext.CashMovements
                .Where(m => sessionIds.Contains(m.SessionId))
                .Select(m => new { m.SessionId, m.MovementType, m.Amount })
                .ToListAsync();

            var moveBySession = cashMoves
                .GroupBy(m => m.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sessionLines = new List<PeriodSessionLineVM>();
            foreach (var s in sessions)
            {
                int invoiceCount = 0;
                decimal netSales = 0m;
                if (saleBySession.TryGetValue(s.SessionId, out var saleList))
                {
                    invoiceCount = saleList.Count;
                    netSales = saleList.Sum(x => x.NetAmount);
                }

                decimal cashSales = 0m, cashReturns = 0m;
                if (payBySession.TryGetValue(s.SessionId, out var payList))
                {
                    cashSales = payList.Where(p => p.PaymentMethod == "cash" && p.Type == enPaymentType.income).Sum(p => p.Amount);
                    cashReturns = payList.Where(p => p.PaymentMethod == "cash" && p.Type == enPaymentType.outcome).Sum(p => p.Amount);
                }

                decimal cashIn = 0m, cashOut = 0m;
                if (moveBySession.TryGetValue(s.SessionId, out var moveList))
                {
                    cashIn = moveList.Where(m => m.MovementType == 'I').Sum(m => m.Amount);
                    cashOut = moveList.Where(m => m.MovementType == 'O').Sum(m => m.Amount);
                }

                sessionLines.Add(new PeriodSessionLineVM
                {
                    SessionId = s.SessionId,
                    UserName = s.User?.Username ?? "",
                    OpeningAmount = s.OpeningAmount,
                    CashSales = cashSales,
                    CashReturns = cashReturns,
                    CashIn = cashIn,
                    CashOut = cashOut,
                    ExpectedCash = s.OpeningAmount + cashSales - cashReturns + cashIn - cashOut,
                    InvoiceCount = invoiceCount,
                    NetSales = netSales,
                    StartedAt = s.StartedAt ?? DateTime.MinValue,
                    ClosedAt = s.ClosedAt
                });
            }

            // ── حساب الإجماليات ──
            decimal totalSales = periods.Sum(p => p.SalesRevenue);
            decimal totalReturns = periods.Sum(p => p.ReturnsAmount);
            decimal totalNetRev = totalSales + totalReturns;
            decimal totalCogsNet = periods.Sum(p => p.CogsNet);
            decimal totalGross = totalNetRev - totalCogsNet;
            decimal totalExpenses = periods.Sum(p => p.Expenses);
            decimal totalNetProfit = totalGross - totalExpenses;
            decimal totalOutstanding = totalNetRev - collected;

            var report = new PeriodicReportVM
            {
                ReportTypeName = reportType switch
                {
                    enPeriodReportType.Quarterly => "ربع سنوي",
                    enPeriodReportType.HalfYearly => "نصف سنوي",
                    _ => "سنوي"
                },
                FromDate = fromFilled ?? fromDate,
                ToDate = toFilled ?? toDate,
                Periods = periods,
                TotalSales = totalSales,
                TotalReturns = totalReturns,
                TotalNetRevenue = totalNetRev,
                TotalCogsNet = totalCogsNet,
                TotalGrossProfit = totalGross,
                TotalExpenses = totalExpenses,
                TotalNetProfit = totalNetProfit,
                TotalCollected = collected,
                TotalOutstanding = totalOutstanding,
                DailyLines = dailyLines,
                SessionLines = sessionLines,
                Invoices = includeInvoiceDetails ? invoiceDetails : new List<InvoicePrintVM>()
            };

            return Result<PeriodicReportVM>.Ok(report);
        }

        /// <summary>يقسّم النطاق الزمني إلى بدايات فترات متساوية حسب النوع (شهر / ربع / نصف / سنة).</summary>
        private static List<DateOnly> BuildPeriodStarts(DateOnly fromDate, DateOnly toDate, enPeriodReportType reportType)
        {
            int monthStep = (int)reportType;
            var starts = new List<DateOnly>();

            DateOnly cursor = fromDate;
            while (cursor <= toDate)
            {
                starts.Add(cursor);
                cursor = cursor.AddMonths(monthStep);
            }

            if (starts.Count == 0)
                starts.Add(fromDate);

            return starts;
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
