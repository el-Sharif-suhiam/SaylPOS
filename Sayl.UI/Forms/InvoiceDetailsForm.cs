using System.Drawing.Printing;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>عرض تفاصيل فاتورة (بنود ومدفوعات) مع إمكانية الطباعة.</summary>
    public class InvoiceDetailsForm : DialogForm
    {
        private readonly int _invoiceId;

        private Label _lblHeader = null!;
        private DataGridView _gridItems = null!;
        private DataGridView _gridPayments = null!;
        private Label _lblTotals = null!;
        private InvoicePrintVM? _invoice;

        public InvoiceDetailsForm(int invoiceId)
        {
            _invoiceId = invoiceId;
            BuildUi();
            Load += async (_, _) => await LoadInvoiceAsync();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — «الجذر المجزّأ» بسبعة صفوف وجدولان متقاسمان
        // (الأساسيات في PayrollForm). الخصوصية هنا — «قسمة النسب»:
        //
        //   صف2 (Percent 46) جدول البنود ← الأكبر (بنود الفاتورة عادة كثيرة)
        //   صف4 (Percent 24) جدول المدفوعات ← الأصغر (دفعات قليلة)
        //
        // الصفان المرنان «يتقاسمان» الباقي بنسبة 46:24 — أي تقليص النافذة
        // ينقص الجدولين معًا بنفس النسبة (الأثقل يفقد أكثر مطلقًا)، بينما
        // العناوين/الإجماليات/الأزرار صفوف مطلقة ثابتة.
        // 💡 لو أردت جدولًا يثبت والآخر يمتص: أعطه Absolute والثاني Percent 100.
        //
        // نمط «عنوان قسم» متكرر: Label 30px («البنود»/«المدفوعات») فوق كل
        // جدول — فاصل بصري بلا خطوط.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("InvoiceDetails.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(680, 700);
            MinimumSize = new Size(520, 440);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // تخطيط متجاوب: الجداول تتمدد وتنكمش مع النافذة دون قصّ أو تراكب
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // «البنود»
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46));    // جدول البنود
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // «المدفوعات»
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 24));    // جدول المدفوعات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // الإجماليات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));   // أزرار

            _lblHeader = new Label
            {
                Text = AppLang.T("InvoiceDetails.Loading"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11F),
                ForeColor = AppTheme.TextPrimary
            };

            var lblItems = new Label
            {
                Text = AppLang.T("InvoiceDetails.Label.Items"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _gridItems = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridItems);
            _gridItems.Columns.Add("colProduct", AppLang.T("Common.Label.Product"));
            _gridItems.Columns.Add("colBatch", AppLang.T("Common.Label.Batch"));
            _gridItems.Columns.Add("colQty", AppLang.T("Common.Label.Qty"));
            _gridItems.Columns.Add("colPrice", AppLang.T("Common.Label.Price"));
            _gridItems.Columns.Add("colTotal", AppLang.T("Common.Label.Total"));
            _gridItems.Columns["colProduct"].FillWeight = 38;
            _gridItems.Columns["colBatch"].FillWeight = 16;
            _gridItems.Columns["colQty"].FillWeight = 12;
            _gridItems.Columns["colPrice"].FillWeight = 17;
            _gridItems.Columns["colTotal"].FillWeight = 17;

            var lblPayments = new Label
            {
                Text = AppLang.T("InvoiceDetails.Label.Payments"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _gridPayments = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridPayments);
            _gridPayments.Columns.Add("colDate", AppLang.T("InvoiceDetails.Label.PaymentDate"));
            _gridPayments.Columns.Add("colMethod", AppLang.T("Common.Label.Method"));
            _gridPayments.Columns.Add("colAmount", AppLang.T("Common.Label.Amount"));
            _gridPayments.Columns["colDate"].FillWeight = 34;
            _gridPayments.Columns["colMethod"].FillWeight = 33;
            _gridPayments.Columns["colAmount"].FillWeight = 33;

            _lblTotals = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(8, 0, 8, 0)
            };

            var btnPrint = AppTheme.MakeButton(AppLang.T("InvoiceDetails.Button.Thermal"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnPrint.Dock = DockStyle.Fill;
            btnPrint.Margin = new Padding(0, 0, 4, 0);
            btnPrint.Click += async (_, _) => await PrintPdfAsync(InvoicePrintFormat.ThermalReceipt);

            // الفاتورة الرسمية A4 — للتقارير المفصلة والاحتياجات الرسمية (توقيعات، دفعات، دفعات)
            var btnPrintA4 = AppTheme.MakeButton(AppLang.T("InvoiceDetails.Button.A4"), AppTheme.Accent, hoverColor: AppTheme.AccentDark);
            btnPrintA4.Dock = DockStyle.Fill;
            btnPrintA4.Margin = new Padding(4, 0, 4, 0);
            btnPrintA4.Click += async (_, _) => await PrintPdfAsync(InvoicePrintFormat.A4Detailed);

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Dock = DockStyle.Fill;
            btnClose.Margin = new Padding(4, 0, 0, 0);
            btnClose.DialogResult = DialogResult.Cancel;

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = AppTheme.CardBg, Margin = new Padding(0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            buttons.Controls.Add(btnPrint, 0, 0);
            buttons.Controls.Add(btnPrintA4, 1, 0);
            buttons.Controls.Add(btnClose, 2, 0);

            root.Controls.Add(_lblHeader, 0, 0);
            root.Controls.Add(lblItems, 0, 1);
            root.Controls.Add(_gridItems, 0, 2);
            root.Controls.Add(lblPayments, 0, 3);
            root.Controls.Add(_gridPayments, 0, 4);
            root.Controls.Add(_lblTotals, 0, 5);
            root.Controls.Add(buttons, 0, 6);

            Controls.Add(root);
            CancelButton = btnClose;
        }

        private async Task LoadInvoiceAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoiceForPrintAsync(_invoiceId);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _invoice = result.Data;
                var inv = _invoice;

                Text = AppLang.T("InvoiceDetails.Title", inv.InvoiceId);
                _lblHeader.Text = AppLang.T("InvoiceDetails.Header",
                    inv.InvoiceId,
                    TranslateType(inv.Type),
                    TranslateStatus(inv.Status),
                    TranslatePayment(inv.PaymentStatus),
                    inv.PartyName ?? AppLang.T("Common.Party.Unknown"),
                    string.IsNullOrWhiteSpace(inv.PartyPhone) ? "" : AppLang.T("InvoiceDetails.Header.Phone", inv.PartyPhone),
                    inv.CashierName,
                    Sayl.UI.Infrastructure.AppClock.ToLocal(inv.CreatedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm"));

                _gridItems.Rows.Clear();
                foreach (var item in inv.Items)
                    _gridItems.Rows.Add(item.ProductName, item.BatchNumber ?? "-",
                        item.Quantity, UiHelpers.Money(item.ItemPrice),
                        UiHelpers.Money(item.ItemPrice * item.Quantity));

                _gridPayments.Rows.Clear();
                foreach (var p in inv.Payments)
                    _gridPayments.Rows.Add(p.PaymentDate.ToString("yyyy-MM-dd"), TranslateMethod(p.PaymentMethod), UiHelpers.Money(p.Amount));

                _lblTotals.Text = AppLang.T("InvoiceDetails.Totals",
                    UiHelpers.Money(inv.TotalAmount),
                    UiHelpers.Money(inv.DiscountAmount),
                    UiHelpers.Money(inv.TaxAmount),
                    UiHelpers.Money(inv.NetAmount),
                    UiHelpers.Money(inv.PaidAmount),
                    UiHelpers.Money(inv.RemainingAmount));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("InvoiceDetails.Msg.LoadFailed"));
            }
        }

        /// <summary>طباعة الفاتورة بالصيغة المطلوبة: حرارية 80mm (الافتراضية للكاشير)
        /// أو A4 احترافية مفصلة (الرسمية) — ثم فتح النسخة الجاهزة.</summary>
        private async Task PrintPdfAsync(InvoicePrintFormat format)
        {
            if (_invoice == null)
                return;

            await InvoicePrintHelper.PrintAsync(_invoice.InvoiceId, this, format);
        }

        internal static string TranslateType(string type) => type switch
        {
            "sale" => AppLang.T("Common.Type.Sale"),
            "purchase" => AppLang.T("Common.Type.Purchase"),
            "customer_return" => AppLang.T("Common.Type.CustomerReturn"),
            "supplier_return" => AppLang.T("Common.Type.SupplierReturn"),
            _ => type
        };

        internal static string TranslateStatus(string status) => status switch
        {
            "completed" => AppLang.T("Common.Status.Completed"),
            "suspended" => AppLang.T("Common.Status.Suspended"),
            "cancelled" => AppLang.T("Common.Status.Cancelled"),
            _ => status
        };

        internal static string TranslatePayment(string status) => status switch
        {
            "paid" => AppLang.T("Common.Payment.Paid"),
            "partial" => AppLang.T("Common.Payment.Partial"),
            "unpaid" => AppLang.T("Common.Payment.Unpaid"),
            _ => status
        };

        internal static string TranslateMethod(string method) => method switch
        {
            "cash" => AppLang.T("Common.Method.Cash"),
            "card" => AppLang.T("Common.Method.Card"),
            "bank_transfer" => AppLang.T("Common.Method.BankTransfer"),
            _ => method
        };
    }
}
