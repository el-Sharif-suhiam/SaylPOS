using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة «آخر فاتورة» (F9 من شاشة البيع): تعرض آخر فاتورة بيع مكتملة لهذا
    /// الكاشير (أو آخر فاتورة مطلقًا) مع زر إعادة طباعة الإيصال — تخدم الزبون
    /// الذي فقد إيصاله أو طلب نسخة، دون مغادرة شاشة البيع.
    /// </summary>
    public class LastInvoiceForm : DialogForm
    {
        private readonly int _userId;
        private Label _lblInfo = null!;
        private DataGridView _grid = null!;
        private Button _btnPrint = null!;
        private int? _invoiceId;

        public LastInvoiceForm(int userId)
        {
            _userId = userId;

            Text = AppLang.T("LastInvoice.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(560, 420);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();
            Load += async (_, _) => await LoadLastAsync();
        }

        private void BuildUi()
        {
            // ══ الجذر المجزّأ (نفس نمط PayrollForm): معلومات / بنود / أزرار ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));   // معلومات الفاتورة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // البنود
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // الأزرار

            _lblInfo = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(10, 8, 10, 8),
                Text = AppLang.T("LastInvoice.Searching")
            };

            _grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_grid);
            _grid.Columns.Add("colProduct", AppLang.T("Common.Label.Product"));
            _grid.Columns.Add("colQty", AppLang.T("Common.Label.Qty"));
            _grid.Columns.Add("colPrice", AppLang.T("Common.Label.Price"));
            _grid.Columns.Add("colTotal", AppLang.T("Common.Label.Total"));
            _grid.Columns["colProduct"].FillWeight = 46;
            _grid.Columns["colQty"].FillWeight = 16;
            _grid.Columns["colPrice"].FillWeight = 19;
            _grid.Columns["colTotal"].FillWeight = 19;

            var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(2, 8, 2, 8) };

            _btnPrint = AppTheme.MakeButton(AppLang.T("LastInvoice.Button.Reprint"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 48);
            _btnPrint.Dock = DockStyle.Left;
            _btnPrint.Width = 190;
            _btnPrint.Enabled = false;
            _btnPrint.Click += async (_, _) => await PrintAsync();

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 110;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(btnClose);
            bottomBar.Controls.Add(_btnPrint);

            root.Controls.Add(_lblInfo, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(bottomBar, 0, 2);

            Controls.Add(root);
        }

        private async Task LoadLastAsync()
        {
            try
            {
                // آخر فاتورة بيع مكتملة لهذا الكاشير؛ وإن لم يبع شيئًا اليوم → آخر فاتورة بالمتجر
                int? invoiceId = null;
                using (var scope = Program.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<Sayl.Core.Data.SaylDbContext>();
                    invoiceId = await db.Invoices
                        .Where(i => i.Type == enInvoicesType.sale && i.Status == enInvoicesStatus.completed && i.UserId == _userId)
                        .OrderByDescending(i => i.InvoiceId)
                        .Select(i => (int?)i.InvoiceId)
                        .FirstOrDefaultAsync()
                        ?? await db.Invoices
                            .Where(i => i.Type == enInvoicesType.sale && i.Status == enInvoicesStatus.completed)
                            .OrderByDescending(i => i.InvoiceId)
                            .Select(i => (int?)i.InvoiceId)
                            .FirstOrDefaultAsync();
                }

                if (invoiceId == null)
                {
                    _lblInfo.Text = AppLang.T("LastInvoice.Msg.None");
                    return;
                }

                _invoiceId = invoiceId;

                using var scope2 = Program.Services.CreateScope();
                var invoiceService = scope2.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var r = await invoiceService.GetInvoiceForPrintAsync(invoiceId.Value);
                if (!r.IsSuccess || r.Data == null)
                {
                    _lblInfo.Text = AppLang.T("LastInvoice.Msg.LoadFailed");
                    return;
                }

                var inv = r.Data;
                _lblInfo.Text = AppLang.T("LastInvoice.Info",
                    inv.InvoiceId,
                    InvoiceDetailsForm.TranslateType(inv.Type),
                    InvoiceDetailsForm.TranslatePayment(inv.PaymentStatus),
                    inv.PartyName ?? AppLang.T("Common.Party.WalkIn"),
                    inv.CashierName,
                    Sayl.UI.Infrastructure.AppClock.ToLocal(inv.CreatedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm"),
                    UiHelpers.Money(inv.NetAmount),
                    UiHelpers.Money(inv.PaidAmount),
                    inv.RemainingAmount > 0 ? AppLang.T("LastInvoice.Remaining", UiHelpers.Money(inv.RemainingAmount)) : "");

                _grid.Rows.Clear();
                foreach (var item in inv.Items)
                    _grid.Rows.Add(item.ProductName, item.Quantity, UiHelpers.Money(item.ItemPrice),
                        UiHelpers.Money(item.ItemPrice * item.Quantity));

                _btnPrint.Enabled = true;
            }
            catch
            {
                _lblInfo.Text = AppLang.T("LastInvoice.Msg.SearchFailed");
            }
        }

        private async Task PrintAsync()
        {
            if (_invoiceId == null) return;
            await InvoicePrintHelper.PrintAsync(_invoiceId.Value, this);
        }
    }
}
