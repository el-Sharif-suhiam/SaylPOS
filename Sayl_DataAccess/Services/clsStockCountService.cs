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
    /// FIX (لقطة قديمة): البيع/الشراء يستمران أثناء الجرد، فتصبح اللقطة قديمة.
    /// عند الترحيل يُعاد حساب الدفتري الفعّال: BookQuantity + صافي حركات
    /// StockMovements منذ بداية الجلسة — والفرق (المعدود − الفعّال) هو ما يُرحَّل.
    /// لا يُطبَّق فرق لقطة قديمة على الرصيد الحالي أبدًا.
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

        /// <summary>يضمن فهرس الجلسة المفتوحة الفريد على أي قاعدة (قديمة أو جديدة) — يُنادى عند الإقلاع.</summary>
        public const string EnsureCountSchemaSql = @"
IF OBJECT_ID(N'dbo.StockCounts', N'U') IS NOT NULL
    AND NOT EXISTS (
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
END";

        public async Task EnsureCountSchemaAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureCountSchemaSql);

        /// <summary>
        /// فتح جلسة جرد جديدة: لقطة كل منتج مخزني نشط بكميته الدفترية وتكلفته الحالية.
        /// يُرفض إن وُجدت جلسة مفتوحة. داخل ترانزاكشن Serializable + قفل تطبيقي
        /// — طلبان متوازيان يتصافحان: الثاني يرى الجلسة المفتوحة ويرفض،
        /// ورقم الجلسة (أعلى + 1) لا يتكرر أبدًا.
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

        /// <summary>سطور جلسة الجرد الحالية (المفتوحة = غير المرحّلة).
        /// BookQuantity المعرض = اللقطة + صافي الحركات منذ بدء الجلسة
        /// — الفرق المعروض والتكلفة يحسوبان على الدفتري الفعّال الحالي.</summary>
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
                    BookQuantity = sc.BookQuantity + (_dbContext.StockMovements
                        .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                        .Sum(m => (int?)m.Quantity) ?? 0),
                    CountedQuantity = sc.CountedQuantity,
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

        /// <summary>تسجيل الكمية المعدودة لسطر واحد (أو تصفيره إلى غير معدود بـ null).
        /// تحت قفل الترحيل — لا تعديل لسطر بينما يُرحَّل من مستخدم آخر.</summary>
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

                line.CountedQuantity = counted;
                await _dbContext.SaveChangesAsync();
                return Result.Ok();
            }
            finally
            {
                await ReleasePostLockAsync(conn);
            }
        }

        /// <summary>ملخص جلسة الجرد المفتوحة: المعدود/غير المعدود/الفروق وتكلفتها — على الدفتري الفعّال (اللقطة + الحركات منذ البدء).</summary>
        public async Task<StockCountSummaryVM> GetSummaryAsync()
        {
            var lines = await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted)
                .Select(sc => new
                {
                    sc.BookQuantity,
                    sc.CountedQuantity,
                    sc.UnitCost,
                    Movements = _dbContext.StockMovements
                        .Where(m => m.ProductId == sc.ProductId && m.CreatedAt >= sc.CreatedAt)
                        .Sum(m => (int?)m.Quantity) ?? 0
                })
                .ToListAsync();

            var summary = new StockCountSummaryVM
            {
                TotalLines = lines.Count,
                CountedLines = lines.Count(l => l.CountedQuantity.HasValue),
                UncountedLines = lines.Count(l => !l.CountedQuantity.HasValue)
            };

            foreach (var l in lines.Where(l => l.CountedQuantity.HasValue))
            {
                int diff = l.CountedQuantity.Value - (l.BookQuantity + l.Movements);
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
        /// (زيادة للفائض / نقصان للعجز). FIX (لقطة قديمة): الدفتري عند الترحيل =
        /// اللقطة + صافي حركات StockMovements منذ بدء الجلسة — فبيعة/شراء حصل
        /// أثناء العد لا يُحتسب عليه مرتين، ولا يُطبَّق فرق لقطة قديمة على الرصيد
        /// الحالي أبدًا. FIX (تزامن): قفل تطبيقي على مستوى الاتصال (بلا ترانزاكشن
        /// خارجية — AdjustStockAsync لها ترانزاكشنها الداخلية) يمنع ترحيلًا مزدوجًا
        /// من طلبين متزامنين. والمصالحة عبر سجل الحركات تجعل إعادة الترحيل بعد فشل
        /// جزئي محصورة: السطر المُرحَّل فعلًا يظهر فرقه صفرًا فيتخطى.
        /// السطور غير المعدودة تُقفل مرحّلة بلا ترحيل (الدفتري الفعّال = الصحيح المفترض).
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

                // صافي الحركات منذ بدء الجلسة لكل منتج — دفعة استعلام واحدة
                DateTime startedAt = lines.Min(l => l.CreatedAt);
                var movementNet = await _dbContext.StockMovements
                    .Where(m => m.CreatedAt >= startedAt)
                    .GroupBy(m => m.ProductId)
                    .Select(g => new { ProductId = g.Key, Net = g.Sum(m => m.Quantity) })
                    .ToDictionaryAsync(x => x.ProductId, x => x.Net);

                var stockService = new clsStockService(_dbContext);

                foreach (var line in lines)
                {
                    // الدفتري الفعّال = اللقطة + الحركات منذ البدء — لا لقطة قديمة أبدًا
                    int effectiveBook = line.BookQuantity + (movementNet.TryGetValue(line.ProductId, out int net) ? net : 0);
                    int diff = line.CountedQuantity!.Value - effectiveBook;
                    if (diff == 0)
                        continue;

                    // ترحيل عبر الخدمة القائمة — التراني وصلاحيات المحاسب تعمل مجانًا
                    var r = await stockService.AdjustStockAsync(new Common.DTOs.RequestModels.DtoStockAdjustmentRequest
                    {
                        ProductId = line.ProductId,
                        Quantity = Math.Abs(diff),
                        IsIncrease = diff > 0,
                        Reason = $"ترحيل فرق جرد #{sessionId}: {(diff > 0 ? "فائض" : "عجز")} {Math.Abs(diff)}",
                        BatchNumber = null,
                        ExpiryDate = null,
                        UserId = userId
                    });

                    if (!r.IsSuccess)
                        return Result.Fail($"فشل ترحيل فرق '{line.ProductName}': {r.Message} — توقف عند هذا المنتج (ما قبله رُحّل، وإعادته آمنة: الفرق يُحسب من جديد).");

                    line.IsPosted = true;
                    adjustments++;
                }

                // كل السطور (المطابقة بلا فرق أيضًا) تُقفل كمرحّلة
                foreach (var line in lines)
                    line.IsPosted = true;

                await _dbContext.SaveChangesAsync();

                clsAudit.Log(_dbContext, userId, "PostStockCount",
                    $"ترحيل جلسة جرد #{sessionId} — {adjustments} تسوية من {lines.Count} منتجًا (مصالحة حركات الجلسة)");
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
                    UnitCost = sc.UnitCost,
                    IsPosted = sc.IsPosted
                })
                .ToListAsync();
        }
    }
}
