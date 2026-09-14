using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>عنصر مؤقت يعرض رسالة «قيد التطوير» حتى تُبنى أقسام المراحل القادمة.</summary>
    internal class SectionPlaceholderControl : UserControl
    {
        public SectionPlaceholderControl(string sectionTitle)
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;

            var lblTitle = new Label
            {
                Text = sectionTitle,
                Font = AppTheme.FontTitle,
                ForeColor = AppTheme.TextPrimary,
                AutoSize = true
            };

            var lblInfo = new Label
            {
                Text = AppLang.T("Placeholder.Msg"),
                Font = AppTheme.FontBody,
                ForeColor = AppTheme.TextMuted,
                AutoSize = true
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));

            layout.Controls.Add(CenterInPanel(lblTitle), 0, 1);
            layout.Controls.Add(CenterInPanel(lblInfo), 0, 2);
            Controls.Add(layout);

            Resize += (_, _) => Recenter(layout);
        }

        private static Panel CenterInPanel(Control inner)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(inner);
            panel.Resize += (_, _) => Recenter(panel);
            return panel;
        }

        private static void Recenter(Panel panel)
        {
            foreach (Control inner in panel.Controls)
            {
                inner.Location = new Point(
                    (panel.ClientSize.Width - inner.Width) / 2,
                    (panel.ClientSize.Height - inner.Height) / 2);
            }
        }
    }
}
