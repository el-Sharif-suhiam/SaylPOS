using Common.Enums;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>حركات النقد داخل جلسة البيع (قبض/صرف) + ملخص النقد المتوقع للجلسة.</summary>
    public class clsCashService
    {
        private readonly SaylDbContext _dbContext;

        public clsCashService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<Result> AddMovementAsync(int sessionId, char movementType, decimal amount, string reason, int userId)
        {
            if (movementType != 'I' && movementType != 'O')
                return Result.Fail("نوع الحركة غير صالح.");

            if (amount <= 0)
                return Result.Fail("المبلغ يجب أن يكون أكبر من صفر.");

            if (string.IsNullOrWhiteSpace(reason))
                return Result.Fail("السبب مطلوب.");

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // فحص الجلسة داخل المعاملة: يمنع قيد حركة على جلسة تُغلق بالتوازي الآن
                var session = await _dbContext.PosSessions.FindAsync(sessionId);
                if (session == null || session.Status != enPosSessionStatusType.open)
                    return Result.Fail("الجلسة غير موجودة أو مغلقة.");

                _dbContext.CashMovements.Add(new CashMovement
                {
                    SessionId = sessionId,
                    MovementType = movementType,
                    Amount = amount,
                    Reason = reason.Trim(),
                    UserId = userId,
                    CreatedAt = DateTime.Now
                });

                _dbContext.SystemLogs.Add(new SystemLog
                {
                    UserId = userId,
                    Action = movementType == 'I' ? "CashIn" : "CashOut",
                    Details = $"مبلغ: {amount:N2} — السبب: {reason.Trim()} — جلسة #{sessionId}",
                    CreatedAt = DateTime.Now
                });

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                return Result.Ok(movementType == 'I' ? "تم تسجيل قبض النقد." : "تم تسجيل صرف النقد.");
            }
            catch
            {
                await transaction.RollbackAsync();
                return Result.Fail("فشل تسجيل حركة النقد.");
            }
        }

        /// <summary>ملخص نقد الجلسة: الافتتاحي + مبيعات نقدي − مرتجعات نقدي + قبض − صرف.</summary>
        public async Task<Result<SessionCashSummary>> GetSessionCashSummaryAsync(int sessionId)
        {
            var session = await _dbContext.PosSessions.FindAsync(sessionId);
            if (session == null)
                return Result<SessionCashSummary>.Fail("الجلسة غير موجودة.");

            var sessionInvoiceIds = _dbContext.Invoices
                .Where(i => i.SessionId == sessionId && i.Status == enInvoicesStatus.completed)
                .Select(i => (int?)i.InvoiceId);

            decimal cashSales = await _dbContext.Payments
                .Where(p => sessionInvoiceIds.Contains(p.InvoiceId)
                         && p.PaymentMethod == "cash"
                         && p.Type == enPaymentType.income)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal cashReturns = await _dbContext.Payments
                .Where(p => sessionInvoiceIds.Contains(p.InvoiceId)
                         && p.PaymentMethod == "cash"
                         && p.Type == enPaymentType.outcome)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            decimal cashIn = await _dbContext.CashMovements
                .Where(m => m.SessionId == sessionId && m.MovementType == 'I')
                .SumAsync(m => (decimal?)m.Amount) ?? 0m;

            decimal cashOut = await _dbContext.CashMovements
                .Where(m => m.SessionId == sessionId && m.MovementType == 'O')
                .SumAsync(m => (decimal?)m.Amount) ?? 0m;

            var summary = new SessionCashSummary
            {
                SessionId = sessionId,
                OpeningAmount = session.OpeningAmount,
                CashSales = cashSales,
                CashReturns = cashReturns,
                CashIn = cashIn,
                CashOut = cashOut,
                ExpectedCash = session.OpeningAmount + cashSales - cashReturns + cashIn - cashOut
            };

            return Result<SessionCashSummary>.Ok(summary);
        }
    }

    public class SessionCashSummary
    {
        public int SessionId { get; set; }
        public decimal OpeningAmount { get; set; }
        public decimal CashSales { get; set; }
        public decimal CashReturns { get; set; }
        public decimal CashIn { get; set; }
        public decimal CashOut { get; set; }
        public decimal ExpectedCash { get; set; }
    }
}
