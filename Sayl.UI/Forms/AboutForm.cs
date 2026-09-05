
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
            Text = "حول البرنامج";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 600);
            MinimumSize = new Size(460, 520);
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
                Text = "سيل — Sayl",
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
            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
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
        private TabPage BuildAboutPage()
        {
            var page = new TabPage("عن النظام") { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 10, 16, 8)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // الإصدار
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // الترخيص/التفعيل
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // الوصف
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));  // المطور
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));  // التواصل

            var lblVersion = new Label
            {
                Text = "الإصدار 1.0.0",
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
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };

            var lblDescription = new Label
            {
                Text = "نظام متكامل للمبيعات وإدارة المخزون\nللمتاجر والصيدليات والمطاعم والأعمال الصغيرة.\n\nمبني على .NET 10 و SQL Server",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(8, 6, 8, 0)
            };

            var lblCreator = new Label
            {
                Text = "By :\nم. الشريف اسحيم",
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
            socialPanel.Controls.Add(MakeSocialIcon("فيسبوك", Properties.Resources.Facebook, "https://facebook.com/yourpage"));
            socialPanel.Controls.Add(MakeSocialIcon("GitHub", Properties.Resources.github, "https://github.com/yourusername"));
            socialPanel.Controls.Add(MakeSocialIcon("الموقع", Properties.Resources.Website, "https://www.sayl.com"));
            socialHost.Controls.Add(socialPanel);

            body.Controls.Add(lblVersion, 0, 0);
            body.Controls.Add(lblLicense, 0, 1);
            body.Controls.Add(lblDescription, 0, 2);
            body.Controls.Add(lblCreator, 0, 3);
            body.Controls.Add(socialHost, 0, 4);

            page.Controls.Add(body);
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
            var page = new TabPage("التراخيص") { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };

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
                Text = "© 2026 م. الشريف اسحيم — جميع الحقوق محفوظة\r\n«سيل» علامة تجارية لمالكها",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold)
            };
            var imgLogo = Brand.MakeLogo(56, AppTheme.SidebarBg);
            imgLogo.Dock = DockStyle.Fill;
            rightsInner.Controls.Add(lblRights, 0, 0);
            rightsInner.Controls.Add(imgLogo, 1, 0);
            rightsCard.Controls.Add(rightsInner);
            AddRow(rightsCard, 96, 14);

            // ── 2) مقدمة ──
            AddRow(MakeText("هذا البرنامج منتج تجاري مغلق المصدر — شراؤك للنسخة يمنحك «رخصة استخدام» غير حصرية وغير قابلة للتحويل، وفق البنود التالية:", bold: false), 42, 6);

            // ── 3) بنود الاتفاقية الخمسة ──
            AddRow(MakeEulaCard("١", "طبيعة الترخيص",
                "منتج تجاري مغلق المصدر. لا يعني شراؤك للنسخة تملّكك للبرنامج، وإنما حصولك على «رخصة استخدام» غير حصرية وغير قابلة للتحويل."), 98);
            AddRow(MakeEulaCard("٢", "نطاق الاستخدام",
                "يُرخَّص استخدام النظام على الجهاز المُفعَّل فقط. يرتبط الترخيص بمعرّف عتاد الجهاز (HWID — بصمة إلكترونية تُستخرج من مكونات الجهاز المادية)، ولا يعمل النظام على أي جهاز آخر دون ترخيص جديد. لإصدار ترخيص بديل (عطل أو ترقية جهاز) تواصل مع مطور النظام."), 122);
            AddRow(MakeEulaCard("٣", "القيود",
                "يُمنع صراحةً: نسخ البرنامج لأغراض التوزيع أو البيع • استخلاص الشيفرة المصدرية أو فك التجميع • إزالة أو التحايل على آلية التفعيل أو إشعارات حقوق الملكية."), 98);
            AddRow(MakeEulaCard("٤", "حماية البيانات",
                "جميع بياناتك تُخزَّن محليًا على جهازك — لا شيء يُرسَل عبر الإنترنت ولا يطّلع المطور على أي بيانات تجارية لك. النظام ينشئ نسخًا احتياطية تلقائية، وتقع مسؤولية نسخ إضافية خارجية على المستخدم."), 122);
            AddRow(MakeEulaCard("٥", "إخلاء المسؤولية",
                "يُقدَّم النظام «كما هو» دون ضمانات صريحة أو ضمنية لملاءمته لغرض معين. المطور غير مسؤول عن أية أرباح فائتة أو فقدان بيانات بما لا يخالف حدود القوانين المحلية. حد المسؤولية لا يتجاوز قيمة الترخيص المدفوعة."), 122, 12);

            // ── 4) عنوان قسم المكونات ──
            AddRow(MakeSectionLabel("المكونات مفتوحة المصدر المستخدمة"), 30, 2);
            AddRow(MakeText("النظام مبنٍ باستخدام مكونات بتراخيص مجانية تسمح بالاستخدام التجاري:", bold: false), 24, 6);

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
                "QuestPDF 2026.8",      "توليد PDF — رخصة Community",
                "ClosedXML 0.104",      "تصدير Excel — رخصة MIT",
                "LiveChartsCore 2.0",   "الرسوم البيانية — MIT",
                "EF Core 10",           "قاعدة البيانات — MIT",
                "Microsoft.Extensions", "بنية التطبيق — MIT/Apache",
                "BCrypt.Net 4.2",       "تشفير كلمات المرور — Apache",
                "SQL Server Express",  "قاعدة بيانات المحل — مجاني",
                ".NET Runtime 10",      "بيئة التشغيل — مجاني",
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
            AddRow(MakeText("لا يتضمن النظام أي مكوّن يفرض الكشف عن الشيفرة المصدرية (GPL / AGPL).", bold: true), 26, 2);
            var lblVer = new Label
            {
                AutoSize = true, ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 8.5F),
                Text = "اتفاقية الإصدار 1.0 — 2026/09",
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
                    MessageBox.Show("تعذر فتح الرابط: " + url, "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            return btn;
        }
    }
}