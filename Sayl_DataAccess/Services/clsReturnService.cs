using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class DtoRefundPayment
    {
        public string Method { get; }
        public decimal Amount { get; }

        public DtoRefundPayment(string method, decimal amount)
        {
            Method = method;
            Amount = amount;
        }
    }

    public class DtoRefundPlan
    {
        public List<DtoRefundPayment> Payments { get; } = new();
        public decimal BalanceRefund { get; set; }
    }

    public class clsReturnService
    {
        private readonly SaylDbContext _dbContext;

        public clsReturnService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // مرتجع بيع من عميل: إعادة للمخزون + استرداد/تخفيض رصيد
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> CreateCustomerReturnAsync(DtoReturnRequest request)
        {
            if (request.Items == null || !request.Items.Any())
                return Result<int>.Fail("يجب إضافة بنود إلى المرتجع.");

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                    return Result<int>.Fail("الكمية يجب أن تكون أكبر من صفر.");
            }

            var originalInvoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.sale
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة بيع مكتملة.");

            var alreadyReturned = await GetReturnedQuantitiesAsync(originalInvoice.InvoiceId, enInvoicesType.customer_return);

            // ══ VULN-01: حارس الازدواج داخل الطلب الواحد ══
            // نفس المنتخ مرتين في الطلب (مثال هجومي: {X:5},{X:5} على فاتورة بِيعت 8)
            // كان يجتاز الفحص الفردي (5≤8 و5≤8) فيُرتجَع 10 ويُخزَّن 10 — سرقة نقد
            // ومخزون وهمي. التجميع أولاً يجعل الفحص ضد المجموع لا ضد كل سطر وحده.
            foreach (var g in GroupRequestItems(request.Items))
            {
                var originalItems = originalInvoice.InvoiceItems.Where(ii => ii.ProductId == g.ProductId).ToList();
                if (originalItems.Count == 0)
                    return Result<int>.Fail($"المنتج {g.ProductId} غير موجود في الفاتورة الأصلية.");

                long sold = originalItems.Sum(ii => (long)ii.Quantity);
                long returnedSoFar = alreadyReturned.GetValueOrDefault(g.ProductId);
                if (g.Quantity > sold - returnedSoFar)
                    return Result<int>.Fail($"الكمية المرتجعة أكبر من الكمية المتبقية للمنتج {g.ProductId} (المتبقي: {sold - returnedSoFar}).");
            }

            // Serializable يمنع إرجاعين متوازيين لنفس الفاتورة من تجاوز الكمية المسموحة
            // (إعادة فحص الكميات المرتجعة داخل المعاملة بعد أن يقفل النطاق الإدراج المتوازي)
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // إعادة-فحص داخل المعاملة: طلب إرجاع متوازٍ commit بين الفحص الأول وبداية المعاملة
                // (بنفس حارس التجميع — الازدواج داخل الطلب يُفحص هنا أيضاً)
                alreadyReturned = await GetReturnedQuantitiesAsync(originalInvoice.InvoiceId, enInvoicesType.customer_return);
                foreach (var g in GroupRequestItems(request.Items))
                {
                    var originalItems = originalInvoice.InvoiceItems.Where(ii => ii.ProductId == g.ProductId).ToList();
                    long sold = originalItems.Sum(ii => (long)ii.Quantity);
                    long returnedSoFar = alreadyReturned.GetValueOrDefault(g.ProductId);
                    if (g.Quantity > sold - returnedSoFar)
                        throw new InvalidOperationException($"الكمية المرتجعة أكبر من الكمية المتبقية للمنتج {g.ProductId} (المتبقي: {sold - returnedSoFar}).");
                }

                // ══ VULN-03 (الحل ب): الاسترداد النسبي الدقيق ══
                // الفاتورة الأصلية بُنيت: صافي = (إجمالي − خصم) + ضريبة.
                // الاسترداد القديم كان بسعر البند الخام → خصم وضريبة يتسربان:
                // خصم 10%: العميل دفع 90 ويُرد 100 (ربح وهمي 10)؛
                // ضريبة 10%: دفع 110 ويُرد 100 (خسارة ضريبته بصمت).
                //
                // 📖 دقة المعادلة (المطلب: أدق تقريب ممكن):
                //  1) النسبة = الصافي/الإجمالي تُحفظ decimal كاملة — بلا أي
                //     تقريب وسيط (كل تقريب وسيط يضاعف الخطأ عبر البنود).
                //  2) الاسترداد التراكمي يُحسب كسرًا كاملًا ثم يُقرَّب إلى
                //     المنزلتين مرة واحدة أخيرة فقط.
                //  3) حارس القسمة على صفر: فاتورة إجماليها صفر (هدية كاملة)
                //     لا تُقسَّم — الاسترداد صفر لكل بنودها (صافيها صفر).
                //
                // مثال التحقق: إجمالي 100 خصم 10 ضريبة 0 → صافي 90.
                // نسبة = 0.9. إرجاع بند 30×2: استرداد = 60×0.9 = 54.00
                // — يطابق ما دفعه العميل عن هذه الوحدات فعلًا.
                decimal invTotal = originalInvoice.TotalAmount;
                decimal invNet = originalInvoice.NetAmount;
                decimal netRatio = invTotal > 0 ? invNet / invTotal : 0m;

                var allocations = AllocateAcrossOriginalLines(originalInvoice.InvoiceItems, GroupRequestItems(request.Items));
                decimal rawRefund = allocations.Sum(a => a.ItemPrice * a.Quantity) * netRatio;
                // تقريب واحد نهائي — لا تقارب متسلسل يتراكم خطؤه
                decimal total = Math.Round(rawRefund, 2, MidpointRounding.AwayFromZero);

                // أثر هذا المرتجع على مستوى الفاتورة الأصلية — للتقرير والتدقيق:
                // الخصم المسترد = نصيب البنود من الخصم (نسبة الخصم × القيمة الخام)
                // الضريبة المستردة = نسبة الضريبة × (الخام المسترد − الخصم المسترد)
                // (نفس ترتيب بناء الفاتورة الأصلية بالضبط — معادلة لا اجتهاد)
                decimal returnedGross = allocations.Sum(a => a.ItemPrice * a.Quantity);
                decimal discountRatio = invTotal > 0
                    ? originalInvoice.DiscountAmount / invTotal
                    : 0m;
                decimal returnedDiscount = Math.Round(returnedGross * discountRatio, 2, MidpointRounding.AwayFromZero);
                decimal afterDiscountPortion = returnedGross - returnedDiscount;
                decimal taxBase = invTotal - originalInvoice.DiscountAmount; // قاعدة الضريبة الأصلية
                decimal taxRatio = taxBase > 0
                    ? originalInvoice.TaxAmount / taxBase
                    : 0m;
                decimal returnedTax = Math.Round(afterDiscountPortion * taxRatio, 2, MidpointRounding.AwayFromZero);

                var returnInvoice = new Invoice
                {
                    Type = enInvoicesType.customer_return,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.paid,
                    PartyType = originalInvoice.PartyType,
                    PartyId = originalInvoice.PartyId,
                    UserId = request.UserId,
                    // VULN-05: فاتورة المرتجع تنتمي لجلسة الفعل (درج اليوم)
                    SessionId = request.SessionId,
                    OriginalInvoiceId = originalInvoice.InvoiceId,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow,
                    TotalAmount = -returnedGross,
                    DiscountAmount = -returnedDiscount,
                    TaxAmount = -returnedTax,
                    NetAmount = -total
                };

                _dbContext.Invoices.Add(returnInvoice);
                await _dbContext.SaveChangesAsync();

                foreach (var g in GroupRequestItems(request.Items))
                {
                    var productAllocations = allocations
                        .Where(a => a.ProductId == g.ProductId)
                        .OrderBy(a => a.LineIndex)
                        .ToList();

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = returnInvoice.InvoiceId,
                        ProductId = g.ProductId,
                        Quantity = g.Quantity,
                        BatchId = productAllocations.Count > 0 ? productAllocations[0].BatchId : null,
                        ItemPrice = productAllocations.Sum(a => a.ItemPrice * a.Quantity) / g.Quantity,
                        ItemCost = productAllocations.Sum(a => (a.ItemCost ?? 0m) * a.Quantity) / g.Quantity
                    });

                    foreach (var a in productAllocations)
                    {
                        await RestockProductAsync(g.ProductId, a.Quantity, a.BatchId, returnInvoice);
                    }
                }

                await ProcessCustomerRefundAsync(originalInvoice, returnInvoice, total, request.SessionId);

                clsAudit.Log(_dbContext, request.UserId, "CustomerReturn",
                    $"مرتجع بيع على الفاتورة #{originalInvoice.InvoiceId} — القيمة: {total:N2}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result<int>.Ok(returnInvoice.InvoiceId, "تم إنشاء مرتجع البيع بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشل إنشاء مرتجع البيع.");
            }
        }

        // ─────────────────────────────────────────────────
        // مرتجع شراء لمورد: خصم من المخزون + استرداد/تخفيض رصيد
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> CreateSupplierReturnAsync(DtoReturnRequest request)
        {
            if (request.Items == null || !request.Items.Any())
                return Result<int>.Fail("يجب إضافة بنود إلى المرتجع.");

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                    return Result<int>.Fail("الكمية يجب أن تكون أكبر من صفر.");
            }

            var originalInvoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.purchase
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة شراء مكتملة.");

            var alreadyReturned = await GetReturnedQuantitiesAsync(originalInvoice.InvoiceId, enInvoicesType.supplier_return);

            // VULN-01: حارس الازدواج داخل الطلب — التجميع قبل الفحص (نفس مرتجع العميل)
            foreach (var g in GroupRequestItems(request.Items))
            {
                var originalItems = originalInvoice.InvoiceItems.Where(ii => ii.ProductId == g.ProductId).ToList();
                if (originalItems.Count == 0)
                    return Result<int>.Fail($"المنتج {g.ProductId} غير موجود في الفاتورة الأصلية.");

                long sold = originalItems.Sum(ii => (long)ii.Quantity);
                long returnedSoFar = alreadyReturned.GetValueOrDefault(g.ProductId);
                if (g.Quantity > sold - returnedSoFar)
                    return Result<int>.Fail($"الكمية المرتجعة أكبر من الكمية المتبقية للمنتج {g.ProductId} (المتبقي: {sold - returnedSoFar}).");
            }

            // Serializable يمنع إرجاعين متوازيين لنفس الفاتورة من تجاوز الكمية المسموحة
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // إعادة-فحص داخل المعاملة بعد قفل النطاق (بنفس حارس التجميع)
                alreadyReturned = await GetReturnedQuantitiesAsync(originalInvoice.InvoiceId, enInvoicesType.supplier_return);
                foreach (var g in GroupRequestItems(request.Items))
                {
                    var originalItems = originalInvoice.InvoiceItems.Where(ii => ii.ProductId == g.ProductId).ToList();
                    long sold = originalItems.Sum(ii => (long)ii.Quantity);
                    long returnedSoFar = alreadyReturned.GetValueOrDefault(g.ProductId);
                    if (g.Quantity > sold - returnedSoFar)
                        throw new InvalidOperationException($"الكمية المرتجعة أكبر من الكمية المتبقية للمنتج {g.ProductId} (المتبقي: {sold - returnedSoFar}).");
                }

                // ══ VULN-03 (الحل ب): الاسترداد النسبي الدقيق — مرتجع المورد ══
                // فاتورة الشراء الأصلية بُنيت بنفس المعادلة (خصم → ضريبة على الباقي).
                // استرداد المورد القديم كان بالتكلفة الخام — نفس تسرب الخصم/الضريبة
                // لكن معكوسًا: التاجر يسترد من المورد أكثر مما دفع له (خصم) أو
                // أقل (ضريبة شراء) — فساد ميزانية المورد بالطريقتين.
                // المعادلة مطابقة لمرتجع العميل أعلاه (نسبة الصافي/الإجمالي
                // بلا تقريب وسيط، تقريب نهائي واحد) — راجع التعليق الكامل هناك.
                decimal invTotalSup = originalInvoice.TotalAmount;
                decimal invNetSup = originalInvoice.NetAmount;
                decimal netRatioSup = invTotalSup > 0 ? invNetSup / invTotalSup : 0m;

                var allocationsSup = AllocateAcrossOriginalLines(originalInvoice.InvoiceItems, GroupRequestItems(request.Items));
                decimal rawRefundSup = allocationsSup.Sum(a => a.ItemPrice * a.Quantity) * netRatioSup;
                decimal total = Math.Round(rawRefundSup, 2, MidpointRounding.AwayFromZero);

                // التفصيل التدقيقي: نصيب البنود من الخصم والضريبة الأصلية
                decimal returnedGrossSup = allocationsSup.Sum(a => a.ItemPrice * a.Quantity);
                decimal discountRatioSup = invTotalSup > 0
                    ? originalInvoice.DiscountAmount / invTotalSup
                    : 0m;
                decimal returnedDiscountSup = Math.Round(returnedGrossSup * discountRatioSup, 2, MidpointRounding.AwayFromZero);
                decimal taxBaseSup = invTotalSup - originalInvoice.DiscountAmount;
                decimal taxRatioSup = taxBaseSup > 0
                    ? originalInvoice.TaxAmount / taxBaseSup
                    : 0m;
                decimal returnedTaxSup = Math.Round((returnedGrossSup - returnedDiscountSup) * taxRatioSup, 2, MidpointRounding.AwayFromZero);

                var returnInvoice = new Invoice
                {
                    Type = enInvoicesType.supplier_return,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.paid,
                    PartyType = originalInvoice.PartyType,
                    PartyId = originalInvoice.PartyId,
                    UserId = request.UserId,
                    // VULN-05: جلسة الفعل — نقد الاسترداد يعود لدرج من دفعه اليوم
                    SessionId = request.SessionId,
                    OriginalInvoiceId = originalInvoice.InvoiceId,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow,
                    TotalAmount = -returnedGrossSup,
                    DiscountAmount = -returnedDiscountSup,
                    TaxAmount = -returnedTaxSup,
                    NetAmount = -total
                };

                _dbContext.Invoices.Add(returnInvoice);
                await _dbContext.SaveChangesAsync();

                foreach (var g in GroupRequestItems(request.Items))
                {
                    var productAllocations = allocationsSup
                        .Where(a => a.ProductId == g.ProductId)
                        .OrderBy(a => a.LineIndex)
                        .ToList();

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = returnInvoice.InvoiceId,
                        ProductId = g.ProductId,
                        Quantity = g.Quantity,
                        BatchId = productAllocations.Count > 0 ? productAllocations[0].BatchId : null,
                        ItemPrice = productAllocations.Sum(a => a.ItemPrice * a.Quantity) / g.Quantity,
                        ItemCost = productAllocations.Sum(a => (a.ItemCost ?? 0m) * a.Quantity) / g.Quantity
                    });

                    foreach (var a in productAllocations)
                    {
                        await DeductProductStockAsync(g.ProductId, a.Quantity, a.BatchId, returnInvoice);
                    }
                }

                await ProcessSupplierRefundAsync(originalInvoice, returnInvoice, total, request.SessionId);

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result<int>.Ok(returnInvoice.InvoiceId, "تم إنشاء مرتجع الشراء بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشل إنشاء مرتجع الشراء.");
            }
        }

        // ═════════════════════════════════════════════════
        // الدوال الخاصة
        // ═════════════════════════════════════════════════

        /// <summary>
        /// VULN-01: تجميع بنود طلب المرتجع حسب المنتخ — المجموع لا السطر.
        /// يُستعمل في كل فحوص الكميات وبناء البنود في المرتجعين معًا،
        /// فلا يمر طلب ببنود مزدوجة حتى لو تكرر المنتخ عشر مرات.
        /// </summary>
        private static IEnumerable<(int ProductId, int Quantity)> GroupRequestItems(
            List<DtoReturnItem> items) =>
            items
                .GroupBy(i => i.ProductId)
                .Select(g => (g.Key, g.Sum(i => i.Quantity)));

        private static List<(int LineIndex, int ProductId, int Quantity, decimal ItemPrice, decimal? ItemCost, int? BatchId)> AllocateAcrossOriginalLines(
            IEnumerable<InvoiceItem> originalItems,
            IEnumerable<(int ProductId, int Quantity)> groupedRequest)
        {
            var ordered = originalItems
                .Select((ii, index) => (Item: ii, Index: index))
                .OrderBy(x => x.Item.Batch == null
                    ? 0
                    : x.Item.Batch.ExpiryDate.HasValue ? x.Item.Batch.ExpiryDate.Value.DayNumber : int.MaxValue)
                .ThenBy(x => x.Item.Batch == null ? DateTime.MinValue : (x.Item.Batch.CreatedAt ?? DateTime.MinValue))
                .ThenBy(x => x.Item.ItemPrice)
                .ThenBy(x => x.Index)
                .ToList();

            var request = groupedRequest.ToDictionary(g => g.ProductId, g => g.Quantity);
            var result = new List<(int LineIndex, int ProductId, int Quantity, decimal ItemPrice, decimal? ItemCost, int? BatchId)>();

            foreach (var x in ordered)
            {
                if (!request.TryGetValue(x.Item.ProductId, out int remaining) || remaining <= 0)
                    continue;

                int take = Math.Min(remaining, x.Item.Quantity);
                if (take <= 0)
                    continue;

                result.Add((x.Index, x.Item.ProductId, take, x.Item.ItemPrice, x.Item.ItemCost, x.Item.BatchId));
                request[x.Item.ProductId] = remaining - take;
            }

            return result;
        }

        private async Task<Dictionary<int, int>> GetReturnedQuantitiesAsync(int originalInvoiceId, enInvoicesType returnType)
        {
            var returnedItems = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.OriginalInvoiceId == originalInvoiceId
                          && ii.Invoice.Type == returnType
                          && ii.Invoice.Status == enInvoicesStatus.completed)
                .GroupBy(ii => ii.ProductId)
                .Select(g => new { ProductId = g.Key, Quantity = g.Sum(ii => ii.Quantity) })
                .ToListAsync();

            return returnedItems.ToDictionary(x => x.ProductId, x => x.Quantity);
        }

        private async Task RestockProductAsync(int productId, int quantity, int? batchId, Invoice returnInvoice)
        {
            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null || product.Type != "stock") return;

            var inventory = await _dbContext.Inventories.FindAsync(productId);
            if (inventory == null)
            {
                inventory = new Inventory
                {
                    ProductId = productId,
                    QuantityAvailable = 0,
                    QuantityReserved = 0
                };
                _dbContext.Inventories.Add(inventory);
            }

            // B5: لا نُرجع مرتجع البيع إلى دفعة انتهت صلاحيتها أصلاً —
            // البيع يستبعد `ExpiryDate < اليوم`، فكان المخزون «متاحًا» لا يُباع
            // أبدًا. عند انتهاء دفعة المصدر نُنشئ دفعة جديدة غير منتهية.
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

            if (batchId.HasValue)
            {
                var batch = await _dbContext.Batches.FindAsync(batchId.Value);
                if (batch != null && batch.ProductId == productId)
                {
                    if (batch.ExpiryDate.HasValue && batch.ExpiryDate.Value < today)
                    {
                        // B5: دفعة المصدر منتهية الصلاحية — دفعة جديدة بلا صلاحية
                        // تحمل كمية المرتجع ليباع المخزون فعليًا.
                        batch = new Batch
                        {
                            ProductId = productId,
                            BatchNumber = $"RET-{returnInvoice.InvoiceId}-{productId}-{DateTime.UtcNow.Ticks}",
                            ExpiryDate = null,
                            BatchQuantity = quantity,
                            CreatedAt = DateTime.UtcNow
                        };
                        _dbContext.Batches.Add(batch);
                    }
                    else
                    {
                        batch.BatchQuantity += quantity;
                    }

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = productId,
                        Batch = batch,
                        MovementType = enStockMovementType.customer_return,
                        Quantity = quantity,
                        SourceType = "Invoice",
                        SourceId = returnInvoice.InvoiceId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            inventory.QuantityAvailable += quantity;
            inventory.UpdatedAt = DateTime.UtcNow;
        }

        private async Task DeductProductStockAsync(int productId, int quantity, int? batchId, Invoice returnInvoice)
        {
            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null || product.Type != "stock") return;

            var inventory = await _dbContext.Inventories.FindAsync(productId);
            if (inventory == null)
                throw new InvalidOperationException($"لا يوجد سجل مخزون للمنتج {productId}");

            int available = (inventory.QuantityAvailable ?? 0) - (inventory.QuantityReserved ?? 0);
            if (available < quantity)
                throw new InvalidOperationException($"المخزون غير كافٍ لمرتجع المورد للمنتج {product.Name}");

            if (batchId.HasValue)
            {
                var batch = await _dbContext.Batches.FindAsync(batchId.Value);
                if (batch != null && batch.ProductId == productId)
                {
                    if (batch.BatchQuantity < quantity)
                        throw new InvalidOperationException($"كمية الدفعة غير كافية لمرتجع المورد للمنتج {product.Name}");

                    batch.BatchQuantity -= quantity;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = productId,
                        BatchId = batch.BatchId,
                        MovementType = enStockMovementType.supplier_return,
                        Quantity = -quantity,
                        SourceType = "Invoice",
                        SourceId = returnInvoice.InvoiceId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            inventory.QuantityAvailable -= quantity;
            inventory.UpdatedAt = DateTime.UtcNow;
        }

        // VULN-05: refundSessionId = درج الجلسة التي تُخرج نقد الاسترداد —
        // تقرير Z لليوم يرى المرتجع الذي دُفع اليوم، لا يُحمَّل على جلسة مجمدة.
        //
        private async Task ProcessCustomerRefundAsync(Invoice originalInvoice, Invoice returnInvoice, decimal refundTotal, int? refundSessionId)
        {
            var refundPlan = await BuildRefundPlanAsync(originalInvoice, refundTotal);

            foreach (var refund in refundPlan.Payments)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.outcome,
                    InvoiceId = returnInvoice.InvoiceId,
                    PartyType = 'C',
                    PartyId = originalInvoice.PartyId,
                    SessionId = refundSessionId, // VULN-05: درج الفعل
                    Amount = refund.Amount,
                    PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    PaymentMethod = refund.Method,
                    Notes = $"قيد استرداد عن الفاتورة {originalInvoice.InvoiceId}",
                    CreatedAt = DateTime.UtcNow
                });
            }

            decimal balanceRefund = refundPlan.BalanceRefund;
            if (balanceRefund > 0 && originalInvoice.PartyId.HasValue)
            {
                var customer = await _dbContext.Customers.FindAsync(originalInvoice.PartyId.Value);
                if (customer != null)
                {
                    customer.Balance -= balanceRefund;
                    if (customer.Balance < 0) customer.Balance = 0;
                }
            }
        }

        // VULN-05: نفس توجيه مرتجع العميل — نقد الاسترداد يعود لدرج جلسة الفعل.
        private async Task ProcessSupplierRefundAsync(Invoice originalInvoice, Invoice returnInvoice, decimal refundTotal, int? refundSessionId)
        {
            var refundPlan = await BuildRefundPlanAsync(originalInvoice, refundTotal);

            foreach (var refund in refundPlan.Payments)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = returnInvoice.InvoiceId,
                    PartyType = 'S',
                    PartyId = originalInvoice.PartyId,
                    SessionId = refundSessionId, // VULN-05: درج الفعل
                    Amount = refund.Amount,
                    PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    PaymentMethod = refund.Method,
                    Notes = $"قيد استرداد من المورد عن الفاتورة {originalInvoice.InvoiceId}",
                    CreatedAt = DateTime.UtcNow
                });
            }

            decimal balanceRefund = refundPlan.BalanceRefund;
            if (balanceRefund > 0 && originalInvoice.PartyId.HasValue)
            {
                var supplier = await _dbContext.Suppliers.FindAsync(originalInvoice.PartyId.Value);
                if (supplier != null)
                {
                    supplier.Balance -= balanceRefund;
                    if (supplier.Balance < 0) supplier.Balance = 0;
                }
            }
        }

        private async Task<DtoRefundPlan> BuildRefundPlanAsync(
            Invoice originalInvoice, decimal refundTotal)
        {
            var originalPayments = await _dbContext.Payments
                .Where(p => p.InvoiceId == originalInvoice.InvoiceId)
                .Select(p => new { p.PaymentMethod, p.Amount })
                .ToListAsync();

            var originalByMethod = originalPayments
                .GroupBy(p => p.PaymentMethod)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

            enInvoicesType returnType = originalInvoice.Type == enInvoicesType.purchase
                ? enInvoicesType.supplier_return
                : enInvoicesType.customer_return;

            var priorRefunds = await _dbContext.Payments
                .Where(p => p.InvoiceId != null
                         && p.Invoice!.OriginalInvoiceId == originalInvoice.InvoiceId
                         && p.Invoice.Type == returnType
                         && p.Invoice.Status == enInvoicesStatus.completed)
                .Select(p => new { p.PaymentMethod, p.Amount })
                .ToListAsync();

            var priorByMethod = priorRefunds
                .GroupBy(p => p.PaymentMethod)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

            var plan = new DtoRefundPlan();
            decimal remaining = refundTotal;

            foreach (var method in originalByMethod.Keys.OrderBy(m => m == "cash" ? 0 : 1))
            {
                decimal paid = originalByMethod[method];
                decimal refunded = priorByMethod.TryGetValue(method, out decimal pr) ? pr : 0m;
                decimal remainingPaid = Math.Max(0m, paid - refunded);
                if (remainingPaid <= 0 || remaining <= 0m)
                    continue;

                decimal amount = Math.Min(remaining, remainingPaid);
                plan.Payments.Add(new DtoRefundPayment(method, Math.Round(amount, 2, MidpointRounding.AwayFromZero)));
                remaining -= amount;
            }

            plan.BalanceRefund = Math.Round(remaining, 2, MidpointRounding.AwayFromZero);
            return plan;
        }
    }
}
