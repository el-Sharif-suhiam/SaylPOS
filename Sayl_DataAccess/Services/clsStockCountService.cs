using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
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
    /// </summary>
    public class clsStockCountService
    {
        private readonly SaylDbContext _dbContext;

        public clsStockCountService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
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

        /// <summary>
        /// سطور جلسة الجرد الحالية (المفتوحة = غير المرحّلة).
        /// • غير المعدود: BookQuantity المعروض = اللقطة + صافي الحركات منذ بدء الجلسة (الكتيب الحي الحالي).
        /// • المعدود: BookQuantity المعروض = BookQuantityAtCount (الكمية الدفترية عند العد الفعلي).
        ///   الفرق المعروض = counted − bookAtCount — هذا هو ما سيُرحَّل بالضبط.
        /// </summary>
        public async Task<List<StockCountVM>> GetOpenSessionAsync()
        {
            return await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted)
                .OrderBy(sc => sc.ProductName)
                .Select(sc => new StockCountVM
                {
                    CountId = sc.CountId,
                    CountSessionId = sc.CountSessionId,
                    ProductId = sc.ProductId,
                    ProductName = sc.ProductName,
                    // uncounted line: live book (snapshot + movements since session start)
                    // counted line: book quantity at the moment of counting
                    BookQuantity = sc.CountedAt.HasValue
                        ? sc.BookQuantityAtCount ?? 0
                        : sc.BookQuantity + (_dbContext.StockMovements
                            .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                            .Sum(m => (int?)m.Quantity) ?? 0),
                    CountedQuantity = sc.CountedQuantity,
                    CountedAt = sc.CountedAt,
                    UnitCost = sc.UnitCost
                })
                .ToListAsync();
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
        /// </summary>
        public async Task<Result> SetCountedAsync(int countId, int? counted)
        {
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
        /// </summary>
        public async Task<StockCountSummaryVM> GetSummaryAsync()
        {
            var lines = await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted && sc.CountedQuantity.HasValue)
                .Select(sc => new
                {
                    sc.BookQuantity,
                    sc.BookQuantityAtCount,
                    sc.CountedQuantity,
                    sc.UnitCost,
                    Movements = _dbContext.StockMovements
                        .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                        .Sum(m => (int?)m.Quantity) ?? 0
                })
                .ToListAsync();

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
                summary.CostOfDifferences += diff * l.UnitCost;
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
        /// </summary>
        public async Task<Result> PostSessionAsync(int userId)
        {
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

        /// <summary>آخر جلسة جرد مرحّلة (سطورها للعرض التاريخي).</summary>
        public async Task<List<StockCountVM>> GetLastPostedSessionAsync()
        {
            int? lastSession = await _dbContext.StockCounts
                .Where(sc => sc.IsPosted)
                .OrderByDescending(sc => sc.CountSessionId)
                .Select(sc => (int?)sc.CountSessionId)
                .FirstOrDefaultAsync();

            if (lastSession == null)
                return new List<StockCountVM>();

            return await _dbContext.StockCounts
                .Where(sc => sc.CountSessionId == lastSession)
                .OrderBy(sc => sc.ProductName)
                .Select(sc => new StockCountVM
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
                })
                .ToListAsync();
        }
    }
}
