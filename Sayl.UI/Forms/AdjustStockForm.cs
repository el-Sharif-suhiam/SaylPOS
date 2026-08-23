using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>تسوية مخزون يدوية: زيادة أو نقصان كمية مع سبب.</summary>
    public class AdjustStockForm : Form
    {
        private readonly int? _preselectedProductId;

        private ComboBox _cmbProduct = null!;
        private TextBox _txtQty = null!;
        private RadioButton _rbIncrease = null!;
        private RadioButton _rbDecrease = null!;
        private TextBox _txtReason = null!;
        private TextBox _txtBatch = null!;

        private List<Common.DTOs.DtoProduct> _products = new();

        public AdjustStockForm(int? preselectedProductId)
        {
            _preselectedProductId = preselectedProductId;
            BuildUi();
            Load += async (_, _) => await LoadProductsAsync();
        }

        private void BuildUi()
        {
            Text = "تسوية مخزون";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 540);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label { Text = "تسوية مخزون يدوية", Bounds = new Rectangle(0, 18, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblProduct = new Label { Text = "المنتج *", Bounds = new Rectangle(30, 60, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbProduct = new ComboBox { Bounds = new Rectangle(30, 84, 400, 40), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };

            var lblQty = new Label { Text = "الكمية *", Bounds = new Rectangle(30, 134, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtQty = new TextBox { Bounds = new Rectangle(30, 158, 400, 40), Font = AppTheme.FontBody, TextAlign = HorizontalAlignment.Center };

            _rbIncrease = new RadioButton { Text = "زيادة (إضافة)", Bounds = new Rectangle(230, 208, 200, 28), Checked = true, ForeColor = AppTheme.TextPrimary };
            _rbDecrease = new RadioButton { Text = "نقصان (خصم)", Bounds = new Rectangle(30, 208, 200, 28), ForeColor = AppTheme.TextPrimary };

            var lblBatch = new Label { Text = "رقم الدفعة (للزيادة فقط، اختياري)", Bounds = new Rectangle(30, 244, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtBatch = new TextBox { Bounds = new Rectangle(30, 268, 400, 40), Font = AppTheme.FontBody };

            var lblReason = new Label { Text = "السبب *", Bounds = new Rectangle(30, 318, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtReason = new TextBox { Bounds = new Rectangle(30, 342, 400, 60), Multiline = true, Font = AppTheme.FontBody };

            var btnSave = AppTheme.MakeButton("تنفيذ التسوية", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(30, 420, 240, 50);
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 420, 140, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, lblProduct, _cmbProduct, lblQty, _txtQty,
                _rbIncrease, _rbDecrease, lblBatch, _txtBatch, lblReason, _txtReason,
                btnSave, btnCancel
            });
            CancelButton = btnCancel;
        }

        private async Task LoadProductsAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                _products = await productService.GetProductsListAsync("", type: "stock");

                foreach (var p in _products)
                    _cmbProduct.Items.Add(p.Name);

                if (_products.Count == 0)
                {
                    UiHelpers.ShowError(this, "لا توجد منتجات مخزنية.");
                    return;
                }

                if (_preselectedProductId.HasValue)
                {
                    int idx = _products.FindIndex(p => p.ProductId == _preselectedProductId.Value);
                    if (idx >= 0)
                        _cmbProduct.SelectedIndex = idx;
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل المنتجات.");
            }
        }

        private async Task SaveAsync()
        {
            if (_cmbProduct.SelectedIndex < 0 || _cmbProduct.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowError(this, "اختر منتجًا.");
                return;
            }

            if (!int.TryParse(_txtQty.Text?.Trim(), out int qty) || qty <= 0)
            {
                UiHelpers.ShowError(this, "أدخل كمية صحيحة أكبر من صفر.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtReason.Text))
            {
                UiHelpers.ShowError(this, "سبب التسوية مطلوب.");
                return;
            }

            var request = new DtoStockAdjustmentRequest
            {
                ProductId = _products[_cmbProduct.SelectedIndex].ProductId,
                Quantity = qty,
                IsIncrease = _rbIncrease.Checked,
                Reason = _txtReason.Text.Trim(),
                BatchNumber = _rbIncrease.Checked && !string.IsNullOrWhiteSpace(_txtBatch.Text)
                    ? _txtBatch.Text.Trim()
                    : null,
                UserId = SessionState.CurrentUser?.UserId ?? 0
            };

            bool confirmed = ConfirmationDialog.Show(this, "تسوية مخزون",
                $"سيتم {(request.IsIncrease ? "زيادة" : "خفض")} كمية المنتج بمقدار {qty}. هل أنت متأكد؟");
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var stockService = scope.ServiceProvider.GetRequiredService<clsStockService>();
                result = await stockService.AdjustStockAsync(request);
            }

            UiHelpers.ShowResult(this, result, "تمت التسوية بنجاح.");
            if (result.IsSuccess)
                DialogResult = DialogResult.OK;
        }
    }
}
