using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// الجرد الفعلي للمخزون: فتح جلسة بلقطة دفترية (BookQuantity Snapshot)، إدخال
    /// المعدود فعليًا، تقرير الفروق وتكلفتها، ثم ترحيل الفروق عبر AdjustStockAsync
    /// القائمة (بترانزاكشناتها وصلاحياتها) — لا كتابة مباشرة في المخزون أبدًا.
    ///
    /// FIX (لقطة وقت العد): يحفظ كل سطر وقت عدّه الفعلي (CountedAt) والكمية
    /// الدفترية عند تلك اللحظة (BookQuantityAtCount) — snapshot فوري للمخزون
    /// لحظة العد. الفرق = المعدود − BookQuantityAtCount.
    /// عند الترحيل: final = currentInventory + diff = (bookAtCount + postCountMovements)
    ///              + (counted − bookAtCount) = counted + postCountMovements.
    /// أي حركة (بيع/استلام/تسوية) حصلت بعد العد تُحفظ في الرصيد النهائي كما يجب.
    ///
    /// FIX (تزامن): فتح الجلسة والترحيل داخل ترانزاكشن Serializable مع
    /// sp_getapplock — يمنع جلستين مفتوحتين متوازيتين، وتكرار رقم الجلسة،
    /// وترحيلًا مزدوجًا لنفس السطر من طلبين متزامنين. وفهرس فريد مُرشَّح
    /// (IsPosted=0 لكل منتج) حاجز قاعدي أخير.
    ///
    /// FIX (صلاحيات — في الخدمة لا الواجهة): كل عملية تُنشئ أو تُعدّل أو تُرحّل
    /// حالة الجرد (StartSessionAsync / SetCountedAsync / PostSessionAsync)
    /// تتطلب صلاحية شاشة المخزون (بت Inventory) — نفس سياسة AdjustStockAsync
    /// في clsStockService الذي يستدعيه الترحيل (سياسة موحّدة للنداء المباشر
    /// وغير المباشر). الفحص مستقل عن الواجهة: إخفاء الأزرار تجميل، والبوابة
    /// هنا — لا يتجاوزها نداء مباشر ولا فرق صفري ولا غياب هوية الفاعل.
    /// GetUserRoleValueAsync يرفض: معرف ≤ 0 / مستخدم غير موجود / مستخدم غير
    /// نشط / دور غير نشط.
    /// </summary>
    public class clsStockCountService
    {
        private readonly SaylDbContext _dbContext;

        public clsStockCountService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        // ─────────────────────────────────────────────────
        // بوابة صلاحيات الجرد (نمط clsReturnService وclsStockService):
        // عمليات الجرد عمليات شاشة المخزون — بت AppPermissions.Inventory حصرًا.
        // (يرجع null = مسموح؛ Result فاشل = يُرفض الطلب قبل أي كتابة.)
        // ─────────────────────────────────────────────────
        private async Task<Result<int>?> CheckInventoryPermissionAsync(int actorUserId)
        {
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, actorUserId);
            if (actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Inventory))
            {
                // تدقيق الرفض: محاولة عملية جرد بلا تصريح — قبل أي كتابة
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "StockCountDenied",
                    "عملية جرد (فتح/عدّ/ترحيل) مرفوضة: تتطلب صلاحية شاشة المخزون.");
                return Result<int>.Fail("عمليات الجرد تتطلب صلاحية شاشة المخزون.");
            }
            return null;
        }

        /// <summary>هل توجد جلسة جرد مفتوحة (سطور غير مرحّلة)؟ — يمنع جردتين متزامنتين.</summary>
        public async Task<bool> HasOpenSessionAsync()
        {
            return await _dbContext.StockCounts.AnyAsync(sc => !sc.IsPosted);
        }

        /// <summary>
        /// يضمن فهرس الجلسة المفتوحة الفريد + العمودين الجديدين على أي قاعدة
        /// (قديمة أو جديدة) — يُنادى عند الإقلاع. Upgrade_12 مدمج هنا.
        /// </summary>
        public const string EnsureCountSchemaSql = @"
IF OBJECT_ID(N'dbo.StockCounts', N'U') IS NOT NULL
BEGIN
    -- أضف العمودين الجديدين Upgrade_12 إن لم يكونا موجودين
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'CountedAt'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD CountedAt DATETIME2 NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'BookQuantityAtCount'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD BookQuantityAtCount INT NULL;
    END;

    -- عمود PostedMovementId (IDEMPOTENCY FIX): يربط السطر بحركة التسوية المقابلة.
    -- يضمن عدم تطبيق التسوية ذاتها أكثر من مرة عند إعادة الترحيل بعد فشل جزئي.
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'PostedMovementId'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD PostedMovementId INT NULL;
    END;

    -- الفهرس الفريد للجلسة المفتوحة
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts')
          AND name = N'UQ_StockCounts_OpenProduct'
          AND is_unique = 1 AND has_filter = 1
    )
    AND NOT EXISTS (
        SELECT 1 FROM dbo.StockCounts WHERE IsPosted = 0
        GROUP BY ProductId HAVING COUNT(*) > 1
    )
    BEGIN
        EXEC(N'CREATE UNIQUE INDEX UQ_StockCounts_OpenProduct ON dbo.StockCounts(ProductId) WHERE IsPosted = 0');
    END;
END";

        public async Task EnsureCountSchemaAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureCountSchemaSql);

        /// <summary>
        /// فتح جلسة جرد جديدة: لقطة كل منتج مخزني نشط بكميته الدفترية وتكلفته الحالية.
        /// يُرفض إن وُجدت جلسة مفتوحة. داخل ترانزاكشن Serializable + قفل تطبيقي
        /// — طلبان متوازيان يتصافحان: الثاني يرى الجلسة المفتوحة ويرفض،
        /// ورقم الجلسة (أعلى + 1) لا يتكرر أبدًا. الكمية الدفترية لحظة البدء
        /// = BookQuantity (لا تتغير بعد بدء الجلسة).
        /// </summary>
        public async Task<Result<int>> StartSessionAsync(int userId)
        {
            // بوابة الصلاحية قبل أي كتابة — النداء المباشر للخدمة لا يتجاوز الواجهة
            var permissionCheck = await CheckInventoryPermissionAsync(userId);
            if (permissionCheck != null)
                return permissionCheck;

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // قفل تطبيقي مسمّى — يسلسل فتح الجلستين المتوازيتين (بمالك المعاملة،
                // فالأمر الخام يُسجَّل في نفس SqlTransaction) حتى مع ReadCommitted
                var lockConn = (Microsoft.Data.SqlClient.SqlConnection)_dbContext.Database.GetDbConnection();
                int lockResult;
                using (var cmd = lockConn.CreateCommand())
                {
                    cmd.Transaction = (Microsoft.Data.SqlClient.SqlTransaction)_dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    cmd.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'StockCountSession', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r";
                    lockResult = (int)(await cmd.ExecuteScalarAsync())!;
                }

                if (lockResult < 0)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("ازدحام في فتح الجرد — حاول مرة أخرى بعد لحظات.");
                }

                if (await HasOpenSessionAsync())
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("توجد جلسة جرد مفتوحة بالفعل — أكملها أو ترحّلها أولاً.");
                }

                var products = await _dbContext.Products
                    .Where(p => p.Type == "stock" && p.IsActive)
                    .OrderBy(p => p.Name)
                    .Select(p => new { p.ProductId, p.Name, p.CostPrice, Book = (p.Inventory != null ? p.Inventory.QuantityAvailable : 0) ?? 0 })
                    .ToListAsync();

                if (products.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("لا توجد منتجات مخزنية نشطة للجرد.");
                }

                // معرف الجلسة = أعلى جلسة سابقة + 1 (0 لأول جرد) — آمن داخل القفل
                int sessionId = await _dbContext.StockCounts.MaxAsync(sc => (int?)sc.CountSessionId) ?? 0;
                sessionId += 1;

                foreach (var p in products)
                {
                    _dbContext.StockCounts.Add(new StockCount
                    {
                        CountSessionId = sessionId,
                        ProductId = p.ProductId,
                        ProductName = p.Name,
                        BookQuantity = p.Book,
                        // سعر آخر شراء لحظة بدء الجلسة — للعرض التاريخي فقط.
                        // تقييم الفروق بتكاليف الدفعات الفعلية (ComputeVarianceValue)
                        // لا بهذا العمود — انظر COSTING.md وGetOpenSessionAsync.
                        UnitCost = p.CostPrice,
                        CountedBy = userId,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                clsAudit.Log(_dbContext, userId, "StartStockCount",
                    $"فتح جلسة جرد #{sessionId} بـ {products.Count} منتجًا");
                await _dbContext.SaveChangesAsync();

                return Result<int>.Ok(sessionId, $"تم فتح جلسة الجرد بـ {products.Count} منتجًا.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result<int>.Fail("فشل فتح جلسة الجرد — حاول مرة أخرى.");
            }
        }

        // ─────────────────────────────────────────────────
        // تقييم الفروق بتكاليف الدفعات (COSTING.md) — لا Product.CostPrice العامة:
        // • العجز (diff < 0): الترحيل يستهلك الدفعات FEFO عبر AdjustStockAsync
        //   (نفس الترتيب: الأقرب انتهاءً أولًا، ثم المحجوبة، ثم الأقدم إنشاءً) —
        //   قيمة الفرق = Σ (المأخوذ من كل دفعة × (UnitCost ?? Product.CostPrice)).
        //   الدفعة بلا تكلفة تعود إلى Product.CostPrice (الاحتياط الموثق).
        // • الفائض (diff > 0): الترحيل ينشئ دفعة تلقائية بلا تكلفة (ADJ-…) يرجع
        //   استهلاكها اللاحق إلى Product.CostPrice — فقيمته = diff × Product.CostPrice
        //   (نفس الاحتياط الموثق، لا تكلفة مُخترعة).
        // • دفعات غير كافية (تقدير قبلي فقط — الترحيل نفسه سيفشل): المتبقي
        //   يُقيَّم صراحةً بـProduct.CostPrice احتياطًا موثقًا.
        // ─────────────────────────────────────────────────
        /// <summary>صنف الدفعة للاستهلاك FEFO: كميتها الحالية وتكلفتها.</summary>
        private sealed record FefoBatch(int Quantity, decimal? UnitCost);

        /// <summary>قيمة الفرق (سالب = عجز / موجب = فائض / صفر) بتكاليف الدفعات الفعلية.
        /// fefoBatches = دفعات المنتج بكمية > 0 بترتيب FEFO نفسه الذي يستهلكه
        /// AdjustStockAsync (نقصان بلا رقم دفعة). productCost = Product.CostPrice
        /// الاحتياطي الموثق للدفعات بلا تكلفة وللفائض.</summary>
        private static decimal ComputeVarianceValue(int diff, decimal productCost, IReadOnlyList<FefoBatch> fefoBatches)
        {
            if (diff == 0)
                return 0m;

            if (diff > 0)
                return diff * productCost; // الفائض: دفعة تلقائية بلا تكلفة → احتياط Product.CostPrice

            decimal value = 0m;
            int remaining = -diff; // حجم العجز
            foreach (var b in fefoBatches)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, b.Quantity); // استهلاك جزئي ممكن — نفس منطق الترحيل
                value -= take * (b.UnitCost ?? productCost);
                remaining -= take;
            }

            if (remaining > 0)
                value -= remaining * productCost; // دفعات غير كافية: التقدير القبي فقط — الترحيل سيرفض

            return value;
        }

        /// <summary>دفعات منتجات محددة بكمية > 0 بترتيب FEFO مطابق لترتيب النقصان
        /// في AdjustStockAsync حرفيًا (صلاحية: الأقرب انتهاءً أولًا وبدون صلاحية أخيرًا،
        /// ثم المحجوبة أولًا، ثم الأقدم إنشاءً) — الاستعلام الواحد لكل المنتجات.</summary>
        private async Task<Dictionary<int, List<FefoBatch>>> LoadFefoBatchesAsync(ICollection<int> productIds)
        {
            if (productIds.Count == 0)
                return new Dictionary<int, List<FefoBatch>>();

            var batches = await _dbContext.Batches
                .Where(b => productIds.Contains(b.ProductId) && b.BatchQuantity > 0)
                .OrderBy(b => b.ExpiryDate.HasValue ? b.ExpiryDate.Value : DateOnly.MaxValue)
                .ThenBy(b => b.IsQuarantined ? 0 : 1)
                .ThenBy(b => b.CreatedAt)
                .Select(b => new { b.ProductId, b.BatchQuantity, b.UnitCost })
                .ToListAsync();

            return batches
                .GroupBy(b => b.ProductId)
                .ToDictionary(g => g.Key, g => g.Select(b => new FefoBatch(b.BatchQuantity, b.UnitCost)).ToList());
        }

        /// <summary>
        /// سطور جلسة الجرد الحالية (المفتوحة = غير المرحّلة).
        /// • غير المعدود: BookQuantity المعروض = اللقطة + صافي الحركات منذ بدء الجلسة (الكتيب الحي الحالي).
        /// • المعدود: BookQuantity المعروض = BookQuantityAtCount (الكمية الدفترية عند العد الفعلي).
        ///   الفرق المعروض = counted − bookAtCount — هذا هو ما سيُرحَّل بالضبط.
        /// • DifferenceCost: تقييم الفرق بتكاليف الدفعات المتأثرة فعلًا (COSTING.md) —
        ///   العجز عبر الدفعات FEFO بتكلفة كل دفعة (UnitCost ?? Product.CostPrice)،
        ///   والفائض باحتياط Product.CostPrice الموثق. UnitCost المخزّن في السطر
        ///   (سعر آخر شراء لحظة بدء الجلسة) للعرض التاريخي فقط — لا يُقيَّم به الفرق.
        /// </summary>
        public async Task<List<StockCountVM>> GetOpenSessionAsync()
        {
            var rows = await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted)
                .OrderBy(sc => sc.ProductName)
                .Select(sc => new
                {
                    sc.CountId,
                    sc.CountSessionId,
                    sc.ProductId,
                    sc.ProductName,
                    sc.BookQuantity,
                    sc.BookQuantityAtCount,
                    sc.CountedQuantity,
                    sc.CountedAt,
                    sc.UnitCost,
                    ProductCost = sc.Product.CostPrice,
                    // uncounted line: live book (snapshot + movements since session start)
                    PostCountMovements = _dbContext.StockMovements
                        .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                        .Sum(m => (int?)m.Quantity) ?? 0
                })
                .ToListAsync();

            var fefoByProduct = await LoadFefoBatchesAsync(rows.Select(r => r.ProductId).Distinct().ToList());

            var result = new List<StockCountVM>(rows.Count);
            foreach (var sc in rows)
            {
                // uncounted line: الكتيب الحي؛ counted line: الكتيب لحظة العد الفعلي
                int book = sc.CountedAt.HasValue
                    ? sc.BookQuantityAtCount ?? 0
                    : sc.BookQuantity + sc.PostCountMovements;

                var vms = new StockCountVM
                {
                    CountId = sc.CountId,
                    CountSessionId = sc.CountSessionId,
                    ProductId = sc.ProductId,
                    ProductName = sc.ProductName,
                    BookQuantity = book,
                    CountedQuantity = sc.CountedQuantity,
                    CountedAt = sc.CountedAt,
                    UnitCost = sc.UnitCost,
                    DifferenceCost = sc.CountedQuantity.HasValue
                        ? ComputeVarianceValue(
                            sc.CountedQuantity.Value - book,
                            sc.ProductCost,
                            fefoByProduct.TryGetValue(sc.ProductId, out var fb) ? fb : new List<FefoBatch>())
                        : 0m
                };
                result.Add(vms);
            }

            return result;
        }

        /// <summary>محاولة حجز قفل الترحيل (Exclusive على مستوى الاتصال المفتوح).
        /// true = حُجز — يجب تحريره بـReleasePostLockAsync. false = ترحيل جارٍ.</summary>
        private async Task<(bool ok, Microsoft.Data.SqlClient.SqlConnection conn)> TryAcquirePostLockAsync()
        {
            var conn = (Microsoft.Data.SqlClient.SqlConnection)_dbContext.Database.GetDbConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'StockCountPost', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 10000; SELECT @r";
            int lockResult = (int)(await cmd.ExecuteScalarAsync())!;
            if (lockResult < 0)
            {
                await conn.CloseAsync();
                return (false, conn);
            }
            return (true, conn);
        }

        private static async Task ReleasePostLockAsync(Microsoft.Data.SqlClient.SqlConnection conn)
        {
            using var rel = conn.CreateCommand();
            rel.CommandText = "IF APPLOCK_MODE('public', N'StockCountPost', 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = N'StockCountPost', @LockOwner = 'Session'";
            await rel.ExecuteNonQueryAsync();
            await conn.CloseAsync();
        }

        /// <summary>
        /// تسجيل الكمية المعدودة لسطر واحد. عند العد الفعلي يُؤخذ snapshot للكمية
        /// الدفترية الحالية (BookQuantityAtCount) ولحظتها (CountedAt) — لا وقت بدء
        /// الجلسة. يحفظ أي حركة (بيع/استلام/تسوية) حصلت بعد العد في الرصيد النهائي.
        ///
        /// تحت قفل الترحيل — لا تعديل لسطر بينما يُرحَّل من مستخدم آخر.
        /// إذا كان السطر معدودًا سابقًا وأُعيد عدّه، تؤخذ لقطة جديدة بلحظة العد الجديد.
        ///
        /// صلاحية: يتطلب actorUserId صاحب صلاحية شاشة المخزون (بوابة في الخدمة —
        /// غياب الهوية أو معرف 0 يُرفض، ولا يُعدَّل السطر أبدًا بلا تصريح).
        /// </summary>
        public async Task<Result> SetCountedAsync(int countId, int? counted, int actorUserId)
        {
            // بوابة الصلاحية قبل أي قراءة/قفل/كتابة
            var permissionCheck = await CheckInventoryPermissionAsync(actorUserId);
            if (permissionCheck != null)
                return Result.Fail(permissionCheck.Message);

            var (locked, conn) = await TryAcquirePostLockAsync();
            if (!locked)
                return Result.Fail("الجلسة قيد الترحيل — انتظر انتهاء الترحيل.");

            try
            {
                var line = await _dbContext.StockCounts.FindAsync(countId);
                if (line == null)
                    return Result.Fail("سطر الجرد غير موجود.");

                if (line.IsPosted)
                    return Result.Fail("السطر مرحّل — لا يمكن تعديله.");

                if (counted.HasValue && counted.Value < 0)
                    return Result.Fail("الكمية المعدودة لا يمكن أن تكون سالبة.");

                if (counted.HasValue)
                {
                    // snapshot الكمية الدفترية لحظة العد — يُقرأ من القاعدة مباشرة
                    // (AsNoTracking: تجنب كيان متتبعًا قديمًا من نفس السياق)،
                    // ويحفظ أي حركات لاحقة للعد في الرصيد النهائي
                    var inventory = await _dbContext.Inventories
                        .AsNoTracking()
                        .FirstOrDefaultAsync(i => i.ProductId == line.ProductId);
                    line.BookQuantityAtCount = inventory?.QuantityAvailable ?? 0;
                    line.CountedAt = DateTime.UtcNow;
                    line.CountedQuantity = counted;
                }
                else
                {
                    // إلغاء العد: يُصبح السطر غير معدود (null)
                    line.CountedQuantity = null;
                    line.CountedAt = null;
                    line.BookQuantityAtCount = null;
                }

                await _dbContext.SaveChangesAsync();
                return Result.Ok();
            }
            finally
            {
                await ReleasePostLockAsync(conn);
            }
        }

        /// <summary>
        /// ملخص جلسة الجرد المفتوحة: المعدود/غير المعدود/الفروق وتكلفتها.
        /// الفرق لكل سطر = المعدود − BookQuantityAtCount (الكمية الدفترية لحظة العد).
        /// أعمدة BookQuantityAtCount = null: تستخدم الفرق من الكتيب الحي (توافق خلفي).
        /// CostOfDifferences: تقييم بتكاليف الدفعات المتأثرة فعلًا (COSTING.md) —
        /// العجز عبر الدفعات FEFO بتكلفة كل دفعة (UnitCost ?? Product.CostPrice)،
        /// والفائض باحتياط Product.CostPrice الموثق — لا UnitCost العامة للسطر.
        /// </summary>
        public async Task<StockCountSummaryVM> GetSummaryAsync()
        {
            var lines = await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted && sc.CountedQuantity.HasValue)
                .Select(sc => new
                {
                    sc.ProductId,
                    sc.BookQuantity,
                    sc.BookQuantityAtCount,
                    sc.CountedQuantity,
                    ProductCost = sc.Product.CostPrice,
                    Movements = _dbContext.StockMovements
                        .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                        .Sum(m => (int?)m.Quantity) ?? 0
                })
                .ToListAsync();

            var fefoByProduct = await LoadFefoBatchesAsync(lines.Select(l => l.ProductId).Distinct().ToList());

            var summary = new StockCountSummaryVM
            {
                TotalLines = await _dbContext.StockCounts.CountAsync(sc => !sc.IsPosted),
                CountedLines = lines.Count,
                UncountedLines = await _dbContext.StockCounts.CountAsync(sc => !sc.IsPosted && !sc.CountedQuantity.HasValue)
            };

            foreach (var l in lines)
            {
                // BookQuantityAtCount = snapshot لحظة العد; null = توافق خلفي (live book)
                int effectiveBook = l.BookQuantityAtCount ?? (l.BookQuantity + l.Movements);
                int diff = l.CountedQuantity!.Value - effectiveBook;
                summary.AbsolutDiff += Math.Abs(diff);
                summary.NetDiff += diff;
                // تقييم بتكاليف الدفعات (FEFO للعجز / احتياط Product.CostPrice للفائض)
                summary.CostOfDifferences += ComputeVarianceValue(
                    diff,
                    l.ProductCost,
                    fefoByProduct.TryGetValue(l.ProductId, out var fb) ? fb : new List<FefoBatch>());
                if (diff > 0) summary.SurplusLines++;
                else if (diff < 0) summary.ShortageLines++;
                else summary.MatchedLines++;
            }

            return summary;
        }

        /// <summary>
        /// ترحيل الفروق: كل سطر معدود بفرق ≠ 0 يُرحَّل عبر AdjustStockAsync القائمة
        /// (زيادة للفائض / نقصان للعجز). الفرق = المعدود − BookQuantityAtCount (snapshot لحظة العد).
        /// finalInventory = currentInventory + diff = (bookAtCount + postCountMovements)
        ///                + (counted − bookAtCount) = counted + postCountMovements —
        /// أي حركة حصلت بعد العد تُحفظ في الرصيد النهائي كما يجب.
        ///
        /// توافق خلفي: سطر معدود بقيم null من Upgrade_12 السابقة —
        /// BookQuantityAtCount = null → يحسب effectiveBook = bookSnapshot + movementsSinceSessionStart
        /// (السلوك القديم، أقل دقة لكنه لا يكسر بيانات قديمة).
        ///
        /// FIX (IDEMPOTENCY — STOCK COUNT PARTIAL FAILURE FIX):
        /// PostedMovementId on each line = دليل دائم على أن تسوية هذا السطر كُتبت.
        /// عند الترحيل: PostedMovementId يُضبط فور نجاح AdjustStockAsync وقبل SaveChangesAsync.
        /// عند إعادة الترحيل بعد فشل جزئي: PostedMovementId.HasValue = true → الخط يتجاوز.
        /// لا reliance on in-memory flags; no double-application possible.
        ///
        /// FIX (ATOMIC SAVE): PostedMovementId و IsPosted لجميع السطور تُحفظ معًا في
        /// SaveChangesAsync واحد. إذا فشل أي ترحيل بعد أول سطرين، PostedMovementId للسطر
        /// الأول محفوظ في الذاكرة ⇒ الخط يُتجاوز عند إعادة المحاولة ⇒ لا حركة مزدوجة.
        ///
        /// FIX (تزامن): قفل تطبيقي على مستوى الاتصال يمنع ترحيلًا مزدوجًا من طلبين متزامنين.
        ///
        /// صلاحية: يتطلب userId صاحب صلاحية شاشة المخزون — قبل القفل وقبل فحص
        /// السطور، فلا يمرّ ترحيل بلا تصريح حتى لو كانت كل الفروق صفرة (الجلسة
        /// تُقفل بنفسها: تعديل حالة يظل تعديلًا). نفس بت المخزون الذي يفحصه
        /// AdjustStockAsync داخليًا — سياسة موحّدة للترحيل المباشر وغير المباشر.
        /// </summary>
        public async Task<Result> PostSessionAsync(int userId)
        {
            // بوابة الصلاحية قبل أي قفل/كتابة — لا تجاوز بنداء مباشر ولا بفرق صفري
            var permissionCheck = await CheckInventoryPermissionAsync(userId);
            if (permissionCheck != null)
                return permissionCheck;

            var (locked, conn) = await TryAcquirePostLockAsync();
            if (!locked)
                return Result.Fail("الجلسة قيد الترحيل من مستخدم آخر — حاول بعد لحظات.");

            try
            {
                var lines = await _dbContext.StockCounts
                    .Where(sc => !sc.IsPosted)
                    .ToListAsync();

                if (lines.Count == 0)
                    return Result.Fail("لا توجد جلسة جرد مفتوحة.");

                int uncounted = lines.Count(l => !l.CountedQuantity.HasValue);
                if (uncounted > 0)
                    return Result.Fail($"{uncounted} منتجًا لم يُعدّ بعد. أكمل العد أو صفّر الكمية (سطر بمعدود صفر فرق مرحّل دفتريًا).");

                int sessionId = lines[0].CountSessionId;
                int adjustments = 0;

                // للحالات الخلفيّة (BookQuantityAtCount = null): net movements since session start per product
                // — تحسب مرة واحدة خارج الحلقة (أقل كفاءة لكن نادرًا ما يُستخدم)
                DateTime startedAt = lines.Min(l => l.CreatedAt);
                var movementNet = await _dbContext.StockMovements
                    .Where(m => m.CreatedAt >= startedAt)
                    .GroupBy(m => m.ProductId)
                    .Select(g => new { ProductId = g.Key, Net = g.Sum(m => m.Quantity) })
                    .ToDictionaryAsync(x => x.ProductId, x => x.Net);

                var stockService = new clsStockService(_dbContext);

                foreach (var line in lines)
                {
                    // IDEMPOTENCY (durable, DB-backed): حركة تسوية مرتبطة بهذا السطر
                    // (SourceType = ManualAdjustment + SourceId = CountId) موجودة بالفعل
                    // ⇒ نجحت في محاولة سابقة فشلت قبل حفظ IsPosted — اتخطاه وأصلح السجل الدائم.
                    // الفحص من القاعدة لا من الذاكرة ⇒ محصّن بعد إعادة تشغيل التطبيق أيضًا.
                    var existing = await _dbContext.StockMovements
                        .AsNoTracking()
                        .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == line.CountId)
                        .OrderByDescending(m => m.MovementId)
                        .FirstOrDefaultAsync();

                    if (existing != null)
                    {
                        line.PostedMovementId = existing.MovementId;
                        line.IsPosted = true;
                        continue;
                    }

                    // BookQuantityAtCount = snapshot لحظة العد الفعلي — الدفتري الصحيح للمقارنة
                    // null = توافق خلفي: استخدم الكتيب الحي عند بدء الجلسة
                    int effectiveBook;
                    if (line.BookQuantityAtCount.HasValue)
                    {
                        effectiveBook = line.BookQuantityAtCount.Value;
                    }
                    else
                    {
                        // سلوك قديم للتوافق: دفتري = لقطة الجلسة + صافي حركات منذ بدئها
                        effectiveBook = line.BookQuantity
                            + (movementNet.TryGetValue(line.ProductId, out int net) ? net : 0);
                    }

                    int diff = line.CountedQuantity!.Value - effectiveBook;
                    if (diff == 0)
                    {
                        // فرق صفري: لا حركة تسوية — لكن الخط يُقفل كمرحّل
                        line.IsPosted = true;
                        continue;
                    }

                    // ترحيل عبر الخدمة القائمة — الترانزاكشن وصلاحيات المحاسب تعمل مجانًا.
                    // SourceId = CountId يربط الحركة الناتجة بهذا السطر (idempotency key).
                    var r = await stockService.AdjustStockAsync(new Common.DTOs.RequestModels.DtoStockAdjustmentRequest
                    {
                        ProductId = line.ProductId,
                        Quantity = Math.Abs(diff),
                        IsIncrease = diff > 0,
                        Reason = $"ترحيل فرق جرد #{sessionId}: {(diff > 0 ? "فائض" : "عجز")} {Math.Abs(diff)}",
                        BatchNumber = null,
                        ExpiryDate = null,
                        UserId = userId,
                        SourceId = line.CountId
                    });

                    if (!r.IsSuccess)
                        return Result.Fail($"فشل ترحيل فرق '{line.ProductName}': {r.Message} — توقف عند هذا المنتج.");

                    // IDEMPOTENCY: اربط السطر بحركة التسوية المُنشأة واحفظ PostedMovementId
                    // قبل أي حفظ. إذا فشل SaveChangesAsync لاحقًا، الحركة محفوظة في القاعدة
                    // والفحص المسبق في أعلى الحلقة يتخطى السطر ⇒ لا حركة مزدوجة أبدًا.
                    var movement = await _dbContext.StockMovements
                        .AsNoTracking()
                        .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId == line.CountId)
                        .OrderByDescending(m => m.MovementId)
                        .FirstOrDefaultAsync();

                    line.PostedMovementId = movement?.MovementId;
                    line.IsPosted = true;
                    adjustments++;
                }

                // ATOMIC SAVE: جميع PostedMovementId و IsPosted تُحفظ معًا في طلب واحد.
                // PostedMovementId لكل خط مُرحَّل مضبوط مسبقًا ⇒ لا حركة مزدوجة عند إعادة المحاولة.
                await _dbContext.SaveChangesAsync();

                clsAudit.Log(_dbContext, userId, "PostStockCount",
                    $"ترحيل جلسة جرد #{sessionId} — {adjustments} تسوية من {lines.Count} منتجًا");
                await _dbContext.SaveChangesAsync();

                return Result.Ok($"تم ترحيل الجرد: {adjustments} تسوية على {lines.Count} منتجًا.");
            }
            finally
            {
                await ReleasePostLockAsync(conn);
            }
        }

        /// <summary>آخر جلسة جرد مرحّلة (سطورها للعرض التاريخي).
        /// DifferenceCost للسطر المرحّل = التكلفة الفعلية لحركاته (idempotency key:
        /// SourceType = ManualAdjustment + SourceId = CountId) — حركة لكل دفعة
        /// خُصمت FEFO بتكلفتها (UnitCost ?? Product.CostPrice)، وحركة الفائض
        /// بكميتها (الدفعة التلقائية بلا تكلفة → احتياط Product.CostPrice).
        /// القيمة تطابق حركات المخزون حرفيًا — لا تقدير ولا Product.CostPrice العامة للسطر.</summary>
        public async Task<List<StockCountVM>> GetLastPostedSessionAsync()
        {
            int? lastSession = await _dbContext.StockCounts
                .Where(sc => sc.IsPosted)
                .OrderByDescending(sc => sc.CountSessionId)
                .Select(sc => (int?)sc.CountSessionId)
                .FirstOrDefaultAsync();

            if (lastSession == null)
                return new List<StockCountVM>();

            var rows = await _dbContext.StockCounts
                .Where(sc => sc.CountSessionId == lastSession)
                .OrderBy(sc => sc.ProductName)
                .Select(sc => new
                {
                    sc.CountId,
                    sc.CountSessionId,
                    sc.ProductId,
                    sc.ProductName,
                    sc.BookQuantity,
                    sc.CountedQuantity,
                    sc.CountedAt,
                    sc.UnitCost,
                    sc.IsPosted,
                    ProductCost = sc.Product.CostPrice
                })
                .ToListAsync();

            // حركات التسوية الفعلية لكل سطر — تكلفتها من الدفعة المستهلكة فعليًا
            var countIds = rows.Select(r => r.CountId).ToList();
            var movements = await _dbContext.StockMovements
                .Where(m => m.SourceType == "ManualAdjustment" && m.SourceId != null && countIds.Contains(m.SourceId.Value))
                .Select(m => new
                {
                    m.SourceId,
                    m.MovementType,
                    m.Quantity,
                    BatchUnitCost = m.Batch != null ? m.Batch.UnitCost : null
                })
                .ToListAsync();

            var movementsByLine = movements
                .GroupBy(m => m.SourceId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<StockCountVM>(rows.Count);
            foreach (var sc in rows)
            {
                var vms = new StockCountVM
                {
                    CountId = sc.CountId,
                    CountSessionId = sc.CountSessionId,
                    ProductId = sc.ProductId,
                    ProductName = sc.ProductName,
                    BookQuantity = sc.BookQuantity,
                    CountedQuantity = sc.CountedQuantity,
                    CountedAt = sc.CountedAt,
                    UnitCost = sc.UnitCost,
                    IsPosted = sc.IsPosted
                };

                if (movementsByLine.TryGetValue(sc.CountId, out var lineMovements))
                {
                    // كل حركة بتكلفة دفعتها الفعلية — مجموعها يطابق حركات المخزون حرفيًا
                    vms.DifferenceCost = lineMovements.Sum(m =>
                        m.Quantity * (m.BatchUnitCost ?? sc.ProductCost));
                }

                result.Add(vms);
            }

            return result;
        }
    }
}
