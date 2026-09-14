using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم المنتجات: عرض، بحث، إضافة/تعديل، وتفعيل/تعطيل.</summary>
    internal class ProductsControl : SectionListControl
    {
        private List<DtoProduct> _products = new();

        protected override string Title => AppLang.T("Products.Section.Title");

        // مفتاح «عرض المعطّلين» في شريط الأدوات — الافتراضي: المنتجات النشطة فقط
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                _products = await productService.GetProductsListAsync(TxtSearch.Text?.Trim() ?? "", includeInactive: ShowInactive);

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", AppLang.T("Common.Label.Name"));
                Grid.Columns.Add("colBarcode", AppLang.T("Common.Label.Barcode"));
                Grid.Columns.Add("colUnit", AppLang.T("Common.Label.Unit"));
                Grid.Columns.Add("colType", AppLang.T("Common.Label.Type"));
                Grid.Columns.Add("colPrice", AppLang.T("Products.Label.SellPrice"));
                Grid.Columns.Add("colCost", AppLang.T("Common.Label.Cost"));
                Grid.Columns.Add("colReorder", AppLang.T("Common.Label.Reorder"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns["colName"].FillWeight = 28;
                Grid.Columns["colBarcode"].FillWeight = 18;
                Grid.Columns["colUnit"].FillWeight = 9;
                Grid.Columns["colType"].FillWeight = 9;
                Grid.Columns["colPrice"].FillWeight = 12;
                Grid.Columns["colCost"].FillWeight = 12;
                Grid.Columns["colReorder"].FillWeight = 8;
                Grid.Columns["colStatus"].FillWeight = 8;

                Grid.Rows.Clear();
                foreach (var p in _products)
                    Grid.Rows.Add(p.Name, p.Barcode ?? "-", p.Unit ?? "-", p.Type,
                        UiHelpers.Money(p.BasePrice), UiHelpers.Money(p.CostPrice),
                        p.ReorderLevel?.ToString() ?? "-",
                        p.IsActive ? AppLang.T("Common.Status.Active") : AppLang.T("Common.Status.Inactive"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Msg.LoadFailed"));
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Products.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditProduct(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.ToggleActive"), AppTheme.Warning, Color.FromArgb(180, 83, 9), async (_, _) => await ToggleActiveAsync(), 140);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _products.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Msg.PickProduct"));
                return;
            }
            EditProduct(_products[idx]);
        }

        private void EditProduct(DtoProduct? existing)
        {
            using var form = new ProductEditForm(existing);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task ToggleActiveAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _products.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Msg.PickProduct"));
                return;
            }

            var product = _products[idx];
            bool confirmed = ConfirmationDialog.Show(this,
                product.IsActive ? AppLang.T("Products.Title.Deactivate") : AppLang.T("Products.Title.Activate"),
                product.IsActive
                    ? AppLang.T("Products.Msg.DeactivateConfirm", product.Name)
                    : AppLang.T("Products.Msg.ActivateConfirm", product.Name));
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                result = product.IsActive
                    ? await productService.DeactivateProductAsync(product.ProductId, SessionState.CurrentUser?.UserId ?? 0)
                    : await productService.ActivateProductAsync(product.ProductId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
