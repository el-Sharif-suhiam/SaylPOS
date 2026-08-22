using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsPurchaseService
    {
        private readonly SaylDbContext _dbContext;

        public clsPurchaseService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // فاتورة شراء: استلام الدفعات + تحديث المخزون + رصيد المورد
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> CreatePurchaseInvoiceAsync(DtoPurchaseRequest request)
        {
            if (request.Items == null || !request.Items.Any())
                return Result<int>.Fail("يجب إضافة بنود إلى فاتورة الشراء.");

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                    return Result<int>.Fail("الكمية يجب أن تكون أكبر من صفر.");

                if (item.UnitCost < 0)
                    return Result<int>.Fail("سعر التكلفة يجب أن يكون صفرا أو أكثر.");

                if (string.IsNullOrWhiteSpace(item.BatchNumber))
                    return Result<int>.Fail("رقم الدفعة مطلوب لكل بند.");
            }

            bool supplierExists = await _dbContext.Suppliers.AnyAsync(s => s.PartyId == request.SupplierId);
            if (!supplierExists)
                return Result<int>.Fail("المورد غير موجود.");

            var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _dbContext.Products
                .Where(p => productIds.Contains(p.ProductId))
                .ToDictionaryAsync(p => p.ProductId);

            foreach (var item in request.Items)
            {
                if (!products.TryGetValue(item.ProductId, out var product))
                    return Result<int>.Fail($"المنتج {item.ProductId} غير موجود.");

                if (product.Type != "stock")
                    return Result<int>.Fail($"لا يمكن شراء المنتج '{product.Name}' لأنه غير مخزني.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                decimal total = request.Items.Sum(i => i.UnitCost * i.Quantity);
                decimal discount = request.DiscountAmount;
                decimal afterDiscount = total - discount;
                decimal tax = request.TaxPercent > 0 ? afterDiscount * request.TaxPercent / 100 : 0;
                decimal net = afterDiscount + tax;

                var invoice = new Invoice
                {
                    Type = enInvoicesType.purchase,
                    Status = enInvoicesStatus.completed,
                    PaymentStatus = enPaymentStatus.unpaid,
                    PartyType = 'S',
                    PartyId = request.SupplierId,
                    UserId = request.UserId,
                    TotalAmount = total,
                    DiscountAmount = discount,
                    TaxAmount = tax,
                    NetAmount = net,
                    Notes = request.Notes,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.Invoices.Add(invoice);
                await _dbContext.SaveChangesAsync();

                foreach (var item in request.Items)
                {
                    var batch = await _dbContext.Batches
                        .FirstOrDefaultAsync(b => b.ProductId == item.ProductId && b.BatchNumber == item.BatchNumber);

                    if (batch == null)
                    {
                        batch = new Batch
                        {
                            ProductId = item.ProductId,
                            BatchNumber = item.BatchNumber.Trim(),
                            ExpiryDate = item.ExpiryDate,
                            BatchQuantity = item.Quantity,
                            CreatedAt = DateTime.UtcNow
                        };
                        _dbContext.Batches.Add(batch);
                    }
                    else
                    {
                        batch.BatchQuantity += item.Quantity;
                        if (item.ExpiryDate.HasValue)
                            batch.ExpiryDate = item.ExpiryDate.Value;
                    }

                    await _dbContext.SaveChangesAsync();

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = invoice.InvoiceId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        BatchId = batch.BatchId,
                        ItemPrice = item.UnitCost,
                        ItemCost = item.UnitCost
                    });

                    var inventory = await _dbContext.Inventories.FindAsync(item.ProductId);
                    if (inventory == null)
                    {
                        inventory = new Inventory
                        {
                            ProductId = item.ProductId,
                            QuantityAvailable = 0,
                            QuantityReserved = 0
                        };
                        _dbContext.Inventories.Add(inventory);
                    }

                    inventory.QuantityAvailable += item.Quantity;
                    inventory.UpdatedAt = DateTime.UtcNow;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = item.ProductId,
                        BatchId = batch.BatchId,
                        MovementType = enStockMovementType.purchase,
                        Quantity = item.Quantity,
                        SourceType = "Invoice",
                        SourceId = invoice.InvoiceId,
                        CreatedAt = DateTime.UtcNow
                    });

                    var product = products[item.ProductId];
                    product.CostPrice = item.UnitCost;
                }

                await ProcessPaymentAsync(invoice, request);

                var supplier = await _dbContext.Suppliers.FindAsync(request.SupplierId);
                if (supplier != null)
                {
                    decimal paid = request.Payment?.Amount ?? 0;
                    supplier.Balance += net - paid;
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result<int>.Ok(invoice.InvoiceId, "تم إنشاء فاتورة الشراء بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشل إنشاء فاتورة الشراء.");
            }
        }

        private async Task ProcessPaymentAsync(Invoice invoice, DtoPurchaseRequest request)
        {
            decimal net = invoice.NetAmount;
            decimal paid = request.Payment?.Amount ?? 0;

            if (paid > 0)
            {
                _dbContext.Payments.Add(new Payment
                {
                    Type = enPaymentType.outcome,
                    InvoiceId = invoice.InvoiceId,
                    PartyType = 'S',
                    PartyId = invoice.PartyId,
                    Amount = paid,
                    PaymentDate = request.Payment!.PaymentDate,
                    PaymentMethod = request.Payment.Method,
                    Notes = null,
                    CreatedAt = DateTime.UtcNow
                });
            }

            if (paid >= net)
                invoice.PaymentStatus = enPaymentStatus.paid;
            else if (paid > 0)
                invoice.PaymentStatus = enPaymentStatus.partial;
            else
                invoice.PaymentStatus = enPaymentStatus.unpaid;

            await Task.CompletedTask;
        }
    }
}
