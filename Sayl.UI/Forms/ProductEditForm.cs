using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل منتج. مرر منتجًا موجودًا للتعديل الجزئي، أو null للإضافة.</summary>
    public class ProductEditForm : Form
    {
        private readonly DtoProduct? _existing;

        private TextBox _txtName = null!;
        private TextBox _txtBarcode = null!;
        private TextBox _txtUnit = null!;
        private ComboBox _cmbType = null!;
        private TextBox _txtBasePrice = null!;
        private TextBox _txtCostPrice = null!;
        private TextBox _txtReorder = null!;

        public ProductEditForm(DtoProduct? existing)
        {
            _existing = existing;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = _existing == null ? "إضافة منتج" : "تعديل منتج";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 470);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            int x = 30, w = 400;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _txtName = MakeField("الاسم *", x, 58, w, _existing?.Name ?? "");
            _txtBarcode = MakeField("الباركود", x, 116, w, _existing?.Barcode ?? "");
            _txtUnit = MakeField("الوحدة", x, 174, 190, _existing?.Unit ?? "");

            _cmbType = new ComboBox
            {
                Bounds = new Rectangle(240, 196, 190, 36),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _cmbType.Items.AddRange(new object[] { "stock", "service", "manufactured" });
            _cmbType.SelectedIndex = _existing == null ? 0 : Math.Max(0, _cmbType.Items.IndexOf(_existing.Type));

            var lblType = new Label { Text = "النوع", Bounds = new Rectangle(240, 174, 190, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _txtBasePrice = MakeField("سعر البيع *", x, 244, 190, _existing?.BasePrice.ToString("0.##") ?? "");
            _txtCostPrice = MakeField("التكلفة", 240, 244, 190, _existing?.CostPrice.ToString("0.##") ?? "");
            _txtReorder = MakeField("حد الطلب", 240, 302, 190, _existing?.ReorderLevel?.ToString() ?? "");

            var btnOk = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(x, 360, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(260, 360, 170, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtName, _txtBarcode, _txtUnit, _cmbType, lblType,
                _txtBasePrice, _txtCostPrice, _txtReorder, btnOk, btnCancel
            });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private TextBox MakeField(string caption, int x, int y, int width, string value)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, width, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, width, 36), Font = AppTheme.FontBody, Text = value };
            Controls.Add(lbl);
            Controls.Add(txt);
            return txt;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "الاسم مطلوب.");
                return;
            }

            if (!decimal.TryParse(_txtBasePrice.Text?.Trim(), out decimal basePrice) || basePrice < 0)
            {
                UiHelpers.ShowError(this, "أدخل سعر بيع صحيحًا.");
                return;
            }

            decimal.TryParse(_txtCostPrice.Text?.Trim(), out decimal costPrice);
            int.TryParse(_txtReorder.Text?.Trim(), out int reorder);

            var dto = new DtoProduct
            {
                ProductId = _existing?.ProductId ?? 0,
                Name = _txtName.Text.Trim(),
                Barcode = string.IsNullOrWhiteSpace(_txtBarcode.Text) ? null : _txtBarcode.Text.Trim(),
                Unit = string.IsNullOrWhiteSpace(_txtUnit.Text) ? null : _txtUnit.Text.Trim(),
                Type = _cmbType.SelectedItem?.ToString() ?? "stock",
                BasePrice = basePrice,
                CostPrice = costPrice,
                ReorderLevel = reorder > 0 ? reorder : null,
                IsActive = _existing?.IsActive ?? true
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                if (_existing == null)
                {
                    var added = await productService.AddNewProductAsync(dto);
                    result = added.IsSuccess ? Result.Ok("تم حفظ المنتج بنجاح.") : Result.Fail(added.Message);
                }
                else
                {
                    result = await productService.UpdateProductAsync(dto);
                }
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشل الحفظ." : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, "تم حفظ المنتج بنجاح.");
            DialogResult = DialogResult.OK;
        }
    }
}
