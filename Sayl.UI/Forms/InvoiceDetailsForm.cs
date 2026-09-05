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
            Text = "تفاصيل الفاتورة";
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
                Text = "جارٍ التحميل...",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11F),
                ForeColor = AppTheme.TextPrimary
            };

            var lblItems = new Label
            {
                Text = "البنود",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _gridItems = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridItems);
            _gridItems.Columns.Add("colProduct", "المنتج");
            _gridItems.Columns.Add("colBatch", "الدفعة");
            _gridItems.Columns.Add("colQty", "الكمية");
            _gridItems.Columns.Add("colPrice", "السعر");
            _gridItems.Columns.Add("colTotal", "الإجمالي");
            _gridItems.Columns["colProduct"].FillWeight = 38;
            _gridItems.Columns["colBatch"].FillWeight = 16;
            _gridItems.Columns["colQty"].FillWeight = 12;
            _gridItems.Columns["colPrice"].FillWeight = 17;
            _gridItems.Columns["colTotal"].FillWeight = 17;

            var lblPayments = new Label
            {
                Text = "المدفوعات",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _gridPayments = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridPayments);
            _gridPayments.Columns.Add("colDate", "التاريخ");
            _gridPayments.Columns.Add("colMethod", "الطريقة");
            _gridPayments.Columns.Add("colAmount", "المبلغ");
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

            var btnPrint = AppTheme.MakeButton("طباعة حرارية 80مم", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnPrint.Dock = DockStyle.Fill;
            btnPrint.Margin = new Padding(0, 0, 4, 0);
            btnPrint.Click += async (_, _) => await PrintPdfAsync(InvoicePrintFormat.ThermalReceipt);

            // الفاتورة الرسمية A4 — للتقارير المفصلة والاحتياجات الرسمية (توقيعات، دفعات، دفعات)
            var btnPrintA4 = AppTheme.MakeButton("فاتورة A4 احترافية", AppTheme.Accent, hoverColor: AppTheme.AccentDark);
            btnPrintA4.Dock = DockStyle.Fill;
            btnPrintA4.Margin = new Padding(4, 0, 4, 0);
            btnPrintA4.Click += async (_, _) => await PrintPdfAsync(InvoicePrintFormat.A4Detailed);

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
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

                Text = $"فاتورة #{inv.InvoiceId}";
                _lblHeader.Text =
                    $"فاتورة رقم: {inv.InvoiceId}    النوع: {TranslateType(inv.Type)}    الحالة: {TranslateStatus(inv.Status)}    الدفع: {TranslatePayment(inv.PaymentStatus)}\n" +
                    $"العميل: {inv.PartyName ?? "عميل مجهول"}    {(string.IsNullOrWhiteSpace(inv.PartyPhone) ? "" : $"جوال: {inv.PartyPhone}    ")}الكاشير: {inv.CashierName}\n" +
                    $"التاريخ: {Sayl.UI.Infrastructure.AppClock.ToLocal(inv.CreatedAt ?? DateTime.UtcNow):yyyy-MM-dd HH:mm}";

                _gridItems.Rows.Clear();
                foreach (var item in inv.Items)
                    _gridItems.Rows.Add(item.ProductName, item.BatchNumber ?? "-",
                        item.Quantity, UiHelpers.Money(item.ItemPrice),
                        UiHelpers.Money(item.ItemPrice * item.Quantity));

                _gridPayments.Rows.Clear();
                foreach (var p in inv.Payments)
                    _gridPayments.Rows.Add(p.PaymentDate.ToString("yyyy-MM-dd"), TranslateMethod(p.PaymentMethod), UiHelpers.Money(p.Amount));

                _lblTotals.Text =
                    $"الإجمالي: {UiHelpers.Money(inv.TotalAmount)}    الخصم: {UiHelpers.Money(inv.DiscountAmount)}    الضريبة: {UiHelpers.Money(inv.TaxAmount)}\n" +
                    $"الصافي: {UiHelpers.Money(inv.NetAmount)}    المدفوع: {UiHelpers.Money(inv.PaidAmount)}    المتبقي: {UiHelpers.Money(inv.RemainingAmount)}";
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل تفاصيل الفاتورة.");
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
            "sale" => "بيع",
            "purchase" => "شراء",
            "customer_return" => "مرتجع عميل",
            "supplier_return" => "مرتجع مورد",
            _ => type
        };

        internal static string TranslateStatus(string status) => status switch
        {
            "completed" => "مكتملة",
            "suspended" => "معلقة",
            "cancelled" => "ملغاة",
            _ => status
        };

        internal static string TranslatePayment(string status) => status switch
        {
            "paid" => "مدفوعة",
            "partial" => "جزئية",
            "unpaid" => "غير مدفوعة",
            _ => status
        };

        internal static string TranslateMethod(string method) => method switch
        {
            "cash" => "نقدي",
            "card" => "شبكة",
            "bank_transfer" => "تحويل بنكي",
            _ => method
        };
    }
}
