using System.Drawing.Printing;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>عرض تفاصيل فاتورة (بنود ومدفوعات) مع إمكانية الطباعة.</summary>
    public class InvoiceDetailsForm : Form
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

        private void BuildUi()
        {
            Text = "تفاصيل الفاتورة";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(680, 720);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            _lblHeader = new Label
            {
                Text = "جارٍ التحميل...",
                Bounds = new Rectangle(20, 14, 640, 92),
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11F),
                ForeColor = AppTheme.TextPrimary
            };

            var lblItems = new Label { Text = "البنود", Bounds = new Rectangle(20, 112, 640, 26), TextAlign = ContentAlignment.MiddleRight, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _gridItems = new DataGridView { Bounds = new Rectangle(20, 140, 640, 240) };
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

            var lblPayments = new Label { Text = "المدفوعات", Bounds = new Rectangle(20, 388, 640, 26), TextAlign = ContentAlignment.MiddleRight, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _gridPayments = new DataGridView { Bounds = new Rectangle(20, 416, 640, 130) };
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
                Bounds = new Rectangle(20, 552, 640, 56),
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                BackColor = AppTheme.FieldBg
            };

            var btnPrint = AppTheme.MakeButton("طباعة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnPrint.Bounds = new Rectangle(20, 626, 200, 50);
            btnPrint.Click += (_, _) => PrintInvoice();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(460, 626, 200, 50);
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                _lblHeader, lblItems, _gridItems, lblPayments, _gridPayments, _lblTotals, btnPrint, btnClose
            });
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
                    $"التاريخ: {inv.CreatedAt:yyyy-MM-dd HH:mm}";

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

        private void PrintInvoice()
        {
            if (_invoice == null)
                return;

            try
            {
                using var doc = new PrintDocument();
                doc.PrintPage += (_, args) => RenderInvoicePage(args.Graphics, args.MarginBounds);
                using var dialog = new PrintDialog { Document = doc, UseEXDialog = true };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    doc.Print();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذرت الطباعة. تحقق من الطابعة.");
            }
        }

        private void RenderInvoicePage(Graphics g, Rectangle bounds)
        {
            var inv = _invoice!;
            g.Clear(Color.White);

            float y = bounds.Top + 10;
            var titleFont = new Font("Segoe UI", 16F, FontStyle.Bold);
            var boldFont = new Font("Segoe UI", 10F, FontStyle.Bold);
            var font = new Font("Segoe UI", 10F);

            StringFormat rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft);

            void Line(string text, Font f, float height)
            {
                g.DrawString(text, f, Brushes.Black, new RectangleF(bounds.Left, y, bounds.Width, height), rtl);
                y += height;
            }

            Line("سيل — Sayl", titleFont, 34);
            Line($"فاتورة رقم: {inv.InvoiceId}    التاريخ: {inv.CreatedAt:yyyy-MM-dd HH:mm}", font, 22);
            Line($"العميل: {inv.PartyName ?? "عميل مجهول"}    الكاشير: {inv.CashierName}", font, 22);
            Line(new string('-', 70), font, 20);

            foreach (var item in inv.Items)
                Line($"{item.ProductName}   × {item.Quantity}   @ {UiHelpers.Money(item.ItemPrice)}   = {UiHelpers.Money(item.ItemPrice * item.Quantity)}", font, 22);

            Line(new string('-', 70), font, 20);
            Line($"الإجمالي: {UiHelpers.Money(inv.TotalAmount)}    الخصم: {UiHelpers.Money(inv.DiscountAmount)}    الضريبة: {UiHelpers.Money(inv.TaxAmount)}", font, 22);
            Line($"الصافي: {UiHelpers.Money(inv.NetAmount)}", boldFont, 24);
            Line($"المدفوع: {UiHelpers.Money(inv.PaidAmount)}    المتبقي: {UiHelpers.Money(inv.RemainingAmount)}", font, 22);

            if (inv.Payments.Count > 0)
            {
                Line("المدفوعات:", boldFont, 22);
                foreach (var p in inv.Payments)
                    Line($"  {p.PaymentDate:yyyy-MM-dd}   {TranslateMethod(p.PaymentMethod)}   {UiHelpers.Money(p.Amount)}", font, 20);
            }

            Line("شكرًا لتعاملكم معنا!", font, 24);
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
