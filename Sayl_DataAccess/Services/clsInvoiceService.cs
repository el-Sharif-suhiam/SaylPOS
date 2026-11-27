using Common.DTOs;
using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    public class clsInvoiceService
    {
        private readonly SaylDbContext _dbContext;

        public clsInvoiceService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<Result<List<InvoiceVM>>> GetInvoicesListAsync(
            enInvoicesType? type = null,
            enInvoicesStatus? status = null,
            enPaymentStatus? paymentStatus = null,
            int? partyId = null,
            int? userId = null,
            int? sessionId = null,
            DateOnly? fromDate = null,
            DateOnly? toDate = null)
        {
            var query = _dbContext.Invoices.AsQueryable();

            if (type.HasValue)
                query = query.Where(i => i.Type == type.Value);

            if (status.HasValue)
                query = query.Where(i => i.Status == status.Value);

            if (paymentStatus.HasValue)
                query = query.Where(i => i.PaymentStatus == paymentStatus.Value);

            if (partyId.HasValue)
                query = query.Where(i => i.PartyId == partyId.Value);

            if (userId.HasValue)
                query = query.Where(i => i.UserId == userId.Value);

            if (sessionId.HasValue)
                query = query.Where(i => i.SessionId == sessionId.Value);

            if (fromDate.HasValue)
            {
                // المصدر الموحّد ProgramClock (VULN-45) — كانت البداية تُقارَن هنا
                // بأوساط ليل محلية بلا أي تحويل إزاحة (نافذة مُزاحة بافتراض جهاز
                // غير موثوق مقابل طوابع UTC) — الآن نفس ساعة البرنامج كبقية النظام.
                var (start, _) = ProgramClock.LocalDayBoundsUtc(fromDate.Value);
                query = query.Where(i => i.CreatedAt >= start);
            }

            if (toDate.HasValue)
            {
                // نهاية toDate حصرية = منتصف ليل اليوم التالي والقارن < النهاية
                // (تكافئ القديم: MaxValue المحلي يستثني بالضبط ما يستثنيه منتصف الليل التالي).
                var (_, endEx) = ProgramClock.LocalDayBoundsUtc(toDate.Value);
                query = query.Where(i => i.CreatedAt < endEx);
            }

            var result = await query
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new InvoiceVM
                {
                    InvoiceId = i.InvoiceId,
                    InvoiceNumber = i.InvoiceNumber,
                    Type = i.Type.ToString(),
                    Status = i.Status.ToString(),
                    PaymentStatus = i.PaymentStatus.ToString(),
                    PartyId = i.PartyId,
                    PartyName = i.Party != null ? i.Party.Name : null,
                    UserName = i.User.Username,
                    SessionId = i.SessionId,
                    OriginalInvoiceId = i.OriginalInvoiceId,
                    OriginalInvoiceNumber = i.OriginalInvoice != null ? i.OriginalInvoice.InvoiceNumber : null,
                    TotalAmount = i.TotalAmount,
                    DiscountAmount = i.DiscountAmount,
                    TaxAmount = i.TaxAmount,
                    NetAmount = i.NetAmount,
                    Notes = i.Notes,
                    CreatedAt = i.CreatedAt,
                    // عدد البنود كعدّ فرعي — يغني عن Include كامل للبنود
                    // في استعلام قائمة (عرض فقط) — شاشة الفواتير المعلقة تعرضه.
                    ItemsCount = i.InvoiceItems.Count
                })
                .ToListAsync();

            return Result<List<InvoiceVM>>.Ok(result);
        }

        /// <summary>
        /// صفحة فواتير (ترقيم SQL): نفس فلاتر GetInvoicesListAsync + عدّ
        /// كامل + Skip/Take — قائمة فواتير اليوم المزدحمة لا تُنقل كاملة.
        /// </summary>
        public async Task<Result<PagedResult<InvoiceVM>>> GetInvoicesPagedAsync(
            enInvoicesType? type = null,
            enInvoicesStatus? status = null,
            enPaymentStatus? paymentStatus = null,
            int? partyId = null,
            int? userId = null,
            int? sessionId = null,
            DateOnly? fromDate = null,
            DateOnly? toDate = null,
            int page = 1,
            int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

            var query = _dbContext.Invoices.AsQueryable();

            if (type.HasValue)
                query = query.Where(i => i.Type == type.Value);

            if (status.HasValue)
                query = query.Where(i => i.Status == status.Value);

            if (paymentStatus.HasValue)
                query = query.Where(i => i.PaymentStatus == paymentStatus.Value);

            if (partyId.HasValue)
                query = query.Where(i => i.PartyId == partyId.Value);

            if (userId.HasValue)
                query = query.Where(i => i.UserId == userId.Value);

            if (sessionId.HasValue)
                query = query.Where(i => i.SessionId == sessionId.Value);

            if (fromDate.HasValue)
            {
                // المصدر الموحّد ProgramClock (VULN-45) — بداية المحل للتاريخ المحدد
                // منتصفا ليل اليوم والتالي محوَّلين بمعنى UTC (نفس إزاحة التقارير).
                var (start, _) = ProgramClock.LocalDayBoundsUtc(fromDate.Value);
                query = query.Where(i => i.CreatedAt >= start);
            }

            if (toDate.HasValue)
            {
                // نهاية toDate حصرية = منتصف ليل اليوم التالي والقارن < النهاية
                // (تكافئ القديم: MaxValue−3h يستثني بالضبط ما يستثنيه منتصف ليل اليوم التالي).
                var (_, endEx) = ProgramClock.LocalDayBoundsUtc(toDate.Value);
                query = query.Where(i => i.CreatedAt < endEx);
            }

            int total = await query.CountAsync();
            var items = await query
                .OrderByDescending(i => i.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(i => new InvoiceVM
                {
                    InvoiceId = i.InvoiceId,
                    InvoiceNumber = i.InvoiceNumber,
                    Type = i.Type.ToString(),
                    Status = i.Status.ToString(),
                    PaymentStatus = i.PaymentStatus.ToString(),
                    PartyId = i.PartyId,
                    PartyName = i.Party != null ? i.Party.Name : null,
                    UserName = i.User.Username,
                    SessionId = i.SessionId,
                    OriginalInvoiceId = i.OriginalInvoiceId,
                    OriginalInvoiceNumber = i.OriginalInvoice != null ? i.OriginalInvoice.InvoiceNumber : null,
                    TotalAmount = i.TotalAmount,
                    DiscountAmount = i.DiscountAmount,
                    TaxAmount = i.TaxAmount,
                    NetAmount = i.NetAmount,
                    Notes = i.Notes,
                    CreatedAt = i.CreatedAt,
                    ItemsCount = i.InvoiceItems.Count
                })
                .ToListAsync();

            return Result<PagedResult<InvoiceVM>>.Ok(new PagedResult<InvoiceVM>
            {
                Items = items,
                TotalCount = total,
                Page = page,
                PageSize = pageSize
            });
        }

        public async Task<Result<InvoiceVM>> GetInvoiceByIdAsync(int invoiceId)
        {
            var invoice = await _dbContext.Invoices
                .Include(i => i.Party)
                .Include(i => i.User)
                .Include(i => i.OriginalInvoice)
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Product)
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);

            if (invoice == null)
                return Result<InvoiceVM>.Fail("الفاتورة غير موجودة.");

            var vm = new InvoiceVM
            {
                InvoiceId = invoice.InvoiceId,
                InvoiceNumber = invoice.InvoiceNumber,
                Type = invoice.Type.ToString(),
                Status = invoice.Status.ToString(),
                PaymentStatus = invoice.PaymentStatus.ToString(),
                PartyId = invoice.PartyId,
                PartyName = invoice.Party?.Name,
                UserName = invoice.User?.Username,
                SessionId = invoice.SessionId,
                OriginalInvoiceId = invoice.OriginalInvoiceId,
                OriginalInvoiceNumber = invoice.OriginalInvoice != null ? invoice.OriginalInvoice.InvoiceNumber : null,
                TotalAmount = invoice.TotalAmount,
                DiscountAmount = invoice.DiscountAmount,
                TaxAmount = invoice.TaxAmount,
                NetAmount = invoice.NetAmount,
                Notes = invoice.Notes,
                CreatedAt = invoice.CreatedAt,
                // VULN-42: كان يملأ Items ويترك ItemsCount=0 — فخ لمستهلك مستقبلي
                ItemsCount = invoice.InvoiceItems.Count,
                Items = invoice.InvoiceItems.Select(ii => new InvoiceItemVM
                {
                    InvoiceItemId = ii.InvoiceItemId,
                    ProductId = ii.ProductId,
                    ProductName = ii.Product.Name,
                    Quantity = ii.Quantity,
                    BatchNumber = ii.Batch != null ? ii.Batch.BatchNumber : null,
                    ItemPrice = ii.ItemPrice,
                    ItemCost = ii.ItemCost,
                    // المرحلة 5: لقطة وحدة الإدخال من السطر المخزن — null = سطر
                    // إرثي/وحدة أساس (العرض يرجع حينها لوحدة المنتج الحالية).
                    UnitName = ii.UnitName,
                    UnitFactorToBase = ii.UnitFactorToBase,
                    EnteredQuantity = ii.EnteredQuantity
                }).ToList()
            };

            return Result<InvoiceVM>.Ok(vm);
        }

        // ─────────────────────────────────────────────────
        // البحث بالرقم التجاري (INV-YYYY-NNNNNN) — مطابقة تامة فقط.
        // لا تفسير رقمي ولا تجريد تنسيق: المُدخل إما رقم تجاري مخزَّن حرفيًا أو رفض.
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> GetInvoiceIdByNumberAsync(string invoiceNumber)
        {
            var input = invoiceNumber?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return Result<int>.Fail("أدخل رقم الفاتورة التجاري.");

            var id = await _dbContext.Invoices.AsNoTracking()
                .Where(i => i.InvoiceNumber == input)
                .Select(i => (int?)i.InvoiceId)
                .FirstOrDefaultAsync();

            if (id == null)
                return Result<int>.Fail("لا توجد فاتورة بالرقم التجاري المُدخل.");

            return Result<int>.Ok(id.Value);
        }

        /// <summary>الرقم التجاري لفاتورة بالمعرف الداخلي — لرسائل النجاح بعد الإيداع
        /// (InvoiceNumber NOT NULL مُسند داخل معاملة الإنشاء، فالفشل نظريًا مستحيل).</summary>
        public async Task<Result<string>> GetInvoiceNumberAsync(int invoiceId)
        {
            var number = await _dbContext.Invoices.AsNoTracking()
                .Where(i => i.InvoiceId == invoiceId)
                .Select(i => i.InvoiceNumber)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(number))
                return Result<string>.Fail("الفاتورة غير موجودة.");

            return Result<string>.Ok(number);
        }

        // ─────────────────────────────────────────────────
        // بيانات الفاتورة الكاملة للطباعة (البنود والمدفوعات)
        // ─────────────────────────────────────────────────
        public async Task<Result<InvoicePrintVM>> GetInvoiceForPrintAsync(int invoiceId)
        {
            var invoice = await _dbContext.Invoices
                .Include(i => i.Party)
                .Include(i => i.User)
                    .ThenInclude(u => u.Employee)
                .Include(i => i.OriginalInvoice)
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Product)
                .Include(i => i.InvoiceItems)
                    .ThenInclude(ii => ii.Batch)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);

            if (invoice == null)
                return Result<InvoicePrintVM>.Fail("الفاتورة غير موجودة.");

            decimal paid = invoice.Payments.Sum(p => p.Amount);

            var vm = new InvoicePrintVM
            {
                InvoiceId = invoice.InvoiceId,
                InvoiceNumber = invoice.InvoiceNumber,
                Type = invoice.Type.ToString(),
                Status = invoice.Status.ToString(),
                PaymentStatus = invoice.PaymentStatus.ToString(),
                PartyId = invoice.PartyId,
                PartyName = invoice.Party?.Name,
                PartyPhone = invoice.Party?.Phone1,
                PartyAddress = invoice.Party?.AddressLine1,
                CashierName = invoice.User?.Employee?.Name ?? invoice.User?.Username ?? "",
                SessionId = invoice.SessionId,
                OriginalInvoiceId = invoice.OriginalInvoiceId,
                OriginalInvoiceNumber = invoice.OriginalInvoice != null ? invoice.OriginalInvoice.InvoiceNumber : null,
                Notes = invoice.Notes,
                CreatedAt = invoice.CreatedAt,
                TotalAmount = invoice.TotalAmount,
                DiscountAmount = invoice.DiscountAmount,
                TaxAmount = invoice.TaxAmount,
                NetAmount = invoice.NetAmount,
                PaidAmount = paid,
                RemainingAmount = invoice.NetAmount - paid,
                Items = invoice.InvoiceItems.Select(ii => new InvoiceItemVM
                {
                    InvoiceItemId = ii.InvoiceItemId,
                    ProductId = ii.ProductId,
                    ProductName = ii.Product.Name,
                    Quantity = ii.Quantity,
                    BatchNumber = ii.Batch != null ? ii.Batch.BatchNumber : null,
                    ItemPrice = ii.ItemPrice,
                    ItemCost = ii.ItemCost,
                    // المرحلة 5: لقطة وحدة الإدخال من السطر المخزن (طباعة/عرض) —
                    // null = سطر إرثي/وحدة أساس.
                    UnitName = ii.UnitName,
                    UnitFactorToBase = ii.UnitFactorToBase,
                    EnteredQuantity = ii.EnteredQuantity
                }).ToList(),
                Payments = invoice.Payments
                    .OrderBy(p => p.PaymentDate)
                    .ThenBy(p => p.PaymentId)
                    .Select(p => new InvoicePaymentLineVM
                    {
                        PaymentId = p.PaymentId,
                        Amount = p.Amount,
                        PaymentMethod = p.PaymentMethod,
                        PaymentDate = p.PaymentDate,
                        Notes = p.Notes
                    }).ToList()
            };

            return Result<InvoicePrintVM>.Ok(vm);
        }
    }
}
