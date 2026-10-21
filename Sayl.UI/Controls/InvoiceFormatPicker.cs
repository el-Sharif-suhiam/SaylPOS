using Sayl.UI.Infrastructure;

namespace Sayl.UI.Controls
{
    /// <summary>
    /// منتقي صيغة طباعة الفاتورة — نافذة واحدة موحدة لكل مواضع الطباعة:
    ///  • بعد اكتمال البيع: ثلاثية (حرارية / A4 / بدون طباعة) — قرار المالك:
    ///    لا طباعة تلقائية إطلاقًا، النافذة هي الخيار الوحيد دائمًا.
    ///  • عند إعادة طباعة نسخة إضافية (فواتير اليوم / آخر فاتورة):
    ///    خياران (حرارية / A4) + إلغاء.
    /// الترجم: حرارية→Yes، A4→No، بدون/إلغاء/Esc→null (لا طباعة).
    /// </summary>
    internal static class InvoiceFormatPicker
    {
        /// <summary>يعرض النافذة ويعيد الصيغة المختارة — null = بدون طباعة أو إلغاء.</summary>
        public static InvoicePrintFormat? Show(IWin32Window? owner, bool allowSkip)
        {
            using var dlg = new DialogForm
            {
                StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                Size = new Size(470, allowSkip ? 350 : 310),
                BackColor = AppTheme.CardBg,
                RightToLeft = RightToLeft.Yes,
                ShowInTaskbar = false,
                KeyPreview = true
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(24, 20, 24, 20)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // العنوان
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));  // التوضيح
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // الأزرار

            var lblTitle = new Label
            {
                Text = AppLang.T("InvoiceFormat.Title"),
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Primary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };

            var lblHint = new Label
            {
                Text = AppLang.T("InvoiceFormat.Msg"),
                Font = AppTheme.FontBody,
                ForeColor = AppTheme.TextMuted,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };

            // أزرار عمودية — الأول حرارية (الطريق الأسرع للكاشير) وأول ما يلتقطه Enter
            var buttonsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };

            var btnThermal = AppTheme.MakeButton(AppLang.T("InvoiceFormat.Thermal"), AppTheme.Primary,
                hoverColor: AppTheme.PrimaryDark, height: 52);
            btnThermal.Size = new Size(360, 52);
            btnThermal.DialogResult = DialogResult.Yes;

            var btnA4 = AppTheme.MakeButton(AppLang.T("InvoiceFormat.A4"), AppTheme.Accent,
                hoverColor: AppTheme.AccentDark, height: 52);
            btnA4.Size = new Size(360, 52);
            btnA4.DialogResult = DialogResult.No;

            buttonsFlow.Controls.Add(btnThermal);
            buttonsFlow.Controls.Add(btnA4);

            var btnSkip = AppTheme.MakeButton(AppLang.T("InvoiceFormat.Skip"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnSkip.Size = new Size(360, 48);
            if (allowSkip)
            {
                btnSkip.DialogResult = DialogResult.OK;
                buttonsFlow.Controls.Add(btnSkip);
            }
            else
            {
                btnSkip.DialogResult = DialogResult.Cancel;
                btnSkip.Text = AppLang.T("Common.Button.Cancel");
                buttonsFlow.Controls.Add(btnSkip);
            }

            layout.Controls.Add(lblTitle, 0, 0);
            layout.Controls.Add(lblHint, 0, 1);
            layout.Controls.Add(buttonsFlow, 0, 2);
            dlg.Controls.Add(layout);

            dlg.AcceptButton = btnThermal;   // Enter = حرارية (الاستخدام الأشهر)
            dlg.CancelButton = btnSkip;      // Esc = بدون طباعة (أو إلغاء)

            if (owner != null)
                dlg.ShowDialog(owner);
            else
                dlg.ShowDialog();

            return dlg.DialogResult switch
            {
                DialogResult.Yes => InvoicePrintFormat.ThermalReceipt,
                DialogResult.No => InvoicePrintFormat.A4Detailed,
                _ => (InvoicePrintFormat?)null
            };
        }
    }
}
