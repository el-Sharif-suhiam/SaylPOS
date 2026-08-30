
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>نافذة «حول البرنامج».</summary>
    public class AboutForm : DialogForm
    {
        public AboutForm()
        {
            Text = "حول البرنامج";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 540);
            MinimumSize = new Size(420, 460);
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

            // تحديد الصفوف
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170)); // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // الإصدار
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // الترخيص
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));  // الوصف
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));  // سطر المطور
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // الأيقونات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));  // زر الإغلاق

            // ── الترويسة ──
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

            // الإصدار
            var lblVersion = new Label
            {
                Text = "الإصدار 1.0.0",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };

            // الترخيص
            string licenseText;
            Color licenseColor;
            if (PosLicensing.ActivationManager.TryGetLicenseInfo(out var customer, out var expiryUtc))
            {
                string who = string.IsNullOrEmpty(customer) ? "—" : customer;
                string until = expiryUtc == null
                    ? "ترخيص دائم"
                    : "حتى " + expiryUtc.Value.ToLocalTime().ToString("yyyy/MM/dd");
                licenseText = $"مرخَّص لـ {who}  •  {until}";
                licenseColor = AppTheme.Success;
            }
            else
            {
                licenseText = "غير مفعَّل";
                licenseColor = AppTheme.Danger;
            }

            var lblLicense = new Label
            {
                Text = licenseText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = licenseColor,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            // الوصف
            var lblDescription = new Label
            {
                Text = "نظام متكامل للمبيعات وإدارة المخزون\nللمتاجر والصيدليات والمطاعم والأعمال الصغيرة.\nمبني على .NET 10 و SQL Server وواجهات WinForms.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(24, 10, 24, 0)
            };

            // سطر المطور
            var lblCreator = new Label
            {
                Text = " By :\n م. الشريف فرج اسحيم",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(3, 16, 82),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };

            // حاوية الأيقونات
            var socialHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0, 4, 0, 0)
            };

            var socialPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = AppTheme.CardBg,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            void CenterSocial()
            {
                socialPanel.Location = new Point(
                    (socialHost.Width - socialPanel.Width) / 2,
                    (socialHost.Height - socialPanel.Height) / 2);
            }
            socialHost.Resize += (_, _) => CenterSocial();

            // إضافة الأيقونات
            socialPanel.Controls.Add(MakeSocialIcon("فيسبوك", Properties.Resources.Facebook, "https://facebook.com/yourpage"));
            socialPanel.Controls.Add(MakeSocialIcon("GitHub", Properties.Resources.github, "https://github.com/yourusername"));
            socialPanel.Controls.Add(MakeSocialIcon("الموقع", Properties.Resources.Website, "https://www.sayl.com"));

            socialHost.Controls.Add(socialPanel);

            // زر الإغلاق
            var btnCloseHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(24, 8, 24, 12)
            };
            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnClose.Dock = DockStyle.Fill;
            btnClose.DialogResult = DialogResult.Cancel;
            btnCloseHost.Controls.Add(btnClose);

            // إضافة كل العناصر إلى root
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(lblVersion, 0, 1);
            root.Controls.Add(lblLicense, 0, 2);
            root.Controls.Add(lblDescription, 0, 3);
            root.Controls.Add(lblCreator, 0, 4);
            root.Controls.Add(socialHost, 0, 5);
            root.Controls.Add(btnCloseHost, 0, 6);

            Controls.Add(root);
            CancelButton = btnClose;
        }

        private Button MakeSocialIcon(string tooltip, Image icon, string url)
        {
            var btn = new Button
            {
                Width = 44,
                Height = 44,
                FlatStyle = FlatStyle.Flat,
                BackColor = AppTheme.CardBg,
                Cursor = Cursors.Hand,
                TabStop = false,
                Margin = new Padding(4),
                Image = icon,
                Text = "",
                ImageAlign = ContentAlignment.MiddleCenter,
                BackgroundImageLayout = ImageLayout.Zoom
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = AppTheme.BorderColor;
            btn.MouseEnter += (_, _) => btn.BackColor = AppTheme.PrimaryLight;
            btn.MouseLeave += (_, _) => btn.BackColor = AppTheme.CardBg;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    MessageBox.Show("تعذر فتح الرابط: " + url, "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            return btn;
        }
    }
}