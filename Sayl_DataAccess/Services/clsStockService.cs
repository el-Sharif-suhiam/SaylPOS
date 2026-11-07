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
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "StockReceiveDenied",
                    "استلام مخزون مرفوض: يتطلب صلاحية شاشة المخزون.");
                return Result.Fail("استلام المخزون يتطلب صلاحية شاشة المخزون.");
            }

            if (request.Quantity <= 0)
                return Result.Fail("الكمية يجب أن تكون أكبر من صفر.");

            if (request.UnitCost is < 0)
                return Result.Fail("سعر التكلفة يجب أن يكون صفرا أو أكثر.");

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
                        UnitCost = request.UnitCost, // تكلفة الاقتناء التاريخية إن وُجدت
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
                        // دفعة أنشأها طلب متوازٍ: لا دمج بصلاحية مختلفة (نفس قاعدة الدمج أعلاه)
                        if (request.ExpiryDate.HasValue && request.ExpiryDate.Value != batch.ExpiryDate)
                        {
                            await transaction.RollbackAsync();
                            string existingStr = batch.ExpiryDate.HasValue
                                ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd")
                                : "(بدون)";
                            string newStr = request.ExpiryDate.Value.ToString("yyyy-MM-dd");
                            return Result.Fail(
                                $"لا يمكن دمج الكمية: رقم الدفعة '{request.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{existingStr}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {newStr}). استخدم رقم دفعة جديدًا.");
                        }
                        // زيادة ذرية — محصّنة من استلام متوازٍ
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                        // دفعة أنشأها طلب متوازٍ: نفس قاعدة الدمج — متوسط مرجح بالوزن
                        // إن كان لها تكلفة مختلفة، لا تطميس ولا تكلفة مُخترعة
                        if (request.UnitCost.HasValue)
                            await MergeReceiveCostAsync(batch.BatchId, request.UnitCost.Value, request.Quantity);
                    }
                }
                else
                {
                    // دفعة قائمة: تاريخ صلاحية الدفعة القائمة لا يُمس أبدًا —
                    // توريد نفس رقم الدفعة بصلاحية مختلفة يُرفض (يتوافق مع TransferBatchAsync).
                    // الدمج المتوافق يزيد الكمية ويوسّط التكلفة بالوزن (أدناه).
                    if (request.ExpiryDate.HasValue && request.ExpiryDate.Value != batch.ExpiryDate)
                    {
                        await transaction.RollbackAsync();
                        string existingStr = batch.ExpiryDate.HasValue
                            ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd")
                            : "(بدون)";
                        string newStr = request.ExpiryDate.Value.ToString("yyyy-MM-dd");
                        return Result.Fail(
                            $"لا يمكن دمج الكمية: رقم الدفعة '{request.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{existingStr}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {newStr}). استخدم رقم دفعة جديدًا.");
                    }
                    // زيادة ذرية — محصّنة من استلام متوازٍ
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                    // دمج في دفعة قائمة: التكلفة تتوسط بالوزن بالكمية (نفس معادلة
                    // دمج الشراء ودمج النقل — COSTING.md) فيظل إجمالي تكلفة الدفعة
                    // مطابقًا لما اقتُني فعليًا عبر عمر الدفعة. الاستلام اليدوي بلا
                    // تكلفة لا يُطمس تكلفة القائمة ولا يخترع بديلًا.
                    if (request.UnitCost.HasValue)
                        await MergeReceiveCostAsync(batch.BatchId, request.UnitCost.Value, request.Quantity);
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
                    $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {request.Quantity}, UpdatedAt = GETUTCDATE() WHERE ProductId = {request.ProductId}");

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

                // FEFO — مع استبعاد الدفعات المنتهية الصلاحية والمحجوبة (حجر)
                // VULN-45: اليوم بتوقيت البرنامج (UTC+3) لا التاريخ UTC الخام —
                // وإلا بقيت دفعة منتهية أمس قابلة للبيع حتى 03:00 فجرًا.
                DateOnly today = ProgramClock.Today;
                var batches = await _dbContext.Batches
                    .Where(b => b.ProductId == productId && b.BatchQuantity > 0
                             && !b.IsQuarantined
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
            {
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "StockAdjustmentDenied",
                    "تسوية مخزون مرفوضة: تتطلب صلاحية شاشة المخزون.");
                return Result.Fail("تسوية المخزون تتطلب صلاحية شاشة المخزون.");
            }

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

                if (request.IsIncrease)
                {
                    int? batchId = null;

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
                                // دفعة أنشأها طلب متوازٍ: لا دمج بصلاحية مختلفة (نفس قاعدة الدمج أعلاه)
                                if (request.ExpiryDate.HasValue && request.ExpiryDate.Value != batch.ExpiryDate)
                                {
                                    await transaction.RollbackAsync();
                                    string existingStr = batch.ExpiryDate.HasValue
                                        ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd")
                                        : "(بدون)";
                                    string newStr = request.ExpiryDate.Value.ToString("yyyy-MM-dd");
                                    return Result.Fail(
                                        $"لا يمكن دمج الكمية: رقم الدفعة '{request.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{existingStr}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {newStr}). استخدم رقم دفعة جديدًا.");
                                }
                                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                                    $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                            }
                        }
                        else
                        {
                            // دفعة قائمة: تاريخ صلاحية الدفعة القائمة لا يُمس —
                            // تسوية بصلاحية مختلفة تُرفض (يتوافق مع TransferBatchAsync).
                            if (request.ExpiryDate.HasValue && request.ExpiryDate.Value != batch.ExpiryDate)
                            {
                                await transaction.RollbackAsync();
                                string existingStr = batch.ExpiryDate.HasValue
                                    ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd")
                                    : "(بدون)";
                                string newStr = request.ExpiryDate.Value.ToString("yyyy-MM-dd");
                                return Result.Fail(
                                    $"لا يمكن دمج الكمية: رقم الدفعة '{request.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{existingStr}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {newStr}). استخدم رقم دفعة جديدًا.");
                            }
                            // زيادة ذرية — محصّنة من تسوية/استلام متوازٍ
                            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                                $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {batch.BatchId}");
                        }

                        batchId = batch.BatchId;
                    }
                    else
                    {
                        // زيادة بلا رقم دفعة — نصنع دفعة تجميعية تلقائيًا:
                        // الخصم يمر عبر الدفعات حصرًا (FEFO)، ومخزون بلا دفعة
                        // يظل «غير قابل للبيع» رغم ظهوره في Inventory (خطأ
                        // خبز البرجر الحي: تسوية +300 بلا دفعة = رفض بيع).
                        // الدفعة التجميعية بلا انتهاء تُبقي المنتج قابلًا للبيع
                        // وتحفظ التوازن: مجموع الدفعات = المتاح دائمًا.
                        var autoBatch = new Batch
                        {
                            ProductId = request.ProductId,
                            BatchNumber = $"ADJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                            ExpiryDate = request.ExpiryDate,
                            BatchQuantity = request.Quantity,
                            CreatedAt = DateTime.UtcNow
                        };
                        _dbContext.Batches.Add(autoBatch);
                        await _dbContext.SaveChangesAsync(); // توليد BatchId قبل الحركة
                        batchId = autoBatch.BatchId;
                    }

                    // زيادة ذرية للمخزون
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {request.Quantity}, UpdatedAt = GETUTCDATE(), LastCountedAt = GETUTCDATE() WHERE ProductId = {request.ProductId}");

                    _dbContext.StockMovements.Add(new StockMovement
                    {
                        ProductId = request.ProductId,
                        BatchId = batchId,
                        MovementType = enStockMovementType.adjustment_in,
                        Quantity = request.Quantity,
                        SourceType = "ManualAdjustment",
                        SourceId = request.SourceId, // StockCount idempotency: يربط الحركة بسطر الجرد
                        CreatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    // (BatchId, Quantity) لكل دفعة خُصمت فعلًا — حركة لكل منها أدناه
                    var deductions = new List<(int? BatchId, int Quantity)>();

                    // إعادة-فحص داخل المعاملة بعد قفل النطاق
                    int available = (inventory.QuantityAvailable ?? 0) - (inventory.QuantityReserved ?? 0);
                    if (available < request.Quantity)
                    {
                        // تدقيق الرفض — كتابة مستقلة تنجو من تراجع المعاملة
                        await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "StockAdjustmentRejected",
                            $"تسوية مرفوضة: الكمية {request.Quantity} أكبر من المتاح ({available}) للمنتج '{product.Name}'.");
                        return Result.Fail($"الكمية المطلوبة أكبر من المخزون المتاح للمنتج {product.Name}.");
                    }

                    if (!string.IsNullOrWhiteSpace(request.BatchNumber))
                    {
                        var batch = await _dbContext.Batches
                            .FirstOrDefaultAsync(b => b.ProductId == request.ProductId && b.BatchNumber == request.BatchNumber);

                        if (batch == null)
                            return Result.Fail("الدفعة المحددة غير موجودة.");

                        if (batch.BatchQuantity < request.Quantity)
                            return Result.Fail("كمية الدفعة غير كافية.");

                        batch.BatchQuantity -= request.Quantity;
                        deductions.Add((batch.BatchId, request.Quantity));
                    }
                    else
                    {
                        // النقصان بلا رقم دفعة يمر عبر الدفعات حصرًا (FEFO) — خصم
                        // Inventory وحده يكسر المعادلة «مجموع الدفعات = المتاح»
                        // فيخلق مخزونًا شبحًا على الورق بلا دفعة يحمله (المطلوب:
                        // كل زيادة/نقصان تحرك الدفعة والمخزون معًا). نقص أولاً من
                        // الأقرب انتهاءً ثم المحجوبة — عجز الكل = رفض.
                        DateOnly today = ProgramClock.Today; /* VULN-45: يوم البرنامج لا التاريخ UTC الخام */
                        var batches = await _dbContext.Batches
                            .Where(b => b.ProductId == request.ProductId && b.BatchQuantity > 0)
                            .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                            .ThenBy(b => b.IsQuarantined ? 0 : 1)
                            .ThenBy(b => b.CreatedAt)
                            .ToListAsync();

                        int remaining = request.Quantity;
                        foreach (var b in batches)
                        {
                            if (remaining <= 0) break;
                            int take = Math.Min(remaining, b.BatchQuantity);
                            b.BatchQuantity -= take;
                            remaining -= take;
                            deductions.Add((b.BatchId, take));
                        }

                        if (remaining > 0)
                        {
                            await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "StockAdjustmentRejected",
                                $"تسوية نقصان مرفوضة: المخزون غير كافٍ لتغطية {request.Quantity} عبر الدفعات للمنتج '{product.Name}'.");
                            return Result.Fail($"المخزون غير كافٍ لتغطية التسوية للمنتج {product.Name}.");
                        }
                    }

                    inventory.QuantityAvailable -= request.Quantity;

                    // حركة لكل دفعة خُصمت فعلًا (نفس نمط SellStockAsync): كل حركة
                    // تحمل رقم دفعتها وكميتها الفعلية، ومجموع الحركات = النقصان
                    // الكلي — التاريخ يعكس أي دفعة غذّت التسوية بدل حركة واحدة
                    // تُسقَط بكمية كاملة على آخر دفعة
                    foreach (var d in deductions)
                    {
                        _dbContext.StockMovements.Add(new StockMovement
                        {
                            ProductId = request.ProductId,
                            BatchId = d.BatchId,
                            MovementType = enStockMovementType.adjustment_out,
                            Quantity = -d.Quantity,
                            SourceType = "ManualAdjustment",
                            SourceId = request.SourceId, // StockCount idempotency: يربط الحركة بسطر الجرد
                            CreatedAt = DateTime.UtcNow
                        });
                    }
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
                        // الكمية المنقولة تحمل تكلفتها التاريخية (COSTING.md: التكلفة من
                        // الدفعة المستهلكة فعليًا) — نهايتها الجديدة بلا تكلفة تجعل بيعها
                        // يرجع إلى Product.CostPrice ويشوّه COGS والتقييم
                        UnitCost = sourceBatch.UnitCost,
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
                        // دفعة أنشأها طلب متوازٍ قد تحمل تكلفة خاصة بها — الدمج
                        // يتوسط بالوزن (نفس معادلة دمج الشراء) لا يطمسها
                        await MergeTransferCostAsync(targetBatch.BatchId, sourceBatch.UnitCost, request.Quantity);
                    }
                }
                else
                {
                    if (targetBatch.BatchId == sourceBatch.BatchId)
                        return Result.Fail("لا يمكن نقل الكمية إلى نفس الدفعة.");

                    // دمج دفعات غير متوافقة ممنوع: الصلاحية الفعلية للكمية المنقولة
                    // (الصريحة من الطلب، وإلا صلاحية المصدر) يجب أن تطابق صلاحية
                    // الدفعة الهدف القائمة — تغيير صلاحية مخزون موجود يفسد تاريخ
                    // الدفعة ويخلط منتهيًا بصالح فيكذب FEFO بعدها. النقل إلى صلاحية
                    // مختلفة يتطلب رقم دفعة جديدًا.
                    DateOnly? requestedExpiry = request.TargetExpiryDate ?? sourceBatch.ExpiryDate;
                    if (targetBatch.ExpiryDate != requestedExpiry)
                        return Result.Fail("لا يمكن دمج دفعات بتواريخ صلاحية مختلفة — استخدم رقم دفعة جديدًا.");

                    // المخزون المحجوب لا يذوب صامتًا في مخزون صالح: الدمج في دفعة
                    // قائمة يتطلب تطابق حالة الحجر. إفراج محجوب = نقل صريح إلى
                    // دفعة جديدة بصلاحية معتمدة (يُسجل في سجل النظام).
                    if (targetBatch.IsQuarantined != sourceBatch.IsQuarantined)
                        return Result.Fail("لا يمكن دمج مخزون محجوب مع مخزون صالح — استخدم رقم دفعة جديدًا.");

                    // زيادة ذرية للدفعة الهدف — محصّنة من استلام/نقل متوازٍ
                    // (صلاحية الدفعة القائمة لا تُمس أبدًا)
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Batches SET BatchQuantity = BatchQuantity + {request.Quantity} WHERE BatchId = {targetBatch.BatchId}");

                    // دمج في دفعة قائمة: التكلفة تتوسط بالوزن بالكمية (نفس معادلة
                    // دمج الشراء — COSTING.md) فيظل إجمالي تكلفة الدفعة مطابقًا
                    // لتكلفة ما اقتُني فعليًا عبر عمر الدفعة
                    await MergeTransferCostAsync(targetBatch.BatchId, sourceBatch.UnitCost, request.Quantity);
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

        /// <summary>دمج تكلفة الكمية المستلامة يدويًا في الدفعة — متوسط مرجح بالكمية،
        /// نفس معادلة دمج الشراء في clsPurchaseService ودمج النقل في
        /// MergeTransferCostAsync (COSTING.md: «إجمالي الدفعة يظل مطابقًا لما دُفع
        /// فعليًا»). يُستدعى بعد زيادة BatchQuantity بالكمية المستلمة، فالقسمة على
        /// BatchQuantity الحالي تعيد القسمة القديمة. UPDATE خام بعد الزيادة،
        /// والخصائص المتتبعة لا تُمس حتى لا يكتبها SaveChanges فوقها.
        /// قواعد null (لا تُخترع تكلفة بديل ولا Product.CostPrice):
        /// - تكلفة الاستلام null: لا يُستدعى أصلًا — تكلفة الدفعة القائمة تبقى كما هي.
        /// - تكلفة الدفعة null: تُملأ بتكلفة الاستلام — أول تكلفة صريحة تملؤها.
        /// - دفعة قائمة فارغة (كمية 0 قبل الزيادة): كل وحداتها من الاستلام → تكلفة الاستلام.</summary>
        private async Task MergeReceiveCostAsync(int batchId, decimal incomingUnitCost, int incomingQuantity)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE Batches
SET UnitCost = CASE
                   WHEN UnitCost IS NULL OR BatchQuantity <= {incomingQuantity} THEN {incomingUnitCost}
                   ELSE ROUND((UnitCost * (BatchQuantity - {incomingQuantity}) + {incomingUnitCost} * {incomingQuantity}) / BatchQuantity, 2)
               END
WHERE BatchId = {batchId}");
        }

        /// <summary>دمج تكلفة الكمية المنقولة في الدفعة الهدف — متوسط مرجح بالكمية،
        /// نفس معادلة دمج الشراء في clsPurchaseService (COSTING.md: «إجمالي الدفعة
        /// يظل مطابقًا لما دُفع فعليًا»). يُستدعى بعد زيادة BatchQuantity بالكمية
        /// المنقولة، فالقسمة على BatchQuantity الحالي تعيد القسمة القديمة.
        /// UPDATE خام بعد الزيادة، والخصائص المتتبعة لا تُمس حتى لا يكتبها
        /// SaveChanges فوقها.
        /// قواعد null (لا تُخترع تكلفة بديل ولا Product.CostPrice):
        /// - تكلفة المصدر null: لا شيء يُكتب — تكلفة الهدف القائمة تبقى كما هي
        ///   (والهدف القديم بلا تكلفة يظل بلا تكلفة).
        /// - تكلفة الهدف null: تُملأ بتكلفة المصدر — أول تكلفة صريحة تملؤها
        ///   (نفس قاعدة ReceiveStockAsync)، والكمية المنقولة تحملها فعلًا.
        /// - دفعة هدف فارغة (كمية 0): كل وحداتها منقولة من المصدر → تكلفة المصدر.</summary>
        private async Task MergeTransferCostAsync(int targetBatchId, decimal? sourceUnitCost, int transferredQuantity)
        {
            if (sourceUnitCost is null)
                return;

            await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE Batches
SET UnitCost = CASE
                   WHEN UnitCost IS NULL OR BatchQuantity <= {transferredQuantity} THEN {sourceUnitCost.Value}
                   ELSE ROUND((UnitCost * (BatchQuantity - {transferredQuantity}) + {sourceUnitCost.Value} * {transferredQuantity}) / BatchQuantity, 2)
               END
WHERE BatchId = {targetBatchId}");
        }
    }
}
