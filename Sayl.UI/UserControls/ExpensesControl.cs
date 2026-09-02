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

        protected override string Title => "المصروفات";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var expenseService = scope.ServiceProvider.GetRequiredService<clsExpenseService>();
                _expenses = await expenseService.GetExpensesListAsync(search: TxtSearch.Text?.Trim() ?? "");

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", "الرقم");
                Grid.Columns.Add("colDate", "التاريخ");
                Grid.Columns.Add("colDesc", "الوصف");
                Grid.Columns.Add("colCategory", "الفئة");
                Grid.Columns.Add("colAmount", "المبلغ");
                Grid.Columns.Add("colNotes", "ملاحظات");
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
                UiHelpers.ShowError(this, "تعذر تحميل المصروفات.");
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة مصروف", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditExpense(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("حذف", AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeleteAsync(), 110);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _expenses.Count)
            {
                UiHelpers.ShowError(this, "اختر مصروفًا من القائمة.");
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
                UiHelpers.ShowError(this, "اختر مصروفًا من القائمة.");
                return;
            }

            var expense = _expenses[idx];
            if (!ConfirmationDialog.Show(this, "حذف مصروف",
                $"سيتم حذف المصروف '{expense.Description}' بمبلغ {UiHelpers.Money(expense.Amount)}. هل أنت متأكد؟"))
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
