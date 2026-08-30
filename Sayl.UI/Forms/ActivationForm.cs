//using PosLicensing;
//using Sayl.UI.Infrastructure;

//namespace Sayl.UI.Forms
//{
//    /// <summary>
//    /// شاشة التفعيل الإلزامية: تعرض كود الجهاز للنسخ والإرسال للموزّع،
//    /// وتستقبل نص الرخصة للصقه وتفعيله. تُفتح قبل تسجيل الدخول عند غياب رخصة صالحة.
//    /// </summary>
//    public class ActivationForm : DialogForm
//    {
//        public ActivationForm()
//        {
//            Text = "تفعيل البرنامج";
//            StartPosition = FormStartPosition.CenterScreen;
//            ClientSize = new Size(520, 596);
//            MinimumSize = new Size(480, 540);
//            BackColor = AppTheme.CardBg;
//            RightToLeft = RightToLeft.Yes;
//            Font = AppTheme.FontBody;
//            ShowInTaskbar = false;
//            FormBorderStyle = FormBorderStyle.None;
//            CornerRadius = 20;

//            var root = new TableLayoutPanel
//            {
//                Dock = DockStyle.Fill,
//                ColumnCount = 1,
//                Margin = new Padding(0)
//            };
//            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));   // الترويسة
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // عنوان كود الجهاز
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));    // حقل الكود + زر نسخ
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // تلميح
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // عنوان مفتاح التفعيل
//            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // حقل الرخصة
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));    // رسالة الخطأ
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));   // الأزرار

//            // ── الترويسة ──
//            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Primary };
//            var logo = Brand.MakeLogo(76, AppTheme.Primary);
//            var lblTitle = new Label
//            {
//                Text = "تفعيل سيل — Sayl POS",
//                Dock = DockStyle.Fill,
//                TextAlign = ContentAlignment.MiddleCenter,
//                ForeColor = Color.White,
//                Font = AppTheme.FontTitle
//            };

//            void LayoutHeader()
//            {
//                int pad = 18;
//                logo.Location = new Point(pad, (header.Height - logo.Height) / 2);
//                lblTitle.SetBounds(pad + logo.Width + 8, 0, header.Width - pad * 2 - logo.Width - 8, header.Height);
//            }
//            header.Resize += (_, _) => LayoutHeader();
//            header.Controls.Add(logo);
//            header.Controls.Add(lblTitle);

//            // ── كود الجهاز ──
//            var lblDeviceTitle = MakeSectionLabel("كود هذا الجهاز");

//            var deviceHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 0, 24, 0) };
//            var txtDeviceCode = new TextBox
//            {
//                ReadOnly = true,
//                BackColor = AppTheme.FieldBg,
//                BorderStyle = BorderStyle.FixedSingle,
//                Font = new Font("Consolas", 13F, FontStyle.Bold),
//                ForeColor = AppTheme.PrimaryDark,
//                TextAlign = HorizontalAlignment.Center,
//                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
//                Bounds = new Rectangle(24, 2, deviceHost.Width - 24 - 118, 48)
//            };
//            var btnCopy = AppTheme.MakeButton("نسخ الكود", AppTheme.PrimaryLight, foreColor: AppTheme.PrimaryDark,
//                hoverColor: Color.FromArgb(200, 216, 233), height: 48);
//            btnCopy.Anchor = AnchorStyles.Right | AnchorStyles.Top;
//            btnCopy.Bounds = new Rectangle(deviceHost.Width - 118 - 24, 2, 118, 48);

//            void LayoutDevice()
//            {
//                txtDeviceCode.SetBounds(24, 2, Math.Max(deviceHost.Width - 24 * 2 - 118, 100), 48);
//                btnCopy.Location = new Point(deviceHost.Width - 118 - 24, 2);
//            }
//            deviceHost.Resize += (_, _) => LayoutDevice();
//            deviceHost.Controls.Add(txtDeviceCode);
//            deviceHost.Controls.Add(btnCopy);

//            var lblHint = new Label
//            {
//                Text = "أرسل هذا الكود إلى الموزّع عبر واتساب أو الرسائل، وسيصلك مفتاح تفعيل خاص بجهازك.",
//                Dock = DockStyle.Fill,
//                TextAlign = ContentAlignment.TopCenter,
//                ForeColor = AppTheme.TextMuted,
//                Padding = new Padding(24, 4, 24, 0)
//            };

//            // ── مفتاح التفعيل ──
//            var lblKeyTitle = MakeSectionLabel("مفتاح التفعيل");

//            var keyHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 0, 24, 0) };
//            var txtLicense = new TextBox
//            {
//                Multiline = true,
//                ScrollBars = ScrollBars.Vertical,
//                BackColor = AppTheme.FieldBg,
//                BorderStyle = BorderStyle.FixedSingle,
//                Font = new Font("Consolas", 9F),
//                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
//                Bounds = new Rectangle(24, 2, keyHost.Width - 48, keyHost.Height - 6)
//            };

//            void LayoutKey()
//            {
//                txtLicense.SetBounds(24, 2, keyHost.Width - 48, keyHost.Height - 6);
//            }
//            keyHost.Resize += (_, _) => LayoutKey();
//            keyHost.Controls.Add(txtLicense);

//            // ── رسالة الخطأ/الحالة ──
//            var lblStatus = new Label
//            {
//                Dock = DockStyle.Fill,
//                TextAlign = ContentAlignment.MiddleCenter,
//                ForeColor = AppTheme.Danger,
//                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
//                Text = ""
//            };

//            // ── الأزرار ──
//            var buttonsHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 12, 24, 16) };
//            var btnCancel = AppTheme.MakeButton("إغلاق", Color.FromArgb(229, 231, 235), foreColor: AppTheme.TextPrimary,
//                hoverColor: Color.FromArgb(209, 213, 219), height: 52);
//            btnCancel.Dock = DockStyle.Fill;
//            btnCancel.DialogResult = DialogResult.Cancel;

//            var btnActivate = AppTheme.MakeButton("تفعيل البرنامج", AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 52);
//            btnActivate.Dock = DockStyle.Top;

//            buttonsHost.Controls.Add(btnCancel);
//            buttonsHost.Controls.Add(btnActivate);
//            btnCancel.BringToFront();

//            root.Controls.Add(header, 0, 0);
//            root.Controls.Add(lblDeviceTitle, 0, 1);
//            root.Controls.Add(deviceHost, 0, 2);
//            root.Controls.Add(lblHint, 0, 3);
//            root.Controls.Add(lblKeyTitle, 0, 4);
//            root.Controls.Add(keyHost, 0, 5);
//            root.Controls.Add(lblStatus, 0, 6);
//            root.Controls.Add(buttonsHost, 0, 7);

//            Controls.Add(root);
//            CancelButton = btnCancel;

//            Load += (_, _) =>
//            {
//                txtDeviceCode.Text = ActivationManager.GetDeviceCode();
//                txtLicense.Focus();
//            };

//            btnCopy.Click += (_, _) =>
//            {
//                Clipboard.SetText(txtDeviceCode.Text);
//                lblStatus.ForeColor = AppTheme.Success;
//                lblStatus.Text = "تم نسخ الكود — أرسله إلى الموزّع.";
//            };

//            btnActivate.Click += (_, _) =>
//            {
//                string license = txtLicense.Text.Trim();
//                if (license.Length == 0)
//                {
//                    ShowError(lblStatus, "يرجى لصق مفتاح التفعيل أولاً.");
//                    return;
//                }

//                Cursor = Cursors.WaitCursor;
//                btnActivate.Enabled = false;
//                try
//                {
//                    if (ActivationManager.Activate(license, out string error))
//                    {
//                        DialogResult = DialogResult.OK;
//                        Close();
//                    }
//                    else
//                        ShowError(lblStatus, error ?? "فشل التفعيل");
//                }
//                finally
//                {
//                    Cursor = Cursors.Default;
//                    btnActivate.Enabled = true;
//                }
//            };
//        }

//        private static Label MakeSectionLabel(string text)
//        {
//            return new Label
//            {
//                Text = text,
//                Dock = DockStyle.Fill,
//                TextAlign = ContentAlignment.BottomRight,
//                ForeColor = AppTheme.TextPrimary,
//                Font = AppTheme.FontHeading,
//                Padding = new Padding(24, 0, 24, 4)
//            };
//        }

//        private static void ShowError(Label status, string message)
//        {
//            status.ForeColor = AppTheme.Danger;
//            status.Text = message;
//        }
//    }
//}



using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PosLicensing;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// شاشة التفعيل الإلزامية: تعرض كود الجهاز للنسخ والإرسال للموزّع،
    /// وتستقبل نص الرخصة للصقه وتفعيله، أو استيراد ملف .lic.
    /// تُفتح قبل تسجيل الدخول عند غياب رخصة صالحة.
    /// </summary>
    public class ActivationForm : DialogForm
    {
        public ActivationForm()
        {
            Text = "تفعيل البرنامج";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 620);
            MinimumSize = new Size(520, 540);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            CornerRadius = 20;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));   // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // عنوان كود الجهاز
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));    // حقل الكود + زر نسخ
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // تلميح
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // عنوان مفتاح التفعيل
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // حقل الرخصة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));    // رسالة الخطأ
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));    // الأزرار (أصبحت أقل ارتفاعًا)

            // ── الترويسة ──
            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Primary };
            var logo = Brand.MakeLogo(76, AppTheme.Primary);
            var lblTitle = new Label
            {
                Text = "تفعيل سيل — Sayl POS",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = AppTheme.FontTitle
            };

            void LayoutHeader()
            {
                int pad = 18;
                logo.Location = new Point(pad, (header.Height - logo.Height) / 2);
                lblTitle.SetBounds(pad + logo.Width + 8, 0, header.Width - pad * 2 - logo.Width - 8, header.Height);
            }
            header.Resize += (_, _) => LayoutHeader();
            header.Controls.Add(logo);
            header.Controls.Add(lblTitle);

            // ── كود الجهاز ──
            var lblDeviceTitle = MakeSectionLabel("كود هذا الجهاز");

            var deviceHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 0, 24, 0) };
            var txtDeviceCode = new TextBox
            {
                ReadOnly = true,
                BackColor = AppTheme.FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 13F, FontStyle.Bold),
                ForeColor = AppTheme.PrimaryDark,
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Bounds = new Rectangle(24, 2, deviceHost.Width - 24 - 118, 48)
            };
            var btnCopy = AppTheme.MakeButton("نسخ الكود", AppTheme.PrimaryLight, foreColor: AppTheme.PrimaryDark,
                hoverColor: Color.FromArgb(200, 216, 233), height: 48);
            btnCopy.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCopy.Bounds = new Rectangle(deviceHost.Width - 118 - 24, 2, 118, 48);

            void LayoutDevice()
            {
                txtDeviceCode.SetBounds(24, 2, Math.Max(deviceHost.Width - 24 * 2 - 118, 100), 48);
                btnCopy.Location = new Point(deviceHost.Width - 118 - 24, 2);
            }
            deviceHost.Resize += (_, _) => LayoutDevice();
            deviceHost.Controls.Add(txtDeviceCode);
            deviceHost.Controls.Add(btnCopy);

            var lblHint = new Label
            {
                Text = "أرسل هذا الكود إلى الموزّع عبر واتساب أو الرسائل، أو استورد ملف .lic المُرسل إليك.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.TextMuted,
                Padding = new Padding(24, 4, 24, 0)
            };

            // ── مفتاح التفعيل ──
            var lblKeyTitle = MakeSectionLabel("مفتاح التفعيل");

            var keyHost = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(24, 0, 24, 0) };
            var txtLicense = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = AppTheme.FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
                Bounds = new Rectangle(24, 2, keyHost.Width - 48, keyHost.Height - 6)
            };

            void LayoutKey()
            {
                txtLicense.SetBounds(24, 2, keyHost.Width - 48, keyHost.Height - 6);
            }
            keyHost.Resize += (_, _) => LayoutKey();
            keyHost.Controls.Add(txtLicense);

            // ── رسالة الخطأ/الحالة ──
            var lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Danger,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Text = ""
            };

            // ── الأزرار ──
            var buttonsHost = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(20, 8, 20, 8)
            };

            var btnCancel = AppTheme.MakeButton("إغلاق", Color.FromArgb(229, 231, 235), foreColor: AppTheme.TextPrimary,
                hoverColor: Color.FromArgb(209, 213, 219), height: 52);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Width = 140;

            var btnImport = AppTheme.MakeButton("استيراد ملف .lic", AppTheme.PrimaryLight, foreColor: AppTheme.PrimaryDark,
                hoverColor: Color.FromArgb(200, 216, 233), height: 52);
            btnImport.Width = 180;

            var btnActivate = AppTheme.MakeButton("تفعيل البرنامج", AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 52);
            btnActivate.Width = 160;

            buttonsHost.Controls.Add(btnActivate);
            buttonsHost.Controls.Add(btnImport);
            buttonsHost.Controls.Add(btnCancel);

            // إضافة العناصر إلى الجدول
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(lblDeviceTitle, 0, 1);
            root.Controls.Add(deviceHost, 0, 2);
            root.Controls.Add(lblHint, 0, 3);
            root.Controls.Add(lblKeyTitle, 0, 4);
            root.Controls.Add(keyHost, 0, 5);
            root.Controls.Add(lblStatus, 0, 6);
            root.Controls.Add(buttonsHost, 0, 7);

            Controls.Add(root);
            CancelButton = btnCancel;

            Load += (_, _) =>
            {
                txtDeviceCode.Text = ActivationManager.GetDeviceCode();
                txtLicense.Focus();
            };

            btnCopy.Click += (_, _) =>
            {
                Clipboard.SetText(txtDeviceCode.Text);
                lblStatus.ForeColor = AppTheme.Success;
                lblStatus.Text = "تم نسخ الكود — أرسله إلى الموزّع.";
            };

            btnImport.Click += (_, _) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
                    Title = "اختر ملف الترخيص"
                })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        try
                        {
                            string licenseText = File.ReadAllText(dlg.FileName);
                            txtLicense.Text = licenseText.Trim();
                            lblStatus.Text = "تم تحميل ملف الترخيص. اضغط تفعيل البرنامج.";
                            lblStatus.ForeColor = AppTheme.Success;
                        }
                        catch (Exception ex)
                        {
                            ShowError(lblStatus, "تعذر قراءة الملف: " + ex.Message);
                        }
                    }
                }
            };

            btnActivate.Click += (_, _) =>
            {
                string license = txtLicense.Text.Trim();
                if (license.Length == 0)
                {
                    ShowError(lblStatus, "يرجى لصق مفتاح التفعيل أو استيراد ملف .lic أولاً.");
                    return;
                }

                Cursor = Cursors.WaitCursor;
                btnActivate.Enabled = false;
                try
                {
                    if (ActivationManager.Activate(license, out string error))
                    {
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                        ShowError(lblStatus, error ?? "فشل التفعيل");
                }
                finally
                {
                    Cursor = Cursors.Default;
                    btnActivate.Enabled = true;
                }
            };
        }

        private static Label MakeSectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomRight,
                ForeColor = AppTheme.TextPrimary,
                Font = AppTheme.FontHeading,
                Padding = new Padding(24, 0, 24, 4)
            };
        }

        private static void ShowError(Label status, string message)
        {
            status.ForeColor = AppTheme.Danger;
            status.Text = message;
        }
    }
}