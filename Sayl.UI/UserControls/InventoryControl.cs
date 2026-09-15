using Common.DTOs;
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
        private List<InventoryStatusVM> _shown = new();
        private CheckBox _chkLowOnly = null!;

        protected override string Title => AppLang.T("Inventory.Section.Title");
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
                var paged = SliceCurrentPage(_items);
                ApplyPaged(paged);
                _shown = paged.Items;

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", AppLang.T("Common.Label.Product"));
                Grid.Columns.Add("colUnit", AppLang.T("Common.Label.Unit"));
                Grid.Columns.Add("colAvailable", AppLang.T("Inventory.Label.Available"));
                Grid.Columns.Add("colReserved", AppLang.T("Inventory.Label.Reserved"));
                Grid.Columns.Add("colReorder", AppLang.T("Common.Label.Reorder"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns.Add("colCounted", AppLang.T("Inventory.Label.LastCount"));
                Grid.Columns["colName"].FillWeight = 34;
                Grid.Columns["colUnit"].FillWeight = 10;
                Grid.Columns["colAvailable"].FillWeight = 12;
                Grid.Columns["colReserved"].FillWeight = 12;
                Grid.Columns["colReorder"].FillWeight = 12;
                Grid.Columns["colStatus"].FillWeight = 10;
                Grid.Columns["colCounted"].FillWeight = 10;

                Grid.Rows.Clear();
                foreach (var i in _shown)
                    Grid.Rows.Add(i.ProductName, i.Unit ?? "-", i.QuantityAvailable, i.QuantityReserved,
                        i.ReorderLevel, i.IsLowStock ? AppLang.T("Common.Status.Low") : AppLang.T("Common.Status.Good"),
                        i.LastCountedAt?.ToString("yyyy-MM-dd") ?? "-");
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Inventory.Msg.LoadFailed"));
            }
        }

        /// <summary>شريحة الصفحة الحالية من قائمة مادّة كاملة (ترقيم على الذاكرة).</summary>
        private PagedResult<InventoryStatusVM> SliceCurrentPage(List<InventoryStatusVM> all)
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)Math.Max(1, _pageSize)));
            int page = Math.Clamp(_currentPage, 1, totalPages);
            return new PagedResult<InventoryStatusVM>
            {
                TotalCount = all.Count,
                Page = page,
                PageSize = _pageSize,
                Items = all.Skip((page - 1) * _pageSize).Take(_pageSize).ToList()
            };
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // خصوصية هنا: عنصر مخصص (CheckBox «المنخفض فقط») يُضاف يدويًا
        // بنفس حيلة الأزرار: Controls.Add ثم BringToFront — فيظهر أقصى
        // اليمين قبل الأزرار. أي عنصر مخصص بشريط الأدوات يسلك هذا الطريق.
        protected override void BuildToolbar()
        {
            _chkLowOnly = new CheckBox
            {
                Text = AppLang.T("Inventory.OnlyLow"),
                Dock = DockStyle.Right,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _chkLowOnly.CheckedChanged += (_, _) => { _currentPage = 1; _ = LoadDataAsync(); };
            _toolbar.Controls.Add(_chkLowOnly);
            _chkLowOnly.BringToFront();

            AddToolbarButton(AppLang.T("Inventory.Button.Adjust"), AppTheme.Warning, Color.FromArgb(180, 83, 9), (_, _) => AdjustStock(), 150);
            AddToolbarGap();

            // الجرد الفعلي: أصحاب شاشة المخزون — ركن الإغلاق السنوي
            if (SessionState.Can(Common.Utils.AppPermissions.Inventory))
            {
                AddToolbarButton(AppLang.T("Inventory.Button.StockCount"), AppTheme.Primary, AppTheme.PrimaryDark,
                    (_, _) =>
                    {
                        using var form = new StockCountForm();
                        if (form.ShowDialog(FindForm()) == DialogResult.OK)
                            _ = LoadDataAsync();
                    }, 120);
                AddToolbarGap();
            }

            AddToolbarButton(AppLang.T("Inventory.Button.Refresh"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => _ = LoadDataAsync(), 110);
            AddToolbarGap();
        }

        private void AdjustStock()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            int? productId = idx >= 0 && idx < _shown.Count ? _shown[idx].ProductId : null;

            using var form = new AdjustStockForm(productId);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }
    }
}
