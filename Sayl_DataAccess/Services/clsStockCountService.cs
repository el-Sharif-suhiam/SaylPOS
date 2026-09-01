using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// الجرد الفعلي للمخزون: فتح جلسة بلقطة دفترية (BookQuantity Snapshot)، إدخال
    /// المعدود فعليًا، تقرير الفروق وتكلفتها، ثم ترحيل الفروق عبر AdjustStockAsync
    /// القائمة (بترانزاكشناتها وصلاحياتها) — لا كتابة مباشرة في المخزون أبدًا.
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
        /// فتح جلسة جرد جديدة: لقطة كل منتج مخزني نشط بكميته الدفترية وتكلفته الحالية.
        /// يُرفض إن وُجدت جلسة مفتوحة.
        /// </summary>
        public async Task<Result<int>> StartSessionAsync(int userId)
        {
            if (await HasOpenSessionAsync())
                return Result<int>.Fail("توجد جلسة جرد مفتوحة بالفعل — أكملها أو ترحّلها أولاً.");

            var products = await _dbContext.Products
                .Where(p => p.Type == "stock" && p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.ProductId, p.Name, p.CostPrice, Book = (p.Inventory != null ? p.Inventory.QuantityAvailable : 0) ?? 0 })
                .ToListAsync();

            if (products.Count == 0)
                return Result<int>.Fail("لا توجد منتجات مخزنية نشطة للجرد.");

            // معرف الجلسة = أعلى جلسة سابقة + 1 (0 لأول جرد)
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
                    CreatedAt = DateTime.Now
                });
            }

            await _dbContext.SaveChangesAsync();

            clsAudit.Log(_dbContext, userId, "StartStockCount",
                $"فتح جلسة جرد #{sessionId} بـ {products.Count} منتجًا");
            await _dbContext.SaveChangesAsync();

            return Result<int>.Ok(sessionId, $"تم فتح جلسة الجرد بـ {products.Count} منتجًا.");
        }

        /// <summary>سطور جلسة الجرد الحالية (المفتوحة = غير المرحّلة).</summary>
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
                    BookQuantity = sc.BookQuantity,
                    CountedQuantity = sc.CountedQuantity,
                    UnitCost = sc.UnitCost
                })
                .ToListAsync();
        }

        /// <summary>تسجيل الكمية المعدودة لسطر واحد (أو تصفيره إلى غير معدود بـ null).</summary>
        public async Task<Result> SetCountedAsync(int countId, int? counted)
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

        /// <summary>ملخص جلسة الجرد المفتوحة: المعدود/غير المعدود/الفروق وتكلفتها.</summary>
        public async Task<StockCountSummaryVM> GetSummaryAsync()
        {
            var lines = await _dbContext.StockCounts
                .Where(sc => !sc.IsPosted)
                .Select(sc => new { sc.BookQuantity, sc.CountedQuantity, sc.UnitCost })
                .ToListAsync();

            var summary = new StockCountSummaryVM
            {
                TotalLines = lines.Count,
                CountedLines = lines.Count(l => l.CountedQuantity.HasValue),
                UncountedLines = lines.Count(l => !l.CountedQuantity.HasValue)
            };

            foreach (var l in lines.Where(l => l.CountedQuantity.HasValue))
            {
                int diff = l.CountedQuantity.Value - l.BookQuantity;
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
        /// (زيادة للفائض / نقصان للعجز) داخل ترانزاكشن واحدة — ثم وسم الجلسة مرحّلة.
        /// السطور غير المعدودة تُسمَّى مرحّلة بلا ترحيل (الدفتري = الصحيح المفترض).
        /// </summary>
                /// <summary>
        /// ترحيل الفروق: كل سطر معدود بفرق ≠ 0 يُرحَّل عبر AdjustStockAsync القائمة
        /// (زيادة للفائض / نقصان للعجز) — كل تسوية بترانزاكشنها الذرية القائمة
        /// (بلا ترانزاكشن خارجية: التداخل مع معاملة الخدمة الداخلية يفشلها).
        /// السطور غير المعدودة تُقفل مرحّلة بلا ترحيل (الدفتري = الصحيح المفترض).
        /// </summary>
        public async Task<Result> PostSessionAsync(int userId)
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

            foreach (var line in lines.Where(l => l.CountedQuantity!.Value != l.BookQuantity))
            {
                int diff = line.CountedQuantity!.Value - line.BookQuantity;

                // ترحيل عبر الخدمة القائمة — التراني وصلاحيات المحاسب تعمل مجانًا
                var stockService = new clsStockService(_dbContext);
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
                    return Result.Fail($"فشل ترحيل فرق '{line.ProductName}': {r.Message} — توقف عند هذا المنتج (ما قبله رُحّل).");

                line.IsPosted = true;
                await _dbContext.SaveChangesAsync();
                adjustments++;
            }

            // كل السطور (المطابقة بلا فرق أيضًا) تُقفل كمرحّلة
            foreach (var line in lines)
                line.IsPosted = true;

            clsAudit.Log(_dbContext, userId, "PostStockCount",
                $"ترحيل جلسة جرد #{sessionId} — {adjustments} تسوية من {lines.Count} منتجًا");
            await _dbContext.SaveChangesAsync();

            return Result.Ok($"تم ترحيل الجرد: {adjustments} تسوية على {lines.Count} منتجًا.");
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
