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
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved
                ? Result<int>.Ok(session.SessionId, "تم فتح الجلسة بنجاح.")
                : Result<int>.Fail("فشل فتح الجلسة.");
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

            decimal sessionIncome = await _dbContext.Payments
                .Where(p => p.Type == enPaymentType.income
                         && p.Invoice != null
                         && p.Invoice.SessionId == sessionId)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            session.ExpectedAmount = session.OpeningAmount + sessionIncome;
            session.ClosingAmount = closingAmount;
            session.ClosedAt = DateTime.UtcNow;
            session.Status = enPosSessionStatusType.closed;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم إغلاق الجلسة بنجاح.");
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
