using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>نافذة «حول البرنامج»: اسم المشروع، الإصدار، ووصف مختصر.</summary>
    public class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "حول البرنامج";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 380);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 20);

            var header = new Panel
            {
                Bounds = new Rectangle(0, 0, 460, 130),
                BackColor = AppTheme.Primary
            };

            var lblLogo = new Label
            {
                Text = "س",
                Bounds = new Rectangle(194, 14, 72, 72),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 30F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(29, 78, 216)
            };
            AppTheme.ApplyRoundedRegion(lblLogo, 36);

            var lblName = new Label
            {
                Text = "سيل — Sayl",
                Bounds = new Rectangle(0, 92, 460, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent
            };

            var lblVersion = new Label
            {
                Text = "الإصدار 1.0.0",
                Bounds = new Rectangle(0, 148, 460, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };

            var lblDescription = new Label
            {
                Text = "نظام متكامل للمبيعات وإدارة المخزون\nللمتاجر والصيدليات والمطاعم والأعمال الصغيرة.\n\nمبني على .NET 10 و SQL Server وواجهات WinForms.",
                Bounds = new Rectangle(40, 182, 380, 110),
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.TextPrimary
            };

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnClose.Bounds = new Rectangle(60, 304, 340, 50);
            btnClose.DialogResult = DialogResult.Cancel;

            header.Controls.Add(lblName);
            header.Controls.Add(lblLogo);
            Controls.Add(header);
            Controls.Add(lblVersion);
            Controls.Add(lblDescription);
            Controls.Add(btnClose);

            CancelButton = btnClose;
        }
    }
}
