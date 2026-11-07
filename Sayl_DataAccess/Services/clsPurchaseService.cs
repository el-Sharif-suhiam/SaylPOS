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
            {
                await clsAudit.LogRejectedAsync(_dbContext, request.UserId, "PurchaseDenied",
                    "فاتورة شراء مرفوضة: تتطلب صلاحية شاشة فواتير الشراء.");
                return Result<int>.Fail("إنشاء فاتورة شراء يتطلب صلاحية شاشة فواتير الشراء.");
            }

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

                decimal discount = Math.Round(request.DiscountAmount, 2, MidpointRounding.AwayFromZero);
                decimal afterDiscount = total - discount;
                decimal tax = request.TaxPercent > 0 ? afterDiscount * request.TaxPercent / 100 : 0;
                decimal net = afterDiscount + tax;

                if (net < 0)
                    throw new InvalidOperationException("صافي فاتورة الشراء لا يمكن أن يكون سالبًا.");

                // الدفع يُقاس على الصافي النهائي (بعد الخصم والضريبة) — لا على
                // الإجمالي الخام: قياسه على الإجمالي يرفض دفعة صحيحة عند وجود
                // ضريبة (دين وهمي) ويقبل دفعة زائدة عند وجود خصم (رصيد مورد فاسد).
                // نفس حساب net أعلاه دون تقريب إضافي — دفعة مساوية للصافي تُقبل.
                if (request.Payment is { } pay && (pay.Amount < 0 || pay.Amount > net))
                    throw new InvalidOperationException("مبلغ دفع الشراء يجب أن يكون بين صفر وصافي الفاتورة (لا دفع زائد).");

                // ══ تخصيص خصم الشراء على البنود (أكبر البواقي — قرش قرشًا) ══
                // سياسة التكلفة: تكلفة الاقتناء التاريخية تُحفظ على الدفعة (Batches.UnitCost)
                // وعلى بند الفاتورة (ItemCost) صافية بعد نصيبها من خصم الفاتورة —
                // فتساوي مخصصات الخصم الخصم الإجمالي بالضبط (لا فلس ضائع/مضاف).
                // Product.CostPrice يبقى سعر آخر شراء للعرض فقط — ليس مصدر COGS.
                var discountAllocations = AllocateDiscountAcrossItems(request.Items, discount);

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

                for (int itemIndex = 0; itemIndex < request.Items.Count; itemIndex++)
                {
                    var item = request.Items[itemIndex];

                    // التكلفة الفعلية للوحدة: التكلفة الخام ناقص نصيب البند من خصم الفاتورة
                    decimal unitEffectiveCost = discountAllocations[itemIndex].EffectiveUnitCost;

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
                            UnitCost = unitEffectiveCost, // تكلفة الاقتناء التاريخية للدفعة
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
                            // دفعة أنشأها طلب متوازٍ: لا دمج بصلاحية مختلفة (نفس قاعدة الدمج أعلاه)
                            if (item.ExpiryDate.HasValue && item.ExpiryDate.Value != batch.ExpiryDate)
                                throw new InvalidOperationException(
                                    $"لا يمكن دمج الكمية: رقم الدفعة '{item.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{(batch.ExpiryDate.HasValue ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd") : "(بدون)")}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {item.ExpiryDate.Value:yyyy-MM-dd}). استخدم رقم دفعة جديدًا.");
                            batch.BatchQuantity += item.Quantity;
                            // دفعة أنشأها طلب متوازٍ: وسّط بالوزن إن كان لها تكلفة مختلفة
                            batch.UnitCost = batch.UnitCost is null || batch.BatchQuantity <= item.Quantity
                                ? unitEffectiveCost
                                : Math.Round((batch.UnitCost.Value * (batch.BatchQuantity - item.Quantity)
                                              + unitEffectiveCost * item.Quantity) / batch.BatchQuantity, 2, MidpointRounding.AwayFromZero);
                        }
                    }
                    else
                    {
                        // دمج في دفعة قائمة: صلاحية الدفعة القائمة لا تُمس أبدًا —
                        // توريد نفس رقم الدفعة بصلاحية مختلفة يُرفض (يتوافق مع TransferBatchAsync)،
                        // فتغيير الصلاحية يفسد تاريخ المخزون القائم ويخلط منتهيًا بصالح فيكذب FEFO.
                        if (item.ExpiryDate.HasValue && item.ExpiryDate.Value != batch.ExpiryDate)
                            throw new InvalidOperationException(
                                $"لا يمكن دمج الكمية: رقم الدفعة '{item.BatchNumber.Trim()}' موجود بتاريخ صلاحية '{(batch.ExpiryDate.HasValue ? batch.ExpiryDate.Value.ToString("yyyy-MM-dd") : "(بدون)")}' — لا يمكن دمج كميات بتواريخ صلاحية مختلفة (الجديد: {item.ExpiryDate.Value:yyyy-MM-dd}). استخدم رقم دفعة جديدًا.");

                        // زيادة ذرية على مستوى القاعدة — لا يمكن أن تضيع مع فاتورة شراء متوازية
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE Batches SET BatchQuantity = BatchQuantity + {item.Quantity} WHERE BatchId = {batch.BatchId}");

                        // دمج في دفعة قائمة: تكلفة الدفعة تتوسط بالوزن (الكمية القديمة بتكلفتها
                        // التاريخية + الكمية الجديدة بتكلفتها الفعلية) — الإجمالي يظل مطابقًا
                        // لما دُفع فعلًا عبر عمر الدفعة. UPDATE خام بعد الزيادة أعلاه،
                        // والخصائص المتتبعة لا تُمس حتى لا يكتبها SaveChanges فوقها.
                        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE Batches
SET UnitCost = CASE
                   WHEN UnitCost IS NULL OR BatchQuantity <= {item.Quantity} THEN {unitEffectiveCost}
                   ELSE ROUND((UnitCost * (BatchQuantity - {item.Quantity}) + {unitEffectiveCost} * {item.Quantity}) / BatchQuantity, 2)
               END
WHERE BatchId = {batch.BatchId}");
                    }

                    await _dbContext.SaveChangesAsync();

                    _dbContext.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = invoice.InvoiceId,
                        ProductId = item.ProductId,
                        Quantity = item.Quantity,
                        BatchId = batch.BatchId,
                        // السعر الخام كما جاء في الطلب (التاريخ) — التكلفة صافية بعد الخصم
                        ItemPrice = item.UnitCost,
                        ItemCost = unitEffectiveCost
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
                        // تثبيت سجل المخزون قبل الزيادة الذرية — UPDATE الخام
                        // ينفذ فورًا على القاعدة، فلو نُفذ قبل وجود الصف أثّر
                        // على صفر صفوف وهدرت كمية الشراء من المخزون الكلي
                        // (الدفعة تحملها والمخزون الكلي لا يراها).
                        await _dbContext.SaveChangesAsync();
                    }

                    // زيادة ذرية للمخزون — محصّنة من فاتورة/استلام متوازٍ
                    // GETUTCDATE لا GETDATE: كل كتابة Inventory.UpdatedAt بمنطقة زمنية واحدة (UTC)
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {item.Quantity}, UpdatedAt = GETUTCDATE() WHERE ProductId = {item.ProductId}");

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
                    product.CostPrice = unitEffectiveCost;
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

        // ═══ تخصيص خصم الشراء على البنود — أكبر البواقي، قرش قرشًا ═══
        // نصيب كل بند من الخصم يتناسب مع قيمته الخام (UnitCost×Quantity)،
        // والبواقي الصغرى (فلسات) توزع على أكبر البنود نسبيًا حتى يساوي
        // مجموع المخصصات الخصم الإجمالي بالضبط — لا فرق تقريب مهما كان
        // عدد البنود. فاتورة بلا خصم = أصفار. (public static — دالة حسابية
        // نقية يختبرها الاختبار مباشرة.)
        public static Dictionary<int, (decimal AllocatedDiscount, decimal EffectiveUnitCost)> AllocateDiscountAcrossItems(
            List<DtoPurchaseItem> items, decimal discount)
        {
            var result = new Dictionary<int, (decimal, decimal)>();
            if (discount <= 0m)
            {
                for (int i = 0; i < items.Count; i++)
                    result[i] = (0m, items[i].UnitCost);
                return result;
            }

            decimal total = items.Sum(i => i.UnitCost * i.Quantity);
            if (total <= 0m)
            {
                for (int i = 0; i < items.Count; i++)
                    result[i] = (0m, items[i].UnitCost);
                return result;
            }

            // الأساس: نصيب كل بند مقطوعًا إلى قروش (لأسفل) — والباقي يوزع قرشًا قرشًا
            long totalCents = (long)Math.Round(discount * 100m, 0, MidpointRounding.AwayFromZero);
            var shares = new List<(int ProductId, long BaseCents, decimal Remainder, decimal Gross, int Index)>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                decimal gross = item.UnitCost * item.Quantity;
                decimal exact = discount * gross / total;
                long baseCents = (long)Math.Floor(exact * 100m);
                shares.Add((item.ProductId, baseCents, exact * 100m - baseCents, gross, i));
            }

            long allocatedCents = shares.Sum(s => s.BaseCents);
            long leftover = totalCents - allocatedCents;

            var byLeftover = shares
                .OrderByDescending(s => s.Remainder)
                .ThenByDescending(s => s.Gross)
                .ThenBy(s => s.Index)
                .ToList();

            var extra = new Dictionary<int, long>();
            for (int i = 0; i < byLeftover.Count && leftover > 0; i++, leftover--)
                extra[byLeftover[i].Index] = 1;

            foreach (var s in shares)
            {
                var item = items[s.Index];
                decimal lineDiscount = (s.BaseCents + extra.GetValueOrDefault(s.Index)) / 100m;
                decimal lineNet = s.Gross - lineDiscount;
                decimal unitCost = item.Quantity > 0
                    ? Math.Round(lineNet / item.Quantity, 2, MidpointRounding.AwayFromZero)
                    : item.UnitCost;

                result[s.Index] = (lineDiscount, unitCost);
            }

            return result;
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
