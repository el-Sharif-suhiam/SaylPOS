using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsStockService
    {
        private readonly SaylDbContext _dbContext;

        public clsStockService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // استلام مخزون: إنشاء/زيادة دفعة وتحديث المخزون
        // ─────────────────────────────────────────────────
        public async Task<Result> ReceiveStockAsync(DtoStockReceiveRequest request)
        {
            if (request.Quantity <= 0)
                return Result.Fail("الكمية يجب أن تكون أكبر من صفر.");

            if (string.IsNullOrWhiteSpace(request.BatchNumber))
                return Result.Fail("رقم الدفعة مطلوب.");

            var product = await _dbContext.Products.FindAsync(request.ProductId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            if (product.Type != "stock")
                return Result.Fail("لا يمكن استلام مخزون لمنتج غير مخزني.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var batch = await _dbContext.Batches
                    .FirstOrDefaultAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber);

                if (batch == null)
                {
                    batch = new Batch
                    {
                        ProductId = request.ProductId,
                        BatchNumber = request.BatchNumber.Trim(),
                        ExpiryDate = request.ExpiryDate,
                        BatchQuantity = request.Quantity,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.Batches.Add(batch);
                }
                else
                {
                    batch.BatchQuantity += request.Quantity;
                    if (request.ExpiryDate.HasValue)
                        batch.ExpiryDate = request.ExpiryDate.Value;
                }

                await _dbContext.SaveChangesAsync();

                var inventory = await _dbContext.Inventories.FindAsync(request.ProductId);
                if (inventory == null)
                {
                    inventory = new Inventory
                    {
                        ProductId = request.ProductId,
                        QuantityAvailable = 0,
                        QuantityReserved = 0
                    };
                    _dbContext.Inventories.Add(inventory);
                }

                inventory.QuantityAvailable += request.Quantity;
                inventory.UpdatedAt = DateTime.UtcNow;

                _dbContext.StockMovements.Add(new StockMovement
                {
                    ProductId = request.ProductId,
                    BatchId = batch.BatchId,
                    MovementType = enStockMovementType.purchase,
                    Quantity = request.Quantity,
                    SourceType = request.InvoiceId.HasValue ? "Invoice" : "StockReceive",
                    SourceId = request.InvoiceId,
                    CreatedAt = DateTime.UtcNow
                });

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم استلام المخزون بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشلت عملية استلام المخزون.");
            }
        }

        // ─────────────────────────────────────────────────
        // بيع مخزون: خصم تلقائي حسب FEFO
        // ─────────────────────────────────────────────────
        public async Task<Result> SellStockAsync(int productId, int quantity, int? invoiceId = null)
        {
            if (quantity <= 0)
                return Result.Fail("الكمية يجب أن تكون أكبر من صفر.");

            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            if (product.Type != "stock")
                return Result.Fail("لا يمكن بيع مخزون لمنتج غير مخزني.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var inventory = await _dbContext.Inventories.FindAsync(productId);
                if (inventory == null)
                    return Result.Fail("لا يوجد سجل مخزون لهذا المنتج.");

                int available = (inventory.QuantityAvailable ?? 0) - (inventory.QuantityReserved ?? 0);
                if (available < quantity)
                    return Result.Fail($"المخزون المتاح غير كافٍ للمنتج {product.Name}.");

                var batches = await _dbContext.Batches
                    .Where(b => b.ProductId == productId && b.BatchQuantity > 0)
                    .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                    .ThenBy(b => b.CreatedAt)
                    .ToListAsync();

                int remaining = quantity;
                foreach (var batch in batches)
                {
                    if (remaining <= 0) break;

                    int take = Math.Min(remaining, batch.BatchQuantity);
                    batch.BatchQuantity -= take;
                    remaining -= take;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = productId,
                        BatchId = batch.BatchId,
                        MovementType = enStockMovementType.sale,
                        Quantity = -take,
                        SourceType = invoiceId.HasValue ? "Invoice" : "StockSale",
                        SourceId = invoiceId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                if (remaining > 0)
                    return Result.Fail($"المخزون غير كافٍ لتغطية الطلب للمنتج {product.Name}.");

                inventory.QuantityAvailable -= quantity;
                inventory.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم خصم المخزون بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشلت عملية خصم المخزون.");
            }
        }

        // ─────────────────────────────────────────────────
        // تسوية مخزون يدوية (زيادة أو نقصان)
        // ─────────────────────────────────────────────────
        public async Task<Result> AdjustStockAsync(DtoStockAdjustmentRequest request)
        {
            if (request.Quantity <= 0)
                return Result.Fail("الكمية يجب أن تكون أكبر من صفر.");

            if (string.IsNullOrWhiteSpace(request.Reason))
                return Result.Fail("سبب التسوية مطلوب.");

            var product = await _dbContext.Products.FindAsync(request.ProductId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            if (product.Type != "stock")
                return Result.Fail("لا يمكن تسوية مخزون لمنتج غير مخزني.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var inventory = await _dbContext.Inventories.FindAsync(request.ProductId);
                if (inventory == null)
                {
                    inventory = new Inventory
                    {
                        ProductId = request.ProductId,
                        QuantityAvailable = 0,
                        QuantityReserved = 0
                    };
                    _dbContext.Inventories.Add(inventory);
                }

                int? batchId = null;

                if (request.IsIncrease)
                {
                    if (!string.IsNullOrWhiteSpace(request.BatchNumber))
                    {
                        var batch = await _dbContext.Batches
                            .FirstOrDefaultAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber);

                        if (batch == null)
                        {
                            batch = new Batch
                            {
                                ProductId = request.ProductId,
                                BatchNumber = request.BatchNumber.Trim(),
                                ExpiryDate = request.ExpiryDate,
                                BatchQuantity = request.Quantity,
                                CreatedAt = DateTime.UtcNow
                            };
                            _dbContext.Batches.Add(batch);
                        }
                        else
                        {
                            batch.BatchQuantity += request.Quantity;
                        }

                        await _dbContext.SaveChangesAsync();
                        batchId = batch.BatchId;
                    }

                    inventory.QuantityAvailable += request.Quantity;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = request.ProductId,
                        BatchId = batchId,
                        MovementType = enStockMovementType.adjustment_in,
                        Quantity = request.Quantity,
                        SourceType = "ManualAdjustment",
                        SourceId = null,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    int available = (inventory.QuantityAvailable ?? 0) - (inventory.QuantityReserved ?? 0);
                    if (available < request.Quantity)
                        return Result.Fail($"الكمية المطلوبة أكبر من المخزون المتاح للمنتج {product.Name}.");

                    if (!string.IsNullOrWhiteSpace(request.BatchNumber))
                    {
                        var batch = await _dbContext.Batches
                            .FirstOrDefaultAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber);

                        if (batch == null)
                            return Result.Fail("الدفعة المحددة غير موجودة.");

                        if (batch.BatchQuantity < request.Quantity)
                            return Result.Fail("كمية الدفعة غير كافية.");

                        batch.BatchQuantity -= request.Quantity;
                        batchId = batch.BatchId;
                    }

                    inventory.QuantityAvailable -= request.Quantity;

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = request.ProductId,
                        BatchId = batchId,
                        MovementType = enStockMovementType.adjustment_out,
                        Quantity = -request.Quantity,
                        SourceType = "ManualAdjustment",
                        SourceId = null,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                inventory.UpdatedAt = DateTime.UtcNow;
                inventory.LastCountedAt = DateTime.UtcNow;

                _dbContext.SystemLogs.Add(new SystemLog
                {
                    UserId = request.UserId,
                    Action = "StockAdjustment",
                    Details = $"تسوية مخزون للمنتج '{product.Name}': {(request.IsIncrease ? "زيادة" : "نقصان")} {request.Quantity}. السبب: {request.Reason}",
                    CreatedAt = DateTime.UtcNow
                });

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تمت تسوية المخزون بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشلت عملية تسوية المخزون.");
            }
        }

        // ─────────────────────────────────────────────────
        // نقل كمية بين دفعتين لنفس المنتج
        // ─────────────────────────────────────────────────
        public async Task<Result> TransferBatchAsync(DtoBatchTransferRequest request)
        {
            if (request.Quantity <= 0)
                return Result.Fail("الكمية يجب أن تكون أكبر من صفر.");

            if (string.IsNullOrWhiteSpace(request.TargetBatchNumber))
                return Result.Fail("رقم الدفعة الهدف مطلوب.");

            var sourceBatch = await _dbContext.Batches.FindAsync(request.SourceBatchId);
            if (sourceBatch == null)
                return Result.Fail("الدفعة المصدر غير موجودة.");

            if (sourceBatch.BatchQuantity < request.Quantity)
                return Result.Fail("كمية الدفعة المصدر غير كافية.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var targetBatch = await _dbContext.Batches
                    .FirstOrDefaultAsync(b => b.ProductId == sourceBatch.ProductId && b.BatchNumber == request.TargetBatchNumber);

                if (targetBatch == null)
                {
                    targetBatch = new Batch
                    {
                        ProductId = sourceBatch.ProductId,
                        BatchNumber = request.TargetBatchNumber.Trim(),
                        ExpiryDate = request.TargetExpiryDate ?? sourceBatch.ExpiryDate,
                        BatchQuantity = request.Quantity,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.Batches.Add(targetBatch);
                }
                else
                {
                    if (targetBatch.BatchId == sourceBatch.BatchId)
                        return Result.Fail("لا يمكن نقل الكمية إلى نفس الدفعة.");

                    targetBatch.BatchQuantity += request.Quantity;
                    if (request.TargetExpiryDate.HasValue)
                        targetBatch.ExpiryDate = request.TargetExpiryDate.Value;
                }

                sourceBatch.BatchQuantity -= request.Quantity;

                await _dbContext.SaveChangesAsync();

                _dbContext.StockMovements.Add(new StockMovement
                {
                    ProductId = sourceBatch.ProductId,
                    BatchId = sourceBatch.BatchId,
                    MovementType = enStockMovementType.adjustment_out,
                    Quantity = -request.Quantity,
                    SourceType = "BatchTransfer",
                    SourceId = targetBatch.BatchId,
                    CreatedAt = DateTime.UtcNow
                });

                _dbContext.StockMovements.Add(new StockMovement
                {
                    ProductId = sourceBatch.ProductId,
                    BatchId = targetBatch.BatchId,
                    MovementType = enStockMovementType.adjustment_in,
                    Quantity = request.Quantity,
                    SourceType = "BatchTransfer",
                    SourceId = sourceBatch.BatchId,
                    CreatedAt = DateTime.UtcNow
                });

                _dbContext.SystemLogs.Add(new SystemLog
                {
                    UserId = request.UserId,
                    Action = "BatchTransfer",
                    Details = $"نقل {request.Quantity} من الدفعة '{sourceBatch.BatchNumber}' إلى الدفعة '{targetBatch.BatchNumber}'.",
                    CreatedAt = DateTime.UtcNow
                });

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم نقل الكمية بين الدفعات بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشلت عملية نقل الكمية بين الدفعات.");
            }
        }
    }
}
