using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Common.Utils;
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
        // صلاحيات المرتجعات — في الخدمة لا الواجهة (الواجهة تخفي فقط):
        //  • مرتجع البيع: عملية شاشة بيع — هوية الكاشير (شاشة POS تُفتح
        //    بالهوية لا بالبت: راجع AppPermissions) أو صلاحية فواتير البيع
        //    (بت 16) — نفس قاعدة إغلاق الجلسة في clsPosSessionService،
        //    فلا يتغيّر سلوك الكاشير في نقطة البيع.
        //  • مرتجع الشراء: عملية شاشة فواتير الشراء (بت 32) حصرًا.
        // الفحص مستقل عن الجلسة: تمرير SessionId = null لا يتجاوزه إطلاقًا.
        // (يرجع null = مسموح؛ Result فاشل = يُرفض الطلب.)
        // ─────────────────────────────────────────────────
        private async Task<Result<int>?> CheckCustomerReturnPermissionAsync(int userId)
        {
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, userId);
            if (actorRole < 0 || (!AppPermissions.IsCashier(actorRole)
                && !AppPermissions.Has(actorRole, AppPermissions.SalesInvoices)))
            {
                await clsAudit.LogRejectedAsync(_dbContext, userId, "ReturnDenied",
                    "مرتجع بيع مرفوض: يتطلب هوية الكاشير أو صلاحية شاشة فواتير البيع.");
                return Result<int>.Fail("إنشاء مرتجع البيع يتطلب هوية الكاشير أو صلاحية شاشة فواتير البيع.");
            }
            return null;
        }

        private async Task<Result<int>?> CheckSupplierReturnPermissionAsync(int userId)
        {
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, userId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.PurchaseInvoices))
            {
                await clsAudit.LogRejectedAsync(_dbContext, userId, "ReturnDenied",
                    "مرتجع شراء مرفوض: يتطلب صلاحية شاشة فواتير الشراء.");
                return Result<int>.Fail("إنشاء مرتجع الشراء يتطلب صلاحية شاشة فواتير الشراء.");
            }
            return null;
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

            var permissionCheck = await CheckCustomerReturnPermissionAsync(request.UserId);
            if (permissionCheck != null)
                return permissionCheck;

            var originalInvoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.sale
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة بيع مكتملة.");

            // ══ حماية السنة المالية المغلقة ══
            // مرتجع على فاتورة سنة مغلقة كان يعيد للفترة القديمة مخزونًا واستردادًا
            // فيقلب نتائج سنة مؤرشفة بصمت. سياسة التسوية: الاعتراف بالمرتجع في
            // الفترة الحالية عبر قيد محاسبي معتمد — لا إعادة فعل على السجلات القديمة.
            if (await IsBeforeCurrentFiscalYearAsync(DateOnly.FromDateTime(originalInvoice.CreatedAt ?? DateTime.UtcNow)))
                return Result<int>.Fail("الفاتورة الأصلية تعود لسنة مالية مغلقة — لا يمكن إنشاء مرتجع مباشر عليها. سجّل تسوية بالفترة الحالية عبر قيد محاسبي معتمد.");

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
                // VULN-05: التحقق الجلسي داخل المعاملة — درج الفعل موجود ومفتوح
                // ومملوك للفاعل (أو مدير) قبل أن يُقيد له أي استرداد
                var sessionCheck = await clsPermissions.ValidateDrawerSessionAsync(
                    _dbContext, request.SessionId, request.UserId);
                if (!sessionCheck.IsSuccess)
                    return Result<int>.Fail(sessionCheck.Message);

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

                var returnedByBatch = await GetReturnedQuantitiesByBatchAsync(originalInvoice.InvoiceId, enInvoicesType.customer_return);
                var allocations = AllocateAcrossOriginalLines(originalInvoice.InvoiceItems, GroupRequestItems(request.Items), returnedByBatch);
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

                    // صف لكل دفعة أصلية مستلمة في المرتجع — الكمية والتكلفة التاريخية
                    // (وBatchId) تُعاد كما كانت على بند الفاتورة الأصلي بالضبط:
                    // المرتجع يقلب تكلفة الاستهلاك الفعلية لا Product.CostPrice الحالية.
                    foreach (var a in productAllocations)
                    {
                        _dbContext.InvoiceItems.Add(new InvoiceItem
                        {
                            InvoiceId = returnInvoice.InvoiceId,
                            ProductId = g.ProductId,
                            Quantity = a.Quantity,
                            BatchId = a.BatchId,
                            ItemPrice = a.ItemPrice,
                            ItemCost = a.ItemCost
                        });

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

            var permissionCheck = await CheckSupplierReturnPermissionAsync(request.UserId);
            if (permissionCheck != null)
                return permissionCheck;

            var originalInvoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.purchase
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة شراء مكتملة.");

            // ══ حماية السنة المالية المغلقة ══ (نفس سياسة مرتجع العميل أعلاه)
            if (await IsBeforeCurrentFiscalYearAsync(DateOnly.FromDateTime(originalInvoice.CreatedAt ?? DateTime.UtcNow)))
                return Result<int>.Fail("فاتورة الشراء الأصلية تعود لسنة مالية مغلقة — لا يمكن إنشاء مرتجع مباشر عليها. سجّل تسوية بالفترة الحالية عبر قيد محاسبي معتمد.");

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
                // VULN-05: التحقق الجلسي داخل المعاملة — درج الفعل موجود ومفتوح
                // ومملوك للفاعل (أو مدير) قبل أن يُقيد له أي استرداد
                var sessionCheck = await clsPermissions.ValidateDrawerSessionAsync(
                    _dbContext, request.SessionId, request.UserId);
                if (!sessionCheck.IsSuccess)
                    return Result<int>.Fail(sessionCheck.Message);

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

                var returnedByBatchSup = await GetReturnedQuantitiesByBatchAsync(originalInvoice.InvoiceId, enInvoicesType.supplier_return);
                var allocationsSup = AllocateAcrossOriginalLines(originalInvoice.InvoiceItems, GroupRequestItems(request.Items), returnedByBatchSup);
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

                    // صف لكل دفعة أصلية مستلمة في المرتجع — الكمية وتكلفتها التاريخية
                    // (وBatchId) كما على بند الشراء الأصلي: خصم المخزون والتكلفة
                    // من الدفعة نفسها التي جاءت منها الكمية.
                    foreach (var a in productAllocations)
                    {
                        _dbContext.InvoiceItems.Add(new InvoiceItem
                        {
                            InvoiceId = returnInvoice.InvoiceId,
                            ProductId = g.ProductId,
                            Quantity = a.Quantity,
                            BatchId = a.BatchId,
                            ItemPrice = a.ItemPrice,
                            ItemCost = a.ItemCost
                        });

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

        /// <summary>كميات المرتجع السابقة (لكل منتج) على فاتورة أصلية — للعرض في
        /// شاشة المرتجع: المباع والمرتجع سابقًا والمتبقي. عرض وحسب: الفيصل النهائي
        /// في الصلاحية يبقى حارس CreateCustomerReturnAsync/CreateSupplierReturnAsync
        /// داخل المعاملة، فلا يُعتمد على الواجهة وحدها أبدًا.</summary>
        public async Task<Dictionary<int, int>> GetPreviouslyReturnedQuantitiesAsync(
            int originalInvoiceId, enInvoicesType returnType) =>
            await GetReturnedQuantitiesAsync(originalInvoiceId, returnType);

        /// <summary>هل التاريخ يقع قبل بداية السنة المالية الحالية (سنة مغلقة)؟
        /// حماية السجلات التاريخية — نفس سياسة clsFiscalYearService.</summary>
        private async Task<bool> IsBeforeCurrentFiscalYearAsync(DateOnly date)
        {
            var setting = await _dbContext.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            if (setting != null && DateOnly.TryParse(setting.SettingValue, out var fiscalYearStart))
                return date < fiscalYearStart;

            var today = ProgramClock.Today; /* VULN-45: اليوم بتوقيت البرنامج UTC+3 — مصدر موحّد */
            return date < new DateOnly(today.Year, 1, 1);
        }

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

        /// <summary>
        /// توزيع الكميات المطلوبة على سطور الفاتورة الأصلية بنفس الترتيب القائم
        /// (FEFO: الأقرب انتهاءً ثم الأقدم ثم الأرخص ثم ترتيب السطر).
        ///
        /// تصحيح تسريب الكمية: سعة كل دفعة = مجموع ما بِيع/اشتري منها عبر سطور
        /// الفاتورة الأصلية ناقص المرتجع سابقًا من الدفعة نفسها (returnedByBatch).
        /// كان التوزيع يعامل كمية السطر الأصلية كأنها كاملة صالحة في كل مرة —
        /// مرتجعان جزئيان متتاليان يعيدان إلى الدفعة الأولى أكثر مما بِيع منها
        /// (مخزون وهمي في الدفعة، وحرمان الدفعات الأخرى من حصتها).
        /// الدفعات التي تشاركها سطور متعددة تُجمع سعاتها (السحب عند البيع يقسم
        /// السطر صفًا لكل دفعة، فقد تكرر الدفعة نفسها على أكثر من سطر).
        /// الحارس الإجمالي لكل منتج يبقى مسؤول الرفض النهائي خارج هذه الدالة.
        /// </summary>
        private static List<(int LineIndex, int ProductId, int Quantity, decimal ItemPrice, decimal? ItemCost, int? BatchId)> AllocateAcrossOriginalLines(
            IEnumerable<InvoiceItem> originalItems,
            IEnumerable<(int ProductId, int Quantity)> groupedRequest,
            IReadOnlyDictionary<(int ProductId, int? BatchId), int> returnedByBatch)
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

            // سعة كل دفعة المتبقية للإرجاع: المباع منها عبر سطور الفاتورة ناقص المرتجع سابقًا
            var batchRemaining = ordered
                .GroupBy(x => (x.Item.ProductId, x.Item.BatchId))
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.Item.Quantity)
                         - returnedByBatch.GetValueOrDefault(g.Key));

            var request = groupedRequest.ToDictionary(g => g.ProductId, g => g.Quantity);
            var result = new List<(int LineIndex, int ProductId, int Quantity, decimal ItemPrice, decimal? ItemCost, int? BatchId)>();

            foreach (var x in ordered)
            {
                if (!request.TryGetValue(x.Item.ProductId, out int remaining) || remaining <= 0)
                    continue;

                var key = (x.Item.ProductId, x.Item.BatchId);
                if (!batchRemaining.TryGetValue(key, out int lineCapacity) || lineCapacity <= 0)
                    continue;

                int take = Math.Min(remaining, lineCapacity);
                if (take <= 0)
                    continue;

                result.Add((x.Index, x.Item.ProductId, take, x.Item.ItemPrice, x.Item.ItemCost, x.Item.BatchId));
                request[x.Item.ProductId] = remaining - take;
                batchRemaining[key] = lineCapacity - take;
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

        /// <summary>
        /// الكميات المرتجعة سابقًا على الفاتورة الأصلية مصنّفة حسب (المنتج، الدفعة).
        /// بند المرتجع يحمل BatchId الدفعة الأصلية كما سُجلت على سطر البيع/الشراء
        /// (السحب عند البيع يقسم السطر صفًا لكل دفعة فعلية) — فالتتبع دقيق
        /// ولا يُخمَّن الدفعة من ProductId وحده. هذا المفتاح يصحح سعة كل
        /// سطر/دفعة في AllocateAcrossOriginalLines: التوزيع لا يعيد إلى دفعة
        /// أكثر مما بِيع منها أصلًا عند تكرار المرتجعات الجزئية.
        /// </summary>
        private async Task<Dictionary<(int ProductId, int? BatchId), int>> GetReturnedQuantitiesByBatchAsync(
            int originalInvoiceId, enInvoicesType returnType)
        {
            var returnedItems = await _dbContext.InvoiceItems
                .Where(ii => ii.Invoice.OriginalInvoiceId == originalInvoiceId
                          && ii.Invoice.Type == returnType
                          && ii.Invoice.Status == enInvoicesStatus.completed)
                .GroupBy(ii => new { ii.ProductId, ii.BatchId })
                .Select(g => new { g.Key.ProductId, g.Key.BatchId, Quantity = g.Sum(ii => ii.Quantity) })
                .ToListAsync();

            return returnedItems.ToDictionary(x => (x.ProductId, x.BatchId), x => x.Quantity);
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

            // B5 + التوازن: كل زيادة مخزون تمر عبر دفعة تحملها — لا مخزون قابل
            // للبيع بلا مصدر دفعة صالح.
            // VULN-45: اليوم بتوقيت البرنامج (UTC+3) لا التاريخ UTC الخام —
            // وإلا بقيت دفعة منتهية أمس قابلة للبيع حتى 03:00 فجرًا.
            DateOnly today = ProgramClock.Today;
            Batch? batch = null;

            if (batchId.HasValue)
            {
                var source = await _dbContext.Batches.FindAsync(batchId.Value);
                if (source != null && source.ProductId == productId)
                    batch = source;
            }

            if (batch != null && batch.ExpiryDate.HasValue && batch.ExpiryDate.Value < today)
            {
                // B5: دفعة المصدر منتهية الصلاحية — الكمية تعود إلى دفعتها المنتهية
                // نفسها فتبقى محجوبة عن البيع تلقائيًا (البيع يستبعد المنتهية).
                // كانت تُصنع دفعة جديدة بلا صلاحية فيتحول المرتجع المنتهي إلى
                // مخزون صالح للبيع دون أي فحص — تلف قديم يُباع كأنه جديد.
                batch.BatchQuantity += quantity;
            }
            else if (batch != null)
            {
                batch.BatchQuantity += quantity;
            }
            else
            {
                // بلا دفعة مصدر صالحة (بند قديم بلا BatchId أو دفعة لمنتج آخر):
                // المخزون يعود إلى دفعة مرتجع محجوبة (حجر) — تظل مستثناة من
                // البيع وحساب المتاح حتى يُفحص ويُفرج عنه صراحة (نقل/اعتماد)،
                // فلا يُخلق مخزون قابل للبيع بلا مصدر دفعة.
                batch = new Batch
                {
                    ProductId = productId,
                    BatchNumber = $"RET-{returnInvoice.InvoiceId}-{productId}-{DateTime.UtcNow.Ticks}",
                    ExpiryDate = null,
                    BatchQuantity = quantity,
                    IsQuarantined = true,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Batches.Add(batch);
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
                else
                {
                    await DeductAcrossBatchesAsync(productId, quantity, returnInvoice);
                }
            }
            else
            {
                // بند بلا دفعة مصدر: الخصم يمر عبر الدفعات حصرًا (FEFO) — خصم
                // Inventory وحده يترك الدفعات أعلى من المخزون الكلي (مخزون شبح).
                await DeductAcrossBatchesAsync(productId, quantity, returnInvoice);
            }

            inventory.QuantityAvailable -= quantity;
            inventory.UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>خصم عبر الدفعات FEFO (الأقرب انتهاءً ثم المحجوبة أولًا) —
        /// يضمن أن كل نقصان مخزون يحمل من دفعة فعلية فتبقى معادلة
        /// «مجموع الدفعات = المخزون الكلي» قائمة.</summary>
        private async Task DeductAcrossBatchesAsync(int productId, int quantity, Invoice returnInvoice)
        {
            DateOnly today = ProgramClock.Today; /* VULN-45: يوم البرنامج لا التاريخ UTC الخام */
            var batches = await _dbContext.Batches
                .Where(b => b.ProductId == productId && b.BatchQuantity > 0)
                .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                .ThenBy(b => b.IsQuarantined ? 0 : 1)
                .ThenBy(b => b.CreatedAt)
                .ToListAsync();

            int remaining = quantity;
            foreach (var b in batches)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, b.BatchQuantity);
                b.BatchQuantity -= take;
                remaining -= take;

                _dbContext.StockMovements.Add(new StockMovement
                {
                    ProductId = productId,
                    BatchId = b.BatchId,
                    MovementType = enStockMovementType.supplier_return,
                    Quantity = -take,
                    SourceType = "Invoice",
                    SourceId = returnInvoice.InvoiceId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            if (remaining > 0)
                throw new InvalidOperationException($"المخزون غير كافٍ لتغطية مرتجع المورد (دفعات المنتج {productId})");
        }

        // VULN-05: refundSessionId = درج الجلسة التي تُخرج نقد الاسترداد —
        // تقرير Z لليوم يرى المرتجع الذي دُفع اليوم، لا يُحمَّل على جلسة مجمدة.
        //
        private async Task ProcessCustomerRefundAsync(Invoice originalInvoice, Invoice returnInvoice, decimal refundTotal, int? refundSessionId)
        {
            var refundPlan = await BuildRefundPlanAsync(originalInvoice, returnInvoice.InvoiceId, refundTotal);

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
                    // VULN-45 + سجل الز: PaymentDate = يوم البرنامج (UTC+3) لا
                    // التاريخ UTC الخام — استرداد بعد منتصف الليل المحلي (00:00–03:00)
                    // كان يُختَم بتاريخ الأمس فيختفي من تقرير Z لليوم الفعلي
                    // (clsReportService يصفّي Payments على PaymentDate = يوم البرنامج).
                    PaymentDate = ProgramClock.Today,
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
            var refundPlan = await BuildRefundPlanAsync(originalInvoice, returnInvoice.InvoiceId, refundTotal);

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
                    // VULN-45: نفس توجيه استرداد العميل — يوم البرنامج (UTC+3) لا التاريخ UTC الخام.
                    PaymentDate = ProgramClock.Today,
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

        // ═════════════════════════════════════════════════
        // خطة الاسترداد: الدَّين أولًا ثم النقد
        // ═════════════════════════════════════════════════
        // مثال المطلب: فاتورة 100 دفع فيها العميل 50 (دين متبقٍ 50)، ثم أرجع بضاعة
        // قيمتها 80. الصحيح: الدين ينخفض 50 (إلى صفر) ويُسترد نقدًا 30.
        // المنطق القديم كان يرد المدفوع النقدي أولًا (50 كاش) ويخفض الدين 30 فقط —
        // فيبقى على العميل دَين 20 عن بضاعة لم يبق منها شيء.
        //
        // الترتيب الجديد الحتمي:
        //  1) مستحق الفاتورة الآن (بتسوية الدفعات المرتبطة والعامة والمرتجعات —
        //     clsPaymentAllocation) ناقص ما خفّضته مرتجعات سابقة من الرصيد
        //     → يُخفَّض أولًا (BalanceRefund).
        //  2) الباقي يُرد نقدًا بطرق الدفع الأصلية، وسقف كل طريقة هو ما دُفع بها
        //     فعلًا (مرتبط + نصيب الدفعات العامة بتخصيصها الزمني) ناقص ما رُدّ
        //     سابقًا بنفس الطريقة — لا استرداد نقدي يتجاوز ما دخل الدرج أبدًا.
        private async Task<DtoRefundPlan> BuildRefundPlanAsync(
            Invoice originalInvoice, int currentReturnInvoiceId, decimal refundTotal)
        {
            char partyType = originalInvoice.PartyType ?? 'C';
            int partyId = originalInvoice.PartyId ?? 0;

            enInvoicesType returnType = originalInvoice.Type == enInvoicesType.purchase
                ? enInvoicesType.supplier_return
                : enInvoicesType.customer_return;

            // 1) مستحق الفاتورة وفق التسوية الحتمية (مرتبط + نصيب الدفعات العامة)
            //    مع استبعاد مرتجع الجلسة الحالية من صافي الالتزام (توزيعه لم يُقرَّر بعد)
            var outstandingRows = await clsPaymentAllocation
                .GetPartyOutstandingAsync(_dbContext, partyType, partyId, currentReturnInvoiceId);
            var row = outstandingRows.FirstOrDefault(r => r.InvoiceId == originalInvoice.InvoiceId);

            // 2) ما خفّضته مرتجعات سابقة من الرصيد على هذه الفاتورة:
            //    مجموع قيم المرتجعات السابقة ناقص ما رُدّ نقدًا عليها — الباقي
            //    بالضرورة هو ما خفّض الرصيد (معادلة هذه الخطة نفسها).
            //    نستثني مرتجع الجلسة الحالية: فاتورته حُفظت داخل المعاملة قبل
            //    استدعاء هذه الدالة، لكن توزيع استردادها لم يُقرَّر بعد.
            decimal priorReturnNet = await _dbContext.Invoices
                .Where(r => r.OriginalInvoiceId == originalInvoice.InvoiceId
                         && r.InvoiceId != currentReturnInvoiceId
                         && r.Type == returnType
                         && r.Status == enInvoicesStatus.completed)
                .SumAsync(r => (decimal?)r.NetAmount) ?? 0m; // سالبة بالتصميم
            decimal priorReturnTotals = -priorReturnNet;

            var priorRefunds = await _dbContext.Payments
                .Where(p => p.InvoiceId != null
                         && p.Invoice!.OriginalInvoiceId == originalInvoice.InvoiceId
                         && p.Invoice.Type == returnType
                         && p.Invoice.Status == enInvoicesStatus.completed
                         && p.InvoiceId != currentReturnInvoiceId)
                .Select(p => new { p.PaymentMethod, p.Amount })
                .ToListAsync();

            var priorByMethod = priorRefunds
                .GroupBy(p => p.PaymentMethod)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

            decimal priorCashRefunds = priorRefunds.Sum(p => p.Amount);
            decimal priorBalanceReduction = Math.Max(0m, priorReturnTotals - priorCashRefunds);

            decimal invoiceOutstanding = Math.Max(0m,
                (row?.Outstanding ?? 0m) - priorBalanceReduction);

            // 3) الدَّين أولًا — ثم ما تبقى يُرد نقدًا
            var plan = new DtoRefundPlan();
            decimal balanceRefund = Math.Min(refundTotal, invoiceOutstanding);
            plan.BalanceRefund = Math.Round(balanceRefund, 2, MidpointRounding.AwayFromZero);
            decimal remaining = refundTotal - balanceRefund;

            if (remaining <= 0m)
                return plan;

            // 4) سقوف النقد لكل طريقة
            var originalPayments = await _dbContext.Payments
                .Where(p => p.InvoiceId == originalInvoice.InvoiceId)
                .Select(p => new { p.PaymentMethod, p.Amount })
                .ToListAsync();

            var linkedByMethod = originalPayments
                .GroupBy(p => p.PaymentMethod)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

            // نصيب الفاتورة من الدفعات العامة يُنسَب لطرقها بترتيبها الزمني
            decimal allocatedGeneral = row?.AllocatedGeneral ?? 0m;
            var generalAvailByMethod = new Dictionary<string, decimal>();
            if (allocatedGeneral > 0m)
            {
                var generalRows = await _dbContext.Payments
                    .Where(p => p.InvoiceId == null
                             && p.PartyType == partyType && p.PartyId == partyId
                             && p.Type == (partyType == 'S'
                                 ? enPaymentType.outcome : enPaymentType.income))
                    .OrderBy(p => p.CreatedAt).ThenBy(p => p.PaymentId)
                    .Select(p => new { p.PaymentMethod, p.Amount })
                    .ToListAsync();

                decimal pool = allocatedGeneral;
                foreach (var g in generalRows)
                {
                    if (pool <= 0m) break;
                    decimal take = Math.Min(g.Amount, pool);
                    generalAvailByMethod[g.PaymentMethod] =
                        generalAvailByMethod.GetValueOrDefault(g.PaymentMethod) + take;
                    pool -= take;
                }
            }

            foreach (var method in linkedByMethod.Keys.Union(generalAvailByMethod.Keys)
                         .OrderBy(m => m == "cash" ? 0 : 1))
            {
                if (remaining <= 0m)
                    break;

                decimal linkedAvail = Math.Max(0m,
                    (linkedByMethod.TryGetValue(method, out decimal paid) ? paid : 0m)
                    - (priorByMethod.TryGetValue(method, out decimal refunded) ? refunded : 0m));
                decimal available = linkedAvail + generalAvailByMethod.GetValueOrDefault(method);
                if (available <= 0m)
                    continue;

                decimal amount = Math.Min(remaining, available);
                plan.Payments.Add(new DtoRefundPayment(method, Math.Round(amount, 2, MidpointRounding.AwayFromZero)));
                remaining -= amount;
            }

            // حارس نظري: ما تعذّر توزيعه نقدًا (لا طرق متاحة) يُخفِّض الدين هو الآخر —
            // لا يُفقد مبلغ مستحق للطرف ولا يُرد من عدم (الاسترداد لا يتجاوز المدفوع).
            if (remaining > 0m)
                plan.BalanceRefund = Math.Round(plan.BalanceRefund + remaining, 2, MidpointRounding.AwayFromZero);

            return plan;
        }
    }
}
