
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
                Text = "نظام متكامل للمبيعات وإدارة المخزون\nللمتاجر والصيدليات والمطاعم والأعمال الصغيرة.\n\nتشمل الباقة:\nنقطة بيع سريعة بباركود وشاشة زبون ثانية • إدارة مخزون بالدفعات والصلاحية (FEFO)\nفواتير بيع وشراء ومرتجعات وخصومات وسداد الديون • كشوف رواتب وشفتات وإجازات\nتقارير أرباح ورسوم بيانية مع تصدير PDF / Excel • نسخ احتياطي واستعادة داخل البرنامج",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
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
        // تبويب 2: التراخيص — نص الاتفاقية المعتمد من المالك
        // ═══════════════════════════════════════════════
        private TabPage BuildLicensePage()
        {
            var page = new TabPage("التراخيص") { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };

            // 📖 RichTextBox للعرض الأمثل:
            //  • ScrollBars.Vertical: التمرير الطبيعي للنصوص الطويلة (بلا WordWrap أفقي مشوه).
            //  • RightToLeft.Yes: البنود العربية محاذاة يمين حقيقية.
            //  • ReadOnly + BorderStyle.None: «وثيقة» تُقرأ وتُنسخ — لا تُحرر.
            //  • WordWrap=true مع ScrollBars عمودية: الأسطر تلتف عند حافة العرض
            //    فلا قص أفقي ولا تمزيق كلمات.
            var doc = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = AppTheme.CardBg,
                ForeColor = AppTheme.TextPrimary,
                RightToLeft = RightToLeft.Yes,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.75F),
                DetectUrls = false,
                Padding = new Padding(6, 8, 6, 8),
                Text = LicenseText.TrimStart()
            };

            // 📖 ReadOnly=true يجعل RichTextBox يتجاهل BackColor من الـ initializer
            // ويعرض SystemColors.Control (رمادي شاحب). إعادة الضبط بعد الإنشاء
            // سلوك موثق: هنا يُحترم ويظهر خلفية الهوية البيضاء.
            doc.BackColor = AppTheme.CardBg;

            // تنظيم مسافة القراءة عن حواف التبويب
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10), BackColor = AppTheme.CardBg };
            host.Controls.Add(doc);
            page.Controls.Add(host);
            return page;
        }

        /// <summary>نص اتفاقية الترخيص — النسخة المعتمدة من المالك (بلا بند دعم، م. الشريف اسحيم، مكونات مختصرة).</summary>
        private const string LicenseText = @"
سيل (Sayl POS) — نظام نقاط البيع
اتفاقية ترخيص المستخدم النهائي — EULA (ملخص)
════════════════════════════════════════════════

© 2026 م. الشريف اسحيم — جميع الحقوق محفوظة.
«سيل» علامة تجارية لمالكها.

──────────────────────────────────────────────
١ — طبيعة الترخيص
──────────────────────────────────────────────
هذا البرنامج منتج تجاري مغلق المصدر. لا يعني
شراؤك للنسخة تملّكك للبرنامج، وإنما حصولك على
«رخصة استخدام» غير حصرية وغير قابلة للتحويل.

٢ — نطاق الاستخدام
──────────────────────────────────────────────
• يُرخَّص استخدام النظام على الجهاز المُفعَّل فقط.
• يرتبط الترخيص بمعرّف عتاد الجهاز (HWID — بصمة
  إلكترونية تُستخرج من مكونات الجهاز المادية)،
  ولا يعمل النظام على أي جهاز آخر دون ترخيص جديد.
• لإصدار ترخيص بديل (عطل / ترقية جهاز) تواصل
  مع مطور النظام.

٣ — القيود
──────────────────────────────────────────────
يُمنع صراحةً:
  • نسخ البرنامج لأغراض التوزيع أو البيع.
  • استخلاص الشيفرة المصدرية أو فك التجميع.
  • إزالة أو التحايل على آلية التفعيل أو إشعارات
    حقوق الملكية.

٤ — حماية البيانات
──────────────────────────────────────────────
جميع بياناتك تُخزَّن محليًا على جهازك — لا شيء
يُرسَل عبر الإنترنت ولا يطّلع المطور على أي بيانات
تجارية لك. النظام ينشئ نسخًا احتياطية تلقائية،
وتقع مسؤولية نسخ إضافية خارجية على المستخدم.

٥ — إخلاء المسؤولية
──────────────────────────────────────────────
• يُقدَّم النظام «كما هو» دون ضمانات صريحة أو
  ضمنية لملاءمته لغرض معين.
• المطور غير مسؤول عن أية أرباح فائتة أو فقدان
  بيانات بما لا يخالف حدود القوانين المحلية.
• حد المسؤولية لا يتجاوز قيمة الترخيص المدفوعة.

──────────────────────────────────────────────
        المكونات مفتوحة المصدر
──────────────────────────────────────────────
النظام مبنٍ باستخدام مكونات مفتوحة المصدر
بتراخيص مجانية تسمح بالاستخدام التجاري:

  • QuestPDF 2026.8.0 — توليد PDF (رخصة Community)
  • ClosedXML 0.104.2 — تصدير Excel (رخصة MIT)
  • LiveChartsCore 2.0.5 — الرسوم البيانية (MIT)
  • Entity Framework Core 10 — قاعدة البيانات (MIT)
  • Microsoft.Extensions / System.* 10 (MIT / Apache)
  • BCrypt.Net-Next 4.2.0 — تشفير كلمات المرور (Apache)
  • SQL Server Express — قاعدة بيانات المحل
    (ترخيص مايكروسوفت المجاني)
  • .NET Runtime 10 — بيئة التشغيل (مايكروسوفت)

لا يتضمن النظام أي مكوّن يفرض الكشف عن
الشيفرة المصدرية (GPL / AGPL).

الإصدار 1.0 — 2026/09
";

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
