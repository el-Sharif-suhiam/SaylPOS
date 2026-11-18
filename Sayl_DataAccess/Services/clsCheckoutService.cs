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
        private readonly clsCreditApprovalService? _creditApprovalService;

        public clsCheckoutService(SaylDbContext dbContext, clsCreditApprovalService? creditApprovalService = null)
        {
            _dbContext = dbContext;
            _creditApprovalService = creditApprovalService;
        }

        // ═══ ترقية مخطط حجوزات الفواتير (idempotent) ═══
        // جدول StockReservations يمنح كل فاتورة معلقة ملكية حجز صريحة (صف لكل
        // (فاتورة، منتج)) — بدونه لا يمكن تحرير/استهلاك حجز فاتورة دون غيرها.
        // عند الإنشاء الأول: صفوف الملكية تُرحَّل من الفواتير المعلقة القائمة
        // والمجموع Inventory.QuantityReserved يُعاد بناؤه ليطابقها.
        public const string EnsureReservationSchemaSql = @"
IF OBJECT_ID(N'dbo.StockReservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockReservations
    (
        ReservationId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_StockReservations PRIMARY KEY,
        InvoiceId INT NOT NULL,
        ProductId INT NOT NULL,
        Quantity INT NOT NULL CONSTRAINT DF_StockReservations_Quantity DEFAULT (1),
        /* Upgrade_15: افتراضية UTC لا GETDATE — منطقة زمنية واحدة مع الكُتّاب (DateTime.UtcNow / GETUTCDATE) */
        CreatedAt DATETIME2 NULL CONSTRAINT DF_StockReservations_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_StockReservations_InvoiceId FOREIGN KEY (InvoiceId) REFERENCES dbo.Invoices(InvoiceId),
        CONSTRAINT FK_StockReservations_ProductId FOREIGN KEY (ProductId) REFERENCES dbo.Products(ProductId),
        CONSTRAINT CK_StockReservations_Quantity CHECK (Quantity > 0)
    );

    CREATE UNIQUE INDEX UQ_StockReservations_Invoice_Product
        ON dbo.StockReservations(InvoiceId, ProductId);
    CREATE INDEX IX_StockReservations_ProductId
        ON dbo.StockReservations(ProductId);

    /* ترحيل الحجوزات القائمة: كل (فاتورة معلقة، منتج مخزني) يصبح مالك حجزه */
    INSERT INTO dbo.StockReservations (InvoiceId, ProductId, Quantity, CreatedAt)
    /* GETUTCDATE لا GETDATE: EF يكتب StockReservations.CreatedAt بـDateTime.UtcNow — منطقة زمنية واحدة */
    SELECT ii.InvoiceId, ii.ProductId, SUM(ii.Quantity), GETUTCDATE()
    FROM dbo.InvoiceItems ii
    JOIN dbo.Invoices i ON i.InvoiceId = ii.InvoiceId
    JOIN dbo.Products p ON p.ProductId = ii.ProductId
    WHERE i.Status = N'suspended' AND p.Type = N'stock'
    GROUP BY ii.InvoiceId, ii.ProductId;

    /* إعادة بناء المجموع ليطابق صفوف الملكية بعد الترحيل */
    UPDATE inv
    SET inv.QuantityReserved = ISNULL(r.TotalReserved, 0),
        inv.UpdatedAt = GETUTCDATE() /* GETUTCDATE لا GETDATE: كل كتابة Inventory.UpdatedAt UTC */
    FROM dbo.Inventory inv
    LEFT JOIN (
        SELECT sr.ProductId, SUM(sr.Quantity) AS TotalReserved
        FROM dbo.StockReservations sr
        GROUP BY sr.ProductId
    ) r ON r.ProductId = inv.ProductId
    WHERE ISNULL(inv.QuantityReserved, 0) <> ISNULL(r.TotalReserved, 0);
END";

        /// <summary>يضمن جدول حجوزات الفواتير على أي قاعدة (قديمة أو جديدة) — يُنادى عند الإقلاع قبل أول تعليق فاتورة.</summary>
        public async Task EnsureReservationSchemaAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureReservationSchemaSql);


     
        // ─────────────────────────────────────────────────
        // الدالة العامة: إتمام عملية شراء أو تعليقها
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> ProcessCheckoutAsync(DtoCheckoutRequest request, bool suspend = false)
        {
            if (request == null || request.Items == null || !request.Items.Any())
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
                {
                    await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "CheckoutDenied",
                        $"بيع مرفوض: خصم ({request.DiscountPercent:0.#}% / {request.DiscountAmount:N2}) يتطلب صلاحية شاشة فواتير البيع.");
                    return Result<int>.Fail($"الخصم فوق {clsPermissions.MaxCashierDiscountPercent:0.#}% يتطلب صلاحية شاشة فواتير البيع. راجع المدير.");
                }
            }

            // VULN-44: صلاحية الضريبة — أي انحراف عن النسبة المعدّة في الإعدادات
            // يتطلب شاشة فواتير البيع (بت 16)، نمط بوابة الخصم أعلاه: الواجهة ليست
            // حد الثقة. النسبة الافتراضية تمر للجميع — تُحمَّل للصندوق تلقائيًا ولا
            // تمسّها يد الكاشير (الواجهة تقفل الحقل للبلا صلاحية).
            decimal defaultTaxPercent = await new clsSettingsService(_dbContext)
                .GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0m);
            if (Math.Round(request.TaxPercent, 2) != Math.Round(defaultTaxPercent, 2))
            {
                int taxActorRole = await GetUserRoleValueAsync(request.UserId);
                if (!AppPermissions.Has(taxActorRole, AppPermissions.SalesInvoices))
                {
                    await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "CheckoutDenied",
                        $"بيع مرفوض: ضريبة ({request.TaxPercent:0.##}%) تخالف النسبة المعدّة ({defaultTaxPercent:0.##}%) — يتطلب صلاحية شاشة فواتير البيع.");
                    return Result<int>.Fail("تعديل نسبة الضريبة يتطلب صلاحية شاشة فواتير البيع. راجع المدير.");
                }
            }

            // ══ صلاحية أسعار الوحدة المُرسلة من العميل (الواجهة ليست حد الثقة) ══
            // UnitPrice لقطة سعر السلة (VULN-14). أي انحراف لأسفل عن سعر البيع
            // الحالي للمنتج = خصم ضمني — كاشير بلا صلاحية فواتير البيع لا يمكنه
            // تجاوز سقف الخصم بتعديل السعر بدل حقل الخصم. الانحراف لأعلى مقبول
            // (رفع سعر مشروع للصلاحيات العليا، والدفع محكوم بصافي الفاتورة).
            List<Product> priceCheckProducts = await _dbContext.Products
                .Where(p => request.Items.Select(i => i.ProductId).Contains(p.ProductId))
                .ToListAsync();

            foreach (var item in request.Items)
            {
                if (item.UnitPrice.HasValue)
                {
                    if (item.UnitPrice.Value < 0)
                        return Result<int>.Fail("سعر الوحدة لا يمكن أن يكون سالبًا.");

                    var prod = priceCheckProducts.FirstOrDefault(p => p.ProductId == item.ProductId);
                    if (prod != null && item.UnitPrice.Value < prod.BasePrice)
                    {
                        decimal implicitDiscountPercent =
                            (prod.BasePrice - item.UnitPrice.Value) / prod.BasePrice * 100m;
                        if (implicitDiscountPercent > clsPermissions.MaxCashierDiscountPercent)
                        {
                            int actorRole = await GetUserRoleValueAsync(request.UserId);
                            if (!AppPermissions.Has(actorRole, AppPermissions.SalesInvoices))
                            {
                                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "CheckoutDenied",
                                    $"بيع مرفوض: سعر وحدة المنتج «{prod.Name}» يمثل خصمًا ضمنيًا {implicitDiscountPercent:0.#}% يتطلب صلاحية شاشة فواتير البيع.");
                                return Result<int>.Fail(
                                    $"سعر الوحدة المُرسل للمنتج «{prod.Name}» يمثل خصمًا ضمنيًا بنسبة {implicitDiscountPercent:0.#}% " +
                                    $"يتجاوز الحد المسموح ({clsPermissions.MaxCashierDiscountPercent:0.#}%) — يتطلب صلاحية شاشة فواتير البيع.");
                            }
                        }
                    }
                }
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
                // B3 + VULN-13: جلسة الدفع تُصادَق بالمساعد الموحد داخل المعاملة
                // (مطابقة AddMovementAsync) — وجود + انفتاح + ملكية الفاعل
                var (paymentSessionId, sessionError) = await ResolveOpenSessionIdAsync(request.SessionId, request.UserId);
                if (sessionError != null)
                    return Result<int>.Fail(sessionError);

                // إنشاء رأس الفاتورة
                Invoice invoice = await CreateInvoiceHeaderAsync(request, suspend);
                await _dbContext.SaveChangesAsync(); // للحصول على InvoiceId

                // إضافة بنود الفاتورة — الصفوف المرتجعة محاذاة مع request.Items
                // (يُملأ لها ItemCost/BatchId عند خصم الدفعات الفعلية)
                List<InvoiceItem> itemRows = await AddInvoiceItemsAsync(invoice.InvoiceId, request.Items);

                if (suspend)
                {
                    // حجز المخزون فقط — بملكية فاتورة (StockReservations)
                    await ReserveStockAsync(request.Items, invoice.InvoiceId);
                }
                else
                {
                    // خصم المخزون (معالجة الدُفعات) — B1: بيع مباشر لا يحرر أي حجز
                    await DeductStockAndCreateMovementsAsync(request.Items, invoice.InvoiceId, releaseReservation: false, itemRows);

                    // معالجة الدفع
                    await ProcessPaymentAsync(invoice, request, paymentSessionId);
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
            catch (CreditApprovalRequiredException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                // اعتماد حملته المحاولة وفشلت: يُحرق (granted -> burned) فلا يستفاد
                // منه لمحاولة أخرى — رفض تحقق الربط أعلاه يقتضي اعتمادًا جديدًا.
                await BurnCarriedApprovalAsync(request.CreditApprovalId);
                // تدقيق الرفض + نتيجة منظمة قابلة للتمييز آليًا (موافقة المدير مطلوبة)
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "CheckoutRejected",
                    $"{(suspend ? "تعليق" : "بيع")} فاشل: {ex.Message} " +
                    $"(العميل #{ex.Info.CustomerId}: الحالي {ex.Info.CurrentOutstanding:N2} + جديد {ex.Info.NewCreditAmount:N2} = {ex.Info.ProjectedOutstanding:N2} > الحد {ex.Info.CreditLimit:N2})");
                return CreditApprovalRequiredResult<int>.Fail(ex.Info, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                // إزالة الكيانات المتتبعة حتى لا تُحقن في العملية القادمة على نفس السياق
                _dbContext.ChangeTracker.Clear();
                // فشل البيع بعد استهلاك الاعتماد داخل معاملة تراجعت: الاستهلاك نفسه
                // تراجع (الاعتماد عاد granted) — الحرق هنا ينهي صلاحيته للأبد ويبقي
                // سجل «مُنح ولم يُستهلك» (granted -> burned) للعملية الفاشلة.
                await BurnCarriedApprovalAsync(request.CreditApprovalId);
                // تدقيق الرفض: رفض تحقق أعمال أثناء المعاملة (دفع زائد، مخزون غير
                // كافٍ، خصم سالب، حد ائتمان…) — كتابة مستقلة بعد التراجع فتنجو منه.
                // InvalidOperationException هنا = رفض أعمال مقصود؛ الأعطال التقنية
                // (catch التالي) تُستثنى عمدًا — ليست محاولات مرفوضة.
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "CheckoutRejected",
                    $"{(suspend ? "تعليق" : "بيع")} فاشل: {ex.Message}");
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
                await BurnCarriedApprovalAsync(request.CreditApprovalId);
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

        /// <summary>هل التاريخ يقع قبل بداية السنة المالية الحالية (سنة مغلقة)؟
        /// حماية السجلات التاريخية: نفس سياسة clsFiscalYearService/clsExpenseService.</summary>
        private async Task<bool> IsBeforeCurrentFiscalYearAsync(DateOnly date)
        {
            var setting = await _dbContext.Settings.FindAsync(clsFiscalYearService.KeyFiscalYearStart);
            if (setting != null && DateOnly.TryParse(setting.SettingValue, out var fiscalYearStart))
                return date < fiscalYearStart;

            var today = ProgramClock.Today; /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */
            return date < new DateOnly(today.Year, 1, 1);
        }

        // B3 + VULN-13: الجلسة المزوَّدة تُصادَق بالمساعد الموحد ValidateDrawerSessionAsync
        // (وجود + انفتاح + ملكية للفاعل — أو مديرًا، السياسة القائمة). بلا جلسة
        // مزوَّدة: (null, null) كما كان — لا قيد جلسي ولا تغيير سلوك. مع جلسة
        // مزوَّدة: SessionId = null + Error = رفض (مغلقة/غير موجودة/ليست ملكه).
        private async Task<(int? SessionId, string? Error)> ResolveOpenSessionIdAsync(int? sessionId, int actorUserId)
        {
            if (!sessionId.HasValue)
                return (null, null);

            var check = await clsPermissions.ValidateDrawerSessionAsync(_dbContext, sessionId, actorUserId);
            return check.IsSuccess
                ? (sessionId, null)
                : (null, check.Message);
        }

        // ─────────────────────────────────────────────────
        // إكمال فاتورة معلقة
        // VULN-08: المستكمِل يُفحص كالإلغاء تمامًا — صاحب الفاتورة يكملها،
        // وغيره يحتاج صلاحية محاسب فأعلى. (كان الإكمال بلا أي فحص ملكية
        // بينما الإلغاء مشدد — سجل: الإكمال يحرك مالًا ومخزونًا نهائيًا.)
        // ─────────────────────────────────────────────────
        public async Task<Result> CompleteSuspendedInvoiceAsync(int invoiceId, List<DtoPaymentInfo> payments, int actorUserId, int? actorSessionId = null, int? creditApprovalId = null)
        {
            if (payments == null || !payments.Any(p => p != null && p.Amount > 0))
                return Result.Fail("يجب إدخال مبلغ دفعة واحد على الأقل.");

            // B3 + VULN-13: جلسة الدرج الفعلية للفاعل تُصادَق قبل بدء أي حركة
            var (paymentSessionId, sessionError) = await ResolveOpenSessionIdAsync(actorSessionId, actorUserId);
            if (sessionError != null)
                return Result.Fail(sessionError);

            var invoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId && i.Status == enInvoicesStatus.suspended);

            if (invoice == null)
                return Result.Fail("الفاتورة المعلقة غير موجودة أو تم إكمالها بالفعل.");

            // ══ حماية السنة المالية المغلقة ══
            // فاتورة معلقة تعود لسنة مُغلقة لا تُستكمَل هنا: إكمالها يُقيّد بيعًا
            // وCOGS ونقدًا في سنة مؤرشفة فيقلب نتائجها المالية. طريقها المشروع
            // الوحيد هو عملية تسوية محاسبية مصرّح بها (قيد إغلاق معتمد) — لا إكمال
            // نقطة بيع صامت. (الفواتير المعلقة داخل السنة الجارية تُحسم قبل الإغلاق
            // — فحوصات الجاهزية في clsFiscalYearService تمنع الإغلاق دون حسمها.)
            if (await IsBeforeCurrentFiscalYearAsync(DateOnly.FromDateTime(invoice.CreatedAt ?? DateTime.UtcNow)))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "SuspendedInvoiceRejected",
                    $"إكمال فاتورة معلقة #{invoiceId} مرفوض: تعود لسنة مالية مغلقة (المحاسب وحده عبر قيد تسوية).");
                return Result.Fail("هذه الفاتورة المعلقة تعود لسنة مالية مغلقة — لا يمكن إكمالها إلا عبر قيد تسوية محاسبية معتمد من المحاسب.");
            }

            // ملكية الإكمال: نفس قاعدة الإلغاء (CancelSuspendedInvoiceAsync)
            if (invoice.UserId != actorUserId)
            {
                int actorRole = await GetUserRoleValueAsync(actorUserId);
                if (!AppPermissions.Has(actorRole, AppPermissions.SalesInvoices))
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "SuspendedInvoiceDenied",
                        $"إكمال فاتورة معلقة #{invoiceId} (صاحبها #{invoice.UserId}) مرفوض: يتطلب صلاحية شاشة فواتير البيع.");
                    return Result.Fail("إكمال فاتورة معلقة لكاشير آخر يتطلب صلاحية شاشة فواتير البيع.");
                }
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

                // B3 + VULN-13: إعادة تأكيد الجلسة داخل المعاملة — قد تغلق أثناء البناء أعلاه
                (paymentSessionId, sessionError) = await ResolveOpenSessionIdAsync(actorSessionId, actorUserId);
                if (sessionError != null)
                    return Result.Fail(sessionError);

                // تحويل الحالة (المحلية — الحالة في القاعدة ثبتت بالانتقال الذري أعلاه)
                invoice.Status = enInvoicesStatus.completed;

                // خصم المخزون (معالجة الدُفعات) — B1: الإكمال وحده يحرر الحجز الخاص بهذه الفاتورة
                // الصفوف محاذاة مع البنود (بُنيت من نفس القائمة) ليُكتب كل صف
                // تكلفته من الدفعات التي يستهلكها فعليًا
                var invoiceItemRows = invoice.InvoiceItems.ToList();
                var items = invoice.InvoiceItems.Select(i => new DtoCheckoutItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    BatchId = i.BatchId
                }).ToList();

                await DeductStockAndCreateMovementsAsync(items, invoice.InvoiceId, releaseReservation: true, invoiceItemRows);

                // معالجة الدفع — VULN-05: دفعات الإكمال تُنسب لدرج جلسة المستكمِل
                // الفعلية (actorSessionId) لا لجلسة التعليق القديمة: النقد دخل درج
                // اليوم فتقرير Z لليوم هو من يراه. null = بلا جلسة (تُهمل بتقارير الجلسات).
                var request = new DtoCheckoutRequest
                {
                    CustomerId = invoice.PartyId,
                    UserId = actorUserId, // فاعل الإكمال — تدقيق الاستهلاك باسمه لا 0
                    Payments = payments,
                    SessionId = paymentSessionId,
                    DiscountPercent = 0, // يمكنك إضافة حقول للخصم في الفاتورة المعلقة
                    TaxPercent = 0,
                    // Task-3: اعتماد المدير للتجاوز — يُستهلك داخل معاملة الإكمال
                    CreditApprovalId = creditApprovalId
                };
                await ProcessPaymentAsync(invoice, request, paymentSessionId);

                // ملاحظة: تحرير الحجز يتم داخل DeductStockAndCreateMovementsAsync

                clsAudit.Log(_dbContext, invoice.UserId, "CompleteSuspendedInvoice",
                    $"إكمال فاتورة معلقة #{invoiceId} — صافي: {invoice.NetAmount:N2}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إكمال الفاتورة بنجاح.");
            }
            catch (CreditApprovalRequiredException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                // اعتماد حملته عملية الإكمال وفشلت: يُحرق — لا صلاحية لمحاولة أخرى
                await BurnCarriedApprovalAsync(creditApprovalId);
                // تدقيق الرفض + نتيجة منظمة قابلة للتمييز آليًا (موافقة المدير مطلوبة)
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "SuspendedInvoiceRejected",
                    $"إكمال فاتورة معلقة #{invoiceId} فاشل: {ex.Message} " +
                    $"(العميل #{ex.Info.CustomerId}: الحالي {ex.Info.CurrentOutstanding:N2} + جديد {ex.Info.NewCreditAmount:N2} = {ex.Info.ProjectedOutstanding:N2} > الحد {ex.Info.CreditLimit:N2})");
                return CreditApprovalRequiredResult.Fail(ex.Info, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                // فشل الإكمال بعد استهلاك الاعتماد داخل معاملة تراجعت: يُحرق
                await BurnCarriedApprovalAsync(creditApprovalId);
                // تدقيق الرفض: فشل تحقق أعمال أثناء إكمال الفاتورة (دفع زائد/مخزون…)
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "SuspendedInvoiceRejected",
                    $"إكمال فاتورة معلقة #{invoiceId} فاشل: {ex.Message}");
                return Result.Fail(ex.Message);
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                await BurnCarriedApprovalAsync(creditApprovalId);
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
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "SuspendedInvoiceDenied",
                        $"إلغاء فاتورة معلقة #{invoiceId} (صاحبها #{invoice.UserId}) مرفوض: يتطلب صلاحية شاشة فواتير البيع.");
                    return Result.Fail("إلغاء فاتورة معلقة لأجل كاشير آخر يتطلب صلاحية شاشة فواتير البيع.");
                }
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

                // تحرير الحجز — من صفوف StockReservations الخاصة بالفاتورة فقط
                // (لا من المجموع) حتى لا يمس الإلغاء حجز فواتير معلقة أخرى
                await ReleaseInvoiceReservationsAsync(invoiceId);

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
            // وهمية تفوق المبيعات (خصوصًا مع حد ائتمان ضخم).
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

            // B16: التقريب مرة واحدة هنا — كل الحسابات اللاحقة (سقف الدفع، حالة الدفع،
            // رصيد الدين) تعمل على القيمة المخزنة نفسها لا على قيمة بدقة أعلى
            net = Math.Round(net, 2, MidpointRounding.AwayFromZero);

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

         private async Task<List<InvoiceItem>> AddInvoiceItemsAsync(int invoiceId, List<DtoCheckoutItem> items)
         {
             var rows = new List<InvoiceItem>();
             foreach (var item in items)
             {
                 var product = await _dbContext.Products.FindAsync(item.ProductId);
                 var invoiceItem = new InvoiceItem
                 {
                     InvoiceId = invoiceId,
                     ProductId = item.ProductId,
                     Quantity = item.Quantity,
                     BatchId = item.BatchId, // قد يكون null — الدفعة الفعلية تُحدَّد عند الخصم
                     // VULN-14: snapshot سعر السلة — لا السعر الحالي بعد لحظة البناء
                     ItemPrice = item.UnitPrice ?? product?.BasePrice ?? 0
                     // ItemCost لا يُقدَّر هنا: يُكتب من الدفعات المستهلكة فعليًا
                     // في DeductStockAndCreateMovementsAsync (سياسة التكلفة بالدفعات)
                 };

            _dbContext.InvoiceItems.Add(invoiceItem);
            rows.Add(invoiceItem);
             }

             return rows;
         }

        // الحجز بملكية فاتورة: صف StockReservations لكل (فاتورة، منتج) + المجموع
        // في Inventory.QuantityReserved يُحدَّث معه في نفس المعاملة (Serializable).
        // البنود تُجمَّع حسب المنتج أولًا — سطران لنفس المنتج في السلة يصيران حجزًا واحدًا.
        private async Task ReserveStockAsync(List<DtoCheckoutItem> items, int invoiceId)
        {
            foreach (var group in items.GroupBy(i => i.ProductId))
            {
                int quantity = group.Sum(i => i.Quantity);
                var product = await _dbContext.Products.FindAsync(group.Key);
                if (product == null || product.Type != "stock") continue;

                // منع الحجز المزدوج: نفس الفاتورة تحجز نفس المنتج مرة واحدة فقط
                // (الفهرس الفريد UQ_StockReservations_Invoice_Product حاجز أخير تحت التوازي).
                bool alreadyReserved = await _dbContext.StockReservations
                    .AnyAsync(r => r.InvoiceId == invoiceId && r.ProductId == group.Key);
                if (alreadyReserved)
                    throw new InvalidOperationException($"يوجد حجز مسبق للفاتورة على المنتج {product.Name}");

                var inv = await _dbContext.Inventories.FindAsync(group.Key);
                if (inv == null)
                    throw new InvalidOperationException($"لا يوجد مخزون للمنتج {group.First().ProductName}");

                int available = (inv.QuantityAvailable ?? 0 ) - (inv.QuantityReserved ?? 0);
                if (available < quantity)
                    throw new InvalidOperationException($"المخزون المتاح غير كافٍ للمنتج {product.Name}");

                _dbContext.StockReservations.Add(new StockReservation
                {
                    InvoiceId = invoiceId,
                    ProductId = group.Key,
                    Quantity = quantity,
                    CreatedAt = DateTime.UtcNow
                });
                inv.QuantityReserved += quantity;
            }
        }

        // تحرير حجز فاتورة معلقة: يُقرأ من صفوف StockReservations الخاصة بالفاتورة
        // لا من المجموع — فلا يمكن أن يمس حجز فاتورة معلقة أخرى مهما كانت الكميات.
        private async Task ReleaseInvoiceReservationsAsync(int invoiceId)
        {
            var reservations = await _dbContext.StockReservations
                .Where(r => r.InvoiceId == invoiceId)
                .ToListAsync();

            foreach (var reservation in reservations)
            {
                var inv = await _dbContext.Inventories.FindAsync(reservation.ProductId);
                if (inv != null)
                {
                    // VULN-20: القصّص الأماني يبقى (لا سالب أبدًا) لكن الانحراف يُسجَّل تشخيصيًا
                    // — الصمت القديم كان يخفي تباعد حجز/مخزون بلا أثر
                    await ClampReservedWithDiagnosticAsync(inv, reservation.Quantity, reservation.ProductId, invoiceId);
                }
            }

            _dbContext.StockReservations.RemoveRange(reservations);
        }

        /// <summary>VULN-20: طرح المحجوز مع حماية عدم السالب القائمة + تشخيص عند
        /// الانحراف الفعلي فقط (قبل الطرح &lt; المطلق) — لا سجل عند الطرح السليم.
        /// التدقيق عبر LogRejectedAsync (اتصال مستقل، أفضل جهد): التباعد حالة بيانات
        /// قائمة قبل المعاملة، فالسجل ينجو من تراجعها ولا يكسر مسار التحرير أبدًا.</summary>
        private async Task ClampReservedWithDiagnosticAsync(Inventory inv, int released, int productId, int invoiceId)
        {
            int before = inv.QuantityReserved ?? 0;
            int after = before - released;
            if (after < 0)
            {
                await clsAudit.LogRejectedAsync(_dbContext, null, "ReservationUnderflow",
                    $"تحت الطرح في المخزون المحجوز: المنتج #{productId} — الفاتورة #{invoiceId} — قبل الطرح {before} — الكمية المطلق تحريرها {released} — قُصَّص إلى 0 (انحراف حجز/مخزون يحتاج تدقيقًا يدويًا).");
                inv.QuantityReserved = 0;
            }
            else
            {
                inv.QuantityReserved = after;
            }
        }

        private async Task DeductStockAndCreateMovementsAsync(
            List<DtoCheckoutItem> items, int invoiceId, bool releaseReservation, List<InvoiceItem>? itemRows = null)
        {
            // حجوزات هذه الفاتورة وحدها — الإكمال يستهلك حجزها الذاتي ولا يمس
            // حجز أي فاتورة معلقة أخرى (صف واحد لكل (فاتورة، منتج) بضمان الفهرس الفريد).
            Dictionary<int, StockReservation> ownReservations = releaseReservation
                ? await _dbContext.StockReservations
                    .Where(r => r.InvoiceId == invoiceId)
                    .ToDictionaryAsync(r => r.ProductId)
                : new Dictionary<int, StockReservation>();

            for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var item = items[itemIndex];
                var product = await _dbContext.Products.FindAsync(item.ProductId);
                if (product == null || product.Type != "stock") continue;

                var inv = await _dbContext.Inventories.FindAsync(item.ProductId);
                if (inv == null)
                    throw new InvalidOperationException($"لا يوجد مخزون للمنتج {item.ProductName}");

                ownReservations.TryGetValue(item.ProductId, out var ownRow);
                int ownRemaining = releaseReservation ? ownRow?.Quantity ?? 0 : 0;

                // B1: التوفر يُحسب من الحجز المتبقي فعلاً فقط — وحين تكمل الفاتورة
                // نفسها يُستثنى حجزها الذاتي من المحجوز لأنه يُستهلك بهذه العملية.
                // VULN-10: صف Inventory بـ NULL كان يتخطى فحص التوفر كليًا (is not null)
                // فباعة بلا حاجز بينما تبقى قيمة NULL تنجرف. الآن: NULL يعامل كصفر —
                // البيع يرفض برسالة واضحة، والقيمة الفاسدة تظهر بدل أن تختبئ.
                int availableNow = (inv.QuantityAvailable ?? 0) - ((inv.QuantityReserved ?? 0) - ownRemaining);
                if (availableNow < item.Quantity)
                    throw new InvalidOperationException($"المخزون غير كافٍ للمنتج {product.Name}");

                // B1: البيع المباشر (releaseReservation=false) لا يحرر أي احتياطي —
                // القيمة المحجوزة لفواتير معلقة أخرى تبقى كما هي. الإكمال
                // (releaseReservation=true) يستهلك حجز هذه الفاتورة المعلقة فقط.
                // تطبيق FEFO أو استخدام الدُفعة المحددة
              
                    // FEFO — مع استبعاد الدفعات المنتهية الصلاحية والمحجوبة (حجر)
                    // سياسة التكلفة: FIFO بالتكلفة الفعلية للدفعة المستهلكة (Batches.UnitCost)
                    // — FEFO يرتب الاستهلاك زمنيًا، أما التكلفة فتُقرأ من كل دفعة
                    // اقتُنيت بها فعلًا، لا من Product.CostPrice (سعر آخر شراء).
                    DateOnly today = ProgramClock.Today; /* VULN-45: يوم البرنامج لا التاريخ UTC الخام */
                    var batches = await _dbContext.Batches
                        .Where(b => b.ProductId == item.ProductId && b.BatchQuantity > 0
                                 && !b.IsQuarantined
                                 && (b.ExpiryDate == null || b.ExpiryDate >= today))
                        .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                        .ThenBy(b => b.CreatedAt)
                        .ToListAsync();

                    int remaining = item.Quantity;
                    var allocations = new List<(Batch Batch, int Take)>();
                    foreach (var batch in batches)
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, batch.BatchQuantity);
                        batch.BatchQuantity -= take;
                        remaining -= take;

                        allocations.Add((batch, take));

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

                    // ══ COGS من الدفعات المستهلكة فعليًا ══
                    // سطر الفاتورة الذي استهلك عدة دفعات يُقسَّم: صف InvoiceItem
                    // لكل دفعة بكمايتها وتكلفتها الفعلية (UnitCost، وإلى الدفعة
                    // بلا تكلفة مخزنة يُرجع إليها سعر المنتج الأخير) — BatchId
                    // الواحد لا يوهم أن عدة دفعات دفعة واحدة. السعر يبقى سعر
                    // البيع على كل صف — التكلفة وحدها تتبع الدفعة.
                    InvoiceItem? row = itemRows != null && itemIndex < itemRows.Count ? itemRows[itemIndex] : null;
                    decimal rowPrice = row?.ItemPrice ?? 0m;
                    foreach (var (batch, take) in allocations)
                    {
                        decimal unitCost = batch.UnitCost ?? product.CostPrice;

                        if (row == null)
                        {
                            row = new InvoiceItem
                            {
                                InvoiceId = invoiceId,
                                ProductId = item.ProductId,
                                Quantity = take,
                                BatchId = batch.BatchId,
                                ItemPrice = rowPrice,
                                ItemCost = unitCost
                            };
                            _dbContext.InvoiceItems.Add(row);
                        }
                        else
                        {
                            row.BatchId = batch.BatchId;
                            row.Quantity = take;
                            row.ItemCost = unitCost;
                        }

                        row = null; // الصفوف التالية دفعات إضافية — صف جديد لكل دفعة
                    }


                // تحديث المخزون
                inv.QuantityAvailable -= item.Quantity;
                if (releaseReservation && ownRow != null)
                {
                    // يُحرَّر من حجز هذه الفاتورة الذاتي فقط — عبر صف StockReservations
                    // الخاص بها (ownRow ينقص عبر بنود نفس المنتج سطرًا سطرًا)
                    int ownRelease = Math.Min(item.Quantity, ownRow.Quantity);
                    ownRow.Quantity -= ownRelease;
                    if (ownRow.Quantity <= 0)
                        _dbContext.StockReservations.Remove(ownRow);

                    // VULN-20: نفس التشخيص — موقع القصّص الثاني (مسار الإكمال)
                    await ClampReservedWithDiagnosticAsync(inv, ownRelease, item.ProductId, invoiceId);
                }
                inv.UpdatedAt = DateTime.UtcNow;
            }
        }

        private async Task ProcessPaymentAsync(Invoice invoice, DtoCheckoutRequest request, int? sessionId)
        {
            decimal net = invoice.NetAmount;

            // B3: جلسة الدفع يجب أن تكون مفتوحة — المعاملة المتسلسلة تمنع سباق الإغلاق
            if (sessionId.HasValue)
            {
                var session = await _dbContext.PosSessions.FindAsync(sessionId.Value);
                if (session == null || session.Status != enPosSessionStatusType.open)
                    throw new InvalidOperationException("جلسة الدرج مغلقة — افتح جلسة جديدة قبل تسجيل الدفع.");
            }

            // دفعات متعددة (نقدي + شبكة...) أو دفعة واحدة
            var payments = request.Payments != null && request.Payments.Any()
                ? request.Payments
                : (request.Payment != null ? new List<DtoPaymentInfo> { request.Payment } : new List<DtoPaymentInfo>());

            decimal paid = 0;
            foreach (var payment in payments)
            {
                if (payment == null || payment.Amount <= 0)
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
                    // (نفس جلسة الفاتورة في البيع المباشر — دع sessionId يقرر)
                    SessionId = sessionId,
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

            // ══ سياسة حد الائتمان — نقطة الإنفاذ المركزية الوحيدة ══
            // كل مسارات إحداث دَين عميل (بيع مباشر وإكمال فاتورة معلقة) تمر من هنا
            // حصريًا، فالسياسة تُطبَّق مرة واحدة ولا يمكن لطريق بيع بديل تخطّيها.
            // البيع النقدي كليًا (paid >= net) لا يمس هذا المسار أصلًا.
            if (invoice.PartyId.HasValue && paid < net)
            {
                decimal debitAmount = net - paid;

                // قراءة حيّة للرصيد والحد داخل المعاملة القابلة للتسلسل مع UPDLOCK
                // على صف العميل — لا ثقة أبدًا برصيد حسبته الواجهة أو قرأناه قبل
                // المعاملة. UPDLOCK يحوّل سباق البيعين المتوازيين لانتظار: العملية
                // الثانية تقرأ الرصيد بعد التزام الأولى (نفس نمط تحصيل العميل
                // في clsPaymentService) — فلا يتجاوز الحد بصمت على رصيد قديم،
                // ولا deadlock (لا تحوّل S→X على الصف نفسه).
                CustomerCreditSnapshot? snapRow = await _dbContext.Database.SqlQuery<CustomerCreditSnapshot>(@$"
                        SELECT ISNULL(Balance, 0) AS [Balance], ISNULL(CreditLimit, 0) AS [CreditLimit]
                        FROM Customers WITH (UPDLOCK)
                        WHERE PartyId = {invoice.PartyId.Value}")
                        .FirstOrDefaultAsync();

                decimal balance = snapRow?.Balance ?? 0m;
                decimal creditLimit = snapRow?.CreditLimit ?? 0m;

                // القيم الفاسدة تُرفض رفض تحقق صريحًا لا تتجاوز بصمت.
                // دفتر Customers.Balance هو المصدر المعتمد لإجمالي الدَّين: يُحدَّث
                // معاملاتيًا هنا (+) وفي السداد والمرتجعات (− مقصوص عند 0) — لا
                // معادلة رصيد ثانية (تسوية clsPaymentAllocation خاصة مستحق فاتورة
                // بعينه وتبقى كما هي).
                if (creditLimit < 0)
                    throw new InvalidOperationException("حد ائتمان العميل قيمة غير صالحة (سالب) — صحّح بيانات العميل أولًا.");

                // ══ سياسة الحد الصفري: منع مطلق لا تجاوز له ══
                // CreditLimit = 0 قيد أعمال صارم لا وضع «تجاوز عادي»: ممنوع إطلاقًا
                // نتيجة «يتطلب موافقة المدير» هنا (لا نافذة اعتماد ولا سجل CreditApprovals
                // ولا تجاوز مدير) — الطريق الوحيد لتمكين الائتمان أن يعدّل مدير
                // مصرَّح به حد العميل الدائم إلى قيمة موجبة قبل البيع.
                // رفض أعمال صريح بنتيجة فشل عادية (لا CreditApprovalRequiredException)
                // فلا تفتح الواجهة نافذة الاعتماد أصلًا، ولا أثر جزئي (المعاملة تراجعت).
                if (creditLimit == 0)
                    throw new InvalidOperationException(
                        "الشراء بالدين ممنوع لهذا العميل: حد الائتمان صفر — لا يمكن تجاوز هذا المنع بموافقة المدير. " +
                        "لتتمكين الائتمان، عدّل حد الائتمان الدائم للعميل إلى قيمة موجبة أولًا.");

                // دلالة الحد: الموجبة فقط تخضع لمنطق التجاوز/الاعتماد — القيمة الموجبة
                // تسمح ضمن الحد فقط، والبيع يُسمح به دون اعتماد المدير فقط حين لا يتجاوز المشروع الحد.
                if (balance + debitAmount > creditLimit)
                {
                    var info = new CreditApprovalRequiredInfo
                    {
                        CustomerId = invoice.PartyId.Value,
                        CurrentOutstanding = balance,
                        CreditLimit = creditLimit,
                        NewCreditAmount = debitAmount,
                        ProjectedOutstanding = balance + debitAmount,
                        ExcessAmount = balance + debitAmount - creditLimit
                    };

                    // ══ اعتماد المدير: استهلاك واحد بالضبط داخل معاملة البيع ══
                    // بلا معرف اعتماد: رفض منظوم «يتطلب موافقة المدير» — لا مسار
                    // بديل ولا ثقة بما زعَمته الواجهة. بمعرف: إعادة فحص الربط كاملًا
                    // هنا (نفس العميل + نفس المبلغ + نفس لقطة الرصيد/الحد + granted)
                    // والانتقال الذري granted -> consumed — CAS يضمن فوز مستهلك
                    // واحد بالضبط تحت التوازي (الصف منحَصَر UPDLOCK على عميل واحد
                    // يرتّب المتسابقين قبل لمس صف الاعتماد). أي انحراف (عميل آخر،
                    // مبلغ آخر، رصيد تغيّر بعد الاعتماد، استهلاك سابق، حرق) = صفر
                    // صفوف = رفض «الاعتماد لم يعد صالحًا — اعتماد جديد مطلوب».
                    if (!request.CreditApprovalId.HasValue)
                        throw new CreditApprovalRequiredException(info,
                            "تجاوز حد الائتمان للعميل — يتطلب موافقة المدير.");

                    int consumed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE CreditApprovals
SET Status = N'consumed', ConsumedAtUtc = SYSUTCDATETIME(), ConsumedByInvoiceId = {invoice.InvoiceId}
WHERE ApprovalId = {request.CreditApprovalId.Value}
  AND Status = N'granted'
  AND CustomerId = {invoice.PartyId.Value}
  AND NewCreditAmount = {debitAmount}
  AND CurrentOutstanding = {balance}
  AND CreditLimit = {creditLimit}");

                    if (consumed == 0)
                        throw new CreditApprovalRequiredException(info,
                            "اعتماد تجاوز حد الائتمان لم يعد صالحًا (مستهلك أو غير مطابق للعملية الحالية) — يلزم موافقة مدير جديدة.");

                    clsAudit.Log(_dbContext, request.UserId, "CreditApprovalConsumed",
                        $"استهلاك اعتماد ائتمان #{request.CreditApprovalId.Value} بفاتورة #{invoice.InvoiceId} " +
                        $"— العميل #{invoice.PartyId.Value} — آجل {debitAmount:N2} (مشروع {balance + debitAmount:N2} فوق الحد {creditLimit:N2}).");
                }

                // القيد الجديد يُكتب بتحديث ذري مباشر (نفس نمط clsPaymentService):
                // يضمن atomicity مع القراءة الحيّة أعلاه ولا يكتب قيمة كيان متتبع
                // قديم فوق خصم/تعديل التزم بعد تحميل الكيان.
                await _dbContext.Customers
                    .Where(c => c.PartyId == invoice.PartyId.Value)
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(c => c.Balance, c => (c.Balance ?? 0m) + debitAmount));
            }
        }

        /// <summary>لقطة رصيد/حد ائتمان العميل للقراءة الحيّة بلا تتبع (SqlQuery).</summary>
        private sealed class CustomerCreditSnapshot
        {
            public decimal Balance { get; set; }
            public decimal CreditLimit { get; set; }
        }

        // ═══ Task-3: إحراق اعتماد حملته عملية فاشلة (بعد تراجع معاملتها) ═══
        // انتقال CAS granted -> burned خارج المعاملة التراجعت: سجل «مُنح ولم
        // يُستهلك» يبقى للمراجعة، والصلاحية تنتهي — الاعتماد لا يعود صالحًا
        // لأي عملية مستقبلية. الاعتماد غير الموجود/غير granted يُترك كما هو
        // (قد يكون استُهلك وحُرِق في مسار سابق). أفضل جهد: فشل الحرق لا يخفي
        // فشل البيع الأصلي — والاعتماد المتبقي granted يظل مُحكمًا بربطه الصارم
        // (نفس العميل/المبلغ/اللقطة) بحيث لا يستفاد منه لعملية مغايرة.
        private async Task BurnCarriedApprovalAsync(int? approvalId)
        {
            if (!approvalId.HasValue)
                return;

            if (_creditApprovalService != null)
            {
                await _creditApprovalService.BurnApprovalAsync(approvalId.Value,
                    "فشلت عملية البيع المرتبطة بعد منح الاعتماد.");
                return;
            }

            // بلا خدمة محقونة (مستدعون أقدم يبنون الخدمة يدويًا) — نفس الانتقال
            try
            {
                int burned = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE CreditApprovals SET Status = N'burned'
WHERE ApprovalId = {approvalId.Value} AND Status = N'granted'");
                if (burned > 0)
                    await clsAudit.LogRejectedAsync(_dbContext, null, "CreditApprovalBurned",
                        $"اعتماد ائتمان #{approvalId.Value} أُحرق بلا استهلاك: فشلت عملية البيع بعد المنح.");
            }
            catch
            {
                // تحصين لا يكسر معالجة الفشل الأصلية
            }
        }
    }
}