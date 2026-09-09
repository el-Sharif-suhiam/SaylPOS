using Microsoft.EntityFrameworkCore;
using Common.DTOs;
using Common.Results;
using Common.Utils;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Common.DTOs.RequestModels;
using Common.Enums;
namespace Sayl.Core.Services
{
    public class clsCheckoutService
    {
        private readonly SaylDbContext _dbContext;

       public clsCheckoutService(SaylDbContext dbContext) => _dbContext = dbContext;


     
        // ─────────────────────────────────────────────────
        // الدالة العامة: إتمام عملية شراء أو تعليقها
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> ProcessCheckoutAsync(DtoCheckoutRequest request, bool suspend = false)
        {
            if (request.Items == null || !request.Items.Any())
                return Result<int>.Fail("يجب إضافة منتجات إلى الفاتورة.");

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                    return Result<int>.Fail("الكمية يجب أن تكون أكبر من صفر.");
            }

            // صلاحية الخصم: خصم أكبر من حد الكاشير يتطلب شاشة فواتير البيع (بت 16)
            // — أوامر POS نفسها بلا فحص بت (الكاشير 0 لا يفتح شيئًا بالبت أصلًا)
            if (request.DiscountPercent > clsPermissions.MaxCashierDiscountPercent || request.DiscountAmount > 0)
            {
                int actorRole = await GetUserRoleValueAsync(request.UserId);
                if (!AppPermissions.Has(actorRole, AppPermissions.SalesInvoices))
                    return Result<int>.Fail($"الخصم فوق {clsPermissions.MaxCashierDiscountPercent:0.#}% يتطلب صلاحية شاشة فواتير البيع. راجع المدير.");
            }

            if (request.CustomerId.HasValue)
            {
                bool customerExists = await _dbContext.Customers.AnyAsync(c => c.PartyId == request.CustomerId.Value);
                if (!customerExists)
                    return Result<int>.Fail("العميل غير موجود.");
            }

            // Serializable مع إعادة محاولة تلقائية عند deadlock (1205):
            // التعارض قابل للاختيار من جديد بأمان، وليس خطأ المستخدم
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await ExecuteCheckoutAsync(request, suspend);
                }
                catch (DeadlockRetryableException)
                {
                    if (attempt >= maxAttempts)
                        return Result<int>.Fail("ازدحام في العمليات — حاول مرة أخرى بعد لحظات.");
                    await Task.Delay(50 * attempt); // تراجع قصير قبل المحاولة التالية
                }
            }
        }

        /// <summary>deadlock في SQL Server (رقم الخطأ 1205) — قابل لإعادة المحاولة.</summary>
        private sealed class DeadlockRetryableException : Exception
        {
            public DeadlockRetryableException(Exception inner) : base(inner.Message, inner) { }
        }

        private async Task<Result<int>> ExecuteCheckoutAsync(DtoCheckoutRequest request, bool suspend)
        {
            //  بدء المعاملة (Serializable لمنع بيع نفس الكمية من طرفيتين في نفس اللحظة)
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // إنشاء رأس الفاتورة
                Invoice invoice = await CreateInvoiceHeaderAsync(request, suspend);
                await _dbContext.SaveChangesAsync(); // للحصول على InvoiceId

                // إضافة بنود الفاتورة
                await AddInvoiceItemsAsync(invoice.InvoiceId, request.Items);

                if (suspend)
                {
                    // حجز المخزون فقط
                    await ReserveStockAsync(request.Items);
                }
                else
                {
                    // خصم المخزون (معالجة الدُفعات)
                    await DeductStockAndCreateMovementsAsync(request.Items, invoice.InvoiceId);

                    // معالجة الدفع
                    await ProcessPaymentAsync(invoice, request);
                }

                await _dbContext.SaveChangesAsync();

                // تدقيق: تسجيل العملية
                clsAudit.Log(_dbContext, request.UserId,
                    suspend ? "SuspendInvoice" : "SaleInvoice",
                    $"فاتورة #{invoice.InvoiceId} — صافي: {invoice.NetAmount:N2} — بنود: {request.Items.Count} — خصم: {invoice.DiscountAmount:N2}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                return Result<int>.Ok(invoice.InvoiceId, suspend ? "تم تعليق الفاتورة وحجز المخزون." : "تمت عملية البيع بنجاح.");
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                // إزالة الكيانات المتتبعة حتى لا تُحقن في العملية القادمة على نفس السياق
                _dbContext.ChangeTracker.Clear();
                return Result<int>.Fail(ex.Message);
            }
            catch (Exception ex) when (IsDeadlock(ex))
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                throw new DeadlockRetryableException(ex); // يلتقطه حلق إعادة المحاولة أعلاه
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                string details = ex.Message;
                var inner = ex.InnerException;
                while (inner != null)
                {
                    details += " ← " + inner.Message;
                    inner = inner.InnerException;
                }
                return Result<int>.Fail($"فشلت عملية البيع ({ex.GetType().Name}): {details}");
            }
        }

        /// <summary>هل الاستثناء deadlock (SQL Server error 1205)؟</summary>
        private static bool IsDeadlock(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is Microsoft.Data.SqlClient.SqlException sql && sql.Number == 1205)
                    return true;
            }
            return false;
        }

        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return 0;
            var user = await _dbContext.Users.Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            return user?.Role.RoleValue ?? 0;
        }

        // ─────────────────────────────────────────────────
        // إكمال فاتورة معلقة
        // VULN-08: المستكمِل يُفحص كالإلغاء تمامًا — صاحب الفاتورة يكملها،
        // وغيره يحتاج صلاحية محاسب فأعلى. (كان الإكمال بلا أي فحص ملكية
        // بينما الإلغاء مشدد — سجل: الإكمال يحرك مالًا ومخزونًا نهائيًا.)
        // ─────────────────────────────────────────────────
        public async Task<Result> CompleteSuspendedInvoiceAsync(int invoiceId, List<DtoPaymentInfo> payments, int actorUserId, int? actorSessionId = null)
        {
            if (payments == null || !payments.Any(p => p.Amount > 0))
                return Result.Fail("يجب إدخال مبلغ دفعة واحد على الأقل.");

            var invoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId && i.Status == enInvoicesStatus.suspended);

            if (invoice == null)
                return Result.Fail("الفاتورة المعلقة غير موجودة أو تم إكمالها بالفعل.");

            // ملكية الإكمال: نفس قاعدة الإلغاء (CancelSuspendedInvoiceAsync)
            if (invoice.UserId != actorUserId)
            {
                int actorRole = await GetUserRoleValueAsync(actorUserId);
                if (!AppPermissions.Has(actorRole, AppPermissions.SalesInvoices))
                    return Result.Fail("إكمال فاتورة معلقة لكاشير آخر يتطلب صلاحية شاشة فواتير البيع.");
            }

            // المعاملة تحتضن الانتقال الذري: فشل أي خطوة لاحقة يعيد الحالة suspended معها
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // انتقال ذري للحالة داخل المعاملة: أول من يصل يفوز — يمنع خصم المخزون مرتين
                int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Invoices SET Status = 'completed' WHERE InvoiceId = {invoiceId} AND Status = 'suspended'");
                if (claimed == 0)
                    return Result.Fail("الفاتورة قيد الإكمال من طرف آخر أو تم إغلاقها.");

                // تحويل الحالة (المحلية — الحالة في القاعدة ثبتت بالانتقال الذري أعلاه)
                invoice.Status = enInvoicesStatus.completed;

                // خصم المخزون (معالجة الدُفعات)
                var items = invoice.InvoiceItems.Select(i => new DtoCheckoutItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    BatchId = i.BatchId
                }).ToList();

                await DeductStockAndCreateMovementsAsync(items, invoice.InvoiceId);

                // معالجة الدفع — VULN-05: دفعات الإكمال تُنسب لدرج جلسة المستكمِل
                // الفعلية (actorSessionId) لا لجلسة التعليق القديمة: النقد دخل درج
                // اليوم فتقرير Z لليوم هو من يراه. null = بلا جلسة (تُهمل بتقارير الجلسات).
                var request = new DtoCheckoutRequest
                {
                    CustomerId = invoice.PartyId,
                    Payments = payments,
                    SessionId = actorSessionId,
                    DiscountPercent = 0, // يمكنك إضافة حقول للخصم في الفاتورة المعلقة
                    TaxPercent = 0
                };
                await ProcessPaymentAsync(invoice, request);

                // ملاحظة: تحرير الحجز يتم داخل DeductStockAndCreateMovementsAsync

                clsAudit.Log(_dbContext, invoice.UserId, "CompleteSuspendedInvoice",
                    $"إكمال فاتورة معلقة #{invoiceId} — صافي: {invoice.NetAmount:N2}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إكمال الفاتورة بنجاح.");
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail(ex.Message);
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail("فشل إكمال الفاتورة المعلقة.");
            }
        }

        // ─────────────────────────────────────────────────
        // إلغاء فاتورة معلقة (تحرير الحجز)
        // الكاشير يلغي فواتيره فقط، والمحاسب فأعلى يلغي أي فاتورة
        // ─────────────────────────────────────────────────
        public async Task<Result> CancelSuspendedInvoiceAsync(int invoiceId, int actorUserId)
        {
            var invoice = await _dbContext.Invoices.FindAsync(invoiceId);
            if (invoice == null || invoice.Status != enInvoicesStatus.suspended)
                return Result.Fail("الفاتورة المعلقة غير موجودة.");

            if (invoice.UserId != actorUserId)
            {
                int actorRole = await GetUserRoleValueAsync(actorUserId);
                if (!AppPermissions.Has(actorRole, AppPermissions.SalesInvoices))
                    return Result.Fail("إلغاء فاتورة معلقة لأجل كاشير آخر يتطلب صلاحية شاشة فواتير البيع.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // انتقال ذري: يمنع إلغاء فاتورة يجري إكمالها بالتوازي (والعكس)
                int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Invoices SET Status = 'cancelled' WHERE InvoiceId = {invoiceId} AND Status = 'suspended'");
                if (claimed == 0)
                    return Result.Fail("الفاتورة لم تعد معلقة (أُكملت أو أُلغيت من طرف آخر).");

                invoice.Status = enInvoicesStatus.cancelled;

                // تحرير الحجز
                var items = await _dbContext.InvoiceItems
                    .Where(ii => ii.InvoiceId == invoiceId)
                    .Select(ii => new { ii.ProductId, ii.Quantity })
                    .ToListAsync();

                foreach (var item in items)
                {
                    var inv = await _dbContext.Inventories.FindAsync(item.ProductId);
                    if (inv != null)
                    {
                        inv.QuantityReserved -= item.Quantity;
                        if (inv.QuantityReserved < 0) inv.QuantityReserved = 0; // أمان
                    }
                }

                clsAudit.Log(_dbContext, actorUserId, "CancelSuspendedInvoice",
                    $"إلغاء فاتورة معلقة #{invoiceId} — صافي: {invoice.NetAmount:N2}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إلغاء الفاتورة وتحرير المخزون المحجوز.");
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail(ex.Message);
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail("فشل إلغاء الفاتورة.");
            }
        }

        // ═════════════════════════════════════════════════
        // الدوال الخاصة
        // ═════════════════════════════════════════════════

        private async Task<Invoice> CreateInvoiceHeaderAsync(DtoCheckoutRequest request, bool suspend)
        {

            var ids = request.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _dbContext.Products
                .Where(p => ids.Contains(p.ProductId))
                .ToDictionaryAsync(p => p.ProductId);

            decimal total = 0;
            foreach (var item in request.Items)
            {
                if (!products.TryGetValue(item.ProductId, out var prod))
                    throw new InvalidOperationException($"المنتج {item.ProductName} غير موجود.");

                // VULN-14: سعر السلة (snapshot) أولًا — وإن غاب (مستدعٍ قديم)
                // فسعر المنتج الحالي كما كان.
                total += (item.UnitPrice ?? prod.BasePrice) * item.Quantity;
            }

            // ══ VULN-02: حراس المبالغ (حدود الخدمة — الواجهة ليست حد الثقة) ══
            // الخصم بالنسبة: 0..100. الخصم بالمبلغ: 0..total — وإلا صافٍ سالب
            // يصنع فواتير «مدفوعة» من العدم ويمحو ديون العملاء وهميًا.
            if (request.DiscountPercent < 0 || request.DiscountPercent > 100)
                throw new InvalidOperationException("نسبة الخصم يجب أن تكون بين 0 و 100.");
            if (request.DiscountAmount < 0 || request.DiscountAmount > total)
                throw new InvalidOperationException("مبلغ الخصم لا يمكن أن يتجاوز إجمالي الفاتورة أو يكون سالبًا.");
            // الضريبة: نسبة فقط وضمن سقف عقلاني — النسب الجنونية تنتج ديونًا
            // وهمية تفوق المبيعات (خصوصًا مع CreditLimit=0 أي «غير محدود»).
            if (request.TaxPercent < 0 || request.TaxPercent > 100)
                throw new InvalidOperationException("نسبة الضريبة يجب أن تكون بين 0 و 100.");

            decimal discount = request.DiscountPercent > 0
                ? total * request.DiscountPercent / 100
                : request.DiscountAmount;

            decimal afterDiscount = total - discount;
            decimal tax = request.TaxPercent > 0
                ? afterDiscount * request.TaxPercent / 100
                : 0;

            decimal net = afterDiscount + tax;

            // الصافي لا يهبط لما دون صفر — حد الفاتورة المجانية صافي 0
            if (net < 0)
                throw new InvalidOperationException("صافي الفاتورة لا يمكن أن يكون سالبًا.");

            var invoice = new Invoice
            {
                Type = enInvoicesType.sale,
                Status = suspend ? enInvoicesStatus.suspended : enInvoicesStatus.completed,
                PaymentStatus = enPaymentStatus.unpaid,
                PartyType = request.CustomerId.HasValue ? (char?)'C' : null,
                PartyId = request.CustomerId ?? null,
                UserId = request.UserId,
                SessionId = request.SessionId,
                TotalAmount = total,
                DiscountAmount = discount,
                TaxAmount = tax,
                NetAmount = net,
                Notes = request.Notes,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Invoices.Add(invoice);
            return invoice;
        }

         private async Task AddInvoiceItemsAsync(int invoiceId, List<DtoCheckoutItem> items)
         {
             foreach (var item in items)
             {
                 var product = await _dbContext.Products.FindAsync(item.ProductId);
                 var invoiceItem = new InvoiceItem
                 {
                     InvoiceId = invoiceId,
                     ProductId = item.ProductId,
                     Quantity = item.Quantity,
                     BatchId = item.BatchId, // قد يكون null
                     // VULN-14: snapshot سعر السلة — لا السعر الحالي بعد لحظة البناء
                     ItemPrice = item.UnitPrice ?? product?.BasePrice ?? 0,
                     ItemCost = product?.CostPrice
                 };

            _dbContext.InvoiceItems.Add(invoiceItem);
             }
         }

        private async Task ReserveStockAsync(List<DtoCheckoutItem> items)
        {
            foreach (var item in items)
            {
                var product = await _dbContext.Products.FindAsync(item.ProductId);
                if (product == null || product.Type != "stock") continue;

                var inv = await _dbContext.Inventories.FindAsync(item.ProductId);
                if (inv == null)
                    throw new InvalidOperationException($"لا يوجد مخزون للمنتج {item.ProductName}");

                int available = (inv.QuantityAvailable ?? 0 ) - (inv.QuantityReserved ?? 0);
                if (available < item.Quantity)
                    throw new InvalidOperationException($"المخزون المتاح غير كافٍ للمنتج {product.Name}");

                inv.QuantityReserved += item.Quantity;
            }
        }

        private async Task ReleaseReservationAsync(List<DtoCheckoutItem> items)
        {
            foreach (var item in items)
            {
                var product = await _dbContext.Products.FindAsync(item.ProductId);
                if (product == null || product.Type != "stock") continue;

                var inv = await _dbContext.Inventories.FindAsync(item.ProductId);
                if (inv != null)
                {
                    inv.QuantityReserved -= item.Quantity;
                    if (inv.QuantityReserved < 0) inv.QuantityReserved = 0;
                }
            }
        }

        private async Task DeductStockAndCreateMovementsAsync(List<DtoCheckoutItem> items, int invoiceId)
        {
            foreach (var item in items)
            {
                var product = await _dbContext.Products.FindAsync(item.ProductId);
                if (product == null || product.Type != "stock") continue;

                var inv = await _dbContext.Inventories.FindAsync(item.ProductId);
                if (inv == null)
                    throw new InvalidOperationException($"لا يوجد مخزون للمنتج {item.ProductName}");

                // يجب أن تكون الكمية المحجوزة قد غطت هذا الطلب (عند الإكمال) أو لم تحجز (عند البيع المباشر)
                // VULN-10: صف Inventory بـ NULL كان يتخطى فحص التوفر كليًا (is not null)
                // فباعة بلا حاجز بينما تبقى قيمة NULL تنجرف. الآن: NULL يعامل كصفر —
                // البيع يرفض برسالة واضحة، والقيمة الفاسدة تظهر بدل أن تختبئ.
                int availableNow = (inv.QuantityAvailable ?? 0) - (inv.QuantityReserved ?? 0);
                if (availableNow < item.Quantity)
                    throw new InvalidOperationException($"المخزون غير كافٍ للمنتج {product.Name}");
                // تطبيق FEFO أو استخدام الدُفعة المحددة
              
                    // FEFO — مع استبعاد الدفعات المنتهية الصلاحية
                    DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
                    var batches = await _dbContext.Batches
                        .Where(b => b.ProductId == item.ProductId && b.BatchQuantity > 0
                                 && (b.ExpiryDate == null || b.ExpiryDate >= today))
                        .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                        .ThenBy(b => b.CreatedAt)
                        .ToListAsync();

                    int remaining = item.Quantity;
                    int? firstConsumedBatchId = null; // VULN-36: الدفعة الأولى المستهلكة فعليًا
                    foreach (var batch in batches)
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, batch.BatchQuantity);
                        batch.BatchQuantity -= take;
                        remaining -= take;

                        firstConsumedBatchId ??= batch.BatchId;

                        _dbContext.StockMovements.Add(new StockMovement
                        {
                            ProductId = item.ProductId,
                            BatchId = batch.BatchId,
                            MovementType = enStockMovementType.sale,
                            Quantity = -take,
                            SourceType = "Invoice",
                            SourceId = invoiceId,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (remaining > 0)
                        throw new InvalidOperationException($"المخزون غير كافٍ لتغطية الطلب للمنتج {product.Name}");

                    // VULN-36: كتابة الدفعة الفعلية على بند الفاتورة — كانت
                    // كل فواتير البيع المباشر تحمل BatchId=null فيُفقد تتبع
                    // FEFO/الصلاحية، ومرتجعها كان يزيد Inventory بلا Batch
                    // ولا حركة (انحراف ثلاثي دائم). أول دفعة مستهلكة تُثبَّت
                    // على البند؛ التوزيع الكامل موثق في StockMovements.
                    // ⚠️ البحث في الكيانات المتتبعة (Local) لا القاعدة:
                    // البنود أُضيفت للـ tracker ولم تُحفظ بعد — استعلام القاعدة
                    // يرمي "Sequence contains no elements". Local يعطي نفس
                    // الكائن الذي سيُحفظ فتُكتب الدفعة في نفس الحفظ.
                    if (firstConsumedBatchId.HasValue && item.BatchId == null)
                    {
                        var trackedItem = _dbContext.InvoiceItems.Local
                            .FirstOrDefault(ii => ii.InvoiceId == invoiceId && ii.ProductId == item.ProductId);
                        if (trackedItem != null)
                        {
                            trackedItem.BatchId = firstConsumedBatchId;
                            item.BatchId = firstConsumedBatchId; // كائن الطلب أيضًا — لأسطح لاحقة داخل نفس الطلب
                        }
                    }
                

                // تحديث المخزون
                inv.QuantityAvailable -= item.Quantity;
                inv.QuantityReserved -= item.Quantity; // تحرير الحجز (إن كان موجوداً)
                if (inv.QuantityReserved < 0) inv.QuantityReserved = 0;
                inv.UpdatedAt = DateTime.UtcNow;
            }
        }

        private async Task ProcessPaymentAsync(Invoice invoice, DtoCheckoutRequest request)
        {
            decimal net = invoice.NetAmount;

            // دفعات متعددة (نقدي + شبكة...) أو دفعة واحدة
            var payments = request.Payments != null && request.Payments.Any()
                ? request.Payments
                : (request.Payment != null ? new List<DtoPaymentInfo> { request.Payment } : new List<DtoPaymentInfo>());

            decimal paid = 0;
            foreach (var payment in payments)
            {
                if (payment.Amount <= 0)
                    continue;

                // ══ VULN-02b: سقف الدفع (حد الخدمة) ══
                // الدفع الزائد عن الصافي كان يُبتلع بلا أثر: لا باقٍ للعميل ولا
                // رصيد — دفعة 500 على فاتورة 100 تجعل 400 تختفي من الدفاتر،
                // ومع المرتجعات يُرد الصافي فقط فتضيع الفاتورة الفارقة نهائيًا.
                if (paid + payment.Amount > net)
                    throw new InvalidOperationException($"مجموع الدفعات يتجاوز صافي الفاتورة ({net:N2}). الباقي للعميل يُحسب خارج الدفعات المسجلة.");

                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = invoice.InvoiceId,
                    PartyType = 'C',
                    PartyId = invoice.PartyId ?? null,
                    // VULN-05: الدفعة تُنسب لدرج الجلسة الفعلية المستلمة
                    // (نفس جلسة الفاتورة في البيع المباشر — دع request.SessionId يقرر)
                    SessionId = request.SessionId,
                    Amount = payment.Amount,
                    PaymentDate = payment.PaymentDate,
                    PaymentMethod = payment.Method,
                    Notes = null,
                    CreatedAt = DateTime.UtcNow
                });
                paid += payment.Amount;
            }

            if (paid >= net)
            {
                invoice.PaymentStatus = enPaymentStatus.paid;
            }
            else if (paid > 0)
            {
                invoice.PaymentStatus = enPaymentStatus.partial;
            }
            else
            {
                invoice.PaymentStatus = enPaymentStatus.unpaid;
            }

            // البيع بالدين (كلي أو جزئي) يتطلب عميلًا مسجلًا — وإلا فالمبلغ ضائع بلا متابعة
            if (paid < net && !invoice.PartyId.HasValue)
                throw new InvalidOperationException("البيع بالدين أو الدفع الجزئي يتطلب اختيار عميل.");

            // إذا كان البيع بالدين (أو جزئي) لعميل مسجل
            if (invoice.PartyId.HasValue && paid < net)
            {
                var customer = await _dbContext.Customers.FindAsync(invoice.PartyId.Value);
                if (customer == null) return;

                decimal debitAmount = net - paid;
                if (customer.CreditLimit > 0 && (customer.Balance + debitAmount > customer.CreditLimit))
                    throw new InvalidOperationException("تجاوز حد الائتمان للعميل.");

                customer.Balance += debitAmount;
            }
        }
    }
}