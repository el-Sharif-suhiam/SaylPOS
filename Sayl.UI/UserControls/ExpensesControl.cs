using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم المصروفات: عرض بالبحث، إضافة/تعديل/حذف.</summary>
    internal class ExpensesControl : SectionListControl
    {
        private List<DtoExpense> _expenses = new();

        protected override string Title => AppLang.T("Expenses.Section.Title");

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var expenseService = scope.ServiceProvider.GetRequiredService<clsExpenseService>();
                var paged = await expenseService.GetExpensesPagedAsync(
                    search: TxtSearch.Text?.Trim() ?? "",
                    page: _currentPage, pageSize: _pageSize);
                if (!ApplyPaged(paged))
                    return;
                _expenses = paged.Items;

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", AppLang.T("Invoices.Label.Id"));
                Grid.Columns.Add("colDate", AppLang.T("Common.Label.Date"));
                Grid.Columns.Add("colDesc", AppLang.T("Expenses.Label.Description"));
                Grid.Columns.Add("colCategory", AppLang.T("Expenses.Label.Category"));
                Grid.Columns.Add("colAmount", AppLang.T("Expenses.Label.Amount"));
                Grid.Columns.Add("colNotes", AppLang.T("Common.Label.Notes"));
                Grid.Columns["colId"].FillWeight = 8;
                Grid.Columns["colDate"].FillWeight = 14;
                Grid.Columns["colDesc"].FillWeight = 30;
                Grid.Columns["colCategory"].FillWeight = 14;
                Grid.Columns["colAmount"].FillWeight = 14;
                Grid.Columns["colNotes"].FillWeight = 20;

                Grid.Rows.Clear();
                foreach (var e in _expenses)
                    Grid.Rows.Add(e.ExpenseId, e.ExpenseDate.ToString("yyyy-MM-dd"),
                        e.Description, e.Category ?? "-", UiHelpers.Money(e.Amount), e.Notes ?? "-");
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Expenses.Msg.LoadFailed"));
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Expenses.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditExpense(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Delete"), AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeleteAsync(), 110);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _expenses.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Expenses.Msg.PickExpense"));
                return;
            }
            EditExpense(_expenses[idx]);
        }

        private void EditExpense(DtoExpense? existing)
        {
            using var form = new ExpenseEditForm(existing);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeleteAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _expenses.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Expenses.Msg.PickExpense"));
                return;
            }

            var expense = _expenses[idx];
            if (!ConfirmationDialog.Show(this, AppLang.T("Expenses.Delete.Title"),
                AppLang.T("Expenses.Delete.Confirm", expense.Description, UiHelpers.Money(expense.Amount))))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var expenseService = scope.ServiceProvider.GetRequiredService<clsExpenseService>();
                result = await expenseService.DeleteExpenseAsync(expense.ExpenseId,
                    SessionState.CurrentUser?.UserId ?? 0);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
