using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsPaymentService
    {
        private readonly SaylDbContext _dbContext;

        public clsPaymentService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // VULN-05 + التحقق الجلسي: يُنفَّذ عبر clsPermissions.ValidateDrawerSessionAsync
        // (مساعد موحّد تشاركه الخدمات — مرتجعات ومدفوعات بنفس القواعد حرفيًا).
        // ─────────────────────────────────────────────────
        private Task<Result> ValidateSessionAsync(int? sessionId, int userId)
            => clsPermissions.ValidateDrawerSessionAsync(_dbContext, sessionId, userId);

        public async Task<Result<List<PaymentVM>>> GetPaymentsListAsync(DateOnly? fromDate = null, DateOnly? toDate = null, enPaymentType? type = null, int? partyId = null)
        {
            var query = _dbContext.Payments.Include(p => p.Party).AsQueryable();

            if (fromDate.HasValue)
                query = query.Where(p => p.PaymentDate >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(p => p.PaymentDate <= toDate.Value);

            if (type.HasValue)
                query = query.Where(p => p.Type == type.Value);

            if (partyId.HasValue)
                query = query.Where(p => p.PartyId == partyId.Value);

            var payments = await query
                .OrderByDescending(p => p.PaymentDate)
                .ToListAsync();

            var result = payments.Select(p => new PaymentVM
            {
                PaymentId = p.PaymentId,
                Type = p.Type.ToString(),
                InvoiceId = p.InvoiceId,
                PartyType = p.PartyType,
                PartyId = p.PartyId,
                PartyName = p.Party?.Name,
                Amount = p.Amount,
                PaymentDate = p.PaymentDate,
                PaymentMethod = p.PaymentMethod,
                Notes = p.Notes,
                CreatedAt = p.CreatedAt
            }).ToList();

            return Result<List<PaymentVM>>.Ok(result);
        }

        // ─────────────────────────────────────────────────
        // سداد دفعة على فاتورة محددة
        // ─────────────────────────────────────────────────
        public async Task<Result> PayInvoiceAsync(DtoPaymentRequest request)
        {
            if (request.Amount <= 0)
                return Result.Fail("مبلغ الدفع يجب أن يكون أكبر من صفر.");

            if (!request.InvoiceId.HasValue)
                return Result.Fail("معرف الفاتورة مطلوب.");

            var invoice = await _dbContext.Invoices.FindAsync(request.InvoiceId.Value);
            if (invoice == null)
                return Result.Fail("الفاتورة غير موجودة.");

            if (invoice.Status != enInvoicesStatus.completed)
                return Result.Fail("لا يمكن الدفع على فاتورة غير مكتملة.");

            if (invoice.PaymentStatus == enPaymentStatus.paid)
                return Result.Fail("الفاتورة مسددة بالكامل.");

            // ══ صلاحية السداد — في الخدمة لا الواجهة (الواجهة تخفي فقط) ══
            // سداد فاتورة شراء = شاشة فواتير الشراء (بت 32)؛ سداد فاتورة بيع
            // = شاشة فواتير البيع (بت 16). الفحص مستقل عن الجلسة: تمرير
            // SessionId = null لا يتجاوزه إطلاقًا.
            int invoicePayRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, request.UserId);
            bool isPurchaseSide = invoice.Type == enInvoicesType.purchase
                               || invoice.Type == enInvoicesType.supplier_return;
            int requiredPayBit = isPurchaseSide ? AppPermissions.PurchaseInvoices : AppPermissions.SalesInvoices;
            if (invoicePayRole < 0 || !AppPermissions.Has(invoicePayRole, requiredPayBit))
            {
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentDenied",
                    $"سداد فاتورة #{invoice.InvoiceId} مرفوض: يتطلب صلاحية {(isPurchaseSide ? "شاشة فواتير الشراء" : "شاشة فواتير البيع")}.");
                return Result.Fail(isPurchaseSide
                    ? "سداد فاتورة الشراء يتطلب صلاحية شاشة فواتير الشراء."
                    : "سداد فاتورة البيع يتطلب صلاحية شاشة فواتير البيع.");
            }

            // Serializable + إعادة حساب المتبقي داخل المعاملة:
            // قفل نطاقي على مدفوعات الفاتورة يمنع إدراج دفعتين متوازيتين تتجاوزان المتبقي معاً
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // VULN-05: التحقق الجلسي داخل المعاملة — الجلسة موجودة ومفتوحة
                // ومملوكة للفاعل (أو مدير) قبل أن يُقيد لها أي نقد
                var sessionCheck = await ValidateSessionAsync(request.SessionId, request.UserId);
                if (!sessionCheck.IsSuccess)
                    return sessionCheck;

                // تسوية الدفعات العامة (InvoiceId = null): كان «المتبقي» محسوبًا على
                // الدفعات المرتبطة بالفاتورة فقط — فإذا خفّت دفعة عامة دين الفاتورة
                // عبر رصيد الطرف كانت غير مرئية هنا فيُسمح بسداد الدَّين نفسه مرتين.
                // الآن المتبقي يُحسب بتخصيص حتمي (الأقدم أولًا) يشمل الدفعات العامة والمرتجعات.
                var outstandingRows = await clsPaymentAllocation
                    .GetPartyOutstandingAsync(_dbContext, invoice.PartyType ?? 'C', invoice.PartyId ?? 0);

                var row = outstandingRows.FirstOrDefault(r => r.InvoiceId == invoice.InvoiceId);
                if (row == null)
                    return Result.Fail("لا يمكن الدفع على فاتورة غير مكتملة.");

                decimal remaining = row.Outstanding;
                if (remaining <= 0)
                    return Result.Fail("الفاتورة مسددة بالكامل (مع مراعاة الدفعات العامة والمرتجعات).");

                if (request.Amount > remaining)
                {
                    // تدقيق الرفض — كتابة مستقلة تنجو من تراجع معاملة السداد
                    await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentRejected",
                        $"سداد مرفوض: المبلغ {request.Amount:N2} أكبر من المتبقي {remaining:N2} على الفاتورة #{invoice.InvoiceId}.");
                    return Result.Fail($"المبلغ أكبر من المتبقي على الفاتورة بعد الدفعات العامة والمرتجعات ({remaining:N2}).");
                }

                _dbContext.Payments.Add(new Payment
                {
                    Type = invoice.Type == enInvoicesType.purchase || invoice.Type == enInvoicesType.supplier_return
                        ? enPaymentType.outcome
                        : enPaymentType.income,
                    InvoiceId = invoice.InvoiceId,
                    PartyType = invoice.PartyType ?? 'C',
                    PartyId = invoice.PartyId,
                    // VULN-05: النقد يُنسب لدرج الجلسة التي استلمه فعليًا —
                    // سداد دَين فاتورة قديمة اليوم يخص درج اليوم لا جلسة الفاتورة
                    SessionId = request.SessionId,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                });

                // VULN-07 + التسوية: حالة السداد تُقيّم على كل ما دُفع فعليًا
                // (مرتبط + نصيب الدفعات العامة + هذه الدفعة) مقابل الصافي بعد المرتجعات
                decimal totalEffectivePaid = row.InvoiceLinkedPaid + row.AllocatedGeneral + request.Amount;
                invoice.PaymentStatus = totalEffectivePaid >= row.EffectiveNet
                    ? enPaymentStatus.paid
                    : enPaymentStatus.partial;

                if (invoice.PartyId.HasValue)
                {
                    if (invoice.PartyType == 'C')
                    {
                        // تحديث الرصيد عبر استعلام مباشر: يقرأ القيمة الحالية ويطرح المبلغ في عملية واحدة
                        await _dbContext.Customers
                            .Where(c => c.PartyId == invoice.PartyId.Value)
                            .ExecuteUpdateAsync(setter => setter
                                .SetProperty(c => c.Balance,
                                    c => (c.Balance ?? 0m) - request.Amount < 0m ? 0m : (c.Balance ?? 0m) - request.Amount));
                    }
                    else if (invoice.PartyType == 'S')
                    {
                        // تحديث الرصيد عبر استعلام مباشر: يقرأ القيمة الحالية ويطرح المبلغ في عملية واحدة
                        await _dbContext.Suppliers
                            .Where(s => s.PartyId == invoice.PartyId.Value)
                            .ExecuteUpdateAsync(setter => setter
                                .SetProperty(s => s.Balance,
                                    s => (s.Balance ?? 0m) - request.Amount < 0m ? 0m : (s.Balance ?? 0m) - request.Amount));
                    }
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم تسجيل الدفعة بنجاح.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Result.Fail($"فشل تسجيل الدفعة. [{ex.GetType().Name}: {ex.Message}]");
            }
        }

        // ─────────────────────────────────────────────────
        // تحصيل دفعة من رصيد عميل (بدون فاتورة)
        // ─────────────────────────────────────────────────
        public async Task<Result> ReceiveCustomerPaymentAsync(DtoPaymentRequest request)
        {
            if (request.Amount <= 0)
                return Result.Fail("مبلغ التحصيل يجب أن يكون أكبر من صفر.");

            if (!request.PartyId.HasValue)
                return Result.Fail("معرف العميل مطلوب.");

            // التحقق من وجود العميل فقط — الرصيد يُقرأ داخل المعاملة من البيانات النظيفة
            var customerExists = await _dbContext.Customers.AnyAsync(c => c.PartyId == request.PartyId.Value);
            if (!customerExists)
                return Result.Fail("العميل غير موجود.");

            // ══ صلاحية التحصيل — شاشة العملاء (بت 4) في الخدمة لا الواجهة ══
            // الفحص مستقل عن الجلسة: تمرير SessionId = null لا يتجاوزه إطلاقًا.
            int collectorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, request.UserId);
            if (collectorRole < 0 || !AppPermissions.Has(collectorRole, AppPermissions.Customers))
            {
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentDenied",
                    "تحصيل دفعة من عميل مرفوض: يتطلب صلاحية شاشة العملاء.");
                return Result.Fail("تحصيل دفعة من عميل يتطلب صلاحية شاشة العملاء.");
            }

            // Serializable + إعادة فحص الرصيد داخل المعاملة: يمنع تحصيلين متوازيين يتجاوزان الرصيد
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // VULN-05: التحقق الجلسي داخل المعاملة — تحصيل اليوم يخص درج جلسة مفتوحة مملوكة للفاعل
                var sessionCheck = await ValidateSessionAsync(request.SessionId, request.UserId);
                if (!sessionCheck.IsSuccess)
                    return sessionCheck;

                // قراءة الرصيد داخل المعاملة بدون تتبع مع UPDLOCK على صف العميل:
                // العملية الثانية المتزامنة تنتظر هنا حتى تلتزم الأولى ثم تقرأ الرصيد
                // الجديد الملتزم — لا سباق (قراءة قديمة) ولا deadlock (S→X) على الصف.
                decimal balance = await _dbContext.Database
                    .SqlQuery<decimal?>($@"
                        SELECT Balance AS [Value]
                        FROM Customers WITH (UPDLOCK)
                        WHERE PartyId = {request.PartyId.Value}")
                    .FirstOrDefaultAsync() ?? 0m;

                if (balance < request.Amount)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentRejected",
                        $"تحصيل مرفوض: المبلغ {request.Amount:N2} أكبر من رصيد العميل #{request.PartyId} المستحق ({balance:N2}).");
                    return Result.Fail($"المبلغ أكبر من رصيد العميل المستحق ({balance:N2}).");
                }

                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = null,
                    PartyType = 'C',
                    PartyId = request.PartyId.Value,
                    // VULN-05: تحصيل دَين قديم اليوم يخص درج جلسة اليوم — النقد دخلها فعليًا
                    SessionId = request.SessionId,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                });

                // التسوية الحتمية: الدفعة العامة تُخصَّص على الفواتير المستحقة
                // (الأقدم أولًا) فتتحدث حالات السداد — وإلا بقيت فاتورة مغطاة
                // بالكامل تظهر «غير مسددة» وتسمح بتحصيل الدَّين مرة ثانية.
                await clsPaymentAllocation.ApplyGeneralPaymentToInvoiceStatusesAsync(
                    _dbContext, 'C', request.PartyId.Value, request.Amount);

                // تحديث الرصيد عبر استعلام مباشر يضمن atomicity مع القراءة النظيفة
                await _dbContext.Customers
                    .Where(c => c.PartyId == request.PartyId.Value)
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(c => c.Balance,
                            c => (c.Balance ?? 0m) - request.Amount < 0m ? 0m : (c.Balance ?? 0m) - request.Amount));

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم تحصيل الدفعة من العميل بنجاح.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Result.Fail($"فشل تحصيل الدفعة. [{ex.GetType().Name}: {ex.Message}]");
            }
        }

        // ─────────────────────────────────────────────────
        // سداد دفعة لرصيد مورد (بدون فاتورة)
        // ─────────────────────────────────────────────────
        public async Task<Result> PaySupplierAsync(DtoPaymentRequest request)
        {
            if (request.Amount <= 0)
                return Result.Fail("مبلغ السداد يجب أن يكون أكبر من صفر.");

            if (!request.PartyId.HasValue)
                return Result.Fail("معرف المورد مطلوب.");

            // التحقق من وجود المورد فقط — الرصيد يُقرأ داخل المعاملة من البيانات النظيفة
            var supplierExists = await _dbContext.Suppliers.AnyAsync(s => s.PartyId == request.PartyId.Value);
            if (!supplierExists)
                return Result.Fail("المورد غير موجود.");

            // ══ صلاحية سداد المورد — شاشة الموردين (بت 8) في الخدمة لا الواجهة ══
            // الفحص مستقل عن الجلسة: تمرير SessionId = null لا يتجاوزه إطلاقًا.
            int payerRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, request.UserId);
            if (payerRole < 0 || !AppPermissions.Has(payerRole, AppPermissions.Suppliers))
            {
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentDenied",
                    "سداد دفعة لمورد مرفوض: يتطلب صلاحية شاشة الموردين.");
                return Result.Fail("سداد دفعة للمورد يتطلب صلاحية شاشة الموردين.");
            }

            // Serializable + إعادة فحص الرصيد داخل المعاملة: يمنع سدادين متوازيين يتجاوزان الرصيد
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // VULN-05: التحقق الجلسي داخل المعاملة — سداد نقد للمورد يخرج من درج جلسة مفتوحة مملوكة للفاعل
                var sessionCheck = await ValidateSessionAsync(request.SessionId, request.UserId);
                if (!sessionCheck.IsSuccess)
                    return sessionCheck;

                // قراءة الرصيد داخل المعاملة بدون تتبع مع UPDLOCK على صف المورد:
                // العملية الثانية المتزامنة تنتظر هنا حتى تلتزم الأولى ثم تقرأ الرصيد
                // الجديد الملتزم — لا سباق (قراءة قديمة) ولا deadlock (S→X) على الصف.
                decimal balance = await _dbContext.Database
                    .SqlQuery<decimal?>($@"
                        SELECT Balance AS [Value]
                        FROM Suppliers WITH (UPDLOCK)
                        WHERE PartyId = {request.PartyId.Value}")
                    .FirstOrDefaultAsync() ?? 0m;

                if (balance < request.Amount)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PaymentRejected",
                        $"سداد مرفوض: المبلغ {request.Amount:N2} أكبر من رصيد المورد #{request.PartyId} المستحق ({balance:N2}).");
                    return Result.Fail($"المبلغ أكبر من رصيد المورد المستحق ({balance:N2}).");
                }

                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.outcome,
                    InvoiceId = null,
                    PartyType = 'S',
                    PartyId = request.PartyId.Value,
                    // VULN-05: سداد المورد النقدي يخص درج الجلسة التي صُرف منه
                    SessionId = request.SessionId,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                });

                // التسوية الحتمية — نفس منطق تحصيل العميل أعلاه
                await clsPaymentAllocation.ApplyGeneralPaymentToInvoiceStatusesAsync(
                    _dbContext, 'S', request.PartyId.Value, request.Amount);

                // تحديث الرصيد عبر استعلام مباشر يضمن atomicity مع القراءة النظيفة
                await _dbContext.Suppliers
                    .Where(s => s.PartyId == request.PartyId.Value)
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(s => s.Balance,
                            s => (s.Balance ?? 0m) - request.Amount < 0m ? 0m : (s.Balance ?? 0m) - request.Amount));

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم سداد الدفعة للمورد بنجاح.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Result.Fail($"فشل سداد الدفعة. [{ex.GetType().Name}: {ex.Message}]");
            }
        }
    }
}
