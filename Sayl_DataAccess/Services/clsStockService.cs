using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Common.Utils;
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

        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return -1;

            return await _dbContext.Users
                .Where(u => u.UserId == userId && u.IsActive && u.Role.IsActive)
                .Select(u => (int?)u.Role.RoleValue)
                .FirstOrDefaultAsync() ?? -1;
        }

        // ─────────────────────────────────────────────────
        // استلام مخزون: إنشاء/زيادة دفعة وتحديث المخزون
        // ─────────────────────────────────────────────────
        public async Task<Result> ReceiveStockAsync(DtoStockReceiveRequest request, int actorUserId = 0)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Inventory))
                return Result.Fail("استلام المخزون يتطلب صلاحية شاشة المخزون.");

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
                    // فهرس UQ_Batches_Product_Batch يمنع التكرار المتوازي — نلتقطه ونزيد الدفعة الموجودة
                    try
                    {
                        await _dbContext.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_Batches_Product_Batch"))
                    {
                        _dbContext.ChangeTracker.Clear();
                        batch = await _dbContext.Batches
                            .FirstAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber.Trim());
                        // زيادة ذرية — محصّنة من استلام متوازٍ
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                    }
                }
                else
                {
                    // زيادة ذرية — محصّنة من استلام متوازٍ
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
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
                    await _dbContext.SaveChangesAsync(); // تثبيت سجل المخزون قبل الزيادة الذرية
                }

                // زيادة ذرية للمخزون — محصّنة من فاتورة شراء/استلام متوازٍ
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {request.Quantity}, UpdatedAt = GETDATE() WHERE ProductId = {request.ProductId}");

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

        /// <summary>هل الاستثناء نتيجة انتهاك قيد فريد؟</summary>
        private static bool IsUniqueConstraintViolation(DbUpdateException ex, string constraintName)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is Microsoft.Data.SqlClient.SqlException sql
                    && sql.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var inventory = await _dbContext.Inventories.FindAsync(productId);
                if (inventory == null)
                    return Result.Fail("لا يوجد سجل مخزون لهذا المنتج.");

                // إعادة-فحص داخل المعاملة بعد قفل النطاق — يمنع بيعين متوازيين يتجاوزان المتاح
                int available = (inventory.QuantityAvailable ?? 0) - (inventory.QuantityReserved ?? 0);
                if (available < quantity)
                    return Result.Fail($"المخزون المتاح غير كافٍ للمنتج {product.Name}.");

                // FEFO — مع استبعاد الدفعات المنتهية الصلاحية
                DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
                var batches = await _dbContext.Batches
                    .Where(b => b.ProductId == productId && b.BatchQuantity > 0
                             && (b.ExpiryDate == null || b.ExpiryDate >= today))
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

            // التسوية اليدوية للمخزون تتطلب شاشة المخزون (بت 2)
            int actorRole = await GetUserRoleValueAsync(request.UserId);
            if (!AppPermissions.Has(actorRole, AppPermissions.Inventory))
                return Result.Fail("تسوية المخزون تتطلب صلاحية شاشة المخزون.");

            var product = await _dbContext.Products.FindAsync(request.ProductId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            if (product.Type != "stock")
                return Result.Fail("لا يمكن تسوية مخزون لمنتج غير مخزني.");

            // Serializable: التسوية قد تنقص مخزونًا — حماية من بيع/تسوية متوازية تتجاوز المتاح
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
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
                    await _dbContext.SaveChangesAsync(); // تثبيت السجل قبل الزيادة الذرية/الفحص
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
                            // التقاط الدفعة المتنافسة (فهرس فريد) وتحويلها إلى زيادة
                            try
                            {
                                await _dbContext.SaveChangesAsync();
                            }
                            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_Batches_Product_Batch"))
                            {
                                _dbContext.ChangeTracker.Clear();
                                inventory = await _dbContext.Inventories.FindAsync(request.ProductId);
                                batch = await _dbContext.Batches
                                    .FirstAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber.Trim());
                                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                                    $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                            }
                        }
                        else
                        {
                            // زيادة ذرية — محصّنة من تسوية/استلام متوازٍ
                            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                                $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                        }

                        batchId = batch.BatchId;
                    }

                    // زيادة ذرية للمخزون
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {request.Quantity}, UpdatedAt = GETDATE(), LastCountedAt = GETDATE() WHERE ProductId = {request.ProductId}");

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
                    // إعادة-فحص داخل المعاملة بعد قفل النطاق
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

                if (!request.IsIncrease)
                {
                    inventory.UpdatedAt = DateTime.UtcNow;
                    inventory.LastCountedAt = DateTime.UtcNow;
                }

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

            // Serializable: فحص كمية المصدر وإعادة-فحصها داخل المعاملة —
            // يمنع نقلين متوازيين يتجاوزان كمية الدفعة المصدر
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                if (sourceBatch.BatchQuantity < request.Quantity)
                    return Result.Fail("كمية الدفعة المصدر غير كافية.");

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
                    // التقاط دفعة هدف مُنشأة بالتوازي (فهرس فريد) وتحويلها إلى زيادة
                    try
                    {
                        await _dbContext.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_Batches_Product_Batch"))
                    {
                        _dbContext.ChangeTracker.Clear();
                        sourceBatch = await _dbContext.Batches.FindAsync(request.SourceBatchId);
                        if (sourceBatch == null)
                            return Result.Fail("الدفعة المصدر غير موجودة.");
                        if (sourceBatch.BatchQuantity < request.Quantity)
                            return Result.Fail("كمية الدفعة المصدر غير كافية.");
                        targetBatch = await _dbContext.Batches
                            .FirstAsync(b => b.ProductId == sourceBatch.ProductId && b.BatchNumber == request.TargetBatchNumber.Trim());
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {targetBatch.BatchId}");
                    }
                }
                else
                {
                    if (targetBatch.BatchId == sourceBatch.BatchId)
                        return Result.Fail("لا يمكن نقل الكمية إلى نفس الدفعة.");

                    // زيادة ذرية للدفعة الهدف — محصّنة من استلام/نقل متوازٍ
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {targetBatch.BatchId}");
                    if (request.TargetExpiryDate.HasValue)
                        targetBatch.ExpiryDate = request.TargetExpiryDate.Value;
                }

                // خصم ذري من المصدر بشرط الكمية — لا يمكن أن يتجاوز المتاح حتى مع تزامن كامل
                int deducted = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Batches SET BatchQuantity = BatchQuantity - {request.Quantity} WHERE BatchId = {sourceBatch.BatchId} AND BatchQuantity >= {request.Quantity}");
                if (deducted == 0)
                {
                    await transaction.RollbackAsync();
                    return Result.Fail("كمية الدفعة المصدر غير كافية.");
                }

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
