using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
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

            decimal paidSoFar = await _dbContext.Payments
                .Where(p => p.InvoiceId == invoice.InvoiceId)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal remaining = invoice.NetAmount - paidSoFar;
            if (request.Amount > remaining)
                return Result.Fail($"المبلغ أكبر من المتبقي على الفاتورة ({remaining}).");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = invoice.Type == enInvoicesType.purchase || invoice.Type == enInvoicesType.supplier_return
                        ? enPaymentType.outcome
                        : enPaymentType.income,
                    InvoiceId = invoice.InvoiceId,
                    PartyType = invoice.PartyType ?? 'C',
                    PartyId = invoice.PartyId,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.Now
                });

                decimal totalPaid = paidSoFar + request.Amount;
                invoice.PaymentStatus = totalPaid >= invoice.NetAmount
                    ? enPaymentStatus.paid
                    : enPaymentStatus.partial;

                if (invoice.PartyId.HasValue)
                {
                    if (invoice.PartyType == 'C')
                    {
                        var customer = await _dbContext.Customers.FindAsync(invoice.PartyId.Value);
                        if (customer != null)
                        {
                            customer.Balance -= request.Amount;
                            if (customer.Balance < 0) customer.Balance = 0;
                        }
                    }
                    else if (invoice.PartyType == 'S')
                    {
                        var supplier = await _dbContext.Suppliers.FindAsync(invoice.PartyId.Value);
                        if (supplier != null)
                        {
                            supplier.Balance -= request.Amount;
                            if (supplier.Balance < 0) supplier.Balance = 0;
                        }
                    }
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم تسجيل الدفعة بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشل تسجيل الدفعة.");
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

            var customer = await _dbContext.Customers.FindAsync(request.PartyId.Value);
            if (customer == null)
                return Result.Fail("العميل غير موجود.");

            if ((customer.Balance ?? 0) < request.Amount)
                return Result.Fail("المبلغ أكبر من رصيد العميل المستحق.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = null,
                    PartyType = 'C',
                    PartyId = request.PartyId.Value,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.Now
                });

                customer.Balance -= request.Amount;

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم تحصيل الدفعة من العميل بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشل تحصيل الدفعة.");
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

            var supplier = await _dbContext.Suppliers.FindAsync(request.PartyId.Value);
            if (supplier == null)
                return Result.Fail("المورد غير موجود.");

            if ((supplier.Balance ?? 0) < request.Amount)
                return Result.Fail("المبلغ أكبر من رصيد المورد المستحق.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.outcome,
                    InvoiceId = null,
                    PartyType = 'S',
                    PartyId = request.PartyId.Value,
                    Amount = request.Amount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.Method,
                    Notes = request.Notes,
                    CreatedAt = DateTime.Now
                });

                supplier.Balance -= request.Amount;

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم سداد الدفعة للمورد بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشل سداد الدفعة.");
            }
        }
    }
}
