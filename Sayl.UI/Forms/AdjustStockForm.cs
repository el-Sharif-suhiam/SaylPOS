using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>تسوية مخزون يدوية: زيادة أو نقصان كمية مع سبب.</summary>
    public class AdjustStockForm : DialogForm
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

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — «الإحداثيات اليدوية» (راجع EmployeeEditForm):
        //
        // نموذج تسوية عمودي بسيط، لكن بثلاث نقاط تعليمية:
        // 1) زرا RadioButton حصريان (زيادة/نقصان) — WinForms يجعل أي زرين
        //    خيار بنفس الأب مجموعة واحدة: تحديد أحدهما يلغي الآخر تلقائيًا.
        //    القراءة عند الحفظ: _rbIncrease.Checked ? 1 : -1.
        // 2) «رقم الدفعة (للزيادة فقط)»: التعليق داخل التسمية نفسها —
        //    حقل يظهر دائمًا لكن معناه مشروط بعملية أخرى (تفسير مستخدم).
        // 3) _preselectedProductId: المنتج يُحدد مسبقًا من المستدعي (قائمة
        //    المخزون تفتح التسوية على منتج بعينه) — FindIndex يطابق.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("AdjustStock.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 540);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label { Text = AppLang.T("AdjustStock.Title"), Bounds = new Rectangle(0, 18, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            // قائمة المنتجات تُعبأ لاحقًا في LoadProductsAsync (نمط شائع:
            // البناء بلا بيانات، التحميل عند Load — لا تجميد لشاشة الافتتاح)
            var lblProduct = new Label { Text = AppLang.T("Common.Label.Product") + " *", Bounds = new Rectangle(30, 60, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbProduct = new ComboBox { Bounds = new Rectangle(30, 84, 400, 40), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };

            var lblQty = new Label { Text = AppLang.T("Common.Label.Qty") + " *", Bounds = new Rectangle(30, 134, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtQty = new TextBox { Bounds = new Rectangle(30, 158, 400, 40), Font = AppTheme.FontBody, TextAlign = HorizontalAlignment.Center };

            // زرا الخيار: زيادة يمين (230..430) ونقصان يسار (30..230) —
            // Checked=true على الزيادة = الافتراضي. لا مقبض Click — قراءة لاحقًا.
            _rbIncrease = new RadioButton { Text = AppLang.T("AdjustStock.Increase"), Bounds = new Rectangle(230, 208, 200, 28), Checked = true, ForeColor = AppTheme.TextPrimary };
            _rbDecrease = new RadioButton { Text = AppLang.T("AdjustStock.Decrease"), Bounds = new Rectangle(30, 208, 200, 28), ForeColor = AppTheme.TextPrimary };

            var lblBatch = new Label { Text = AppLang.T("AdjustStock.Label.Batch"), Bounds = new Rectangle(30, 244, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtBatch = new TextBox { Bounds = new Rectangle(30, 268, 400, 40), Font = AppTheme.FontBody };

            // السبب Multiline 60px — التسوية عملية رقابية تستوثق (لماذا؟)
            var lblReason = new Label { Text = AppLang.T("CashMove.Label.Reason"), Bounds = new Rectangle(30, 318, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtReason = new TextBox { Bounds = new Rectangle(30, 342, 400, 60), Multiline = true, Font = AppTheme.FontBody };

            var btnSave = AppTheme.MakeButton(AppLang.T("AdjustStock.Button.Execute"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(30, 420, 240, 50);
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 420, 140, 50);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء

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
                    UiHelpers.ShowError(this, AppLang.T("AdjustStock.Msg.NoStockProducts"));
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
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.LoadFailed"));
            }
        }

        private async Task SaveAsync()
        {
            if (_cmbProduct.SelectedIndex < 0 || _cmbProduct.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("AdjustStock.Msg.PickProduct"));
                return;
            }

            if (!int.TryParse(_txtQty.Text?.Trim(), out int qty) || qty <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.InvalidQty"));
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtReason.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("AdjustStock.Msg.ReasonRequired"));
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

            bool confirmed = ConfirmationDialog.Show(this, AppLang.T("AdjustStock.Confirm.Title"),
                AppLang.T("AdjustStock.Confirm.Body",
                    request.IsIncrease ? AppLang.T("AdjustStock.IncreaseWord") : AppLang.T("AdjustStock.DecreaseWord"),
                    qty));
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var stockService = scope.ServiceProvider.GetRequiredService<clsStockService>();
                result = await stockService.AdjustStockAsync(request);
            }

            UiHelpers.ShowResult(this, result, AppLang.T("AdjustStock.Msg.Done"));
            if (result.IsSuccess)
                DialogResult = DialogResult.OK;
        }
    }
}
