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
        private readonly clsStoreClockService _storeClock;

        public clsProductService(SaylDbContext dbContext, clsStoreClockService? storeClock = null)
        {
            _dbContext = dbContext;
            // ساعة المتجر (Task 2): مُمرَّرة عبر DI أو نسخة على السياق نفسه في
            // التركيب اليدوي/الاختبارات — اللحظة دائمًا من SQL Server لا ساعة الجهاز.
            _storeClock = storeClock ?? new clsStoreClockService(dbContext);
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

        private static readonly string[] ValidTypes = { "stock", "service", "manufactured" };

        // ═══ ترقية مخطط وحدات المنتج (المرحلة 3 — idempotent، مخطط خامل) ═══
        // جدول ProductUnits: صف أساس واحد لكل منتج (IsBase = 1, FactorToBase = 1)
        // هو المرآة القانونية لـ Product.Unit — لا سلوك جديد في هذه المرحلة.
        // نفس DDL لسكربت Upgrade_20_ProductUnits.sql (المصدران يُثبَّتان متطابقَين
        // باختبار تكافؤ الترقية). لاحظ: دفعة واحدة بلا GO — CREATE TABLE ثم
        // INSERT يعمل بحل الأسماء المؤجَّل (نمط EnsureReservationSchemaSql).
        public const string EnsureProductUnitsSchemaSql = @"
IF OBJECT_ID(N'dbo.ProductUnits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductUnits
    (
        ProductUnitId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ProductUnits PRIMARY KEY,
        ProductId INT NOT NULL,
        Name NVARCHAR(20) NOT NULL,
        FactorToBase DECIMAL(18,6) NOT NULL,
        IsBase BIT NOT NULL CONSTRAINT DF_ProductUnits_IsBase DEFAULT (0),
        PricePerUnit DECIMAL(10,2) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_ProductUnits_IsActive DEFAULT (1),
        CreatedAt DATETIME2 NULL CONSTRAINT DF_ProductUnits_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT FK_ProductUnits_Product FOREIGN KEY (ProductId) REFERENCES dbo.Products(ProductId),
        CONSTRAINT CK_ProductUnits_Factor CHECK (FactorToBase > 0),
        CONSTRAINT CK_ProductUnits_BaseFactor CHECK (IsBase = 0 OR FactorToBase = 1),
        CONSTRAINT UQ_ProductUnits_Product_Name UNIQUE (ProductId, Name)
    );

    CREATE UNIQUE INDEX UQ_ProductUnits_Base ON dbo.ProductUnits(ProductId) WHERE IsBase = 1;
    CREATE INDEX IX_ProductUnits_Product ON dbo.ProductUnits(ProductId);
END

/* ترحيل التكوين (لا اختراع تاريخ): لكل منتج بلا صف أساس — صف أساس واحد
   باسم وحدته الحالية (أو 'وحدة' إن كانت فارغة). الحارس الثاني يمنع
   اصطدام UQ_ProductUnits_Product_Name إن كان الاسم محجوزًا لصف آخر. */
INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
SELECT p.ProductId,
       ISNULL(NULLIF(LTRIM(RTRIM(p.Unit)), N''), N'وحدة'),
       1, 1, 1
FROM dbo.Products p
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.ProductUnits pu
    WHERE pu.ProductId = p.ProductId AND pu.IsBase = 1)
  AND NOT EXISTS (
    SELECT 1 FROM dbo.ProductUnits pu
    WHERE pu.ProductId = p.ProductId
      AND pu.Name = ISNULL(NULLIF(LTRIM(RTRIM(p.Unit)), N''), N'وحدة'))";

        /// <summary>يضمن جدول وحدات المنتج + صفوف الأساس على أي قاعدة (قديمة أو جديدة) — يُنادى عند الإقلاع.</summary>
        public async Task EnsureProductUnitsSchemaAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureProductUnitsSchemaSql);

        // ═══ ترقية لقطات وحدة الإدخال على InvoiceItems (المرحلة 3 — idempotent) ═══
        // أعمدة NULL فقط: الصفوف الإرثية تبقى NULL ولا تُختلق لها وحدة (لا backfill).
        // نفس DDL لسكربت Upgrade_21_TransactionUnitSnapshots.sql. الفهرس المُرشَّح
        // يُنشأ عبر EXEC كي تُرى الأعمدة المضافة في نفس الدفعة (نمط EnsureCountSchemaSql).
        // يشترط وجود dbo.ProductUnits — يُنادى بعده في مسار الإقلاع.
        public const string EnsureInvoiceItemSnapshotSchemaSql = @"
IF OBJECT_ID(N'dbo.InvoiceItems', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'UnitName'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD UnitName NVARCHAR(20) NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'UnitFactorToBase'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD UnitFactorToBase DECIMAL(18,6) NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'EnteredQuantity'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD EnteredQuantity DECIMAL(10,3) NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'ProductUnitId'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD ProductUnitId INT NULL
            CONSTRAINT FK_ProductUnits_ProductUnitId REFERENCES dbo.ProductUnits(ProductUnitId);
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems')
          AND name = N'IX_InvoiceItems_ProductUnitId'
    )
    BEGIN
        EXEC(N'CREATE NONCLUSTERED INDEX IX_InvoiceItems_ProductUnitId ON dbo.InvoiceItems(ProductUnitId) WHERE ProductUnitId IS NOT NULL');
    END;
END";

        /// <summary>يضمن أعمدة لقطات الوحدة على بنود الفواتير — يُنادى عند الإقلاع بعد EnsureProductUnitsSchemaAsync.</summary>
        public async Task EnsureInvoiceItemSnapshotSchemaAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureInvoiceItemSnapshotSchemaSql);


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

        /// <summary>
        /// صفحة من المنتجات (الترقيم على مستوى SQL): نفس فلاتر
        /// GetProductsListAsync + عدّ كامل + Skip/Take — عمود قوائم الشاشات.
        /// </summary>
        public async Task<PagedResult<DtoProduct>> GetProductsPagedAsync(
            string search = "", bool includeInactive = false, int page = 1, int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

            var query = _dbContext.Products.AsQueryable();

            if (!includeInactive)
                query = query.Where(p => p.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                string like = search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
                query = query.Where(p => p.Name.Contains(like) || (p.Barcode != null && p.Barcode.Contains(like)));
            }

            int total = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
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

            return new PagedResult<DtoProduct> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
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

        /// <summary>المتاح الفعلي للبيع من قاعدة البيانات لحظيًا:
        /// (مجموع الدفعات الصالحة) − (QuantityReserved) — الصالح = غير منتهي
        /// الصلاحية وغير محجوب (حجر)، فلا يعرض الكاشير مخزونًا لا يمكن بيعه.
        /// المحجوز للمعلّقات يخصم من السقف، وغير سالب دائمًا.
        /// المنتج غير المخزّن = غير محدود (بلا دفعات ولا مخزون = 0).</summary>
        public async Task<decimal> GetNetAvailableQuantityAsync(int productId)
        {
            // اليوم بمعنى المتجر من ساعة SQL (Task 2) — FEFO والصلاحية بمعنى المحل
            DateOnly today = await _storeClock.GetStoreTodayAsync(_dbContext);

            decimal reserved = await _dbContext.Inventories
                .Where(i => i.ProductId == productId)
                .Select(i => (decimal?)(i.QuantityReserved ?? 0))
                .FirstOrDefaultAsync() ?? 0;

            decimal sellable = await _dbContext.Batches
                .Where(b => b.ProductId == productId && b.BatchQuantity > 0
                         && !b.IsQuarantined
                         && (b.ExpiryDate == null || b.ExpiryDate >= today))
                .SumAsync(b => (decimal?)b.BatchQuantity) ?? 0;

            return Math.Max(0m, sellable - reserved);
        }

        /// <summary>المتاح الفعلي للبيع لعدة منتجات دفعة واحدة — نفس قواعد
        /// GetNetAvailableQuantityAsync حرفيًا: (مجموع الدفعات الصالحة) −
        /// (QuantityReserved) وغير سالب، والصالح = غير منتهي الصلاحية وغير
        /// محجوب (حجر). موجّه للتقارير الجماعية (لوحة التحكم/حالة المخزون)
        /// حتى لا تُعاد كتابة القاعدة بمعادلة منفصلة في خدمة التقارير —
        /// استعلامان اثنان مهما كان عدد المنتجات (لا استعلام لكل منتج).</summary>
        public async Task<Dictionary<int, decimal>> GetNetAvailableQuantitiesAsync(IEnumerable<int>? productIds = null)
        {
            // اليوم بمعنى المتجر من ساعة SQL (Task 2) — FEFO والصلاحية بمعنى المحل
            DateOnly today = await _storeClock.GetStoreTodayAsync(_dbContext);

            var sellableQuery = _dbContext.Batches
                .Where(b => b.BatchQuantity > 0
                         && !b.IsQuarantined
                         && (b.ExpiryDate == null || b.ExpiryDate >= today));

            if (productIds != null)
            {
                int[] ids = productIds.Distinct().ToArray();
                if (ids.Length == 0)
                    return new Dictionary<int, decimal>();
                sellableQuery = sellableQuery.Where(b => ids.Contains(b.ProductId));
            }

            // مجموع الدفعات الصالحة لكل منتج
            Dictionary<int, decimal> sellableByProduct = await sellableQuery
                .GroupBy(b => b.ProductId)
                .Select(g => new { ProductId = g.Key, Qty = g.Sum(b => b.BatchQuantity) })
                .ToDictionaryAsync(x => x.ProductId, x => x.Qty);

            // المحجوز لكل منتج
            var reservedQuery = _dbContext.Inventories.Select(i => new { i.ProductId, Reserved = i.QuantityReserved ?? 0 });
            if (productIds != null)
            {
                int[] ids = productIds.Distinct().ToArray();
                reservedQuery = reservedQuery.Where(i => ids.Contains(i.ProductId));
            }

            Dictionary<int, decimal> reservedByProduct = await reservedQuery
                .ToDictionaryAsync(x => x.ProductId, x => x.Reserved);

            // المتاح الصافي = غير سالب دائمًا (نفس معادلة النسخة الأحادية)
            var result = new Dictionary<int, decimal>();
            foreach (int productId in sellableByProduct.Keys.Union(reservedByProduct.Keys))
            {
                sellableByProduct.TryGetValue(productId, out decimal sellable);
                reservedByProduct.TryGetValue(productId, out decimal reserved);
                result[productId] = Math.Max(0m, sellable - reserved);
            }
            return result;
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
                    IsQuarantined = b.IsQuarantined,
                    CreatedAt = b.CreatedAt
                })
                .ToListAsync();
        }

        /// <summary>المرحلة 5: وحدات المنتج للاختيار في الواجهات —
        /// الوحدة الأساس أولًا (IsBase = 1) ثم الباقية بالاسم. PricePerUnit
        /// للعرض فقط (سعر الأساس × المعامل، مقرّب لخانتين) — الحقيقة المخزنة
        /// تبقى سعر الأساس على المنتج. المنتج غير الموجود = قائمة فارغة
        /// (الواجهة تُسقط عمود الوحدات إلى الأساس).
        /// المرحلة 6: includeInactive (لتحرير وحدات المنتج) يجلب المعطلة
        /// أيضًا ويعيد PricePerUnit المخزن كما هو — الواجهة الافتراضية
        /// (false) تبقى نشطة حصرًا بسعر العرض المشتق كما كانت.</summary>
        public async Task<List<DtoProductUnit>> GetProductUnitsAsync(int productId, bool includeInactive = false)
        {
            var product = await _dbContext.Products
                .AsNoTracking()
                .Where(p => p.ProductId == productId)
                .Select(p => new { p.BasePrice })
                .FirstOrDefaultAsync();

            if (product == null)
                return new List<DtoProductUnit>();

            var query = _dbContext.ProductUnits
                .AsNoTracking()
                .Where(u => u.ProductId == productId);

            if (!includeInactive)
                query = query.Where(u => u.IsActive);

            var units = await query
                .OrderByDescending(u => u.IsBase)
                .ThenBy(u => u.Name)
                .Select(u => new DtoProductUnit
                {
                    ProductUnitId = u.ProductUnitId,
                    Name = u.Name,
                    FactorToBase = u.FactorToBase,
                    IsBase = u.IsBase,
                    PricePerUnit = u.PricePerUnit,
                    IsActive = u.IsActive
                })
                .ToListAsync();

            // سعر وحدة الإدخال للعرض فقط — يُشتق من سعر الأساس المخزن على
            // المنتج (لا يُعتمد على PricePerUnit المخزن القديم إن اختلفت
            // لأن الحقيقة المخزنة هي سعر الأساس). وضع التحرير (includeInactive)
            // يعيد القيمة المخزنة كما هي ليحررها المستخدم.
            if (!includeInactive)
            {
                foreach (var unit in units)
                {
                    if (product.BasePrice > 0m && unit.FactorToBase > 0m)
                        unit.PricePerUnit = Math.Round(product.BasePrice * unit.FactorToBase, 2, MidpointRounding.AwayFromZero);
                }
            }

            return units;
        }

        // ═══ المرحلة 6: إدارة وحدات المنتج (الأساس + البدائل) من محرر المنتج ═══
        // سياسة التاريخ (المرحلة 5): كل سطر فاتورة يحمل لقطته النصية
        // (UnitName/UnitFactorToBase/EnteredQuantity) تُقرأ ولا تُعاد كتابتها
        // أبدًا — إعادة تسمية وحدة آمنة لأن الصفوف القديمة تُبقي نصها القديم،
        // أما تغيير المعامل فيُرفض للوحدة المستخدمة لأنه يُغيّر معنى الكميات
        // المسجلة، والحذف يُرفض ما دامت فواتير تشير للوحدة (التعطيل هو دورة
        // الحياة الآمنة: ResolveUnitAsync يحجب المعطلة في المعاملات الجديدة).

        /// <summary>تحقق مشترك لحقول وحدة المنتج — يُعيد رسالة الرفض أو null.
        /// الاسم: غير فارغ بعد القص وبحد 20 حرفًا (عرض العمود NVARCHAR(20)).
        /// المعامل: أكبر من صفر وبخانات لا تتجاوز 6 (DECIMAL(18,6)) — خانات
        /// أكثر = دقة مفرطة تُرفض لا تُقرَّب. السعر: صفر أو أكثر أو null.</summary>
        private static string? ValidateUnitFields(string? name, decimal factorToBase, decimal? pricePerUnit)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "اسم الوحدة مطلوب.";

            if (name.Trim().Length > 20)
                return "اسم الوحدة يجب ألا يتجاوز 20 حرفًا.";

            if (factorToBase <= 0m)
                return "معامل التحويل يجب أن يكون أكبر من صفر.";

            // دقة مفرطة: تقريب إلى 6 خانات يُغيّر القيمة ⇒ كان بها أكثر من 6
            if (Math.Round(factorToBase, 6) != factorToBase)
                return "معامل التحويل يتجاوز 6 منازل عشرية — دقة مفرطة غير مسموحة.";

            if (pricePerUnit.HasValue && pricePerUnit.Value < 0m)
                return "سعر الوحدة يجب أن يكون صفراً أو أكثر.";

            return null;
        }

        /// <summary>هل استُخدمت الوحدة في لقطة فاتورة؟ المرجع بالمفتاح
        /// (ProductUnitId) إن وُجد أو بمطابقة الاسم الحالي على سطور نفس
        /// المنتج — مسار المعاملات الحالي (المرحلة 5) يكتب اللقطة النصية
        /// ويترك المفتاح التيسيري NULL، فالمطابقة النصية هي الحقيقة العاملة.
        /// ملاحظة التصميم: إعادة التسمية تفصل الصفوف القديمة عن هذا الصف
        /// بالاسم، لكن معناها يبقى مجمّدًا في نصها القديم (سياسة اللقطة)
        /// فلا يُفسد التاريخ بذلك الفصل.</summary>
        private async Task<bool> IsProductUnitUsedAsync(int productUnitId, int productId, string name)
        {
            return await _dbContext.InvoiceItems
                .AsNoTracking()
                .AnyAsync(ii => ii.ProductUnitId == productUnitId
                             || (ii.ProductId == productId && ii.UnitName == name));
        }

        /// <summary>المرحلة 6: إضافة وحدة بديلة لمنتج — نشطة فورًا وغير أساس.
        /// الاسم فريد لكل منتج بمطابقة قيد الفردية في القاعدة (UQ_ProductUnits_Product_Name
        /// عبر تساوي EF الذي يُترجم لمقارنة العمود نفسها — يتضمن اسم صف الأساس).</summary>
        public async Task<Result<int>> AddProductUnitAsync(int productId, string name,
            decimal factorToBase, decimal? pricePerUnit = null, int actorUserId = 0)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"إضافة وحدة للمنتج #{productId} مرفوضة: تتطلب صلاحية شاشة المنتجات.");
                return Result<int>.Fail("إدارة وحدات المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            if (productId <= 0)
                return Result<int>.Fail("معرف المنتج غير صالح.");

            bool productExists = await _dbContext.Products.AnyAsync(p => p.ProductId == productId);
            if (!productExists)
                return Result<int>.Fail("المنتج غير موجود.");

            string? validationError = ValidateUnitFields(name, factorToBase, pricePerUnit);
            if (validationError != null)
                return Result<int>.Fail(validationError);

            string trimmed = name.Trim();

            bool duplicate = await _dbContext.ProductUnits
                .AnyAsync(u => u.ProductId == productId && u.Name == trimmed);
            if (duplicate)
                return Result<int>.Fail($"اسم الوحدة «{trimmed}» مستخدم لهذا المنتج — اختر اسمًا آخر.");

            var unit = new ProductUnit
            {
                ProductId = productId,
                Name = trimmed,
                FactorToBase = factorToBase,
                IsBase = false,
                PricePerUnit = pricePerUnit,
                IsActive = true,
                CreatedAt = await _storeClock.GetUtcNowAsync(_dbContext)
            };

            _dbContext.ProductUnits.Add(unit);
            await _dbContext.SaveChangesAsync();
            return Result<int>.Ok(unit.ProductUnitId, "تمت إضافة الوحدة بنجاح.");
        }

        /// <summary>المرحلة 6: تعديل وحدة بديلة. إعادة التسمية مسموحة دائمًا —
        /// اللقطات النصية على الفواتير القديمة تُبقي نصها القديم فلا يتغير
        /// معناها (سياسة اللقطة). تغيير المعامل يُرفض للوحدة المستخدمة في أي
        /// فاتورة سابقة: الكميات المسجلة حُسبت بالمعامل القديم وتغييره يُعيد
        /// كتابة معنى التاريخ. السعر قابل للتعديل دائمًا. الوحدة الأساس لا
        /// تُعدل من هنا (عاملها 1 بقيد CK_ProductUnits_BaseFactor وتُدار من
        /// حقل وحدة الأساس على المنتج).</summary>
        public async Task<Result> UpdateProductUnitAsync(int productUnitId, string name,
            decimal factorToBase, decimal? pricePerUnit = null, int actorUserId = 0)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"تعديل وحدة منتج #{productUnitId} مرفوض: يتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("إدارة وحدات المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            var unit = await _dbContext.ProductUnits
                .FirstOrDefaultAsync(u => u.ProductUnitId == productUnitId);
            if (unit == null)
                return Result.Fail("وحدة المنتج غير موجودة.");

            if (unit.IsBase)
                return Result.Fail("الوحدة الأساس تُدار من حقل وحدة الأساس في شاشة المنتج.");

            string? validationError = ValidateUnitFields(name, factorToBase, pricePerUnit);
            if (validationError != null)
                return Result.Fail(validationError);

            string trimmed = name.Trim();

            bool duplicate = await _dbContext.ProductUnits
                .AnyAsync(u => u.ProductId == unit.ProductId
                            && u.ProductUnitId != unit.ProductUnitId
                            && u.Name == trimmed);
            if (duplicate)
                return Result.Fail($"اسم الوحدة «{trimmed}» مستخدم لوحدة أخرى لنفس المنتج.");

            // حماية التاريخ: المعامل ثابت للوحدة المستخدمة — المقارنة دقيقة
            // (المدخل مُتحقق منه بـ 6 خانات والمخزن DECIMAL(18,6))
            if (unit.FactorToBase != factorToBase)
            {
                bool used = await IsProductUnitUsedAsync(unit.ProductUnitId, unit.ProductId, unit.Name);
                if (used)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductUnitFactorChangeDenied",
                        $"تغيير معامل الوحدة '{unit.Name}' (#{unit.ProductUnitId}) مرفوض: استُخدمت في فواتير سابقة.");
                    return Result.Fail(
                        $"لا يمكن تغيير معامل الوحدة «{unit.Name}» لأنها استُخدمت في فواتير سابقة — " +
                        "تغيير المعامل يُغيّر معنى الكميات المسجلة تاريخيًا. يمكنك إعادة تسميتها أو تعطيلها وإنشاء وحدة جديدة بالمعامل الصحيح.");
                }
            }

            unit.Name = trimmed;
            unit.FactorToBase = factorToBase;
            unit.PricePerUnit = pricePerUnit;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث الوحدة بنجاح.");
        }

        /// <summary>المرحلة 6: تعطيل وحدة بديلة — دورة الحياة الآمنة: تعمل للوحدات
        /// المستخدمة وغير المستخدمة، والفواتير القديمة تُبقي لقطتها النصية،
        /// وResolveUnitAsync يحجب المعطلة في المعاملات الجديدة. الوحدة الأساس
        /// لا تُعطَّل (هي المرآة القانونية لـ Product.Unit).</summary>
        public async Task<Result> DeactivateProductUnitAsync(int productUnitId, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"تعطيل وحدة منتج #{productUnitId} مرفوض: يتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("إدارة وحدات المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            var unit = await _dbContext.ProductUnits
                .FirstOrDefaultAsync(u => u.ProductUnitId == productUnitId);
            if (unit == null)
                return Result.Fail("وحدة المنتج غير موجودة.");

            if (unit.IsBase)
                return Result.Fail("لا يمكن تعطيل الوحدة الأساس — هي المرآة القانونية لوحدة المنتج.");

            unit.IsActive = false;

            clsAudit.Log(_dbContext, actorUserId, "DeactivateProductUnit",
                $"تعطيل وحدة المنتج '{unit.Name}' (#{unit.ProductUnitId}) للمنتج رقم {unit.ProductId}");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل الوحدة بنجاح — لن تُقبل في المعاملات الجديدة، والفواتير القديمة تحفظ لقطتها.");
        }

        /// <summary>المرحلة 6: حذف صارم لوحدة بديلة لا تشير إليها أي فاتورة
        /// (بالمفتاح أو بمطابقة الاسم). ما استُخدم في فواتير يُرفض حذفه —
        /// التعطيل هو المسار الآمن لأنه يُبقي الصف مرجعًا ساكنًا للتاريخ.</summary>
        public async Task<Result> DeleteProductUnitAsync(int productUnitId, int actorUserId)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"حذف وحدة منتج #{productUnitId} مرفوض: يتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("إدارة وحدات المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            var unit = await _dbContext.ProductUnits
                .FirstOrDefaultAsync(u => u.ProductUnitId == productUnitId);
            if (unit == null)
                return Result.Fail("وحدة المنتج غير موجودة.");

            if (unit.IsBase)
                return Result.Fail("لا يمكن حذف الوحدة الأساس — كل منتج يحتاج وحدة أساس واحدة.");

            bool used = await IsProductUnitUsedAsync(unit.ProductUnitId, unit.ProductId, unit.Name);
            if (used)
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductUnitDeleteDenied",
                    $"حذف الوحدة '{unit.Name}' (#{unit.ProductUnitId}) مرفوض: فواتير سابقة تشير إليها.");
                return Result.Fail(
                    $"لا يمكن حذف الوحدة «{unit.Name}» لأن فواتير سابقة تشير إليها — عطّلها بدلًا من الحذف.");
            }

            _dbContext.ProductUnits.Remove(unit);

            clsAudit.Log(_dbContext, actorUserId, "DeleteProductUnit",
                $"حذف وحدة المنتج '{unit.Name}' (#{unit.ProductUnitId}) من المنتج رقم {unit.ProductId}");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم حذف الوحدة بنجاح.");
        }

        public async Task<Result<int>> AddNewProductAsync(DtoProduct dtoProduct, int actorUserId = 0)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    "إضافة منتج مرفوضة: تتطلب صلاحية شاشة المنتجات.");
                return Result<int>.Fail("إضافة المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            if (string.IsNullOrWhiteSpace(dtoProduct.Name))
                return Result<int>.Fail("اسم المنتج مطلوب.");

            if (string.IsNullOrWhiteSpace(dtoProduct.Type) || !ValidTypes.Contains(dtoProduct.Type.ToLower()))
                return Result<int>.Fail("نوع المنتج غير صالح. الأنواع المسموحة: stock, service, manufactured.");

            if (dtoProduct.BasePrice < 0)
                return Result<int>.Fail("سعر البيع يجب أن يكون صفراً أو أكثر.");

            if (dtoProduct.CostPrice < 0)
                return Result<int>.Fail("سعر التكلفة يجب أن يكون صفراً أو أكثر.");

            // المرحلة 6: منع تكوين وحدة أساس فارغ للمنتجات المخزنية — صف الأساس
            // (ProductUnits) يحمل اسمًا NOT NULL وكل حساب المخزون يُشتق منه؛
            // منتج مخزني بلا وحدة = تكوين غير صالح يُرفض عند الحد. الأنواع
            // الأخرى (service/manufactured) تبقى حرة النص كما كانت.
            if (dtoProduct.Type.ToLower() == "stock" && string.IsNullOrWhiteSpace(dtoProduct.Unit))
                return Result<int>.Fail("وحدة الأساس مطلوبة للمنتجات المخزنية.");

            if (!string.IsNullOrWhiteSpace(dtoProduct.Barcode))
            {
                bool barcodeExists = await _dbContext.Products.AnyAsync(p => p.Barcode == dtoProduct.Barcode);
                if (barcodeExists)
                    return Result<int>.Fail("الباركود مستخدم لمنتج آخر.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // لقطة SQL واحدة للعملية (Task 2): المنتج والمخزون الابتدائي لحظة واحدة
                DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

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
                    CreatedAt = nowUtc
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
                        UpdatedAt = nowUtc
                    });
                    await _dbContext.SaveChangesAsync();
                }

                // المرحلة 3 (تكوين فقط): صف الأساس الوحيد — المرآة القانونية لـ
                // Product.Unit (عامل 1). وحدة فارغة تأخذ الافتراضي مثل الترحيل.
                _dbContext.ProductUnits.Add(new ProductUnit
                {
                    ProductId = product.ProductId,
                    Name = string.IsNullOrWhiteSpace(product.Unit) ? "وحدة" : product.Unit,
                    FactorToBase = 1m,
                    IsBase = true,
                    IsActive = true,
                    CreatedAt = nowUtc
                });
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();
                return Result<int>.Ok(product.ProductId, "تمت إضافة المنتج بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشلت عملية إضافة المنتج.");
            }
        }

        public async Task<Result> UpdateProductAsync(DtoProduct dtoProduct, int actorUserId = 0)
        {
            int actorRole = await GetUserRoleValueAsync(actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"تعديل منتج #{dtoProduct.ProductId} مرفوض: يتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("تعديل المنتجات يتطلب صلاحية شاشة المنتجات.");
            }

            if (dtoProduct.ProductId <= 0)
                return Result.Fail("معرف المنتج غير صالح.");

            var product = await _dbContext.Products.FindAsync(dtoProduct.ProductId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            // المرحلة 6: منع تكوين وحدة أساس فارغ للمنتجات المخزنية عند التعديل —
            // إرسال وحدة فارغة لمنتج مخزني بلا وحدة أصلًا يُرفض. منتج له وحدة
            // يُبقي وحدته عند إرسال نص فارغ (السلوك القائم: الوحدة لا تُمسح).
            string effectiveType = !string.IsNullOrWhiteSpace(dtoProduct.Type)
                ? dtoProduct.Type.ToLower() : product.Type;
            if (effectiveType == "stock"
                && string.IsNullOrWhiteSpace(dtoProduct.Unit)
                && string.IsNullOrWhiteSpace(product.Unit))
                return Result.Fail("وحدة الأساس مطلوبة للمنتجات المخزنية.");

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
            {
                bool unitChanged = product.Unit != dtoProduct.Unit;
                product.Unit = dtoProduct.Unit;

                // المرحلة 3 (تكوين فقط): صف الأساس مرآة لـ Unit — إعادة تسمية الصف
                // القائم فقط، لا إنشاء بديل (التكوين تُدار عند الإضافة/الترحيل).
                if (unitChanged)
                {
                    var baseUnit = await _dbContext.ProductUnits
                        .FirstOrDefaultAsync(u => u.ProductId == product.ProductId && u.IsBase);
                    if (baseUnit != null && baseUnit.Name != dtoProduct.Unit)
                        baseUnit.Name = dtoProduct.Unit;
                }
            }

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

        /// <summary>
        /// تعطيل منتج. المنتجات المخزنية التي تحمل مخزونًا فعليًا (دفعات أو مخزون
        /// متاح) لا يمكن تعطيلها لأن التعطيل يُخفيها من كل شاشات المخزون والفواتير
        /// فتبقى الكميات عالقة بلا مسار استخراج. الكميات المحجوزة (لأقساط معلقة)
        /// تمنع التعطيل أيضًا. المنتجات غير المخزنية ليس لها مخزون فيُسمح بالتعطيل.
        /// </summary>
        public async Task<Result> DeactivateProductAsync(int productId, int actorUserId)
        {
            var actor = await _dbContext.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || !AppPermissions.Has(actor.Role.RoleValue, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"تعطيل منتج #{productId} مرفوض: يتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("تعطيل المنتجات يتطلب صلاحية شاشة المنتجات.");
            }

            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            // الكميات المحجوزة (فسخ فاتورة معلقة): تمنع التعطيل
            var inventory = await _dbContext.Inventories.FindAsync(productId);
            if (inventory != null && (inventory.QuantityReserved ?? 0m) > 0m)
                return Result.Fail("لا يمكن تعطيل المنتج لوجود كميات محجوزة.");

            // المنتجات المخزنية: المخزون الفعلي (دفعات) يجب أن يكون صفرًا —
            // التعطيل يخفي المنتج من منتقيات الفواتير فيصعب تحرير المخزون يدويًا.
            if (product.Type == "stock")
            {
                if (inventory != null && (inventory.QuantityAvailable ?? 0m) > 0m)
                    return Result.Fail($"لا يمكن تعطيل المنتج — عليه مخزون متاح ({inventory.QuantityAvailable} وحدة). أفرغ المخزون أولًا.");

                bool hasBatchStock = await _dbContext.Batches.AnyAsync(b => b.ProductId == productId && b.BatchQuantity > 0);
                if (hasBatchStock)
                    return Result.Fail("لا يمكن تعطيل المنتج — عليه دفعات بكمية. أفرغ الدفعات أولًا.");
            }

            product.IsActive = false;

            clsAudit.Log(_dbContext, actorUserId, "DeactivateProduct", $"تعطيل المنتج '{product.Name}' (رقم {productId})");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل المنتج بنجاح.");
        }

        /// <summary>إعادة تفعيل منتج — يتطلب صلاحية شاشة المنتجات.</summary>
        public async Task<Result> ActivateProductAsync(int productId, int actorUserId)
        {
            var actor = await _dbContext.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || !AppPermissions.Has(actor.Role.RoleValue, AppPermissions.Products))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "ProductOpDenied",
                    $"إعادة تفعيل منتج #{productId} مرفوضة: تتطلب صلاحية شاشة المنتجات.");
                return Result.Fail("إعادة تفعيل المنتجات تتطلب صلاحية شاشة المنتجات.");
            }

            var product = await _dbContext.Products.FindAsync(productId);
            if (product == null)
                return Result.Fail("المنتج غير موجود.");

            product.IsActive = true;
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تفعيل المنتج بنجاح.");
        }
    }
}
