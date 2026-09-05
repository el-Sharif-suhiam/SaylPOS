using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsPosSessionService
    {
        private readonly SaylDbContext _dbContext;

        public clsPosSessionService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<Result<PosSessionVM>> GetOpenSessionByUserAsync(int userId)
        {
            var session = await _dbContext.PosSessions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.UserId == userId && s.Status == enPosSessionStatusType.open);

            if (session == null)
                return Result<PosSessionVM>.Fail("لا توجد جلسة مفتوحة لهذا المستخدم.");

            return Result<PosSessionVM>.Ok(MapToVM(session));
        }

        public async Task<Result<List<PosSessionVM>>> GetSessionsListAsync(int? userId = null, enPosSessionStatusType? status = null)
        {
            var query = _dbContext.PosSessions.Include(s => s.User).AsQueryable();

            if (userId.HasValue)
                query = query.Where(s => s.UserId == userId.Value);

            if (status.HasValue)
                query = query.Where(s => s.Status == status.Value);

            var sessions = await query
                .OrderByDescending(s => s.StartedAt)
                .ToListAsync();

            return Result<List<PosSessionVM>>.Ok(sessions.Select(MapToVM).ToList());
        }

        public async Task<Result<int>> OpenSessionAsync(int userId, decimal openingAmount)
        {
            if (openingAmount < 0)
                return Result<int>.Fail("مبلغ الافتتاح يجب أن يكون صفرا أو أكثر.");

            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result<int>.Fail("المستخدم غير موجود.");

            bool hasOpenSession = await _dbContext.PosSessions
                .AnyAsync(s => s.UserId == userId && s.Status == enPosSessionStatusType.open);

            if (hasOpenSession)
                return Result<int>.Fail("يوجد لدى المستخدم جلسة مفتوحة بالفعل.");

            var session = new PosSession
            {
                UserId = userId,
                OpeningAmount = openingAmount,
                StartedAt = DateTime.UtcNow,
                Status = enPosSessionStatusType.open
            };

            _dbContext.PosSessions.Add(session);
            try
            {
                bool saved = (await _dbContext.SaveChangesAsync()) > 0;
                if (!saved)
                    return Result<int>.Fail("فشل فتح الجلسة.");
                return Result<int>.Ok(session.SessionId, "تم فتح الجلسة بنجاح.");
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UX_POSSessions_OneOpenPerUser"))
            {
                // فهرس فريد مفلتر يمنع جلستين مفتوحتين لمستخدم واحد حتى لو تجاوز الفحصَ طلبان متوازيان
                _dbContext.ChangeTracker.Clear();
                return Result<int>.Fail("يوجد لدى المستخدم جلسة مفتوحة بالفعل.");
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

        public async Task<Result> CloseSessionAsync(int sessionId, decimal closingAmount)
        {
            if (closingAmount < 0)
                return Result.Fail("مبلغ الإغلاق يجب أن يكون صفرا أو أكثر.");

            var session = await _dbContext.PosSessions.FindAsync(sessionId);
            if (session == null)
                return Result.Fail("الجلسة غير موجودة.");

            if (session.Status != enPosSessionStatusType.open)
                return Result.Fail("الجلسة مغلقة بالفعل.");

            // الانتقال الذري داخل معاملة: أول من يصل يفوز — يمنع إغلاقين متوازيين
            // ويلتقط حركات نقد تُسجَّل الآن (سيرفضها AddMovementAsync على جلسة مغلقة)
            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                int claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE POSSessions SET Status = 'closed' WHERE SessionId = {sessionId} AND Status = 'open'");
                if (claimed == 0)
                    return Result.Fail("الجلسة مغلقة بالفعل.");

                // النقد المتوقع في الدرج = الافتتاحي + مبيعات نقدي − مرتجعات نقدي + قبض − صرف
                // (الشبكة والتحويل لا تدخل الدرج)
                //
                // 📖 VULN-05 — المصدر المزدوج (نفس منطق clsCashService.GetSessionCashSummaryAsync):
                //  1) جديد: دفعات موجهة صراحة لدرج هذه الجلسة (Payment.SessionId).
                //  2) متوافق: دفعات قديمة SessionId=null تُنسب لجلسة فاتورتها —
                //     فلا تتزحزح أرقام أي تقرير Z قديم بعد الترقية.
                decimal cashIncomeNew = await _dbContext.Payments
                    .Where(p => p.Type == enPaymentType.income
                             && p.PaymentMethod == "cash"
                             && p.SessionId == sessionId)
                    .SumAsync(p => (decimal?)p.Amount) ?? 0m;

                decimal cashOutcomeNew = await _dbContext.Payments
                    .Where(p => p.Type == enPaymentType.outcome
                             && p.PaymentMethod == "cash"
                             && p.SessionId == sessionId)
                    .SumAsync(p => (decimal?)p.Amount) ?? 0m;

                var sessionInvoiceIds = _dbContext.Invoices
                    .Where(i => i.SessionId == sessionId && i.Status == enInvoicesStatus.completed)
                    .Select(i => (int?)i.InvoiceId);

                decimal cashIncomeLegacy = await _dbContext.Payments
                    .Where(p => p.Type == enPaymentType.income
                             && p.PaymentMethod == "cash"
                             && p.SessionId == null
                             && p.InvoiceId != null
                             && sessionInvoiceIds.Contains(p.InvoiceId))
                    .SumAsync(p => (decimal?)p.Amount) ?? 0m;

                decimal cashOutcomeLegacy = await _dbContext.Payments
                    .Where(p => p.Type == enPaymentType.outcome
                             && p.PaymentMethod == "cash"
                             && p.SessionId == null
                             && p.InvoiceId != null
                             && sessionInvoiceIds.Contains(p.InvoiceId))
                    .SumAsync(p => (decimal?)p.Amount) ?? 0m;

                decimal cashIn = await _dbContext.CashMovements
                    .Where(m => m.SessionId == sessionId && m.MovementType == 'I')
                    .SumAsync(m => (decimal?)m.Amount) ?? 0m;

                decimal cashOut = await _dbContext.CashMovements
                    .Where(m => m.SessionId == sessionId && m.MovementType == 'O')
                    .SumAsync(m => (decimal?)m.Amount) ?? 0m;

                decimal expected = session.OpeningAmount
                                 + (cashIncomeNew + cashIncomeLegacy)
                                 - (cashOutcomeNew + cashOutcomeLegacy)
                                 + cashIn - cashOut;

                session.ExpectedAmount = expected;
                session.ClosingAmount = closingAmount;
                session.ClosedAt = DateTime.UtcNow;
                session.Status = enPosSessionStatusType.closed;

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return Result.Ok("تم إغلاق الجلسة بنجاح.");
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail("فشل إغلاق الجلسة.");
            }
        }

        private static PosSessionVM MapToVM(PosSession session)
        {
            return new PosSessionVM
            {
                SessionId = session.SessionId,
                UserId = session.UserId,
                UserName = session.User?.Username,
                OpeningAmount = session.OpeningAmount,
                ClosingAmount = session.ClosingAmount,
                ExpectedAmount = session.ExpectedAmount,
                StartedAt = session.StartedAt,
                ClosedAt = session.ClosedAt,
                Status = session.Status.ToString()
            };
        }
    }
}
