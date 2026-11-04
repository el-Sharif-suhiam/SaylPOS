using Common.DTOs;
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
            DateTime dayStart = date.ToDateTime(TimeOnly.MinValue).AddHours(-3);
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

            // تحصيلات العملاء فقط (PartyType='C') — دفعات الطرف تحمل جهة الحركة
            // ('C' عميل / 'S' مورد حتى للطرف المشترك 'B')، فاسترداد مورد (دخل بطرف
            // 'S') ليس تحصيلًا من عميل — نفس تصنيف GetCashFlowReportAsync.
            decimal collected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PartyType == 'C'
                         && p.PaymentDate == date)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal returnsNet = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                         && i.Status == enInvoicesStatus.completed
                         && i.CreatedAt >= dayStart && i.CreatedAt < dayEnd)
                .SumAsync(i => (decimal?)i.NetAmount) ?? 0m;

            // ══ المتبقي بتخصيص الدفعات على الفواتير (لا طرح تحصيل اليوم من مبيعات اليوم) ══
            // تحصيل اليوم قد يسدد فواتير أيام سابقة (التخصيص الأقدم أولًا)، وفواتير
            // اليوم قد تكون سُددت من تحصيل قديم أو مرتجعات ضدها تُخفّض دينها.
            // لكل فاتورة بيع مكتملة اليوم: متبقيها الفعلي = صافيها + مرتجعاتها − دفعاتها
            // (المرتبطة ونصيب العام المخصص)، ومجموعها هو المتحرك على فواتير اليوم.
            var todaySaleInvoiceIds = await invoicesQuery
                .Select(i => i.InvoiceId)
                .ToListAsync();

            var outstandingByInvoice = await clsPaymentAllocation
                .GetOutstandingForInvoicesAsync(_dbContext, todaySaleInvoiceIds);

            decimal outstanding = todaySaleInvoiceIds
                .Sum(id => Math.Max(0m, outstandingByInvoice.GetValueOrDefault(id)));

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
                TotalOutstanding = outstanding
            };

            return Result<DailySalesVM>.Ok(vm);
        }

        // ─────────────────────────────────────────────────
        // تقرير حالة المخزون
        // ─────────────────────────────────────────────────
        public async Task<Result<List<InventoryStatusVM>>> GetInventoryStatusAsync(bool lowStockOnly = false, int[]? productIds = null)
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

            // C1: تقرير جزئي لأصناف بعينها (IN(...)) — نافذة POS بعد البيع
            // تُحدث أصناف السلة الملموسة فقط بدل نقل الكتالوج كاملًا.
            if (productIds is { Length: > 0 })
                query = query.Where(x => productIds.Contains(x.ProductId));

            var result = await query.OrderBy(x => x.ProductName).ToListAsync();
            return Result<List<InventoryStatusVM>>.Ok(result);
        }

        // C4: نسخة مرقّمة من حالة المخزون — التصفح على مستوى SQL (Count/Skip/Take)
        // بدل نقل الكتالوج كاملًا ثم تقطيع جانب العميل في شاشة المخزون.
        public async Task<Result<PagedResult<InventoryStatusVM>>> GetInventoryStatusPagedAsync(bool lowStockOnly = false, int page = 1, int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

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

            int total = await query.CountAsync();
            var items = await query
                .OrderBy(x => x.ProductName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Result<PagedResult<InventoryStatusVM>>.Ok(new PagedResult<InventoryStatusVM>
            {
                Items = items,
                TotalCount = total,
                Page = page,
                PageSize = pageSize
            });
        }

        // ─────────────────────────────────────────────────
        // ملخص الأرباح (COGS من تكلفة البنود المسجلة)
        // ─────────────────────────────────────────────────
        public async Task<Result<ProfitSummaryVM>> GetProfitSummaryAsync(DateOnly fromDate, DateOnly toDate)
        {
            if (fromDate > toDate)
                return Result<ProfitSummaryVM>.Fail("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

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

            // مرتجع العميل وحده يقلب تكلفة مبيعات: بند المرتجع يحمل تكلفة البنود
            // المُباعة الأصلية (ItemCost التاريخية) — راجع docs/COSTING.md.
            // مرتجع المورد ليس خصمًا من تكلفة مبيعات العملاء: بضاعته لم تدخل
            // المبيعات أصلًا، وتكلفتها تُرد من المورد (استرداد شراء) وتُخصم من
            // الدفعة والمخزون — إدخالها هنا كان يضخّم الربح وهميًا.
            decimal returnsCogs = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.Type == enInvoicesType.customer_return
                         && ii.Invoice.Status == enInvoicesStatus.completed
                         && ii.Invoice.CreatedAt >= start && ii.Invoice.CreatedAt <= end)
                .SumAsync(ii => (decimal?)(ii.ItemCost ?? 0m) * ii.Quantity) ?? 0m;

            decimal cogsNet = cogs - returnsCogs;

            decimal expenses = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate <= toDate)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            decimal grossProfit = netRevenue - cogsNet;
            decimal netProfit = grossProfit - expenses;

            var vm = new ProfitSummaryVM
            {
                FromDate = fromDate,
                ToDate = toDate,
                SalesRevenue = salesRevenue,
                ReturnsAmount = returnsAmount,
                NetRevenue = netRevenue,
                COGS = cogsNet,
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

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

            // ── لفّ واحد على الفواتير المكتملة (بيع + مرتجع بيع) في النطاق ──
            var invoices = (await _dbContext.Invoices
                .Where(i => i.CreatedAt >= start && i.CreatedAt <= end
                         && i.Status == enInvoicesStatus.completed
                         && (i.Type == enInvoicesType.sale || i.Type == enInvoicesType.customer_return))
                .Select(i => new
                {
                    i.InvoiceId,
                    i.Type,
                    i.NetAmount,
                    i.CreatedAt,
                    i.PartyId,
                    i.PartyType,
                    i.TotalAmount,
                    i.DiscountAmount,
                    i.TaxAmount,
                    UserName = i.User.Username,
                    PartyName = i.Party != null ? i.Party.Name : null
                })
                .ToListAsync())
                .Select(x => new
                {
                    x.InvoiceId,
                    x.Type,
                    x.NetAmount,
                    Day = ProgramDay(x.CreatedAt!.Value),
                    x.PartyId,
                    x.PartyType,
                    x.TotalAmount,
                    x.DiscountAmount,
                    x.TaxAmount,
                    x.UserName,
                    x.PartyName
                })
                .ToList();

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

            // ── الدفعات (تحصيلات العملاء على مدى النطاق — PartyType='C') ──
            // استرداد المورد (دخل بطرف 'S') ليس تحصيلًا من عميل — نفس تصنيف CashFlow.
            decimal collected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PartyType == 'C'
                         && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // المحصل مجمّعاً باليوم لدعم حساب كل فترة من الذاكرة (بدل استعلام per-period)
            var collectedByDate = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PartyType == 'C'
                         && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .GroupBy(p => p.PaymentDate)
                .Select(g => new { Date = g.Key, Total = g.Sum(p => p.Amount) })
                .ToDictionaryAsync(x => x.Date, x => x.Total);

            // ── المتبقي لكل فاتورة بيع بتخصيص الدفعات (لا طرح تحصيل الفترة من إيرادها) ──
            // تحصيل الفترة قد يسدد ديونًا من فترات سابقة، وفواتير الفترة قد تكون
            // سُددت بتحصيلات لاحقة — المتبقي الصحيح يُحسب على مستوى الفاتورة.
            var outstandingByInvoice = await clsPaymentAllocation
                .GetOutstandingForInvoicesAsync(_dbContext, invoiceIds);

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

                var periodInvoices = invoices.Where(i => i.Day >= pFrom && i.Day <= pTo).ToList();

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

                // متبقي فواتير البيع داخل الفترة بتخصيص الدفعات (المرتجعات مخفَّضة
                // في المتبقي نفسه عبر OriginalInvoiceId — راجع clsPaymentAllocation)
                decimal periodOutstanding = periodInvoices
                    .Where(i => i.Type == enInvoicesType.sale)
                    .Sum(i => Math.Max(0m, outstandingByInvoice.GetValueOrDefault(i.InvoiceId)));

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
                    Outstanding = periodOutstanding
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
                            CreatedAt = inv.Day.ToDateTime(TimeOnly.MinValue)
                        };
                        invoiceDetails.Add(detail);
                    }
                }
            }

            // ── تجميع الإيرادات اليومية (بدل سرد كل فاتورة على حدة) ──
            var dailyLines = invoices
                .GroupBy(i => i.Day)
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

            // دفعات الجلسات دفعة واحدة — مساران بلا ازدواج (نفس منهج clsCashService):
            //  • الجديد: دفعات موجّهة صراحة لدرج الجلسة (SessionId) ولو بلا فاتورة
            //    (تحصيل عام/سداد مورد/استرداد) أو بفاتورة أقدم (تحصيل دَين قديم اليوم).
            //  • المتوافق: دفعات ما قبل Upgrade_06 (SessionId=null) تُسقط على جلسة فاتورتها.
            // كل دفعة تُحسب مرة واحدة: مفتاح التجميع SessionId أولًا ثم جلسة الفاتورة.
            var sessionPayments = await _dbContext.Payments
                .Where(p => sessionIds.Contains(p.SessionId)
                         || (p.SessionId == null && p.InvoiceId != null
                          && sessionInvoiceIds.Contains(p.InvoiceId.Value)))
                .Select(p => new { p.InvoiceId, p.SessionId, p.PaymentMethod, p.Type, p.PartyType, p.Amount })
                .ToListAsync();

            var payBySession = sessionPayments
                .GroupBy(p => p.SessionId ?? invoiceSessionMap[p.InvoiceId!.Value])
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

                // تصنيف نقدي بحسب جهة الدفعة (PartyType 'C' عميل / 'S' مورد — حتى
                // للطرف المشترك) — نفس معادلة clsCashService.GetSessionCashSummaryAsync:
                // النقد الداخل يشمل المبيعات والتحصيلات واستردادات الموردين، وسداد
                // المورد النقدي يخرج من الدرج (ليس مرتجع عميل).
                decimal cashSales = 0m, cashReturns = 0m, cashSupplierPayments = 0m;
                if (payBySession.TryGetValue(s.SessionId, out var payList))
                {
                    cashSales = payList.Where(p => p.PaymentMethod == "cash" && p.Type == enPaymentType.income).Sum(p => p.Amount);
                    cashReturns = payList.Where(p => p.PaymentMethod == "cash" && p.Type == enPaymentType.outcome && p.PartyType == 'C').Sum(p => p.Amount);
                    cashSupplierPayments = payList.Where(p => p.PaymentMethod == "cash" && p.Type == enPaymentType.outcome && p.PartyType == 'S').Sum(p => p.Amount);
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
                    CashSupplierPayments = cashSupplierPayments,
                    CashIn = cashIn,
                    CashOut = cashOut,
                    ExpectedCash = s.OpeningAmount + cashSales - cashReturns - cashSupplierPayments + cashIn - cashOut,
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
            // المتبقي الكلي = مجموع متبقي فواتير البيع بتخصيص الدفعات (فواتير الفترات كلها)
            decimal totalOutstanding = periods.Sum(p => p.Outstanding);

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

        private static DateOnly ProgramDay(DateTime startUtc)
            => DateOnly.FromDateTime(startUtc.AddHours(3));

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

            DateTime start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            DateTime end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

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
            DateTime dayStart = date.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            DateTime dayEnd = dayStart.AddDays(1);

            DateOnly monthStartDate = new DateOnly(date.Year, date.Month, 1);
            DateTime mtdStart = monthStartDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);

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

            // تحصيلات العملاء فقط (PartyType='C') — استرداد مورد (دخل بطرف 'S') مستثنى
            decimal todayCollected = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.PartyType == 'C'
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

            // مرتجع العميل فقط يقلب تكلفة المبيعات — مرتجع المورد ليس خصمًا من
            // تكلفة مبيعات العملاء (تكلفتها تُرد من المورد، راجع GetProfitSummaryAsync)
            decimal returnsCogs = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.Type == enInvoicesType.customer_return
                         && ii.Invoice.Status == enInvoicesStatus.completed
                         && ii.Invoice.CreatedAt >= mtdStart && ii.Invoice.CreatedAt < dayEnd)
                .SumAsync(ii => (decimal?)(ii.ItemCost ?? 0m) * ii.Quantity) ?? 0m;

            decimal cogsNet = cogs - returnsCogs;

            decimal monthExpenses = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= monthStartDate && e.ExpenseDate <= date)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            decimal monthGrossProfit = monthNetSales - cogsNet;
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

            DateOnly today = ProgramDay(DateTime.UtcNow);
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

        // ═════════════════════════════════════════════════
        // تقارير الربحية والتحصيل — تعتمد فواتير مكتملة فقط
        // (المرتجعات تخفض الإيراد والتكلفة معًا: بند المرتجع يحمل نفس سعر وتكلفة الأصل)
        // ═════════════════════════════════════════════════

        /// <summary>ربحية كل منتج في فترة: كمية/إيراد/تكلفة/ربح/هامش/حصة — مرتبًا بالربح تنازليًا.</summary>
        public async Task<Result<List<ProductProfitVM>>> GetProductProfitReportAsync(DateOnly fromDate, DateOnly toDate)
        {
            // حدود اليوم البرنامجية (UTC+3) — مطابقة بقية التقارير
            var start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            var end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

            // ══ إيراد البند الفعلي يخصم نصيبه من خصم رأس الفاتورة (والضريبة وفق سياسة الصافي) ══
            // الفاتورة بُنيت: الصافي = الإجمالي − الخصم + الضريبة، والإيراد المعتمد في كل
            // تقارير الأرباح هو NetAmount (الضريبة جزء منه بلا تصنيف منفرد — سياسة قائمة).
            // نصيب البند = قيمته الخام × (الصافي/الإجمالي) — مجموع البنود يساوي صافي
            // الفاتورة بالضبط، فيتطابق مجموع ربحية المنتجات مع إجمالي ربح تقرير الأرباح.
            var saleRows = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.Type == enInvoicesType.sale
                          && ii.Invoice.Status == enInvoicesStatus.completed
                          && ii.Invoice.CreatedAt >= start && ii.Invoice.CreatedAt <= end)
                .Select(ii => new
                {
                    ii.ProductId,
                    ProductName = ii.Product.Name,
                    ii.Quantity,
                    ii.ItemPrice,
                    ItemCost = ii.ItemCost ?? 0m,
                    InvoiceTotal = ii.Invoice.TotalAmount,
                    InvoiceNet = ii.Invoice.NetAmount
                })
                .ToListAsync();

            // المرتجعات تُطرح كميتًا وإيرادًا وتكلفة — إيراد المرتجع بنسبة صافي فاتورة
            // المرتجع نفسها (الاسترداد الفعلي بعد خصم الضريبة/الخصم المعكوسين، VULN-03):
            // فاتورة المرتجع سالبة الرأس (Total/Net) فالنسبة موجبة وتطابق ما رُد للعميل.
            var returnRows = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.Type == enInvoicesType.customer_return
                          && ii.Invoice.Status == enInvoicesStatus.completed
                          && ii.Invoice.CreatedAt >= start && ii.Invoice.CreatedAt <= end)
                .Select(ii => new
                {
                    ii.ProductId,
                    ii.Quantity,
                    ii.ItemPrice,
                    ItemCost = ii.ItemCost ?? 0m,
                    InvoiceTotal = ii.Invoice.TotalAmount,
                    InvoiceNet = ii.Invoice.NetAmount
                })
                .ToListAsync();

            // النسبة معرفة على أي رأس غير صفري: فاتورة البيع موجبة الرأس، وفاتورة
            // المرتجع سالبة الرأس (Net/Total كلاهما سالب → نسبة موجبة تعكس الاسترداد)
            static decimal NetRatio(decimal total, decimal net)
                => total != 0 ? net / total : 0m;

            var agg = new Dictionary<int, (string Name, int Qty, decimal Revenue, decimal Cost)>();
            void Accumulate(int productId, string name, int qty, decimal revenue, decimal cost, int sign)
            {
                var cur = agg.TryGetValue(productId, out var v)
                    ? v
                    : (Name: name, Qty: 0, Revenue: 0m, Cost: 0m);
                agg[productId] = (cur.Name, cur.Qty + sign * qty, cur.Revenue + sign * revenue, cur.Cost + sign * cost);
            }

            foreach (var r in saleRows)
                Accumulate(r.ProductId, r.ProductName, r.Quantity,
                    r.ItemPrice * r.Quantity * NetRatio(r.InvoiceTotal, r.InvoiceNet),
                    r.ItemCost * r.Quantity, sign: +1);

            foreach (var r in returnRows)
                Accumulate(r.ProductId, "", r.Quantity,
                    r.ItemPrice * r.Quantity * NetRatio(r.InvoiceTotal, r.InvoiceNet),
                    r.ItemCost * r.Quantity, sign: -1);

            var list = new List<ProductProfitVM>();
            foreach (var (productId, row) in agg)
            {
                if (row.Qty <= 0) continue; // صافي الصفر لا يفيد التحليل

                decimal revenue = row.Revenue;
                decimal cost = row.Cost;
                decimal profit = revenue - cost;
                list.Add(new ProductProfitVM
                {
                    ProductId = productId,
                    ProductName = row.Name,
                    Quantity = row.Qty,
                    Revenue = Math.Round(revenue, 2),
                    Cost = Math.Round(cost, 2),
                    Profit = Math.Round(profit, 2),
                    MarginPercent = revenue > 0 ? Math.Round(profit * 100 / revenue, 1) : 0
                });
            }

            decimal totalProfit = list.Sum(x => x.Profit);
            foreach (var p in list)
                p.ProfitSharePercent = totalProfit > 0 ? Math.Round(p.Profit * 100 / totalProfit, 1) : 0;

            return Result<List<ProductProfitVM>>.Ok(list.OrderByDescending(p => p.Profit).ToList());
        }

        /// <summary>أفضل العملاء في فترة: عدد فواتيرهم وإيرادهم وربحهم وصافي ما تحصّل منهم.</summary>
        public async Task<Result<List<CustomerProfitVM>>> GetTopCustomersReportAsync(DateOnly fromDate, DateOnly toDate)
        {
            // حدود اليوم البرنامجية (UTC+3) — مطابقة بقية التقارير
            var start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            var end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

            var sales = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt >= start && i.CreatedAt <= end)
                .GroupBy(i => new { i.PartyId, i.Party!.Name })
                .Select(g => new
                {
                    g.Key.PartyId,
                    g.Key.Name,
                    Invoices = g.Count(),
                    Revenue = g.Sum(x => x.NetAmount)
                })
                .ToListAsync();

            var salesIds = _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt >= start && i.CreatedAt <= end)
                .Select(i => (int?)i.InvoiceId);

            var cogsByParty = await _dbContext.InvoiceItems
                .Where(ii => salesIds.Contains(ii.InvoiceId))
                .GroupBy(ii => ii.Invoice.PartyId!.Value)
                .Select(g => new { PartyId = g.Key, Cost = g.Sum(x => (x.ItemCost ?? 0m) * x.Quantity) })
                .ToDictionaryAsync(x => x.PartyId, x => x.Cost);

            var returnsByParty = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.customer_return
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt >= start && i.CreatedAt <= end)
                .GroupBy(i => i.PartyId!.Value)
                .Select(g => new { PartyId = g.Key, Returns = g.Sum(x => x.NetAmount) })
                .ToDictionaryAsync(x => x.PartyId, x => x.Returns);

            // ══ المرتجع يقلب تكلفته أيضًا: بند المرتجع يحمل تكلفة البنود المُباعة الأصلية ══
            // إيراد العميل ينخفض بقيمة المرتجع — بلا قلب التكلفة يظهر ربح العميل أقل
            // من حقيقته بمقدار تكلفة البضاعة المرتجعة.
            var returnsCogsByParty = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.Type == enInvoicesType.customer_return
                          && ii.Invoice.Status == enInvoicesStatus.completed
                          && ii.Invoice.PartyId != null
                          && ii.Invoice.CreatedAt >= start && ii.Invoice.CreatedAt <= end)
                .GroupBy(ii => ii.Invoice.PartyId!.Value)
                .Select(g => new { PartyId = g.Key, Cost = g.Sum(x => (x.ItemCost ?? 0m) * x.Quantity) })
                .ToDictionaryAsync(x => x.PartyId, x => x.Cost);

            var result = new List<CustomerProfitVM>();
            foreach (var s in sales)
            {
                decimal cost = cogsByParty.TryGetValue(s.PartyId!.Value, out var c) ? c : 0m;
                decimal returns = returnsByParty.TryGetValue(s.PartyId.Value, out var r) ? Math.Abs(r) : 0m;
                decimal returnsCost = returnsCogsByParty.TryGetValue(s.PartyId.Value, out var rc) ? rc : 0m;
                decimal netRevenue = s.Revenue - returns;
                decimal netCost = cost - returnsCost;
                decimal profit = netRevenue - netCost;

                result.Add(new CustomerProfitVM
                {
                    CustomerId = s.PartyId.Value,
                    CustomerName = s.Name,
                    InvoiceCount = s.Invoices,
                    NetRevenue = Math.Round(netRevenue, 2),
                    Profit = Math.Round(profit, 2),
                    MarginPercent = netRevenue > 0 ? Math.Round(profit * 100 / netRevenue, 1) : 0
                });
            }

            return Result<List<CustomerProfitVM>>.Ok(result.OrderByDescending(x => x.Profit).ToList());
        }

        /// <summary>ربحية الأقسام — الفئة المستخدمة: فئة المورد المصدر (Supplier.Category) إن رغبت؛ حُذف مخططًا لغياب فئة منتج.</summary>
        
        /// <summary>
        /// أعمار الديون حتى تاريخ معين: لكل عميل ذي رصيد، توزيع الدين على شرائح عمره
        /// (0-30/31-60/61-90/90+) حسب أول تاريخ دين غير مسدد (FIFO على الفواتير الآجلة).
        /// </summary>
        public async Task<Result<List<DebtAgeingVM>>> GetDebtAgeingReportAsync(DateOnly asOf)
        {
            var asOfDate = asOf.ToDateTime(TimeOnly.MaxValue);

            // كل فواتير البيع المكتملة غير المسددة بالكامل لعملاء مسجلين — الأقدم أولًا
            var openInvoices = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt <= asOfDate)
                .OrderBy(i => i.CreatedAt)
                .Select(i => new { i.InvoiceId, i.PartyId, i.NetAmount, i.CreatedAt })
                .ToListAsync();

            // مجموع المدفوع لكل فاتورة (لحساب المتبقي)
            var paidByInvoice = await _dbContext.Payments
                .Where(p => p.InvoiceId != null)
                .GroupBy(p => p.InvoiceId!.Value)
                .Select(g => new { InvoiceId = g.Key, Paid = g.Sum(p => p.Amount) })
                .ToDictionaryAsync(x => x.InvoiceId, x => x.Paid);

            var openInvoiceIds = openInvoices.Select(i => i.InvoiceId).ToList();
            var returnNetByInvoice = await _dbContext.Invoices
                .Where(i => i.Status == enInvoicesStatus.completed
                         && (i.Type == enInvoicesType.customer_return || i.Type == enInvoicesType.supplier_return)
                         && i.OriginalInvoiceId != null
                         && openInvoiceIds.Contains(i.OriginalInvoiceId.Value))
                .GroupBy(i => i.OriginalInvoiceId!.Value)
                .Select(g => new { InvoiceId = g.Key, Net = g.Sum(x => x.NetAmount) })
                .ToDictionaryAsync(x => x.InvoiceId, x => x.Net);

            // أسماء العملاء
            var customerNames = await _dbContext.Parties
                .Where(p => p.PartyType == 'C')
                .Select(p => new { p.PartyId, p.Name, p.Phone1 })
                .ToDictionaryAsync(p => p.PartyId);

            // (partyId → قائمة فواتير بمتبقيها) ثم رصيد العميل الرسمي هو الحكم النهائي:
            // نعرض العميل إذا كان رصيده > 0، ونوزع رصيده على شرائح أعمار فواتيره المفتوحة (FIFO)
            var balances = await _dbContext.Customers
                .Where(c => c.Balance > 0)
                .ToDictionaryAsync(c => c.PartyId, c => c.Balance!.Value);

            var invoicesByParty = new Dictionary<int, List<(decimal remaining, int ageDays)>>();
            foreach (var inv in openInvoices)
            {
                decimal paid = paidByInvoice.TryGetValue(inv.InvoiceId, out var p) ? p : 0;
                decimal returned = returnNetByInvoice.TryGetValue(inv.InvoiceId, out var rn) ? rn : 0m;
                decimal remaining = inv.NetAmount - paid + returned;
                if (remaining <= 0) continue;

                int ageDays = asOf.DayNumber - DateOnly.FromDateTime(inv.CreatedAt!.Value).DayNumber;
                if (!invoicesByParty.TryGetValue(inv.PartyId!.Value, out var list2))
                {
                    list2 = new List<(decimal, int)>();
                    invoicesByParty[inv.PartyId!.Value] = list2;
                }
                list2.Add((remaining, ageDays));
            }

            var result = new List<DebtAgeingVM>();
            foreach (var (partyId, balance) in balances)
            {
                if (!customerNames.TryGetValue(partyId, out var cust))
                    continue;

                // توزيع الرصيد على الشرائح FIFO: من الفواتير الأقدم حتى استنفاد الرصيد
                decimal a30 = 0, a60 = 0, a90 = 0, aPlus = 0, allocated = 0;
                if (invoicesByParty.TryGetValue(partyId, out var invs))
                {
                    foreach (var (remaining, ageDays) in invs.OrderBy(x => x.ageDays))
                    {
                        if (allocated >= balance) break;
                        decimal take = Math.Min(remaining, balance - allocated);
                        allocated += take;

                        if (ageDays <= 30) a30 += take;
                        else if (ageDays <= 60) a60 += take;
                        else if (ageDays <= 90) a90 += take;
                        else aPlus += take;
                    }
                }
                // أي جزء من الرصيد بلا فاتورة مفتوحة (دين تاريخي) — يُسجل في أقدم شريحة
                if (allocated < balance) aPlus += balance - allocated;

                result.Add(new DebtAgeingVM
                {
                    CustomerId = partyId,
                    CustomerName = cust.Name,
                    Phone = cust.Phone1,
                    Balance = Math.Round(balance, 2),
                    Age0to30 = Math.Round(a30, 2),
                    Age31to60 = Math.Round(a60, 2),
                    Age61to90 = Math.Round(a90, 2),
                    Age90plus = Math.Round(aPlus, 2)
                });
            }

            return Result<List<DebtAgeingVM>>.Ok(result.OrderByDescending(a => a.Age90plus).ThenByDescending(a => a.Balance).ToList());
        }

        /// <summary>حركة الصندوق لفترة: كل النقد داخلًا وخارجًا (بيع/مرتجع/تحصيل/سداد/جلسات/مصروفات/رواتب).
        ///
        /// التصنيف حسب الطرف والاتجاه والطريقة:
        ///   • CashSales: دخل نقدي من العملاء (مبيعات + تحصيلات) — الدرج زاد.
        ///   • CustomerPayments: دخل غير نقدي من العملاء (شبكة/تحويل) — لا يدخل الدرج.
        ///   • CashReturns: مرتجعات عملاء نقدية — الدرج نقص.
        ///   • SupplierPayments: كل مدفوعات الموردين (نقدي يخرج من الدرج + غير نقدي).
        ///   • SupplierRefunds: استردادات الموردين الداخلة (نقدًا أو غيره) — ليست تحصيلات عملاء.
        ///   • الرواتب تُحسب مرة واحدة (SalariesPaid) — مصروفها المرتبط مستثنى من ExpensesPaid.</summary>
        public async Task<Result<CashFlowVM>> GetCashFlowReportAsync(DateOnly fromDate, DateOnly toDate)
        {
            // حدود اليوم البرنامجية (UTC+3) — مطابقة بقية التقارير: أعمدة UTC
            // (CashMovements.CreatedAt و PayrollRecords.PaidAt) تُقارن بحدود UTC
            // لمنتصف الليل المحلي، فلا تُسقط حركات ما بعد 9 مساءً ولا تُحمَّل
            // حركات منتصف الليل (00:00–03:00) على اليوم الخطأ.
            var start = fromDate.ToDateTime(TimeOnly.MinValue).AddHours(-3);
            var end = toDate.ToDateTime(TimeOnly.MaxValue).AddHours(-3);

            // مبيعات وتحصيلات نقدية من العملاء — حسب الطرف (PartyType='C') لا نوع الفاتورة:
            // استرداد مورد (دخل بطرف 'S') لا يُصنَّف أبدًا تحصيلًا للعملاء
            var cashSales = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income && p.PaymentMethod == "cash"
                          && p.PartyType == 'C'
                          && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            var cashReturns = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.outcome && p.PaymentMethod == "cash"
                          && p.PartyType == 'C'
                          && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // دخل العملاء غير النقدي — بطرف عميل صراحة كي لا تتسرب استردادات الموردين غير النقدية إليه
            var customerPayments = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income && p.PaymentMethod != "cash"
                          && p.PartyType == 'C'
                          && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // مدفوعات الموردين بكل الطرق — السداد النقدي يخرج من الدرج فعليًا
            // فلا يُحذف من التقرير لأنه «نقدي» (كان مفقودًا كليًا قبل الإصلاح)
            var supplierPayments = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.outcome
                          && p.PartyType == 'S'
                          && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // استردادات الموردين (دخل بطرف مورد) — تصنيف مستقل عن تحصيلات العملاء
            var supplierRefunds = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                          && p.PartyType == 'S'
                          && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            var cashIn = await _dbContext.CashMovements
                .Where(m => m.MovementType == 'I'
                          && m.CreatedAt >= start && m.CreatedAt <= end)
                .SumAsync(m => (decimal?)m.Amount) ?? 0m;

            var cashOut = await _dbContext.CashMovements
                .Where(m => m.MovementType == 'O'
                          && m.CreatedAt >= start && m.CreatedAt <= end)
                .SumAsync(m => (decimal?)m.Amount) ?? 0m;

            // مصروفات غير رواتب: صرف الراتب ينشئ مصروفًا مرتبطًا (ExpenseId في
            // PayrollRecord) — استثناؤه هنا كي لا يُحسب مرتين مع SalariesPaid
            var expensesPaid = await _dbContext.Expenses
                .Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate <= toDate
                          && !_dbContext.PayrollRecords.Any(pr => pr.ExpenseId == e.ExpenseId))
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            var salariesPaid = await _dbContext.PayrollRecords
                .Where(p => p.Status == enPayrollStatus.paid && p.PaidAt >= start && p.PaidAt <= end)
                .SumAsync(p => (decimal?)p.Net) ?? 0m;

            return Result<CashFlowVM>.Ok(new CashFlowVM
            {
                CashSales = cashSales,
                CashReturns = cashReturns,
                CustomerPayments = customerPayments,
                SupplierPayments = supplierPayments,
                SupplierRefunds = supplierRefunds,
                CashIn = cashIn,
                CashOut = cashOut,
                ExpensesPaid = expensesPaid,
                SalariesPaid = salariesPaid
            });
        }
    }
}
