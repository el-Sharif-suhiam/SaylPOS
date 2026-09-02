using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم المخزون: حالة الأصناف + تسوية يدوية.</summary>
    internal class InventoryControl : SectionListControl
    {
        private List<InventoryStatusVM> _items = new();
        private CheckBox _chkLowOnly = null!;

        protected override string Title => "المخزون";
        protected override bool HasSearch => false;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var result = await reportService.GetInventoryStatusAsync(lowStockOnly: _chkLowOnly.Checked);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _items = result.Data;
                Grid.Columns.Clear();
                Grid.Columns.Add("colName", "المنتج");
                Grid.Columns.Add("colUnit", "الوحدة");
                Grid.Columns.Add("colAvailable", "المتاح");
                Grid.Columns.Add("colReserved", "محجوز");
                Grid.Columns.Add("colReorder", "حد الطلب");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns.Add("colCounted", "آخر جرد");
                Grid.Columns["colName"].FillWeight = 34;
                Grid.Columns["colUnit"].FillWeight = 10;
                Grid.Columns["colAvailable"].FillWeight = 12;
                Grid.Columns["colReserved"].FillWeight = 12;
                Grid.Columns["colReorder"].FillWeight = 12;
                Grid.Columns["colStatus"].FillWeight = 10;
                Grid.Columns["colCounted"].FillWeight = 10;

                Grid.Rows.Clear();
                foreach (var i in _items)
                    Grid.Rows.Add(i.ProductName, i.Unit ?? "-", i.QuantityAvailable, i.QuantityReserved,
                        i.ReorderLevel, i.IsLowStock ? "منخفض" : "جيد",
                        i.LastCountedAt?.ToString("yyyy-MM-dd") ?? "-");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل حالة المخزون.");
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // خصوصية هنا: عنصر مخصص (CheckBox «المنخفض فقط») يُضاف يدويًا
        // بنفس حيلة الأزرار: Controls.Add ثم BringToFront — فيظهر أقصى
        // اليمين قبل الأزرار. أي عنصر مخصص بشريط الأدوات يسلك هذا الطريق.
        protected override void BuildToolbar()
        {
            _chkLowOnly = new CheckBox
            {
                Text = "المنخفض فقط",
                Dock = DockStyle.Right,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _chkLowOnly.CheckedChanged += (_, _) => _ = LoadDataAsync();
            _toolbar.Controls.Add(_chkLowOnly);
            _chkLowOnly.BringToFront();

            AddToolbarButton("تسوية مخزون", AppTheme.Warning, Color.FromArgb(180, 83, 9), (_, _) => AdjustStock(), 150);
            AddToolbarGap();

            // الجرد الفعلي: أدمن ومحاسب — ركن الإغلاق السنوي
            if (SessionState.IsAdmin || SessionState.IsAccountant)
            {
                AddToolbarButton("جرد فعلي", AppTheme.Primary, AppTheme.PrimaryDark,
                    (_, _) =>
                    {
                        using var form = new StockCountForm();
                        if (form.ShowDialog(FindForm()) == DialogResult.OK)
                            _ = LoadDataAsync();
                    }, 120);
                AddToolbarGap();
            }

            AddToolbarButton("تحديث", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => _ = LoadDataAsync(), 110);
            AddToolbarGap();
        }

        private void AdjustStock()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            int? productId = idx >= 0 && idx < _items.Count ? _items[idx].ProductId : null;

            using var form = new AdjustStockForm(productId);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }
    }
}
