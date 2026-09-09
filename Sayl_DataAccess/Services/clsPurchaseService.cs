using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Common.Utils;
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
        // بوابة بتات خدمية — فاتورة الشراء تحرك مالًا (رصيد المورد)
        // ومخزونًا وسعر تكلفة؛ تثق الواجهة وحدها لا يكفي (الشراء قرار
        // مكتب خلفي). المطلوب: شاشة فواتير الشراء (بت 32).
        // ─────────────────────────────────────────────────
        public async Task<Result<int>> CreatePurchaseInvoiceAsync(DtoPurchaseRequest request)
        {
            if (request.Items == null || !request.Items.Any())
                return Result<int>.Fail("يجب إضافة بنود إلى فاتورة الشراء.");

            int creatorRole = await GetUserRoleValueAsync(request.UserId);
            if (!AppPermissions.Has(creatorRole, AppPermissions.PurchaseInvoices))
                return Result<int>.Fail("إنشاء فاتورة شراء يتطلب صلاحية شاشة فواتير الشراء.");

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

                // ══ VULN-06: حراس المبالغ (حدود الخدمة) ══
                // خصم شراء يتجاوز الإجمالي يجعل الصافي سالبًا فيُخصم من رصيد
                // المورد بلا حد (Balance += سالب ضخم) — المورد «يستحق علينا»
                // وهمًا ثم يستحيل سداده. والدفع الزائد يقلب الرصيد سالبًا كذلك.
                if (request.DiscountAmount < 0 || request.DiscountAmount > total)
                    throw new InvalidOperationException("مبلغ خصم الشراء لا يمكن أن يتجاوز إجمالي الفاتورة أو يكون سالبًا.");
                if (request.TaxPercent < 0 || request.TaxPercent > 100)
                    throw new InvalidOperationException("نسبة ضريبة الشراء يجب أن تكون بين 0 و 100.");
                if (request.Payment is { } pay && (pay.Amount < 0 || pay.Amount > total))
                    throw new InvalidOperationException("مبلغ دفع الشراء يجب أن يكون بين صفر وإجمالي الفاتورة (لا دفع زائد).");

                decimal discount = request.DiscountAmount;
                decimal afterDiscount = total - discount;
                decimal tax = request.TaxPercent > 0 ? afterDiscount * request.TaxPercent / 100 : 0;
                decimal net = afterDiscount + tax;

                if (net < 0)
                    throw new InvalidOperationException("صافي فاتورة الشراء لا يمكن أن يكون سالبًا.");

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
                        // فهرس UQ_Batches_Product_Batch يمنع إنشاء دفعة مكررة عند طلبين متوازيين —
                        // الالتقاط أدناه يحوله إلى زيادة على الدفعة الموجودة بدل فشل غامض
                        try
                        {
                            await _dbContext.SaveChangesAsync();
                        }
                        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_Batches_Product_Batch"))
                        {
                            _dbContext.ChangeTracker.Clear();
                            batch = await _dbContext.Batches
                                .FirstAsync(b => b.ProductId == item.ProductId && b.BatchNumber == item.BatchNumber.Trim());
                            batch.BatchQuantity += item.Quantity;
                        }
                    }
                    else
                    {
                        // زيادة ذرية على مستوى القاعدة — لا يمكن أن تضيع مع فاتورة شراء متوازية
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE Batches SET BatchQuantity = BatchQuantity + {item.Quantity} WHERE BatchId = {batch.BatchId}");
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

                    // زيادة ذرية للمخزون — محصّنة من فاتورة/استلام متوازٍ
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {item.Quantity}, UpdatedAt = GETDATE() WHERE ProductId = {item.ProductId}");

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

                // زيادة ذرية لرصيد المورد — محصّنة من سداد متوازٍ
                decimal paidAmount = request.Payment?.Amount ?? 0;
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Suppliers SET Balance = Balance + {net - paidAmount} WHERE PartyId = {request.SupplierId}");

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result<int>.Ok(invoice.InvoiceId, "تم إنشاء فاتورة الشراء بنجاح.");
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                // رسائل حراس المبالغ تصل للمستخدم كما هي — لا تُبتلع
                return Result<int>.Fail(ex.Message);
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشل إنشاء فاتورة الشراء.");
            }
        }

        /// <summary>هل الاستثناء نتيجة انتهاك قيد فريد (فهرس/مفتاح فريد)؟ يُستخدم لالتقاط الدفعات المتنافسة.</summary>
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

        /// <summary>قيمة دور المستخدم (VULN-26 — بوابة الشراء الخدمية).</summary>
        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return 0;
            var user = await _dbContext.Users.Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            return user?.Role.RoleValue ?? 0;
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
