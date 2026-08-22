using Microsoft.EntityFrameworkCore;
using Common.DTOs;
using Common.Results;
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

            if (request.CustomerId.HasValue)
            {
                bool customerExists = await _dbContext.Customers.AnyAsync(c => c.PartyId == request.CustomerId.Value);
                if (!customerExists)
                    return Result<int>.Fail("العميل غير موجود.");
            }

            //  بدء المعاملة
            using var transaction = await _dbContext.Database.BeginTransactionAsync();
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
                await transaction.CommitAsync();

                return Result<int>.Ok(invoice.InvoiceId, suspend ? "تم تعليق الفاتورة وحجز المخزون." : "تمت عملية البيع بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشلت عملية البيع. تم إلغاء جميع التغييرات.");
            }
        }

        // ─────────────────────────────────────────────────
        // إكمال فاتورة معلقة
        // ─────────────────────────────────────────────────
        public async Task<Result> CompleteSuspendedInvoiceAsync(int invoiceId, DtoPaymentInfo paymentInfo)
        {
            var invoice = await _dbContext.Invoices
                .Include(i => i.InvoiceItems)
                .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId && i.Status == enInvoicesStatus.suspended);

            if (invoice == null)
                return Result.Fail("الفاتورة المعلقة غير موجودة أو تم إكمالها بالفعل.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // تحويل الحالة
                invoice.Status = enInvoicesStatus.completed;

                // خصم المخزون (معالجة الدُفعات)
                var items = invoice.InvoiceItems.Select(i => new DtoCheckoutItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    BatchId = i.BatchId
                }).ToList();

                await DeductStockAndCreateMovementsAsync(items, invoice.InvoiceId);

                // معالجة الدفع
                var request = new DtoCheckoutRequest
                {
                    CustomerId = invoice.PartyId,
                    Payment = paymentInfo,
                    DiscountPercent = 0, // يمكنك إضافة حقول للخصم في الفاتورة المعلقة
                    TaxPercent = 0
                };
                await ProcessPaymentAsync(invoice, request);

                // تحرير الحجز (طرح الكمية من QuantityReserved)
                await ReleaseReservationAsync(items);

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إكمال الفاتورة بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشل إكمال الفاتورة المعلقة.");
            }
        }

        // ─────────────────────────────────────────────────
        // إلغاء فاتورة معلقة (تحرير الحجز)
        // ─────────────────────────────────────────────────
        public async Task<Result> CancelSuspendedInvoiceAsync(int invoiceId)
        {
            var invoice = await _dbContext.Invoices.FindAsync(invoiceId);
            if (invoice == null || invoice.Status != enInvoicesStatus.suspended)
                return Result.Fail("الفاتورة المعلقة غير موجودة.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
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

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إلغاء الفاتورة وتحرير المخزون المحجوز.");
            }
            catch
            {
                await transaction.RollbackAsync();
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
                total += prod.BasePrice * item.Quantity;
            }
            
            decimal discount = request.DiscountPercent > 0
                ? total * request.DiscountPercent / 100
                : request.DiscountAmount;

            decimal afterDiscount = total - discount;
            decimal tax = request.TaxPercent > 0
                ? afterDiscount * request.TaxPercent / 100
                : 0;

            decimal net = afterDiscount + tax;

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
                    ItemPrice = product?.BasePrice ?? 0,
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
                if (inv.QuantityAvailable is not null)
                {
                    int availableNow = (inv.QuantityAvailable ?? 0) - (inv.QuantityReserved ?? 0);
                    if (availableNow < item.Quantity)
                        throw new InvalidOperationException($"المخزون غير كافٍ للمنتج {product.Name}");
                }
                // تطبيق FEFO أو استخدام الدُفعة المحددة
              
                    // FEFO
                    var batches = await _dbContext.Batches
                        .Where(b => b.ProductId == item.ProductId && b.BatchQuantity > 0)
                        .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                        .ThenBy(b => b.CreatedAt)
                        .ToListAsync();

                    int remaining = item.Quantity;
                    foreach (var batch in batches)
                    {
                        if (remaining <= 0) break;
                        int take = Math.Min(remaining, batch.BatchQuantity);
                        batch.BatchQuantity -= take;
                        remaining -= take;

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
            decimal paid = request.Payment?.Amount ?? 0;

            if (paid > 0)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = invoice.InvoiceId,
                    PartyType = 'C',
                    PartyId = invoice.PartyId ?? null,
                    Amount = paid,
                    PaymentDate = request.Payment.PaymentDate,
                    PaymentMethod = request.Payment.Method,
                    Notes = null,
                    CreatedAt = DateTime.UtcNow
                });
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

            // إذا كان البيع بالدين (أو جزئي) لعميل مسجل
            if (invoice.PartyId.HasValue && (paid < net || paid == 0))
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