using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;

namespace Sayl.UI.Infrastructure
{
    /// <summary>مساعدات مشتركة للواجهات: تنسيق الجداول، الرسائل، وتنفيذ الخدمات ضمن Scope.</summary>
    internal static class UiHelpers
    {
        public static string Money(decimal value) => value.ToString("N2");

        public static void StyleGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = AppTheme.CardBg;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = AppTheme.BorderColor;
            grid.EnableHeadersVisualStyles = false;

            grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.SidebarBg;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = AppTheme.TextOnDark;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 6, 8, 6);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 44;

            grid.DefaultCellStyle.Font = new Font("Segoe UI", 10.5F);
            grid.DefaultCellStyle.BackColor = AppTheme.CardBg;
            grid.DefaultCellStyle.ForeColor = AppTheme.TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor = AppTheme.PrimaryLight;
            grid.DefaultCellStyle.SelectionForeColor = AppTheme.TextPrimary;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);

            grid.AlternatingRowsDefaultCellStyle.BackColor = AppTheme.FieldBg;
            grid.RowTemplate.Height = 40;
        }

        public static void ShowError(IWin32Window? owner, string message) =>
            MessageBox.Show(owner, message, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

        public static void ShowInfo(IWin32Window? owner, string message) =>
            MessageBox.Show(owner, message, "تم", MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

        public static void ShowResult(IWin32Window? owner, Result result, string? successOverride = null)
        {
            if (result.IsSuccess)
                ShowInfo(owner, string.IsNullOrWhiteSpace(successOverride) ? result.Message : successOverride);
            else
                ShowError(owner, string.IsNullOrWhiteSpace(result.Message) ? "فشلت العملية." : result.Message);
        }

        /// <summary>تنفيذ عملية خدمة ضمن Scope مستقل (سياق EF قصير العمر).</summary>
        public static async Task<Result> UseServiceAsync(Func<clsStockService, Task<Result>> action)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<clsStockService>();
                return await action(service);
            }
            catch
            {
                return Result.Fail("تعذر الاتصال بقاعدة البيانات.");
            }
        }
    }
}
