
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة «حول البرنامج» — تبويبان (TabControl):
    ///  1) «عن النظام»: الشعار + الإصدار + حالة التفعيل + الوصف + المطور + التواصل.
    ///  2) «التراخيص»: ملخص اتفاقية ترخيص المستخدم النهائي (EULA) + المكونات
    ///     مفتوحة المصدر — داخل مربع نص قابل للتمرير والنسخ.
    /// 📖 لماذا RichTextBox لا Label؟ نص الاتفاقية طويل ويجب أن:
    ///  • يتمرر (Scrollbar) بلا قص،
    ///  • يُنسخ منه (المستخدم ينسخ بنودًا للمراجعة القانونية)،
    ///  • يبقى محاذى يمينًا RTL كاملًا (RichTextBox يحترم RightToLeft
    ///    في النص العربي أفضل من Labels المتكدسة).
    /// </summary>
    public class AboutForm : DialogForm
    {
        public AboutForm()
        {
            Text = AppLang.T("About.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            // 📖 760×680: عرض أوسع لتبويب التراخيص — بطاقات المكونات بعمودين
            // كاملين وبنود EULA تتنفس. (كانت 520×600 فانحشرت البطاقات في ~420px
            // بعد خصم الرأس والتبويبات والزر.) الحواريات تُقص تلقائيًا على
            // شاشات صغيرة (ClampToWorkingArea في DialogForm) فلا خطر كسر.
            ClientSize = new Size(760, 680);
            MinimumSize = new Size(620, 560);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            CornerRadius = 20;

            // ══ الجذر: شريط عنوان كحلي قصير + TabControl يملأ الباقي ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // شريط الشعار والاسم
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // التبويبات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // زر الإغلاق

            // ── شريط علوي: الشعار يسارًا والاسم وسطًا (هوية ثابتة مهما تغير التبويب) ──
            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Primary };
            var logo = Brand.MakeLogo(42, AppTheme.Primary);
            logo.Dock = DockStyle.Left;
            logo.Padding = new Padding(2);

            var lblName = new Label
            {
                Text = AppLang.T("About.App.Brand"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 15F, FontStyle.Bold)
            };

            header.Controls.Add(lblName);
            header.Controls.Add(logo);

            // ══ التبويبات ══
            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                RightToLeft = RightToLeft.Yes,   // تبويبات من اليمين
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                Padding = new Point(10, 8)
            };
            tabs.TabPages.Add(BuildAboutPage());
            tabs.TabPages.Add(BuildLicensePage());

            // ── زر الإغلاق أسفل النافذة ──
            var btnCloseHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(24, 12, 24, 14)
            };
            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnClose.Dock = DockStyle.Fill;
            btnClose.DialogResult = DialogResult.Cancel;
            btnCloseHost.Controls.Add(btnClose);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(tabs, 0, 1);
            root.Controls.Add(btnCloseHost, 0, 2);

            Controls.Add(root);
            CancelButton = btnClose;
        }

        // ═══════════════════════════════════════════════
        // تبويب 1: عن النظام — (محتوى النافذة القديمة كاملًا)
        // ═══════════════════════════════════════════════
        // ═══════════════════════════════════════════════
        // تبويب 1: عن النظام — محتوى مركزي مضبوط العرض
        // 📖 درس جمالي: كبرت النافذة (لأجل تبويب التراخيص) فتبعثرت هذه
        // البطاقات الصغيرة وسط فراغ. الحل الكلاسيكي: «عمود مضبوط يتوسط» —
        // المحتوى داخل عمود TLP بعرض ثابت ~440px مثبت وسط الصفحة
        // (عمودان جانبيان مرنان يمتصان الفضوة) — نفس إحساس النافذة
        // الضيقة القديمة أناقةً، داخل إطار واسع.
        // ═══════════════════════════════════════════════
        private TabPage BuildAboutPage()
        {
            var page = new TabPage(AppLang.T("About.Tab.System")) { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };

            // الحاضنة: ثلاثة أعمدة — مرن | عمود المحتوى 460px | مرن
            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(12, 10, 12, 8)
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // عمود المحتوى الفعلي — كامل منطق الشاشة القديمة كما كان
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // الإصدار
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // التفعيل
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // الوصف
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));  // المطور
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));  // التواصل

            var lblVersion = new Label
            {
                Text = AppLang.T("About.Version"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };

            // حالة التفعيل — كما كانت تمامًا
            string licenseText;
            Color licenseColor;
            if (PosLicensing.ActivationManager.TryGetLicenseInfo(out var customer, out var expiryUtc))
            {
                string who = string.IsNullOrEmpty(customer) ? "—" : customer;
                string until = expiryUtc == null
                    ? AppLang.T("About.License.Permanent")
                    : AppLang.T("About.License.Until", expiryUtc.Value.ToLocalTime().ToString("yyyy/MM/dd"));
                licenseText = AppLang.T("About.License.LicensedTo", who, until);
                licenseColor = AppTheme.Success;
            }
            else
            {
                licenseText = AppLang.T("About.License.NotActivated");
                licenseColor = AppTheme.Danger;
            }

            var lblLicense = new Label
            {
                Text = licenseText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = licenseColor,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };

            var lblDescription = new Label
            {
                Text = AppLang.T("About.Description"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(8, 6, 8, 0)
            };

            var lblCreator = new Label
            {
                Text = AppLang.T("About.Creator"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(3, 16, 82),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };

            // التواصل — الأيقونات (كما كانت)
            var socialHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0, 6, 0, 0)
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
            socialPanel.Controls.Add(MakeSocialIcon(AppLang.T("About.Social.Facebook"), Properties.Resources.Facebook, "https://facebook.com/yourpage"));
            socialPanel.Controls.Add(MakeSocialIcon("GitHub", Properties.Resources.github, "https://github.com/yourusername"));
            socialPanel.Controls.Add(MakeSocialIcon(AppLang.T("About.Social.Website"), Properties.Resources.Website, "https://www.sayl.com"));
            socialHost.Controls.Add(socialPanel);

            body.Controls.Add(lblVersion, 0, 0);
            body.Controls.Add(lblLicense, 0, 1);
            body.Controls.Add(lblDescription, 0, 2);
            body.Controls.Add(lblCreator, 0, 3);
            body.Controls.Add(socialHost, 0, 4);

            // تثبيت العمود المركزي: العمود 1 من الحاضنة (RTL: وسط حقيقي)
            shell.Controls.Add(body, 1, 0);
            page.Controls.Add(shell);
            return page;
        }

        // ═══════════════════════════════════════════════
        // ═══════════════════════════════════════════════
        // تبويب 2: التراخيص — بطاقات بصرية بدل جدار نص
        // 📖 درس هندسي (علة الانحشار): FlowLayoutPanel بـ Dock=Fill داخل
        // Panel{AutoScroll} «يتقزم» بارتفاع المنطقة المرئية ولا يمتد
        // للمحتوى — علة WinForms كلاسيكية. الحل الموثوق: عمود TLP
        // بـ Dock=Top وارتفاعات محسوبة (نمط AddRow الموثق في كل النظام).
        // ═══════════════════════════════════════════════
        private TabPage BuildLicensePage()
        {
            var page = new TabPage(AppLang.T("About.Tab.Licenses")) { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                AutoScroll = true,
                Padding = new Padding(16, 12, 16, 16)
            };

            // عمود المحتوى: AutoSize (لا Dock!) — TLP بـ Dock=Top داخل
            // Panel{AutoScroll} قصّ ارتفاعه عند أول layout مهما بلغ مجموع
            // الصفوف (علة قيست حيًا: 12 صفًا بمجموع ~882px ظهرت بارتفاع
            // 100px). AutoSize وحده يجعل الارتفاع يتبع المحتوى، والموقع
            // الافتراضي (أعلى يسار) يثبته في رأس الممرر.
            var col = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0)
            };
            col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 📖 مصنع الصفوف: Height محسوب لكل صف (Absolute) — البطاقات Dock=Top
            void AddRow(Control c, int height, int gapAfter = 8)
            {
                col.RowStyles.Add(new RowStyle(SizeType.Absolute, height + gapAfter));
                c.Dock = DockStyle.Top;
                c.Height = height;
                c.Margin = new Padding(0);
                col.Controls.Add(c, 0, col.RowStyles.Count - 1);
            }

            // ── 1) بطاقة الحقوق الكحلية ──
            var rightsCard = new Panel { BackColor = AppTheme.SidebarBg, Padding = new Padding(0) };
            AppTheme.ApplyRoundedRegion(rightsCard, 14);
            var rightsInner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = Color.Transparent, Padding = new Padding(18, 12, 18, 10)
            };
            rightsInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rightsInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            var lblRights = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.White,
                Text = AppLang.T("About.License.Rights"),
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold)
            };
            var imgLogo = Brand.MakeLogo(56, AppTheme.SidebarBg);
            imgLogo.Dock = DockStyle.Fill;
            rightsInner.Controls.Add(lblRights, 0, 0);
            rightsInner.Controls.Add(imgLogo, 1, 0);
            rightsCard.Controls.Add(rightsInner);
            AddRow(rightsCard, 96, 14);

            // ── 2) مقدمة ──
            AddRow(MakeText(AppLang.T("About.License.Intro"), bold: false), 42, 6);

            // ── 3) بنود الاتفاقية الخمسة ──
            AddRow(MakeEulaCard(AppLang.T("About.License.Eula1.Number"), AppLang.T("About.License.Eula1.Title"),
                AppLang.T("About.License.Eula1.Body")), 98);
            AddRow(MakeEulaCard(AppLang.T("About.License.Eula2.Number"), AppLang.T("About.License.Eula2.Title"),
                AppLang.T("About.License.Eula2.Body")), 122);
            AddRow(MakeEulaCard(AppLang.T("About.License.Eula3.Number"), AppLang.T("About.License.Eula3.Title"),
                AppLang.T("About.License.Eula3.Body")), 98);
            AddRow(MakeEulaCard(AppLang.T("About.License.Eula4.Number"), AppLang.T("About.License.Eula4.Title"),
                AppLang.T("About.License.Eula4.Body")), 122);
            AddRow(MakeEulaCard(AppLang.T("About.License.Eula5.Number"), AppLang.T("About.License.Eula5.Title"),
                AppLang.T("About.License.Eula5.Body")), 122, 12);

            // ── 4) عنوان قسم المكونات ──
            AddRow(MakeSectionLabel(AppLang.T("About.License.Components")), 30, 2);
            AddRow(MakeText(AppLang.T("About.License.ComponentsIntro"), bold: false), 24, 6);

            // ── 5) شبكة المكونات: 4 صفوف × عمودان (Absolute 58 لا AutoSize) ──
            var grid = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 4,
                BackColor = AppTheme.CardBg, Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int r = 0; r < 4; r++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            string[] components =
            {
                "QuestPDF 2026.8",      AppLang.T("About.License.ComponentPdf"),
                "ClosedXML 0.104",      AppLang.T("About.License.ComponentExcel"),
                "LiveChartsCore 2.0",   AppLang.T("About.License.ComponentCharts"),
                "EF Core 10",           AppLang.T("About.License.ComponentDb"),
                "Microsoft.Extensions", AppLang.T("About.License.ComponentFramework"),
                "BCrypt.Net 4.2",       AppLang.T("About.License.ComponentCrypto"),
                "SQL Server Express",  AppLang.T("About.License.ComponentSql"),
                ".NET Runtime 10",      AppLang.T("About.License.ComponentRuntime"),
            };
            // 📖 خطوة 4 (زوجان لكل دورة) — خطوة 2 كانت تقرأ خارج الحدود
            for (int i = 0; i + 3 < components.Length; i += 4)
            {
                int row = i / 4;
                var right = MakeComponentCard(components[i], components[i + 1]);
                right.Dock = DockStyle.Fill; right.Margin = new Padding(2);
                grid.Controls.Add(right, 0, row);
                var left = MakeComponentCard(components[i + 2], components[i + 3]);
                left.Dock = DockStyle.Fill; left.Margin = new Padding(2);
                grid.Controls.Add(left, 1, row);
            }
            AddRow(grid, 4 * 58, 10);

            // ── 6) خلاصة GPL + الإصدار ──
            AddRow(MakeText(AppLang.T("About.License.NoGpl"), bold: true), 26, 2);
            var lblVer = new Label
            {
                AutoSize = true, ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 8.5F),
                Text = AppLang.T("About.License.Version"),
                Margin = new Padding(0), Padding = new Padding(0)
            };
            AddRow(lblVer, 22, 0);

            // عرض العمود يتبع أصل التمرير
            void SyncWidth()
            {
                int w = Math.Max(scroll.ClientSize.Width - 34, 380);
                col.Width = w;
                grid.Width = w;
            }
            scroll.Resize += (_, _) => SyncWidth();
            SyncWidth();

            scroll.Controls.Add(col);
            page.Controls.Add(scroll);
            return page;
        }

        // ── مصنع بطاقة بند EULA: رقم دائري كحلي + عنوان + نص ──
        private Control MakeEulaCard(string number, string title, string body)
        {
            var card = new Panel
            {
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(14, 10, 14, 10)
            };
            AppTheme.ApplyRoundedRegion(card, 10);
            card.Paint += (s, e) =>
                e.Graphics.DrawLine(new Pen(AppTheme.BorderColor), 0, card.Height - 1, card.Width, card.Height - 1);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = Color.Transparent, Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var num = new Panel { Width = 36, Height = 36, BackColor = Color.Transparent, Dock = DockStyle.Fill };
            num.Paint += (s, e) =>
            {
                using var brush = new SolidBrush(AppTheme.Primary);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(brush, 2, 2, 32, 32);
                TextRenderer.DrawText(e.Graphics, number,
                    new Font("Segoe UI", 12F, FontStyle.Bold),
                    new Rectangle(2, 2, 32, 32), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };

            var text = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopRight,
                ForeColor = AppTheme.TextPrimary, Font = new Font("Segoe UI", 9.25F),
                Text = title + "\r\n" + body, Padding = new Padding(6, 2, 8, 0)
            };

            layout.Controls.Add(text, 1, 0);
            layout.Controls.Add(num, 0, 0);
            card.Controls.Add(layout);
            return card;
        }

        // ── مصنع بطاقة مكوّن: اسم غامق + الرخصة بالأخضر ──
        private Control MakeComponentCard(string name, string license)
        {
            var card = new Panel
            {
                BackColor = Color.White,
                Padding = new Padding(10, 7, 10, 7)
            };
            AppTheme.ApplyRoundedRegion(card, 10);
            card.Paint += (s, e) =>
                e.Graphics.DrawRectangle(new Pen(AppTheme.BorderColor), 0, 0, card.Width - 1, card.Height - 1);

            var lblName = new Label
            {
                Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextPrimary, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Text = name
            };
            var lblLic = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.SuccessDark, Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
                Text = license
            };
            card.Controls.Add(lblLic);
            card.Controls.Add(lblName);
            return card;
        }

        private Label MakeSectionLabel(string text) => new()
        {
            AutoSize = true,
            ForeColor = AppTheme.PrimaryDark,
            Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
            Text = text,
            Margin = new Padding(0, 2, 0, 0)
        };

        private Label MakeText(string text, bool bold) => new()
        {
            AutoSize = true,
            ForeColor = bold ? AppTheme.PrimaryDark : AppTheme.TextPrimary,
            Font = new Font("Segoe UI", bold ? 9.75F : 9.25F, bold ? FontStyle.Bold : FontStyle.Regular),
            Text = text,
            Margin = new Padding(0),
            MaximumSize = new Size(600, 0)
        };

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
                    MessageBox.Show(AppLang.T("About.Msg.OpenLinkFailed", url), AppLang.T("About.Msg.OpenLinkTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            return btn;
        }
    }
}