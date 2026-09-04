using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
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
                DateTime start = fromDate.Value.ToDateTime(TimeOnly.MinValue);
                query = query.Where(i => i.CreatedAt >= start);
            }

            if (toDate.HasValue)
            {
                DateTime end = toDate.Value.ToDateTime(TimeOnly.MaxValue);
                query = query.Where(i => i.CreatedAt <= end);
            }

            var result = await query
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new InvoiceVM
                {
                    InvoiceId = i.InvoiceId,
                    Type = i.Type.ToString(),
                    Status = i.Status.ToString(),
                    PaymentStatus = i.PaymentStatus.ToString(),
                    PartyId = i.PartyId,
                    PartyName = i.Party != null ? i.Party.Name : null,
                    UserName = i.User.Username,
                    SessionId = i.SessionId,
                    OriginalInvoiceId = i.OriginalInvoiceId,
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

        public async Task<Result<InvoiceVM>> GetInvoiceByIdAsync(int invoiceId)
        {
            var invoice = await _dbContext.Invoices
                .Include(i => i.Party)
                .Include(i => i.User)
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
                Type = invoice.Type.ToString(),
                Status = invoice.Status.ToString(),
                PaymentStatus = invoice.PaymentStatus.ToString(),
                PartyId = invoice.PartyId,
                PartyName = invoice.Party?.Name,
                UserName = invoice.User?.Username,
                SessionId = invoice.SessionId,
                OriginalInvoiceId = invoice.OriginalInvoiceId,
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
                    ItemCost = ii.ItemCost
                }).ToList()
            };

            return Result<InvoiceVM>.Ok(vm);
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
                    ItemCost = ii.ItemCost
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
