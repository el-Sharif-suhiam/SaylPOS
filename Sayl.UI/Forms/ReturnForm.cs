using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// مرتجع بيع في نقطة البيع: إدخال رقم الفاتورة، اختيار الكميات المرتجعة، تنفيذ المرتجع.
    /// </summary>
    public class ReturnForm : DialogForm
    {
        private TextBox _txtInvoiceId = null!;
        private Button _btnLoad = null!;
        private DataGridView _grid = null!;
        private Label _lblInfo = null!;

        private int _originalInvoiceId;
        private List<Common.DTOs.ViewModel.InvoiceItemVM> _items = new();

        public bool Returned { get; private set; }

        public ReturnForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "مرتجع بيع";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 600);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label { Text = "مرتجع بيع", Bounds = new Rectangle(0, 18, 620, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblInvoice = new Label { Text = "رقم فاتورة البيع الأصلية", Bounds = new Rectangle(30, 58, 300, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtInvoiceId = new TextBox { Bounds = new Rectangle(30, 82, 300, 40), Font = AppTheme.FontBody, TextAlign = HorizontalAlignment.Center };

            _btnLoad = AppTheme.MakeButton("تحميل الفاتورة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnLoad.Bounds = new Rectangle(350, 82, 240, 40);
            _btnLoad.Click += async (_, _) => await LoadInvoiceAsync();

            _grid = new DataGridView { Bounds = new Rectangle(30, 136, 560, 330) };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.Columns.Add("colProduct", "المنتج");
            _grid.Columns.Add("colSold", "المباع");
            _grid.Columns.Add("colPrice", "السعر");
            var colReturnQty = new DataGridViewTextBoxColumn { Name = "colReturnQty", HeaderText = "كمية المرتجع", ReadOnly = false };
            _grid.Columns.Add(colReturnQty);
            _grid.Columns["colProduct"].FillWeight = 45;
            _grid.Columns["colSold"].FillWeight = 15;
            _grid.Columns["colPrice"].FillWeight = 18;
            _grid.Columns["colReturnQty"].FillWeight = 22;

            _lblInfo = new Label { Text = "", Bounds = new Rectangle(30, 474, 560, 26), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            var btnReturn = AppTheme.MakeButton("تنفيذ المرتجع", AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9));
            btnReturn.Bounds = new Rectangle(30, 508, 260, 52);
            btnReturn.Click += async (_, _) => await ExecuteReturnAsync();

            var btnCancel = AppTheme.MakeButton("إغلاق", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(330, 508, 260, 52);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, lblInvoice, _txtInvoiceId, _btnLoad, _grid, _lblInfo, btnReturn, btnCancel });
            CancelButton = btnCancel;
            AcceptButton = _btnLoad;
        }

        private async Task LoadInvoiceAsync()
        {
            if (!int.TryParse(_txtInvoiceId.Text?.Trim(), out int invoiceId) || invoiceId <= 0)
            {
                UiHelpers.ShowError(this, "أدخل رقم فاتورة صحيحًا.");
                return;
            }

            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                if (result.Data.Type != "sale" || result.Data.Status != "completed")
                {
                    UiHelpers.ShowError(this, "المرتجع مسموح فقط على فواتير بيع مكتملة.");
                    return;
                }

                _originalInvoiceId = invoiceId;
                _items = result.Data.Items;

                _grid.Rows.Clear();
                foreach (var item in _items)
                    _grid.Rows.Add(item.ProductName, item.Quantity, UiHelpers.Money(item.ItemPrice), 0);

                _lblInfo.Text = $"فاتورة #{invoiceId} — العميل: {result.Data.PartyName ?? "مجهول"} — أدخل كمية المرتجع لكل بند.";
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الفاتورة.");
            }
        }

        private async Task ExecuteReturnAsync()
        {
            if (_originalInvoiceId <= 0 || _items.Count == 0)
            {
                UiHelpers.ShowError(this, "حمّل الفاتورة أولًا.");
                return;
            }

            var returnItems = new List<DtoReturnItem>();
            decimal returnTotal = 0;

            for (int i = 0; i < _grid.Rows.Count && i < _items.Count; i++)
            {
                if (!int.TryParse(_grid.Rows[i].Cells["colReturnQty"].Value?.ToString(), out int qty) || qty <= 0)
                    continue;

                var item = _items[i];
                if (qty > item.Quantity)
                {
                    UiHelpers.ShowError(this, $"كمية المرتجع للمنتج '{item.ProductName}' أكبر من المباع ({item.Quantity}).");
                    return;
                }

                returnItems.Add(new DtoReturnItem { ProductId = item.ProductId, Quantity = qty });
                returnTotal += item.ItemPrice * qty;
            }

            if (returnItems.Count == 0)
            {
                UiHelpers.ShowError(this, "أدخل كمية مرتجعة لبند واحد على الأقل.");
                return;
            }

            bool confirmed = ConfirmationDialog.Show(this, "تنفيذ مرتجع",
                $"سيتم إرجاع {returnItems.Count} بند بقيمة {UiHelpers.Money(returnTotal)} للمخزون. متابعة؟");
            if (!confirmed)
                return;

            var request = new DtoReturnRequest
            {
                OriginalInvoiceId = _originalInvoiceId,
                UserId = SessionState.CurrentUser?.UserId ?? 0,
                Items = returnItems,
                Notes = null
            };

            Result<int> result;
            using (var scope = Program.Services.CreateScope())
            {
                var returnService = scope.ServiceProvider.GetRequiredService<clsReturnService>();
                result = await returnService.CreateCustomerReturnAsync(request);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشل تنفيذ المرتجع." : result.Message);
                return;
            }

            UiHelpers.ShowInfo(this, $"تم تنفيذ المرتجع بنجاح. رقم المرتجع: {result.Data}");
            Returned = true;
            DialogResult = DialogResult.OK;
        }
    }
}
