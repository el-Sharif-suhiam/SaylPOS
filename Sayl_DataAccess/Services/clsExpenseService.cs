using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsExpenseService
    {
        private readonly SaylDbContext _dbContext;

        public clsExpenseService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<DtoExpense>> GetExpensesListAsync(DateOnly? fromDate = null, DateOnly? toDate = null, string? category = null, string search = "")
        {
            var query = _dbContext.Expenses.AsQueryable();

            if (fromDate.HasValue)
                query = query.Where(e => e.ExpenseDate >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(e => e.ExpenseDate <= toDate.Value);

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(e => e.Category == category);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(e => e.Description.Contains(search) || (e.Notes != null && e.Notes.Contains(search)));

            return await query
                .OrderByDescending(e => e.ExpenseDate)
                .Select(e => new DtoExpense
                {
                    ExpenseId = e.ExpenseId,
                    Description = e.Description,
                    Amount = e.Amount,
                    ExpenseDate = e.ExpenseDate,
                    Category = e.Category,
                    Notes = e.Notes,
                    UserId = e.UserId,
                    CreatedAt = e.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<Result<DtoExpense>> GetExpenseByIdAsync(int expenseId)
        {
            var expense = await _dbContext.Expenses.FindAsync(expenseId);
            if (expense == null)
                return Result<DtoExpense>.Fail("المصروف غير موجود.");

            return Result<DtoExpense>.Ok(new DtoExpense
            {
                ExpenseId = expense.ExpenseId,
                Description = expense.Description,
                Amount = expense.Amount,
                ExpenseDate = expense.ExpenseDate,
                Category = expense.Category,
                Notes = expense.Notes,
                UserId = expense.UserId,
                CreatedAt = expense.CreatedAt
            });
        }

        // VULN-26: المصروف يعدل ربح الفترة مباشرة — الإضافة/التعديل الآن
        // يطلبان محاسبًا فأعلى (الحذف كان وحده محروسًا).
        public async Task<Result<int>> AddNewExpenseAsync(DtoExpense dtoExpense)
        {
            if (string.IsNullOrWhiteSpace(dtoExpense.Description))
                return Result<int>.Fail("وصف المصروف مطلوب.");

            if (dtoExpense.Amount <= 0)
                return Result<int>.Fail("مبلغ المصروف يجب أن يكون أكبر من صفر.");

            int creatorRole = await GetUserRoleValueAsync(dtoExpense.UserId ?? 0);
            if (!clsPermissions.HasAtLeast(creatorRole, clsPermissions.Accountant))
                return Result<int>.Fail("إضافة مصروف يتطلب صلاحية محاسب أو أعلى.");

            if (dtoExpense.UserId.HasValue)
            {
                bool userExists = await _dbContext.Users.AnyAsync(u => u.UserId == dtoExpense.UserId.Value);
                if (!userExists)
                    return Result<int>.Fail("المستخدم غير موجود.");
            }

            var expense = new Expense
            {
                Description = clsValidation.ValidateString(dtoExpense.Description, "Description", 255),
                Amount = dtoExpense.Amount,
                ExpenseDate = dtoExpense.ExpenseDate,
                Category = string.IsNullOrWhiteSpace(dtoExpense.Category) ? null : dtoExpense.Category,
                Notes = string.IsNullOrWhiteSpace(dtoExpense.Notes) ? null : dtoExpense.Notes,
                UserId = dtoExpense.UserId,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Expenses.Add(expense);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved
                ? Result<int>.Ok(expense.ExpenseId, "تمت إضافة المصروف بنجاح.")
                : Result<int>.Fail("فشلت إضافة المصروف.");
        }

        public async Task<Result> UpdateExpenseAsync(DtoExpense dtoExpense)
        {
            if (dtoExpense.ExpenseId <= 0)
                return Result.Fail("معرف المصروف غير صالح.");

            var expense = await _dbContext.Expenses.FindAsync(dtoExpense.ExpenseId);
            if (expense == null)
                return Result.Fail("المصروف غير موجود.");

            // VULN-26: تعديل المصروف = تعديل الربح — محاسب فأعلى
            int editorRole = await GetUserRoleValueAsync(dtoExpense.UserId ?? 0);
            if (!clsPermissions.HasAtLeast(editorRole, clsPermissions.Accountant))
                return Result.Fail("تعديل المصروفات يتطلب صلاحية محاسب أو أعلى.");

            if (!string.IsNullOrWhiteSpace(dtoExpense.Description))
                expense.Description = clsValidation.ValidateString(dtoExpense.Description, "Description", 255);

            if (dtoExpense.Amount > 0)
                expense.Amount = dtoExpense.Amount;

            if (dtoExpense.ExpenseDate != default)
                expense.ExpenseDate = dtoExpense.ExpenseDate;

            if (!string.IsNullOrWhiteSpace(dtoExpense.Category))
                expense.Category = dtoExpense.Category;

            if (!string.IsNullOrWhiteSpace(dtoExpense.Notes))
                expense.Notes = dtoExpense.Notes;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث المصروف بنجاح.");
        }

        public async Task<Result> DeleteExpenseAsync(int expenseId, int actorUserId)
        {
            var expense = await _dbContext.Expenses.FindAsync(expenseId);
            if (expense == null)
                return Result.Fail("المصروف غير موجود.");

            // مصروف الرواتب يُنشأ ويُحذف عبر كشف الراتب فقط — حفاظًا على تناسق الربط
            if (expense.Category == clsPayrollService.SalaryExpenseCategory)
                return Result.Fail("مصروف الرواتب يُدار من قسم الرواتب — احذفه بعكس صرف الكشف المرتبط.");

            // حذف المصروفات يتطلب صلاحية محاسب فأعلى
            var actor = await _dbContext.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || !clsPermissions.HasAtLeast(actor.Role.RoleValue, clsPermissions.Accountant))
                return Result.Fail("حذف المصروفات يتطلب صلاحية محاسب أو أعلى.");

            _dbContext.Expenses.Remove(expense);

            clsAudit.Log(_dbContext, actorUserId, "DeleteExpense",
                $"حذف مصروف '{expense.Description}' بمبلغ {expense.Amount:N2} بتاريخ {expense.ExpenseDate:yyyy-MM-dd}");

            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved ? Result.Ok("تم حذف المصروف بنجاح.") : Result.Fail("فشل حذف المصروف.");
        }

        /// <summary>قيمة دور المستخدم (VULN-26 — بوابات المصروفات الخدمية).</summary>
        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return 0;
            var user = await _dbContext.Users.Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            return user?.Role.RoleValue ?? 0;
        }
    }
}
