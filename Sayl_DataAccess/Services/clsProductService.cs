using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsProductService
    {
        private readonly SaylDbContext _dbContext;

        public clsProductService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        private static readonly string[] ValidTypes = { "stock", "service", "manufactured" };

        public async Task<List<DtoProduct>> GetProductsListAsync(string search = "", string? type = null, bool includeInactive = false, int take = 0)
        {
            var query = _dbContext.Products.AsQueryable();

            if (!includeInactive)
                query = query.Where(p => p.IsActive);

            if (!string.IsNullOrWhiteSpace(type))
                query = query.Where(p => p.Type == type.ToLower());

            if (!string.IsNullOrWhiteSpace(search))
            {
                // VULN-28: أحرف البدل % و _ تُهرَّب قبل Contains — كانت تقلب
                // البحث إلى LIKE '%\%%' فتطابق كل الكتالوج (DoS بحث الكاشير
                // مع استعلامات النقل الكامل أدناه).
                string like = search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
                query = query.Where(p => p.Name.Contains(like) || (p.Barcode != null && p.Barcode.Contains(like)));
            }

            // VULN-38: حد اختياري على مستوى SQL — كان الاستدعاء الحي في POS
            // يجلب الكتالوج كاملًا ثم Take(8) في الذاكرة (نقل كامل عبر السلك
            // كل 250ms من الكتابة الحية). الاستدعاءات الأخرى بلا حد = سلوكها كما كان.
            if (take > 0)
                query = query.Take(take);

            return await query
                .OrderBy(p => p.Name)
                .Select(p => new DtoProduct
                {
                    ProductId = p.ProductId,
                    Name = p.Name,
                    Barcode = p.Barcode,
                    Unit = p.Unit,
                    Type = p.Type,
                    BasePrice = p.BasePrice,
                    CostPrice = p.CostPrice,
                    ReorderLevel = p.ReorderLevel,
                    IsActive = p.IsActive,
                    CreatedAt = p.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<Result<DtoProduct>> GetProductByIdAsync(int productId)
        {
            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result<DtoProduct>.Fail("المنتج غير موجود.");

            return Result<DtoProduct>.Ok(new DtoProduct
            {
                ProductId = product.ProductId,
                Name = product.Name,
                Barcode = product.Barcode,
                Unit = product.Unit,
                Type = product.Type,
                BasePrice = product.BasePrice,
                CostPrice = product.CostPrice,
                ReorderLevel = product.ReorderLevel,
                IsActive = product.IsActive,
                CreatedAt = product.CreatedAt
            });
        }

        public async Task<Result<DtoProduct>> GetProductByBarcodeAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return Result<DtoProduct>.Fail("الباركود مطلوب.");

            var product = await _dbContext.Products
                .FirstOrDefaultAsync(p => p.Barcode == barcode && p.IsActive);

            if (product == null)
                return Result<DtoProduct>.Fail("لا يوجد منتج نشط بهذا الباركود.");

            return Result<DtoProduct>.Ok(new DtoProduct
            {
                ProductId = product.ProductId,
                Name = product.Name,
                Barcode = product.Barcode,
                Unit = product.Unit,
                Type = product.Type,
                BasePrice = product.BasePrice,
                CostPrice = product.CostPrice,
                ReorderLevel = product.ReorderLevel,
                IsActive = product.IsActive,
                CreatedAt = product.CreatedAt
            });
        }

        public async Task<List<DtoBatch>> GetProductBatchesAsync(int productId)
        {
            return await _dbContext.Batches
                .Where(b => b.ProductId == productId && b.BatchQuantity > 0)
                .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                .ThenBy(b => b.CreatedAt)
                .Select(b => new DtoBatch
                {
                    BatchId = b.BatchId,
                    ProductId = b.ProductId,
                    BatchNumber = b.BatchNumber,
                    ExpiryDate = b.ExpiryDate,
                    BatchQuantity = b.BatchQuantity,
                    CreatedAt = b.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<Result<int>> AddNewProductAsync(DtoProduct dtoProduct)
        {
            if (string.IsNullOrWhiteSpace(dtoProduct.Name))
                return Result<int>.Fail("اسم المنتج مطلوب.");

            if (string.IsNullOrWhiteSpace(dtoProduct.Type) || !ValidTypes.Contains(dtoProduct.Type.ToLower()))
                return Result<int>.Fail("نوع المنتج غير صالح. الأنواع المسموحة: stock, service, manufactured.");

            if (dtoProduct.BasePrice < 0)
                return Result<int>.Fail("سعر البيع يجب أن يكون صفراً أو أكثر.");

            if (dtoProduct.CostPrice < 0)
                return Result<int>.Fail("سعر التكلفة يجب أن يكون صفراً أو أكثر.");

            if (!string.IsNullOrWhiteSpace(dtoProduct.Barcode))
            {
                bool barcodeExists = await _dbContext.Products.AnyAsync(p => p.Barcode == dtoProduct.Barcode);
                if (barcodeExists)
                    return Result<int>.Fail("الباركود مستخدم لمنتج آخر.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                var product = new Product
                {
                    Name = clsValidation.ValidateString(dtoProduct.Name, "Name", 100),
                    Barcode = string.IsNullOrWhiteSpace(dtoProduct.Barcode) ? null : dtoProduct.Barcode.Trim(),
                    Unit = string.IsNullOrWhiteSpace(dtoProduct.Unit) ? null : dtoProduct.Unit,
                    Type = dtoProduct.Type.ToLower(),
                    BasePrice = dtoProduct.BasePrice,
                    CostPrice = dtoProduct.CostPrice,
                    ReorderLevel = dtoProduct.ReorderLevel,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.Products.Add(product);
                await _dbContext.SaveChangesAsync();

                if (product.Type == "stock")
                {
                    _dbContext.Inventories.Add(new Inventory
                    {
                        ProductId = product.ProductId,
                        QuantityAvailable = 0,
                        QuantityReserved = 0,
                        UpdatedAt = DateTime.UtcNow
                    });
                    await _dbContext.SaveChangesAsync();
                }

                await transaction.CommitAsync();
                return Result<int>.Ok(product.ProductId, "تمت إضافة المنتج بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشلت عملية إضافة المنتج.");
            }
        }

        public async Task<Result> UpdateProductAsync(DtoProduct dtoProduct)
        {
            if (dtoProduct.ProductId <= 0)
                return Result.Fail("معرف المنتج غير صالح.");

            var product = await _dbContext.Products.FindAsync(dtoProduct.ProductId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            if (!string.IsNullOrWhiteSpace(dtoProduct.Name))
                product.Name = clsValidation.ValidateString(dtoProduct.Name, "Name", 100);

            if (!string.IsNullOrWhiteSpace(dtoProduct.Barcode))
            {
                bool barcodeExists = await _dbContext.Products
                    .AnyAsync(p => p.Barcode == dtoProduct.Barcode && p.ProductId != dtoProduct.ProductId);
                if (barcodeExists)
                    return Result.Fail("الباركود مستخدم لمنتج آخر.");
                product.Barcode = dtoProduct.Barcode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(dtoProduct.Unit))
                product.Unit = dtoProduct.Unit;

            if (!string.IsNullOrWhiteSpace(dtoProduct.Type))
            {
                if (!ValidTypes.Contains(dtoProduct.Type.ToLower()))
                    return Result.Fail("نوع المنتج غير صالح.");

                string newType = dtoProduct.Type.ToLower();
                if (newType != product.Type)
                {
                    bool hasStockData = await _dbContext.Inventories.AnyAsync(i => i.ProductId == product.ProductId);
                    bool hasBatches = await _dbContext.Batches.AnyAsync(b => b.ProductId == product.ProductId);
                    if (hasStockData || hasBatches)
                        return Result.Fail("لا يمكن تغيير نوع المنتج لأنه مرتبط ببيانات مخزون.");
                    product.Type = newType;
                }
            }

            if (dtoProduct.BasePrice > 0)
                product.BasePrice = dtoProduct.BasePrice;

            if (dtoProduct.CostPrice > 0)
                product.CostPrice = dtoProduct.CostPrice;

            if (dtoProduct.ReorderLevel.HasValue)
                product.ReorderLevel = dtoProduct.ReorderLevel.Value;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث المنتج بنجاح.");
        }

        public async Task<Result> DeactivateProductAsync(int productId, int actorUserId)
        {
            var actor = await _dbContext.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || !clsPermissions.HasAtLeast(actor.Role.RoleValue, clsPermissions.Accountant))
                return Result.Fail("تعطيل المنتجات يتطلب صلاحية محاسب أو أعلى.");

            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            var inventory = await _dbContext.Inventories.FindAsync(productId);
            if (inventory != null && (inventory.QuantityReserved ?? 0) > 0)
                return Result.Fail("لا يمكن تعطيل المنتج لوجود كميات محجوزة.");

            product.IsActive = false;

            clsAudit.Log(_dbContext, actorUserId, "DeactivateProduct", $"تعطيل المنتج '{product.Name}' (رقم {productId})");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل المنتج بنجاح.");
        }

        public async Task<Result> ActivateProductAsync(int productId)
        {
            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            product.IsActive = true;
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تفعيل المنتج بنجاح.");
        }
    }
}
