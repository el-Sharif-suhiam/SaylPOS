using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
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
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.sale
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة بيع مكتملة.");

            foreach (var item in request.Items)
            {
                var originalItem = originalInvoice.InvoiceItems
                    .FirstOrDefault(ii => ii.ProductId == item.ProductId);

                if (originalItem == null)
                    return Result<int>.Fail($"المنتج {item.ProductId} غير موجود في الفاتورة الأصلية.");

                if (item.Quantity > originalItem.Quantity)
                    return Result<int>.Fail($"الكمية المرتجعة أكبر من الكمية المباعة للمنتج {item.ProductId}.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                decimal total = 0;
                var returnInvoice = new Invoice
                {
                    Type = enInvoicesType.customer_return,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.paid,
                    PartyType = originalInvoice.PartyType,
                    PartyId = originalInvoice.PartyId,
                    UserId = request.UserId,
                    SessionId = originalInvoice.SessionId,
                    OriginalInvoiceId = originalInvoice.InvoiceId,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                };

                foreach (var item in request.Items)
                {
                    var originalItem = originalInvoice.InvoiceItems
                        .First(ii => ii.ProductId == item.ProductId);

                    total += originalItem.ItemPrice * item.Quantity;
                }

                returnInvoice.TotalAmount = -total;
                returnInvoice.DiscountAmount = 0;
                returnInvoice.TaxAmount = 0;
                returnInvoice.NetAmount = -total;

                _dbContext.Invoices.Add(returnInvoice);
                await _dbContext.SaveChangesAsync();

                foreach (var item in request.Items)
                {
                    var originalItem = originalInvoice.InvoiceItems
                        .First(ii => ii.ProductId == item.ProductId);

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = returnInvoice.InvoiceId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        BatchId = item.BatchId ?? originalItem.BatchId,
                        ItemPrice = originalItem.ItemPrice,
                        ItemCost = originalItem.ItemCost
                    });

                    await RestockProductAsync(item.ProductId, item.Quantity, item.BatchId ?? originalItem.BatchId, returnInvoice);
                }

                await ProcessCustomerRefundAsync(originalInvoice, returnInvoice, total);

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
                .FirstOrDefaultAsync(i => i.InvoiceId == request.OriginalInvoiceId
                                       && i.Type == enInvoicesType.purchase
                                       && i.Status == enInvoicesStatus.completed);

            if (originalInvoice == null)
                return Result<int>.Fail("الفاتورة الأصلية غير موجودة أو ليست فاتورة شراء مكتملة.");

            foreach (var item in request.Items)
            {
                var originalItem = originalInvoice.InvoiceItems
                    .FirstOrDefault(ii => ii.ProductId == item.ProductId);

                if (originalItem == null)
                    return Result<int>.Fail($"المنتج {item.ProductId} غير موجود في الفاتورة الأصلية.");

                if (item.Quantity > originalItem.Quantity)
                    return Result<int>.Fail($"الكمية المرتجعة أكبر من الكمية المشتراة للمنتج {item.ProductId}.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                decimal total = 0;
                var returnInvoice = new Invoice
                {
                    Type = enInvoicesType.supplier_return,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.paid,
                    PartyType = originalInvoice.PartyType,
                    PartyId = originalInvoice.PartyId,
                    UserId = request.UserId,
                    OriginalInvoiceId = originalInvoice.InvoiceId,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                };

                foreach (var item in request.Items)
                {
                    var originalItem = originalInvoice.InvoiceItems
                        .First(ii => ii.ProductId == item.ProductId);

                    total += originalItem.ItemPrice * item.Quantity;
                }

                returnInvoice.TotalAmount = -total;
                returnInvoice.DiscountAmount = 0;
                returnInvoice.TaxAmount = 0;
                returnInvoice.NetAmount = -total;

                _dbContext.Invoices.Add(returnInvoice);
                await _dbContext.SaveChangesAsync();

                foreach (var item in request.Items)
                {
                    var originalItem = originalInvoice.InvoiceItems
                        .First(ii => ii.ProductId == item.ProductId);

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = returnInvoice.InvoiceId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        BatchId = item.BatchId ?? originalItem.BatchId,
                        ItemPrice = originalItem.ItemPrice,
                        ItemCost = originalItem.ItemCost
                    });

                    await DeductProductStockAsync(item.ProductId, item.Quantity, item.BatchId ?? originalItem.BatchId, returnInvoice);
                }

                await ProcessSupplierRefundAsync(originalInvoice, returnInvoice, total);

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

            if (batchId.HasValue)
            {
                var batch = await _dbContext.Batches.FindAsync(batchId.Value);
                if (batch != null && batch.ProductId == productId)
                {
                    batch.BatchQuantity += quantity;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = productId,
                        BatchId = batch.BatchId,
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

        private async Task ProcessCustomerRefundAsync(Invoice originalInvoice, Invoice returnInvoice, decimal refundTotal)
        {
            decimal paidSoFar = await _dbContext.Payments
                .Where(p => p.InvoiceId == originalInvoice.InvoiceId)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal cashRefund = Math.Min(refundTotal, paidSoFar);

            if (cashRefund > 0)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.outcome,
                    InvoiceId = returnInvoice.InvoiceId,
                    PartyType = 'C',
                    PartyId = originalInvoice.PartyId,
                    Amount = cashRefund,
                    PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    PaymentMethod = "cash",
                    Notes = $"استرداد نقدي عن الفاتورة {originalInvoice.InvoiceId}",
                    CreatedAt = DateTime.UtcNow
                });
            }

            decimal balanceRefund = refundTotal - cashRefund;
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

        private async Task ProcessSupplierRefundAsync(Invoice originalInvoice, Invoice returnInvoice, decimal refundTotal)
        {
            decimal paidSoFar = await _dbContext.Payments
                .Where(p => p.InvoiceId == originalInvoice.InvoiceId)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal cashRefund = Math.Min(refundTotal, paidSoFar);

            if (cashRefund > 0)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.income,
                    InvoiceId = returnInvoice.InvoiceId,
                    PartyType = 'S',
                    PartyId = originalInvoice.PartyId,
                    Amount = cashRefund,
                    PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    PaymentMethod = "cash",
                    Notes = $"استرداد من المورد عن الفاتورة {originalInvoice.InvoiceId}",
                    CreatedAt = DateTime.UtcNow
                });
            }

            decimal balanceRefund = refundTotal - cashRefund;
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
    }
}
