using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>استلام مخزون جديد: منتج + رقم دفعة + كمية + تاريخ انتهاء (اختياري).</summary>
    public class ReceiveStockForm : Form
    {
        private TextBox _txtSearch = null!;
        private ListBox _lstProducts = null!;
        private TextBox _txtBatch = null!;
        private TextBox _txtQty = null!;
        private DateTimePicker _dtpExpiry = null!;
        private CheckBox _chkHasExpiry = null!;

        private List<Common.DTOs.DtoProduct> _products = new();

        public ReceiveStockForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadProductsAsync("");
        }

        private void BuildUi()
        {
            Text = "استلام مخزون";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 620);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label
            {
                Text = "استلام مخزون جديد",
                Bounds = new Rectangle(0, 18, 560, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _txtSearch = new TextBox
            {
                Bounds = new Rectangle(30, 58, 380, 40),
                Font = AppTheme.FontBody
            };
            _txtSearch.PlaceholderText = "ابحث بالاسم أو الباركود ثم Enter";

            var btnSearch = AppTheme.MakeButton("بحث", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSearch.Bounds = new Rectangle(420, 58, 110, 40);
            btnSearch.Click += async (_, _) => await LoadProductsAsync(_txtSearch.Text?.Trim() ?? "");

            var lblProducts = new Label
            {
                Text = "المنتج (المخزني)",
                Bounds = new Rectangle(30, 106, 500, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _lstProducts = new ListBox
            {
                Bounds = new Rectangle(30, 130, 500, 170),
                Font = new Font("Segoe UI", 11F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            _lstProducts.DoubleClick += (_, _) => { if (_lstProducts.SelectedIndex >= 0) _txtQty.Focus(); };

            var lblBatch = new Label
            {
                Text = "رقم الدفعة *",
                Bounds = new Rectangle(30, 312, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtBatch = new TextBox
            {
                Bounds = new Rectangle(30, 336, 240, 40),
                Font = AppTheme.FontBody
            };

            var lblQty = new Label
            {
                Text = "الكمية *",
                Bounds = new Rectangle(290, 312, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtQty = new TextBox
            {
                Bounds = new Rectangle(290, 336, 240, 40),
                Font = AppTheme.FontBody,
                TextAlign = HorizontalAlignment.Center
            };

            _chkHasExpiry = new CheckBox
            {
                Text = "له تاريخ انتهاء",
                Bounds = new Rectangle(290, 388, 240, 26),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _dtpExpiry = new DateTimePicker
            {
                Bounds = new Rectangle(30, 386, 240, 40),
                Format = DateTimePickerFormat.Short,
                Enabled = false
            };
            _chkHasExpiry.CheckedChanged += (_, _) => _dtpExpiry.Enabled = _chkHasExpiry.Checked;

            var btnReceive = AppTheme.MakeButton("استلام", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            btnReceive.Bounds = new Rectangle(30, 452, 240, 52);
            btnReceive.Click += async (_, _) => await ReceiveAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(290, 452, 240, 52);
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtSearch, btnSearch, lblProducts, _lstProducts,
                lblBatch, _txtBatch, lblQty, _txtQty, _chkHasExpiry, _dtpExpiry,
                btnReceive, btnClose
            });

            CancelButton = btnClose;
            AcceptButton = btnSearch;
        }

        private async Task LoadProductsAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                var products = await productService.GetProductsListAsync(search, type: "stock");

                _products = products;
                _lstProducts.Items.Clear();
                foreach (var p in products)
                    _lstProducts.Items.Add($"{p.Name}  [{p.Barcode ?? "بدون باركود"}]");

                if (_products.Count == 0)
                    UiHelpers.ShowInfo(this, "لا توجد منتجات مطابقة.");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل المنتجات.");
            }
        }

        private async Task ReceiveAsync()
        {
            if (_lstProducts.SelectedIndex < 0 || _lstProducts.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowError(this, "اختر منتجًا من القائمة.");
                return;
            }

            var product = _products[_lstProducts.SelectedIndex];

            if (string.IsNullOrWhiteSpace(_txtBatch.Text))
            {
                UiHelpers.ShowError(this, "رقم الدفعة مطلوب.");
                _txtBatch.Focus();
                return;
            }

            if (!int.TryParse(_txtQty.Text?.Trim(), out int qty) || qty <= 0)
            {
                UiHelpers.ShowError(this, "أدخل كمية صحيحة أكبر من صفر.");
                _txtQty.Focus();
                return;
            }

            var request = new DtoStockReceiveRequest
            {
                ProductId = product.ProductId,
                BatchNumber = _txtBatch.Text.Trim(),
                Quantity = qty,
                ExpiryDate = _chkHasExpiry.Checked
                    ? DateOnly.FromDateTime(_dtpExpiry.Value.Date)
                    : null
            };

            Result result = await UiHelpers.UseServiceAsync(s => s.ReceiveStockAsync(request));
            UiHelpers.ShowResult(this, result, $"تم استلام {qty} وحدة للمنتج {product.Name}.");

            if (result.IsSuccess)
            {
                _txtBatch.Clear();
                _txtQty.Clear();
                _chkHasExpiry.Checked = false;
                _txtBatch.Focus();
            }
        }
    }
}
