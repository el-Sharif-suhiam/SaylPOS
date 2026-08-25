using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>نافذة «حول البرنامج»: اسم المشروع، الإصدار، ووصف مختصر.</summary>
    public class AboutForm : DialogForm
    {
        public AboutForm()
        {
            Text = "حول البرنامج";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 410);
            MinimumSize = new Size(420, 370);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            CornerRadius = 20;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));   // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));    // الإصدار
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // الوصف
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));    // الزر

            // ── الترويسة: شعار دائري + الاسم على خلفية زرقاء ──
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.Primary,
                ColumnCount = 1,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            var logoHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Primary };
            var logo = Brand.MakeLogo(88, AppTheme.Primary);

            void CenterLogo()
            {
                logo.Location = new Point(
                    (logoHost.Width - logo.Width) / 2,
                    (logoHost.Height - logo.Height) / 2);
            }
            logoHost.Resize += (_, _) => CenterLogo();
            logoHost.Controls.Add(logo);

            var lblName = new Label
            {
                Text = "سيل — Sayl",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 14.5F, FontStyle.Bold)
            };

            header.Controls.Add(logoHost, 0, 0);
            header.Controls.Add(lblName, 0, 1);

            var lblVersion = new Label
            {
                Text = "الإصدار 1.0.0",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };

            var lblDescription = new Label
            {
                Text = "نظام متكامل للمبيعات وإدارة المخزون\nللمتاجر والصيدليات والمطاعم والأعمال الصغيرة.\n\nمبني على .NET 10 و SQL Server وواجهات WinForms.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(24, 10, 24, 0)
            };

            var btnCloseHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 8, 24, 12) };
            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnClose.Dock = DockStyle.Fill;
            btnClose.DialogResult = DialogResult.Cancel;
            btnCloseHost.Controls.Add(btnClose);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(lblVersion, 0, 1);
            root.Controls.Add(lblDescription, 0, 2);
            root.Controls.Add(btnCloseHost, 0, 3);

            Controls.Add(root);
            CancelButton = btnClose;
        }
    }
}
