using Microsoft.Extensions.DependencyInjection;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// 📖 شاشة الزبون الثانية (Customer Display):
    /// نافذة عرض فقط تعرض فاتورة السلة الحية على الشاشة الثانية للمتجر
    /// (إن وُجدت) — بلا أزرار ولا تشتيتات. تفتح تلقائيًا مع شاشة البيع
    /// وتغلق معها، وتتحول لشاشة ترحيب «متنفسة» عند خمول السلة.
    ///
    /// ▪ من يغذيها بالبيانات؟ PosForm عبر الحدث CartChanged (لا تقرأ
    ///   قاعدة البيانات بنفسها إطلاقًا — عنصر عرض صرف).
    /// ▪ لو لا توجد شاشة ثانية؟ لا تفتح أبدًا ولا يظهر أي أثر لها.
    /// ▪ أي فشل عرض (شاشة غريبة/دقة شاذة) = صمت تام — لا تُعطل عملية البيع.
    /// </summary>
    public class CustomerDisplayForm : Form
    {
        // ── عناصر العرض (كلها للعرض فقط — لا شيء قابل للنقر) ──
        private Label _lblStoreName = null!;        // اسم المتجر بالترويسة
        private DataGridView _grid = null!;          // بنود الفاتورة
        private Label _lblDiscount = null!;          // سطر الخصم (يظهر عند وجوده)
        private Label _lblTax = null!;               // سطر الضريبة (يظهر عند وجوده)
        private Label _lblTotal = null!;              // الإجمالي — أكبر عنصر بالشاشة
        private Label _lblUnits = null!;              // "عدد القطع: N" صغير
        private Label _lblPayHint = null!;             // "يرجى تجهيز..." عند الدفع
        private Label _lblFooter = null!;              // رسالة المتجر (من الإعدادات)

        // ── طبقة الترحيب (تظهر مكان كل شيء عند الخمول) ──
        private Panel _welcomeLayer = null!;
        private Label _lblWelcomeTitle = null!;
        private Label _lblWelcomeSub = null!;

        // ── محرك الخمول والطفو ──
        private System.Windows.Forms.Timer? _idleTimer;    // يراقب صمت السلة
        private System.Windows.Forms.Timer? _floatTimer;   // حركة «التنفس» للترحيب
        private DateTime _lastCartChange = DateTime.UtcNow;
        private readonly int _idleSeconds;                   // مهلة الخمول (قابلة للضبط)
        private float _floatPhase;                            // طور الموجة الجيبية

        // لقطة الفاتورة الحالية (تصل من PosForm)
        private record CartSnapshot(
            IReadOnlyList<(string Name, int Qty, decimal Price)> Items,
            decimal Discount, decimal Tax, decimal Total, bool CheckoutRequested);

        private CartSnapshot _cart = new(new List<(string, int, decimal)>(), 0, 0, 0, false);

        // ═══════════════════════════════════════════════
        // 📖 الالتقاط التلقائي للشاشة الثانية:
        // Screen.AllScreens = كل شاشات الجهاز. الأولى (رقم 0) عليها شاشة
        /// الكاشير دائمًا — لذلك نبحث عن أي شاشة غيرها (رقم 1 فأعلى).
        // لو لا توجد: المنتِج (TryCreate) يرجع null والفتح لا يحدث أصلًا.
        // ═══════════════════════════════════════════════
        public static CustomerDisplayForm? TryCreate()
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length < 2)
                return null; // شاشة واحدة — الميزة صامتة تمامًا

            return new CustomerDisplayForm(screens[1]);
        }

        private CustomerDisplayForm(Screen targetScreen)
        {
            // مهلة الخمول: 30 ثانية من آخر تغيير بالسلة (وليس من تفاعل الكاشير —
            // كاشير يجهز فاتورة كبيرة بهدوء لا يجب أن تُقاطَع شاشته بالترحيب)
            _idleSeconds = 30;

            // ── قواعد نافذة العرض ──
            // FormBorderStyle.None: بلا إطار ولا شريط عنوان — زجاج عرض صرف.
            // ShowInTaskbar=false: لا تظهر بشريط مهام الكاشير.
            // TopMost=true: تبقى فوق أي شيء يفتح على شاشة الزبون.
            // StartPosition=Manual + Bounds: نضعها بإحداثيات الشاشة الثانية نفسها.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = targetScreen.Bounds;      // ← ملء كامل لمساحة الشاشة الثانية
            ShowInTaskbar = false;
            TopMost = true;
            RightToLeft = RightToLeft.Yes;
            BackColor = Color.FromArgb(11, 27, 48);   // كحلي داكن (فوق SidebarBg)

            BuildUi();
            Load += (_, _) => StartTimers();
            FormClosing += (_, _) => StopTimers();

            // لو فُصل كابل الشاشة الثانية أثناء العمل: تعود النافذة بهدوء
            // للشاشة الأساسية بدل أن تضيع في الفضاء (حدث نظام العرض).
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            FormClosing += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        // ───────────────── بناء الواجهة ─────────────────
        // 📖 بنية الشاشة (جذر مجزّأ عمودي — نفس عائلة PayrollForm لكن داكنة):
        //   ┌ الترويسة: اسم المتجر (سطر واحد)              ┐ 60px
        //   ├ الجدول: البنود (يتمدد — هو المتن البصري)       │ 100%
        //   ├ لوحة الأرقام: خصم/ضريبة/الإجمالي/القطع         │ 170px
        //   └ شريط التذييل: رسالة المتجر من الإعدادات       ┘ 46px
        // فوق الكل: طبقة الترحيب (Panel عتامة كاملة تغطي الشاشة عند الخمول).
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(28, 20, 28, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));    // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // الجدول
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));    // الأرقام
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));     // التذييل

            // ── الترويسة: اسم المتجر (يُقرأ من الإعدادات مع fallback «سيل») ──
            _lblStoreName = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Segoe UI", 22F, FontStyle.Bold)
            };
            root.Controls.Add(_lblStoreName, 0, 0);

            // ── جدول البنود (نسخة داكنة — عناصر عرض لا تفاعل) ──
            // ⚠️ لا نستخدم StyleGrid القياسي (فاتح) — هذه شاشة زبون داكنة.
            // ReadOnly دائمًا، بلا تحديد صف، ارتفاع أسطر مريح للقراءة عن بُعد.
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(11, 27, 48),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 52 },
                GridColor = Color.FromArgb(30, 58, 95)
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0, 43, 84);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(191, 219, 254);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 8, 10, 8);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 43, 84);
            _grid.DefaultCellStyle.BackColor = Color.FromArgb(11, 27, 48);
            _grid.DefaultCellStyle.ForeColor = Color.White;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 43, 84);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 14F);
            _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.DefaultCellStyle.Padding = new Padding(10, 4, 10, 4);

            _grid.Columns.Add("colName", AppLang.T("Display.Label.Item"));
            _grid.Columns["colName"].FillWeight = 50;
            _grid.Columns.Add("colQty", AppLang.T("Display.Label.Qty"));
            _grid.Columns["colQty"].FillWeight = 16;
            _grid.Columns["colQty"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add("colPrice", AppLang.T("Display.Label.Price"));
            _grid.Columns["colPrice"].FillWeight = 17;
            _grid.Columns.Add("colTotal", AppLang.T("Display.Label.Total"));
            _grid.Columns["colTotal"].FillWeight = 17;
            root.Controls.Add(_grid, 0, 1);

            // ── لوحة الأرقام (خصم/ضريبة/الإجمالي/القطع/تلميح الدفع) ──
            // الإجمالي هو «ملك الشاشة»: أكبر خط + لون برتقالي الهوية.
            var totalsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                BackColor = Color.FromArgb(0, 43, 84),
                Padding = new Padding(20, 14, 20, 10)
            };
            totalsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            totalsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            // التسمية يمين والقيمة يسار — تراص أنيق للقراءة عن بُعد.
            void AddMoneyRow(Label lbl, Label val, int row, Font f, Color c)
            {
                lbl.Dock = DockStyle.Fill;
                lbl.TextAlign = ContentAlignment.MiddleRight;
                lbl.ForeColor = Color.FromArgb(148, 163, 184);
                lbl.Font = new Font("Segoe UI", 13F);
                val.Dock = DockStyle.Fill;
                val.TextAlign = ContentAlignment.MiddleLeft;
                val.ForeColor = c;
                val.Font = f;
                totalsPanel.Controls.Add(lbl, 0, row);
                totalsPanel.Controls.Add(val, 1, row);
                totalsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            }

            _lblDiscount = new Label();
            _lblTax = new Label();
            _lblTotal = new Label();
            _lblUnits = new Label();
            _lblPayHint = new Label();

            AddMoneyRow(new Label { Text = AppLang.T("Display.Label.Discount") }, _lblDiscount, 0,
                new Font("Segoe UI", 14F, FontStyle.Bold), Color.FromArgb(252, 165, 165));
            AddMoneyRow(new Label { Text = AppLang.T("Display.Label.Tax") }, _lblTax, 1,
                new Font("Segoe UI", 14F, FontStyle.Bold), Color.FromArgb(147, 197, 253));
            AddMoneyRow(new Label { Text = AppLang.T("Display.Label.Total") }, _lblTotal, 2,
                new Font("Segoe UI", 30F, FontStyle.Bold), AppTheme.Accent);
            AddMoneyRow(new Label { Text = AppLang.T("Display.Label.Units") }, _lblUnits, 3,
                new Font("Segoe UI", 13F), Color.FromArgb(203, 213, 225));

            // تلميح الدفع: يظهر تحت الأرقام عند طلب POS (يرجى تجهيز المبلغ)
            // — سطر صامت ثابت بلا وميض (قرار تصميمي ضد الوميض المزعج).
            _lblPayHint.Dock = DockStyle.Fill;
            _lblPayHint.TextAlign = ContentAlignment.MiddleRight;
            _lblPayHint.ForeColor = Color.FromArgb(74, 222, 128);
            _lblPayHint.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            totalsPanel.SetColumnSpan(_lblPayHint, 2);
            totalsPanel.Controls.Add(_lblPayHint, 0, 4);
            totalsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            root.Controls.Add(totalsPanel, 0, 2);

            // ── التذييل: رسالة المتجر (مفتاح WelcomeMessage بالإعدادات) ──
            _lblFooter = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("Segoe UI", 11F)
            };
            root.Controls.Add(_lblFooter, 0, 3);

            // ── طبقة الترحيب (تغطي الشاشة عند الخمول — أسفل الحديث) ──
            // Panel بلون الخلفية نفسه يظهر/يخفي — بدل إنشاء/هدم عناصر.
            _welcomeLayer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(11, 27, 48),
                Visible = true   // تبدأ الشاشة بالترحيب — أول انطباع متعمد
            };

            _lblWelcomeTitle = new Label
            {
                Text = AppLang.T("Display.Welcome"),
                Dock = DockStyle.Top,
                Height = 150,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 44F, FontStyle.Bold)
            };
            _lblWelcomeSub = new Label
            {
                Text = AppLang.T("Display.WelcomeSub"),
                Dock = DockStyle.Top,
                Height = 90,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 16F)
            };

            _welcomeLayer.Controls.Add(_lblWelcomeSub);
            _welcomeLayer.Controls.Add(_lblWelcomeTitle);

            Controls.Add(root);
            Controls.Add(_welcomeLayer);  // آخر Add = يظهر فوق الجذر

            // رسم شعار متجر بسيط فوق الترحيب (لو توفر BrandLogo)
            _welcomeLayer.Paint += (_, e) =>
            {
                try
                {
                    using var logo = Brand.MakeLogo(90, Color.FromArgb(11, 27, 48));
                    logo.Location = new Point((_welcomeLayer.Width - 90) / 2, _welcomeLayer.Height / 2 - 220);
                    _welcomeLayer.Controls.Add(logo);
                }
                catch { /* الشعار تحسين بصري — فشله لا يعطل الترحيب */ }
            };
        }

        // ───────────────── المؤقتات ─────────────────
        private void StartTimers()
        {
            // idleTimer: كل ثانية يفحص — هل هدأت السلة مدة كافية؟
            _idleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _idleTimer.Tick += (_, _) =>
            {
                if (_cart.Items.Count == 0)
                    ShowWelcome();
            };
            _idleTimer.Start();

            // floatTimer: 30 إطارًا/ث — «تنفس» الترحيب (طفو جيبي ناعم 3px)
            _floatTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _floatTimer.Tick += (_, _) =>
            {
                if (!_welcomeLayer.Visible) return;
                _floatPhase += 0.055f;
                float dy = (float)(Math.Sin(_floatPhase) * 3.0);
                _lblWelcomeTitle.Top = _welcomeLayer.Height / 2 - 190 + (int)dy;
                _lblWelcomeSub.Top = _lblWelcomeTitle.Bottom + 8 + (int)(dy * 0.6f);
            };
            _floatTimer.Start();

            // اقرأ اسم المتجر ورسالة الترحيب من الإعدادات (مع قيم سليمة عند الغياب)
            _ = LoadSettingsAsync();
        }

        private void StopTimers()
        {
            _idleTimer?.Stop();
            _idleTimer?.Dispose();
            _floatTimer?.Stop();
            _floatTimer?.Dispose();
        }

        // ───────────────── البيانات الحية (يستدعيها PosForm) ─────────────────
        // 📖 هذه نقطة الاتصال الوحيدة من شاشة البيع:
        // PosForm ترسل لقطة السلة عند كل تغيير (RefreshCart → OnCartChanged).
        // لا استعلامات قاعدة بيانات هنا — عنصر عرض صرف.
        /// <summary>تحديث لقطة السلة — أول بند جديد يخفي الترحيب فورًا.</summary>
        public void UpdateCart(IReadOnlyList<(string Name, int Qty, decimal Price)> items,
            decimal discount, decimal tax, decimal total, bool checkoutRequested)
        {
            if (InvokeRequired)
            {
                // نداء من خيط غير الواجهة (احتياط أمان) — نعيد لأنفسنا بأمان
                BeginInvoke(() => UpdateCart(items, discount, tax, total, checkoutRequested));
                return;
            }

            _cart = new CartSnapshot(items, discount, tax, total, checkoutRequested);
            _lastCartChange = DateTime.UtcNow;

            // أول بند = إنهاء الترحيب فورًا (لا انتظار — استجابة الزبون مقدسة)
            if (items.Count > 0 && _welcomeLayer.Visible)
                _welcomeLayer.Visible = false;

            RenderCart();
        }

        /// <summary>إفراغ العرض بعد إتمام البيع — يعيد الترحيب (لحظة الشكر تسبقه داخل POS).</summary>
        public void Clear()
        {
            if (InvokeRequired)
            {
                BeginInvoke(Clear);
                return;
            }

            _cart = new CartSnapshot(new List<(string, int, decimal)>(), 0, 0, 0, false);
            RenderCart();
            ShowWelcome();
        }

        // ── رسم اللقطة على العناصر (كل تحديث = رسم كامل — أرخص وأصدق من diff) ──
        private void RenderCart()
        {
            _grid.Rows.Clear();
            foreach (var it in _cart.Items)
                _grid.Rows.Add(it.Name, it.Qty.ToString(), it.Price.ToString("N2"),
                    (it.Price * it.Qty).ToString("N2"));

            // الحقول الصفرية تختبئ (لا «خصم 0.00» — مبدأ الإيصالات نفسه)
            _lblDiscount.Text = _cart.Discount > 0 ? "-" + _cart.Discount.ToString("N2") : "";
            _lblTax.Text = _cart.Tax > 0 ? _cart.Tax.ToString("N2") : "";
            _lblTotal.Text = _cart.Total.ToString("N2");
            _lblUnits.Text = _cart.Items.Sum(i => i.Qty).ToString();
            _lblPayHint.Text = _cart.CheckoutRequested && _cart.Total > 0
                ? AppLang.T("Display.PayHint", _cart.Total)
                : "";

            if (_cart.Items.Count == 0)
                ShowWelcome();
        }

        // ── إظهار/إخفاء الترحيب (تحكم مركزي واحد) ──
        private void ShowWelcome()
        {
            if (!_welcomeLayer.Visible)
                _welcomeLayer.Visible = true;
            _lblPayHint.Text = ""; // لا تلميح دفع فوق الترحيب
        }

        // ───────────────── الإعدادات والشاشات ─────────────────
        private async Task LoadSettingsAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<Sayl.Core.Services.clsSettingsService>();

                string storeName = await settings.GetAsync(Sayl.Core.Services.clsSettingsService.KeyStoreName, AppLang.T("App.Store.Default"));
                string footer = await settings.GetAsync(
                    Sayl.Core.Services.clsSettingsService.KeyWelcomeMessage, AppLang.T("Settings.Default.Welcome"));

                _lblStoreName.Text = storeName;
                _lblWelcomeTitle.Text = AppLang.T("Display.WelcomeIn", storeName);
                _lblFooter.Text = footer;
            }
            catch
            {
                // الإعدادات غير متاحة؟ قيم افتراضية محترمة — العرض لا يتوقف
                _lblStoreName.Text = AppLang.T("App.Store.Default");
                _lblWelcomeTitle.Text = AppLang.T("Display.Welcome");
                _lblFooter.Text = AppLang.T("Settings.Default.Welcome");
            }
        }

        private void OnDisplayChanged(object? sender, EventArgs e)
        {
            // فُصلت الشاشة الثانية؟ عُد بهدوء إلى الشاشة الأساسية
            BeginInvoke(() =>
            {
                if (Screen.AllScreens.Length < 2)
                {
                    if (Screen.FromControl(this).Primary) return;
                    Location = Screen.PrimaryScreen!.Bounds.Location;
                }
            });
        }
    }
}
