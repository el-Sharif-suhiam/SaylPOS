using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// واجهة الكاشير: بحث حي أثناء الكتابة، سلة بيع في المنتصف، وأزرار مجمعة بلمسات كبيرة.
    /// </summary>
    public class PosForm : Form
    {
        // ═══════════════════════════════════════════════
        // 📖 نماذج البيانات الصغيرة للشاشة (nested classes):
        //  • CartItem: بند السلة في الذاكرة فقط — Total خاصية محسوبة
        //    (Price × Quantity) تتحدث تلقائيًا، لا حقل يُنسى تحديثه.
        //  • SearchItem: منتج + رصيده الحالي — القائمة المنسدلة تعرض
        //    الرصيد بجانب الاسم (لماذا القائمة؟ الكاشير يقرأ «متوفر 3»
        //    قبل الإضافة لا بعدها — قرار UX بلا رسائل مزعجة).
        // قاعدة عامة: نماذج العرض الصغيرة تعيش داخل الشاشة التي تخدمها
        // ولا تتسرب للطبقات الأدنى — العزل يبقي PosForm مفهومًا كوحدة.
        // ═══════════════════════════════════════════════
        private sealed class CartItem
        {
            public int ProductId { get; init; }
            public string ProductName { get; init; } = "";
            public decimal Price { get; init; }
            public int Quantity { get; set; }
            public decimal Total => Price * Quantity;
        }

        private sealed class SearchItem
        {
            public DtoProduct Product { get; init; } = null!;
            public int Stock { get; init; }
        }

        private readonly List<CartItem> _cart = new();
        private Dictionary<int, int> _stockByProduct = new();
        private int? _sessionId;

        // شريط العلوي
        private TextBox _txtSearch = null!;
        private Label _lblSession = null!;

        // قائمة البحث الحية
        private Panel _searchPopup = null!;
        private ListBox _lstResults = null!;
        private System.Windows.Forms.Timer _searchTimer = null!;
        private int _searchSeq;

        // منطقة السلة
        private Label _lblItemsBadge = null!;
        private Label _lblStockWarn = null!;
        private DataGridView _gridCart = null!;
        private System.Windows.Forms.Timer _warnTimer = null!;

        // لوحة الأوامر
        private Label _lblCustomer = null!;
        private Label _lblSubtotal = null!;
        private Label _lblNet = null!;
        private TextBox _txtDiscount = null!;
        private TextBox _txtTax = null!;
        private Button _btnPay = null!;
        private Button _btnSuspend = null!;

        private int? _customerId;
        private string? _customerName;

        public PosForm()
        {
            BuildUi();
            Load += async (_, _) =>
            {
                await OpenSessionAsync();
                await RefreshStockAsync();
                OpenCustomerDisplay(); // 📖 شاشة الزبون الثانية (إن وُجدت شاشة ثانية)
            };
            FormClosing += (_, _) => CloseCustomerDisplay();
        }

        // ═══════════════════════════════════════════════
        // 📖 شاشة الزبون الثانية (Customer Display) — دورة حياتها هنا:
        //
        //  1. OpenCustomerDisplay (عند فتح POS):
        //     TryCreate يفحص Screen.AllScreens — شاشة واحدة = لا شيء يفتح
        //     (صمت تام بلا أخطاء). شاشة ثانية = النافذة تفتح عليها بحدودها.
        //
        //  2. التغذية الحية:
        //     كل RefreshCart (إضافة/حذف/كمية/تعليق/مسح) يمر بـ
        //     NotifyCustomerDisplay فيصل الجديد للزبون لحظيًا.
        //
        //  3. ClearCustomerDisplay:
        //     بعد إتمام البيع نعيد شاشة الزبون للترحيب (لحظة الشكر
        //     ثم شاشة نظيفة للزبون التالي).
        //
        //  4. CloseCustomerDisplay (عند إغلاق POS): الشاشة تموت مع أمها.
        //
        //  أي فشل بالشاشة الثانية لا يمس البيع — try/catch حول كل لمسة.
        // ═══════════════════════════════════════════════
        private CustomerDisplayForm? _customerDisplay;

        private void OpenCustomerDisplay()
        {
            try
            {
                _customerDisplay = CustomerDisplayForm.TryCreate();
                _customerDisplay?.Show(this); // Show لا ShowDialog — عرض مستمر بالخلفية
            }
            catch
            {
                _customerDisplay = null; // فشل فتح الشاشة الثانية لا يعطل البيع أبدًا
            }
        }

        /// <summary>إيصال لقطة السلة الحالية لشاشة الزبون (يستدعى من RefreshCart).</summary>
        private void NotifyCustomerDisplay(bool checkoutRequested = false)
        {
            if (_customerDisplay == null || _customerDisplay.IsDisposed) return;
            try
            {
                _customerDisplay.UpdateCart(
                    _cart.Select(i => (i.ProductName, i.Quantity, i.Price)).ToList(),
                    DiscountAmountForDisplay,
                    TaxAmountForDisplay,
                    NetTotal,
                    checkoutRequested);
            }
            catch
            {
                // عرض الزبون تحسين — أي خلل فيه يُهمل بصمت
            }
        }

        private void CloseCustomerDisplay()
        {
            try
            {
                _customerDisplay?.Close();
                _customerDisplay?.Dispose();
            }
            catch { /* هدوء */ }
            _customerDisplay = null;
        }

        // ═══════════════════════════════════════════════
        // 📖 اختصارات الكاشير (Function Keys) — تُلتقط هنا مركزيًا
        // عبر ProcessCmdKey حتى تعمل ولو كان التركيز داخل مربع البحث.
        // المفاتيح المعتمدة (تظهر على الأزرار أيضًا):
        //   F2 = دفع الآن   | F3 = تعليق   | F4 = اختيار عميل
        //   F9 = آخر فاتورة | F12 = مرتجع
        // لإضافة اختصار: أضف case هنا + أظهر المفتاح في نص الزر المعني.
        // ملاحظة الأمان: لا يوجد تعارض مع Enter (بحث/إضافة) وEsc (إغلاق القوائم)
        // لأن مفاتيح F لا تُستهلك بأي معالج آخر في هذه الشاشة.
        // ═══════════════════════════════════════════════
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F2:
                    _ = CheckoutAsync(suspend: false);
                    return true;

                case Keys.F3:
                    _ = CheckoutAsync(suspend: true);
                    return true;

                case Keys.F4:
                    PickCustomer();
                    return true;

                case Keys.F9:
                    using (var form = new LastInvoiceForm(SessionState.CurrentUser?.UserId ?? 0))
                        form.ShowDialog(this);
                    _txtSearch.Focus(); // الماسح جاهز بعد إغلاق النافذة
                    return true;

                case Keys.F12:
                    ShowReturnForm();
                    return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ═══════════════════════════════════════════════
        // بناء الواجهة
        // 📖 شاشة POS تبنى بنمط «الإرساء الثلاثي»:
        //    ┌───────────┬──────────────────┬──────────┐
        //    │ (Top)     │ شريط العلوي       │          │
        //    ├───────────┴──────────────────┤ الأزرار   │
        //    │                            │ (Right   │
        //    │   سلة الفاتورة (Fill)        │  388px)  │
        //    │                            │          │
        //    └────────────────────────────┴──────────┘
        //  قاعدة الإرساء في WinForms: **آخر ما يُضاف يُرسّى أولًا**
        //  (يأخذ حافته قبل غيره) — لذلك ترتيب الأسطر الأربعة أدناه
        //  مقصود وعكسي منطقيًا: نضيف Fill أولًا حتى يُرسى أخيرًا
        //  ويملأ فقط ما تبقى بعد Top وRight.
        //  لتغيير عرض لوحة الأزرار: Width=388 داخل BuildActionPanel.
        //  لتغيير ارتفاع الشريط العلوي: Height=74 داخل BuildTopBar.
        // ═══════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "سيل — نقاط البيع";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1024, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Icon = Brand.AppIcon;

            BuildCartArea();     // Fill (يُرسّى أخيرًا)
            BuildActionPanel();  // Dock Right
            BuildTopBar();       // Dock Top (يمتد بكامل العرض)
            BuildSearchPopup();
        }

        // ───────────────── الشريط العلوي ─────────────────
        // 📖 الشريط الكحلي العلوي: الشعار + اسم النظام + مربع البحث
        // (يستقبله الكتابة اليدوية والماسح الضوئي معًا) + حالة الجلسة + خروج.
        // داخل اللوحة: عناصر بإرساء Left/Right/Fill — في RTL:
        // ترتيب Add يحدد التراصف من الحواف نحو الداخل (اقرأ بترتيب الأسطر).
        private void BuildTopBar()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = AppTheme.SidebarBg, Padding = new Padding(14, 12, 14, 12) };

            _lblSession = new Label
            {
                Text = "جارٍ فتح الجلسة...",
                Dock = DockStyle.Left,
                Width = 170,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 9.5F)
            };

            var _logoMark = Brand.MakeLogo(46, AppTheme.SidebarBg);
            _logoMark.Dock = DockStyle.Left;
            _logoMark.Margin = new Padding(0);
            _logoMark.Padding = new Padding(2);

            var lblBrand = new Label
            {
                Text = "سيل | نقاط البيع",
                Dock = DockStyle.Left,
                Width = 165,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold)
            };

            var searchHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 0, 24, 0) };
            _txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 14F),
                BackColor = AppTheme.SidebarHover,
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            // 📖 مربع البحث — قلب شاشة الكاشير:
            //  • الكتابة اليدوية: TextChanged → بحث حي بعد 250ms (debounce
            //    داخل _searchTimer) يعرض قائمة النتائج المنسدلة.
            //  • الماسح الضوئي/QR: يكتب بسرعة ثم Enter → SearchCommitAsync
            //    يضيف المنتج المطابق فورًا للسلة.
            //  • PlaceholderText: النص الإرشادي الرمادي — غيّره بحرية.
            //  • Font بحجم 14: مقصود كبيرًا لقراءة سريعة خلف الكاشير.
            _txtSearch.PlaceholderText = " اكتب اسم المنتج للبحث الفوري أو امسح الباركود ثم Enter ";
            _txtSearch.TextChanged += (_, _) => OnSearchTextChanged();
            _txtSearch.KeyDown += Search_KeyDown;
            searchHost.Controls.Add(_txtSearch);

            var btnLogout = AppTheme.MakeButton("خروج", AppTheme.SidebarHover,
                foreColor: Color.FromArgb(252, 165, 165), hoverColor: AppTheme.PrimaryDark, height: 50);
            btnLogout.Dock = DockStyle.Left;
            btnLogout.Width = 92;
            btnLogout.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnLogout.Click += async (_, _) => await LogoutAsync();

            // ترتيب الإرساء المعكوس: الأخير يُرسّى أولًا
            top.Controls.Add(_txtSearch.Parent!);
            top.Controls.Add(_lblSession);
            top.Controls.Add(lblBrand);
            top.Controls.Add(_logoMark);
            top.Controls.Add(btnLogout);

            Controls.Add(top);
        }

        // ───────────────── قائمة نتائج البحث الحية ─────────────────
        // ═══════════════════════════════════════════════
        // 📖 BuildSearchPopup: «القائمة المنسدلة المصنوعة يدويًا» — Panel
        // عائم فوق كل شيء (BringToFront) يظهر/يختفي حسب نتائج البحث:
        //
        //  • ليس ComboBox: نحتاج بطاقة نتائج غنية (اسم + سعر + رصيد
        //    ملوّن حسب الحالة) — فنرسمها بأنفسنا (OwnerDrawFixed).
        //  • الموقع: RepositionSearchPopup يحسب إحداثيات شاشة مربع البحث
        //    (PointToScreen) ثم يعيدها لنظام النافذة (PointToClient) —
        //    القائمة «تلتصق» بالمربع حتى لو تحركت النافذة.
        //  • الارتفاع ديناميكي: صف × 54px بحد أقصى 380 (ShowSearchPopup).
        //  • مؤقتان حيويان هنا:
        //    _searchTimer (250ms) = debounce: الكتابة السريعة لا ترمي
        //      استعلامًا لكل حرف — استعلام واحد بعد توقف القلم.
        //    _warnTimer (4s) = تلاشي تلقائي لتحذيرات المخزون — الرسالة
        //      الحمراء تموت وحدها (لا نقر إغلاق إضافي على الكاشير).
        //  • Resize يخفي القائمة: نافذة كشير تتحجم كثيرًا — القائمة
        //    المعرفة على موضع قديم تبدو معلقة؛ الإخفاء أرخص من مطاردتها.
        // ═══════════════════════════════════════════════
        private void BuildSearchPopup()
        {
            _searchPopup = new Panel
            {
                Visible = false,
                Width = 600,
                Height = 320,
                BackColor = AppTheme.CardBg,
                BorderStyle = BorderStyle.FixedSingle
            };

            _lstResults = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 54,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 11F),
                IntegralHeight = false
            };
            _lstResults.DrawItem += LstResults_DrawItem;
            _lstResults.Click += (_, _) => AddSelectedResult();
            _lstResults.KeyDown += LstResults_KeyDown;
            _searchPopup.Controls.Add(_lstResults);

            Controls.Add(_searchPopup);
            _searchPopup.BringToFront();

            _searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer.Stop();
                await RunLiveSearchAsync();
            };

            _warnTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _warnTimer.Tick += (_, _) =>
            {
                _lblStockWarn.Text = "";
                _warnTimer.Stop();
            };

            Resize += (_, _) => { if (_searchPopup.Visible) HideSearchPopup(); };
        }

        // 📖 LstResults_DrawItem: الرسم اليدوي لبطاقة نتيجة (OwnerDraw):
        // DrawBackground ثم نرسم بأنفسنا مستطيل الخلفية (محدد=PrimaryLight)
        // وسطرين: الاسم (غامق 11.5) ثم «السعر • الرصيد» (9pt).
        // الرصيد ملوّن دلاليًا: رمادي لغير المخزني، أحمر=صفر، برتقالي≤10،
        // أخضر غير ذلك — عين الكاشير تمسك الحالة قبل قراءة الرقم.
        // StringFlags.DirectionRightToLeft: النص يرتب يمينًا داخل المستطيل.
        private void LstResults_DrawItem(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _lstResults.Items.Count)
                return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using var bgBrush = new SolidBrush(selected ? AppTheme.PrimaryLight : AppTheme.CardBg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            var item = (SearchItem)_lstResults.Items[e.Index]!;

            using var nameBrush = new SolidBrush(AppTheme.TextPrimary);
            using var subBrush = new SolidBrush(selected ? AppTheme.PrimaryDark : AppTheme.TextMuted);
            using var nameFont = new Font("Segoe UI", 11.5F, FontStyle.Bold);
            using var subFont = new Font("Segoe UI", 9F);

            var format = new StringFormat(StringFormatFlags.DirectionRightToLeft);
            var rect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 5, e.Bounds.Width - 24, e.Bounds.Height - 10);

            string stockText = item.Product.Type == "stock"
                ? $"متوفر: {item.Stock}"
                : "غير مخزني";
            var stockBrush = new SolidBrush(
                item.Product.Type != "stock" ? AppTheme.TextMuted :
                item.Stock <= 0 ? AppTheme.Danger :
                item.Stock <= 10 ? AppTheme.Warning : AppTheme.Success);

            e.Graphics.DrawString(item.Product.Name, nameFont, nameBrush,
                new Rectangle(rect.X, rect.Y, rect.Width - 150, 26), format);
            e.Graphics.DrawString($"{UiHelpers.Money(item.Product.BasePrice)}  •  {stockText}", subFont, stockBrush,
                new Rectangle(rect.X, rect.Y + 27, rect.Width - 24, 20), format);
        }

        // 📖 RepositionSearchPopup/Show/Hide: دورة حياة القائمة المنسدلة —
        // Show: موضع + ارتفاع ديناميكي (صفوف × 54 بحد 380) + BringToFront.
        // Hide: إخفاء + إعادة التركيز لمربع البحث (الماسح أولوية دائمًا).
        private void RepositionSearchPopup()
        {
            var host = _txtSearch.Parent!;
            var screenPoint = host.PointToScreen(new Point(host.Width - _searchPopup.Width - 4, host.Bottom + 6));
            _searchPopup.Location = PointToClient(screenPoint);
            _searchPopup.BringToFront();
        }

        private void ShowSearchPopup(int resultCount)
        {
            RepositionSearchPopup();
            _searchPopup.Height = Math.Min(resultCount * _lstResults.ItemHeight + 8, 380);
            _searchPopup.Visible = true;
            _searchPopup.BringToFront();
        }

        private void HideSearchPopup()
        {
            _searchPopup.Visible = false;
            ActiveControl = _txtSearch;
        }

        // ───────────────── تنظيف مدخلات الماسح الضوئي ─────────────────
        /// <summary>
        /// يزيل ترويسات معرّفات الرموز التي ترسلها قارئات 2D (QR/DataMatrix/GS1)
        /// مثل ]Q3 و ]C1 و ]d2، إضافة إلى أسطر الإرجاع الزائدة — حتى تطابق
        /// قيمة الباركود المخزنة في قاعدة البيانات مطابقة تامة.
        /// </summary>
        private static string CleanScannerInput(string raw)
        {
            string text = (raw ?? "").Trim().TrimEnd('\r', '\n').Trim();

            // ترويسة معرّف الرمز: ']' + حرف معرّف + محرف فك الترميز (AIM symbology identifier)
            while (text.Length > 3 && text[0] == ']' && char.IsLetter(text[1]) && char.IsLetterOrDigit(text[2]))
                text = text[3..].Trim();

            return text;
        }

        /// <summary>
        /// هل النمط يشبه إدخال ماسح ضوئي (وليس بحث اسم يدوي)؟
        /// أرقام فقط (باركود EAN) أو SKU حرفي-رقمي متصل (SAYL-0123 / P00123) —
        /// النص العربي المفصول بمسافات لا يدخل النمط أبدًا فلا تُبتلع كتابة الكاشير.
        /// </summary>
        private static bool LooksLikeScannerToken(string text) =>
            text.Length >= 4 && text.Length <= 30 && !text.Contains(' ')
            && text.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetter(c) || c == '-' || c == '.');

        /// <summary>تنبيه مسح غير معروف: رسالة حمراء مؤقتة + صوت تحذير، دون أي نافذة حوارية تعيق المسح.</summary>
        private void ShowScanNotFound(string scanned)
        {
            ShowStockWarn($"المنتج غير موجود: {scanned}");
            System.Media.SystemSounds.Exclamation.Play();
        }

        private void OnSearchTextChanged()
        {
            _searchTimer.Stop();

            if (string.IsNullOrWhiteSpace(_txtSearch.Text))
            {
                HideSearchPopup();
                return;
            }

            _searchTimer.Start();
        }

        // ═══════════════════════════════════════════════
        // 📖 منطق مسارات البحث الثلاثة — خريطة القرار في RunLiveSearchAsync:
        //
        //  1) LooksLikeScannerToken(term) صادف المنتج → أضف فورًا وأنهِ.
        //  2) رمز نمطي لا يعرف منتجًا → ShowScanNotFound (رسالة حمراء
        //     مؤقتة + صوت) — لا صمت أبدًا: كاشير القارئ 2D يعتمد على
        //     الإشارة السلبية مثل الإيجابية.
        //  3) نص حر → 8 نتائج كحد أقصى + تحديد أول نتيجة جاهزًا لـ Enter.
        //
        // ⚠️ _searchSeq (عداد التسلسل): حارس السباق — لو بدأ الكاشير بحثًا
        //    ثم عدّل النص قبل عودة النتيجة الأولى، النتيجة القديمة
        //    تُهمل (seq != _searchSeq) فلا «يرتد» البحث لاحقًا ويكتب فوق
        //    نتائج أحدث. كل مسار async يعود للواجهة عبر هذا الحارس.
        // ═══════════════════════════════════════════════
        private async Task RunLiveSearchAsync()
        {
            string term = CleanScannerInput(_txtSearch.Text);
            if (term.Length == 0)
            {
                HideSearchPopup();
                return;
            }

            if (term != (_txtSearch.Text?.Trim() ?? ""))
                _txtSearch.Text = term; // إظهار النص المنظف (بلا ترويسة الرمز) للكاشير

            int seq = ++_searchSeq;
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();

                // مسار الماسح الضوئي (باركود/QR بدون Enter): إضافة فورية عند المطابقة التامة،
                // وتنبيه واضح عند عدم معرفة الرمز — لا صمت (كاشير القارئ 2D يعتمد عليه)
                if (LooksLikeScannerToken(term))
                {
                    var byBarcode = await productService.GetProductByBarcodeAsync(term);
                    if (seq != _searchSeq) return;
                    if (byBarcode.IsSuccess && byBarcode.Data != null)
                    {
                        AddToCart(byBarcode.Data);
                        FinishSearchInput();
                        return;
                    }

                    // رمز نمطي-مسح لا يعرف منتجًا: ليس بحث اسم — ننبه و ننهي الإدخال
                    ShowScanNotFound(term);
                    FinishSearchInput();
                    return;
                }

                // VULN-38: الحد يُدفع إلى SQL (take: 8) — كان الكتالوج
                // يُنقل كاملًا عبر السلك ثم يُقص في الذاكرة كل ضغطة بحث حية.
                var matches = (await productService.GetProductsListAsync(term, take: 8))
                    .Select(p => new SearchItem
                    {
                        Product = p,
                        Stock = _stockByProduct.TryGetValue(p.ProductId, out int st) ? st : 0
                    })
                    .ToList();

                if (seq != _searchSeq) return;

                _lstResults.BeginUpdate();
                _lstResults.Items.Clear();
                foreach (var m in matches)
                    _lstResults.Items.Add(m);
                _lstResults.EndUpdate();

                if (matches.Count == 0)
                    HideSearchPopup();
                else
                {
                    ShowSearchPopup(matches.Count);
                    _lstResults.SelectedIndex = 0;
                }
            }
            catch
            {
                // فشل البحث لا يعطل الكاشير
            }
        }

        // 📖 Search_KeyDown: مفتاحان فقط — Down ينقل التحديد للقائمة
        // (سهم الكاشير لا يحتاج فأرة)، وEnter يقطع الـ debounce ويكلف
        // فورًا (الماسح يرسل Enter — الانتظار 250ms يبطئ مسحًا متسلسلًا).
        private void Search_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && _searchPopup.Visible && _lstResults.Items.Count > 0)
            {
                e.SuppressKeyPress = true;
                _lstResults.Focus();
                _lstResults.SelectedIndex = Math.Max(0, _lstResults.SelectedIndex);
                return;
            }

            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;
            _searchTimer.Stop();
            _ = SearchCommitAsync();
        }

        /// <summary>Enter في مربع البحث: اختيار المحدد إن كانت القائمة ظاهرة، وإلا بحث مباشر.</summary>
        private async Task SearchCommitAsync()
        {
            if (_searchPopup.Visible && _lstResults.SelectedIndex >= 0)
            {
                AddSelectedResult();
                return;
            }

            string term = CleanScannerInput(_txtSearch.Text);
            if (term != (_txtSearch.Text?.Trim() ?? ""))
                _txtSearch.Text = term;
            if (term.Length == 0) return;

            bool scannerLike = LooksLikeScannerToken(term);

            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();

                var byBarcode = await productService.GetProductByBarcodeAsync(term);
                if (byBarcode.IsSuccess && byBarcode.Data != null)
                {
                    AddToCart(byBarcode.Data);
                    FinishSearchInput();
                    return;
                }

                // رمز مسح بلا مطابقة: تنبيه وإنهاء — لا نوقع الكاشير في بحث اسم متبقي في الحقل
                if (scannerLike)
                {
                    ShowScanNotFound(term);
                    FinishSearchInput();
                    return;
                }

                await RunLiveSearchAsync();
                if (_lstResults.Items.Count == 1)
                    AddSelectedResult();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر البحث عن المنتج.");
            }
        }

        // 📖 LstResults_KeyDown: تفاعل لوحة مفاتيح كامل داخل القائمة —
        // Enter=اختيار، Esc=إخفاء (وعودة للمربع)، Up عند أول عنصر يعيد
        // التركيز للمربع (دورة مغلقة: المربع ↔ القائمة بلا فأرة أبدًا).
        private void LstResults_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddSelectedResult();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                HideSearchPopup();
            }
            else if (e.KeyCode == Keys.Up && _lstResults.SelectedIndex <= 0)
            {
                e.SuppressKeyPress = true;
                _txtSearch.Focus();
            }
        }

        private void AddSelectedResult()
        {
            if (_lstResults.SelectedIndex < 0 || _lstResults.SelectedIndex >= _lstResults.Items.Count)
                return;

            if (_lstResults.Items[_lstResults.SelectedIndex] is not SearchItem item)
                return;

            AddToCart(item.Product);
            FinishSearchInput();
        }

        // 📖 FinishSearchInput: «تصفير دورة البحث» — يبطل أي بحث جارٍ
        // (زيادة _searchSeq تهمل نتائجه)، يوقف المؤقت، يمسح المربع،
        // ويخفي القائمة. يستدعى بعد كل إضافة ناجحة — المربع جاهز
        // دائمًا للمسح التالي (دورة ماسح متسلسلة بلا بقايا نص قديم).
        private void FinishSearchInput()
        {
            _searchSeq++;
            _searchTimer.Stop();
            _txtSearch.Clear();
            HideSearchPopup();
            _txtSearch.Focus();
        }

        // ───────────────── منطقة السلة (المنتصف) ─────────────────
        // 📖 BuildCartArea: بطاقة السلة = شريط عنوان Top + جدول Fill.
        // ⚠️ header.Paint: نمط «خط يدوي» — نرسم خطًا سفليًا فاصلًا بـ GDI+
        //    لأن Panel بلا إطار لا يملك خاصية «خط سفلي» — Paint يُستدعى
        //    كل إعادة رسم، فالخط يعاد رسمه صحيحًا دائمًا (لا يتوارى).
        // ترتيب الإرساء داخل الشريط (RTL): العنوان أقصى اليمين (Right
        // أولًا)، شارة البنود يساره، وتحذير المخزون Fill في الوسط.
        // داخل الحاوية: الجدول Fill أولًا ثم الشريط Top (ترتيب معكوس).
        private void BuildCartArea()
        {
            var container = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(16, 12, 16, 12) };

            // شريط عنوان السلة
            var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = AppTheme.CardBg, Padding = new Padding(16, 0, 16, 0) };
            header.Paint += (s, e) =>
                e.Graphics.DrawLine(new Pen(AppTheme.BorderColor), 0, header.Height - 1, header.Width, header.Height - 1);

            var lblTitle = new Label
            {
                Text = "الفاتورة الحالية",
                Dock = DockStyle.Right,
                Width = 160,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary
            };

            _lblItemsBadge = new Label
            {
                Text = "لا توجد بنود",
                Dock = DockStyle.Right,
                Width = 130,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10F),
                ForeColor = AppTheme.TextMuted
            };

            _lblStockWarn = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = AppTheme.Danger
            };

            header.Controls.Add(_lblStockWarn);
            header.Controls.Add(_lblItemsBadge);
            header.Controls.Add(lblTitle);

            // شبكة السلة
            _gridCart = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            StyleCartGrid();

            container.Controls.Add(_gridCart);
            container.Controls.Add(header);

            Controls.Add(container);
        }

        // 📖 StyleCartGrid: جدول السلة — أثقل جدول تخصيصًا في النظام.
        // الفكرة: «أزرار داخل خلايا» — ثلاثة أعمدة أزرار (− كمية + حذف)
        // بينها عمود كمية قابل للتحرير مباشرة.
        //  • أزرار الأعمدة: DataGridViewButtonColumn — النقر يلتقط في
        //    GridCart_CellClick بمقارنة اسم العمود (وليس برقمه — لو
        //    أعدت ترتيب الأعمدة يبقى الالتقاط صحيحًا).
        //  • colQty وحده ReadOnly=false + خلفية FieldBg — «دعوة بصرية»
        //    للكتابة فوقه (بقية الجدول عرض فقط).
        //  • أرقام أكبر (12-14pt) من StyleGrid القياسي: شاشة كاشير
        //    تُقرأ من مسافة ذراع واقفًا — قاعدة «حجم الخط = مسافة القارئ».
        //  • CellValidating: حارس إدخال — أي نص غير رقمي في الكمية
        //    يُرفض قبل وصوله للبيانات (e.Cancel = ارجع للخلية).
        private void StyleCartGrid()
        {
            UiHelpers.StyleGrid(_gridCart);
            _gridCart.ReadOnly = false;
            _gridCart.RowTemplate.Height = 48;
            _gridCart.Font = new Font("Segoe UI", 12F);

            _gridCart.Columns.Clear();

            _gridCart.Columns.Add("colProduct", "المنتج");

            var cPrice = new DataGridViewCellStyle { Format = "N2", Alignment = DataGridViewContentAlignment.MiddleCenter };
            _gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "colPrice", HeaderText = "السعر", DefaultCellStyle = cPrice, ReadOnly = true });

            var colMinus = new DataGridViewButtonColumn { Name = "colMinus", HeaderText = " ", Text = "−", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, FillWeight = 6 };
            colMinus.DefaultCellStyle.ForeColor = AppTheme.Primary;
            colMinus.DefaultCellStyle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);

            var colQty = new DataGridViewTextBoxColumn { Name = "colQty", HeaderText = "الكمية", ReadOnly = false, FillWeight = 10 };
            colQty.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colQty.DefaultCellStyle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            colQty.DefaultCellStyle.BackColor = AppTheme.FieldBg;

            var colPlus = new DataGridViewButtonColumn { Name = "colPlus", HeaderText = " ", Text = "+", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, FillWeight = 6 };
            colPlus.DefaultCellStyle.ForeColor = AppTheme.Success;
            colPlus.DefaultCellStyle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);

            var colTotal = new DataGridViewTextBoxColumn { Name = "colTotal", HeaderText = "الإجمالي", ReadOnly = true, FillWeight = 16 };
            colTotal.DefaultCellStyle = new DataGridViewCellStyle(cPrice) { Font = new Font("Segoe UI", 12F, FontStyle.Bold) };

            var colDelete = new DataGridViewButtonColumn { Name = "colDelete", HeaderText = "حذف", Text = "حذف", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, FillWeight = 8 };
            colDelete.DefaultCellStyle.ForeColor = AppTheme.Danger;

            _gridCart.Columns.AddRange(new DataGridViewColumn[] { colMinus, colQty, colPlus, colTotal, colDelete });

            _gridCart.Columns["colProduct"].FillWeight = 39;
            _gridCart.Columns["colPrice"].FillWeight = 15;
            _gridCart.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            _gridCart.CellClick += GridCart_CellClick;
            _gridCart.CellValidating += GridCart_CellValidating;
            _gridCart.CellEndEdit += GridCart_CellEndEdit;
        }

        // 📖 GridCart_CellValidating/CellEndEdit — زوج حراسة الكمية:
        //  Validating: يرفض أي نص غير رقمي قبل خروج التركيز (فيبقى
        //  المحرر مفتوحًا حتى يصحح المستخدم — لا قيمة فاسدة تصل أبدًا).
        //  CellEndEdit: قراءة القيمة → تقريب لصحيح → حد أدنى 1 →
        //  حد أقصى المتاح من المخزون (مع رسالة تقييد) → تحديث البند
        //  ثم BeginInvoke(RefreshCart): التأجيل للخارج من محرر الجدول
        //  بأمان — إعادة بناء الصفوف داخل CellEndEdit مباشرة تقتل
        //  التحرير نفسه (DataGridView يستاء من هدم صفوفه أثناء حديثه).
        private void GridCart_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
        {
            if (_gridCart.Columns[e.ColumnIndex].Name != "colQty") return;
            if (!decimal.TryParse(e.FormattedValue?.ToString()?.Trim(), out _))
            {
                e.Cancel = true;
            }
        }

        private void GridCart_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _cart.Count) return;
            if (_gridCart.Columns[e.ColumnIndex].Name != "colQty") return;

            var item = _cart[e.RowIndex];
            string raw = _gridCart[e.ColumnIndex, e.RowIndex].Value?.ToString() ?? "";
            int qty = decimal.TryParse(raw.Trim(), out decimal d) ? (int)Math.Round(d) : item.Quantity;

            if (qty < 1) qty = 1;

            // حد أقصى صلب: لا تجاوز الكمية المتاحة
            if (_stockByProduct.TryGetValue(item.ProductId, out int stock) &&
                stock > 0 && qty > stock)
            {
                qty = stock;
                ShowStockWarn($"تم تقييد الكمية إلى المتاح من '{item.ProductName}' ({stock})");
            }

            item.Quantity = qty;
            BeginInvoke(new Action(RefreshCart));
        }

        // 📖 GridCart_CellClick: «موزع أزرار السلة» — النقر على +/−/حذف
        // يلتقط بمقارنة اسم العمود (لا رقمه — إعادة ترتيب الأعمدة آمنة).
        //  • قبل أي شيء: إثبات التحرير المعلق (EndEdit) — الكمية المكتوبة
        //    جزء من العملية، وإلا عمل + على قيمة لم تُعتمد بعد.
        //  • + يفحص سقف المخزون قبل الزيادة (برسالة، لا صمت).
        //  • − لا ينزل تحت 1 (للحذف زر حذف — خلط العمليتين يشوش).
        private void GridCart_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            HideSearchPopup();

            // إثبات الكمية المكتوبة أولًا قبل أي عملية (+ / − / حذف)
            if (_gridCart.IsCurrentCellInEditMode)
            {
                if (!_gridCart.EndEdit())
                    _gridCart.CancelEdit();
            }

            if (e.RowIndex < 0 || e.RowIndex >= _cart.Count)
                return;

            string? colName = _gridCart.Columns[e.ColumnIndex].Name;
            var item = _cart[e.RowIndex];

            switch (colName)
            {
                case "colPlus":
                    if (_stockByProduct.TryGetValue(item.ProductId, out int stockPlus) &&
                        stockPlus > 0 && item.Quantity >= stockPlus)
                    {
                        ShowStockWarn($"لا يمكن تجاوز المتاح من '{item.ProductName}' ({stockPlus})");
                        return;
                    }
                    item.Quantity++;
                    break;

                case "colMinus":
                    if (item.Quantity > 1) item.Quantity--;
                    break;

                case "colDelete":
                    _cart.RemoveAt(e.RowIndex);
                    break;

                default:
                    return;
            }

            RefreshCart();
        }

        private void ShowStockWarn(string message)
        {
            _lblStockWarn.Text = message;
            _warnTimer.Stop();
            _warnTimer.Start();
        }

        // ───────────────── لوحة الأوامر (يمين) ─────────────────
        // ═══════════════════════════════════════════════════════════════
        // 📖 BuildActionPanel: عمود يمين شاشة الكاشير — «مصنع الصفوف» AddRow.
        //
        // البنية: TLP عمود واحد + AutoScroll، كل مقطع «صف» يُضاف بـ AddRow
        // (الدالة تضيف RowStyle بالارتفاع المطلوب وترسي العنصر Top):
        //   1. بطاقة العميل (68)      4. زر الدفع الكبير 78px — F2
        //   2. بطاقة الإجماليات (132)  5. تعليق/مسح — صف ثنائي MakeTwoColumns
        //   3. الصافي (62) Accent      6. عنوان + شبكة عمليات 2×2
        //   ... ثم صف Percent يمتص الفائض (قاعدة TLP الذهبية).
        //
        // 💡 لإضافة مقطع: var x = ...; AddRow(layout, x, ارتفاعه);
        //    الأرقام في AddRow = ارتفاعات مطلقة بالبكسل — عدّل بحرية.
        // بطاقات MakeCard: حدود دائرية مرسومة يدويًا (Paint) — انظر الدالة.
        // ═══════════════════════════════════════════════════════════════
        private void BuildActionPanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 388, BackColor = AppTheme.Bg, Padding = new Padding(4, 12, 12, 12) };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(0),
                AutoScroll = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 1) بطاقة العميل
            var customerCard = MakeCard(64);
            _lblCustomer = new Label
            {
                Text = "عميل مجهول",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(0, 0, 10, 0)
            };
            var btnCustomer = AppTheme.MakeButton("عميل — F4", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 44);
            btnCustomer.Dock = DockStyle.Left;
            btnCustomer.Width = 128;
            btnCustomer.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnCustomer.Click += (_, _) => PickCustomer();
            customerCard.Controls.Add(_lblCustomer);
            customerCard.Controls.Add(btnCustomer);
            AddRow(layout, customerCard, 68);

            // 2) بطاقة الإجماليات
            var totalsCard = MakeCard(128);
            var totals = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = AppTheme.CardBg };
            totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

            _lblSubtotal = new Label
            {
                Text = "الإجمالي: 0.00",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary
            };
            totals.SetColumnSpan(_lblSubtotal, 2);
            totals.Controls.Add(_lblSubtotal, 0, 0);

            var lblDiscount = new Label { Text = "خصم %", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted, Font = new Font("Segoe UI", 10F) };
            _txtDiscount = new TextBox { Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, Text = "0", Font = new Font("Segoe UI", 11F) };
            _txtDiscount.TextChanged += (_, _) => RefreshTotals();
            totals.Controls.Add(lblDiscount, 0, 1);
            totals.Controls.Add(_txtDiscount, 1, 1);

            var lblTax = new Label { Text = "ضريبة %", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted, Font = new Font("Segoe UI", 10F) };
            _txtTax = new TextBox { Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, Text = "0", Font = new Font("Segoe UI", 11F) };
            _txtTax.TextChanged += (_, _) => RefreshTotals();
            totals.Controls.Add(lblTax, 0, 2);
            totals.Controls.Add(_txtTax, 1, 2);

            totalsCard.Controls.Add(totals);
            AddRow(layout, totalsCard, 132);

            // 3) الصافي
            _lblNet = new Label
            {
                Text = "الصافي: 0.00",
                Dock = DockStyle.Top,
                Height = 58,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = AppTheme.Accent
            };
            AddRow(layout, _lblNet, 62);

            // 4) زر الدفع الكبير — F2 (اختصار الكاشير الأشهر عالميًا)
            _btnPay = AppTheme.MakeButton("دفع الآن — F2", AppTheme.Success, hoverColor: AppTheme.SuccessDark, height: 78);
            _btnPay.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            _btnPay.Click += async (_, _) => await CheckoutAsync(suspend: false);
            AddRow(layout, _btnPay, 82);

            // 5) تعليق + مسح (صفان متجاوران) — F3 للتعليق
            _btnSuspend = AppTheme.MakeButton("تعليق — F3", AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9), height: 54);
            _btnSuspend.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _btnSuspend.Click += async (_, _) => await CheckoutAsync(suspend: true);

            var btnClear = AppTheme.MakeButton("مسح السلة", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 54);
            btnClear.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnClear.FlatAppearance.BorderSize = 1;
            btnClear.FlatAppearance.BorderColor = AppTheme.BorderColor;
            btnClear.Click += (_, _) =>
            {
                if (_cart.Count == 0) return;
                if (ConfirmationDialog.Show(this, "مسح السلة", "هل تريد مسح جميع بنود الفاتورة؟"))
                    ClearCart();
            };
            AddRow(layout, MakeTwoColumns(_btnSuspend, btnClear), 58);

            // 6) عنوان قسم العمليات
            var lblOps = new Label
            {
                Text = "── عمليات أخرى ──",
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 9F)
            };
            AddRow(layout, lblOps, 30);

            // 7) عمليات 2×2
            var btnReturn = AppTheme.MakeButton("مرتجع — F12", Color.FromArgb(124, 58, 237), hoverColor: Color.FromArgb(91, 33, 182), height: 52);
            btnReturn.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnReturn.Margin = new Padding(0, 0, 4, 8);
            btnReturn.Click += (_, _) => ShowReturnForm();

            var btnCash = AppTheme.MakeButton("قبض وصرف", AppTheme.PrimaryLight, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 52);
            btnCash.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnCash.Margin = new Padding(4, 0, 0, 8);
            btnCash.Click += (_, _) => ShowCashMovement();

            var btnSuspended = AppTheme.MakeButton("فواتير معلقة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 52);
            btnSuspended.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnSuspended.Margin = new Padding(0, 0, 4, 0);
            btnSuspended.Click += (_, _) => ShowSuspendedInvoices();

            var btnCloseSession = AppTheme.MakeButton("إغلاق (Z)", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 52);
            btnCloseSession.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnCloseSession.Margin = new Padding(4, 0, 0, 0);
            btnCloseSession.Click += async (_, _) => await CloseSessionFlowAsync();

            var opsGrid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 2, RowCount = 2, BackColor = AppTheme.Bg, Margin = new Padding(0) };
            opsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            opsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            opsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            opsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            btnReturn.Dock = DockStyle.Fill;
            btnCash.Dock = DockStyle.Fill;
            btnSuspended.Dock = DockStyle.Fill;
            btnCloseSession.Dock = DockStyle.Fill;
            opsGrid.Controls.Add(btnReturn, 0, 0);
            opsGrid.Controls.Add(btnCash, 1, 0);
            opsGrid.Controls.Add(btnSuspended, 0, 1);
            opsGrid.Controls.Add(btnCloseSession, 1, 1);
            AddRow(layout, opsGrid, 112);

            // 8) استلام المخزون (حسب الصلاحية)
            var btnReceive = AppTheme.MakeButton("استلام مخزون", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 50);
            btnReceive.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnReceive.Visible = SessionState.CanReceiveStock;
            btnReceive.Click += (_, _) =>
            {
                Program.Services.GetRequiredService<ReceiveStockForm>().ShowDialog(this);
                _ = RefreshStockAsync();
            };
            AddRow(layout, btnReceive, 54);

            // صف مرن يملأ المساحة المتبقية
            AddRow(layout, new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg }, 0, SizeType.Percent);

            panel.Controls.Add(layout);
            Controls.Add(panel);
        }

        // 📖 MakeCard: بطاقة بيضاء بحد دائري — الحد يُرسم يدويًا في Paint:
        // Panel العادي بلا BorderRadius، فنرسم مسارًا مدورًا (CreateRoundedPath)
        // بقلم BorderColor. Paint يعاد مع كل رسم فالحد يتبع حجم البطاقة.
        private static Panel MakeCard(int height)
        {
            var card = new Panel { Dock = DockStyle.Top, Height = height, BackColor = AppTheme.CardBg, Padding = new Padding(8) };
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(AppTheme.BorderColor);
                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = AppTheme.CreateRoundedPath(rect, 10);
                e.Graphics.DrawPath(pen, path);
            };
            return card;
        }

        // 📖 MakeTwoColumns: صف ثنائي 50/50 — عنصران يمين (عمود 0 في RTL)
        // ويسار (عمود 1). Margin بينهما 4px هو الفاصل البصري.
        // يُستعمل حيث الزران كثيرا يرتبطان (تعليق+مسح، مرتجع+قبض...).
        private static Control MakeTwoColumns(Control right, Control left)
        {
            var two = new TableLayoutPanel { Dock = DockStyle.Top, Height = right.Height, ColumnCount = 2, BackColor = AppTheme.Bg, Margin = new Padding(0) };
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            right.Dock = DockStyle.Fill;
            left.Dock = DockStyle.Fill;
            right.Margin = new Padding(0, 0, 4, 0);
            left.Margin = new Padding(4, 0, 0, 0);
            two.Controls.Add(right, 0, 0);
            two.Controls.Add(left, 1, 0);
            return two;
        }

        // 📖 AddRow: «مصنع الصفوف» — يضيف عنصرًا كصف جديد في TLP عمودي:
        //  • Absolute: العنصر Dock=Top بارتفاع محدد (كل مقاطع اللوحة).
        //  • Percent: العنصر Dock=Fill بارتفاع 0 — يكبر ليأكل الفائض
        //    (الصف الفاصل الأخير — بلاه تتمدد كل الصفوف وتتشوه النسب).
        // Margin سفلي 10 = المسافة الرأسية بين كل مقطعين (تباعد مجاني).
        private static void AddRow(TableLayoutPanel layout, Control control, int height, SizeType sizeType = SizeType.Absolute)
        {
            control.Margin = new Padding(0, 0, 0, 10);
            control.Dock = sizeType == SizeType.Percent ? DockStyle.Fill : DockStyle.Top;
            if (sizeType == SizeType.Percent)
                control.Height = 0;
            layout.Controls.Add(control);
            layout.RowStyles.Add(new RowStyle(sizeType, sizeType == SizeType.Percent ? 100 : height));
        }

        // ───────────────── الجلسة والمخزون ─────────────────
        // 📖 OpenSessionAsync: «جلسة العمل» — النموذج المحاسبي لكل كاشير:
        //  كل عملية بيع/قبض/صرف تُنسب لجلسة لها نقد افتتاحي؛ إغلاقها (Z)
        //  يطابق النقد المتوقع مع المعدود. هنا: جلسة مفتوحة سابقًا؟ تُستأنف
        //  (استدعاء العمل بعد استراحة يكمل نفس الجلسة) وإلا OpenSessionForm
        //  لعدّ النقد الافتتاحي. ثم تُقرأ نسبة الضريبة الافتراضية من
        //  الإعدادات وتُحقن في المربع — الكاشير لا يكتبها يدويًا كل مرة.
        private async Task OpenSessionAsync()
        {
            int userId = SessionState.CurrentUser?.UserId ?? 0;
            try
            {
                using var scope = Program.Services.CreateScope();
                var posService = scope.ServiceProvider.GetRequiredService<clsPosSessionService>();

                var existing = await posService.GetOpenSessionByUserAsync(userId);
                if (existing.IsSuccess && existing.Data != null)
                {
                    _sessionId = existing.Data.SessionId;
                }
                else
                {
                    // جلسة جديدة: إدخال النقد الافتتاحي (الإلغاء = فتح بصفر)
                    decimal opening = 0;
                    using (var openForm = new OpenSessionForm())
                    {
                        if (openForm.ShowDialog(this) == DialogResult.OK)
                            opening = openForm.OpeningAmount;
                    }

                    var opened = await posService.OpenSessionAsync(userId, opening);
                    if (opened.IsSuccess)
                        _sessionId = opened.Data;
                }

                _lblSession.Text = _sessionId.HasValue ? $"الجلسة #{_sessionId.Value}" : "بدون جلسة";

                // نسبة الضريبة الافتراضية من الإعدادات
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                decimal tax = await settings.GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0m);
                _txtTax.Text = tax > 0 ? tax.ToString("0.##") : "0";
            }
            catch
            {
                _lblSession.Text = "بدون جلسة";
            }
        }

        // 📖 RefreshStockAsync: لقطة أرصدة المخزون كاملة للذاكرة
        // (_stockByProduct: معرف → متاح). تُقرأ عند الفتح وبعد كل
        // بيع/مرتجع/استلام — هي مصدر «سقف الكمية» في السلة بأكملها.
        // فشلها لا يعطل البيع: القيم تصفر ببساطة (فقط تفقد التقييد).
        private async Task RefreshStockAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var reports = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var result = await reports.GetInventoryStatusAsync();
                if (result.IsSuccess && result.Data != null)
                    _stockByProduct = result.Data.ToDictionary(x => x.ProductId, x => x.QuantityAvailable);
            }
            catch
            {
                // عرض المخزون تحسين فقط — لا يعطل البيع
            }
        }

        // ───────────────── البحث والإضافة للسلة ─────────────────
        // 📖 AddToCart: قلب السلة — قاعدتان:
        //  1) «دمج أو إضافة»: منتج موجود؟ كمية++ (سطر واحد لكل منتج —
        //     الفاتورة تبقى قصيرة)؛ جديد؟ CartItem بكمية 1.
        //  2) «سقف ناعم ثم تنبيه»: تجاوز المتاح يمنع الزيادة أصلًا
        //     (منع مسبق أفضل من فشل لاحق)، والوصول للحد ينبّه دون منع —
        //     لاحظ الفرق: AddToCart الموجود = منع، الجديد الذي نال
        //     الكمية كاملة = تنبيه فقط (بيع خدمي/صفر مخزون ممكن).
        private void AddToCart(DtoProduct product)
        {
            var existing = _cart.FirstOrDefault(i => i.ProductId == product.ProductId);
            if (existing != null)
            {
                if (_stockByProduct.TryGetValue(product.ProductId, out int stockAvail) &&
                    stockAvail > 0 && existing.Quantity >= stockAvail)
                {
                    ShowStockWarn($"لا يمكن تجاوز المتاح من '{product.Name}' ({stockAvail})");
                    return;
                }
                existing.Quantity++;
            }
            else
                _cart.Add(new CartItem
                {
                    ProductId = product.ProductId,
                    ProductName = product.Name,
                    Price = product.BasePrice,
                    Quantity = 1
                });

            if (_stockByProduct.TryGetValue(product.ProductId, out int stock) &&
                product.Type == "stock")
            {
                int inCart = _cart.First(i => i.ProductId == product.ProductId).Quantity;
                if (inCart > stock)
                    ShowStockWarn($"تنبيه: المتاح من '{product.Name}' هو {stock} فقط");
            }

            RefreshCart();
        }

        // ───────────────── السلة والإجماليات ─────────────────
        // 📖 RefreshCart: «مصدر الحقيقة يرسم» — يهدم صفوف الجدول ويعيد
        // بناءها من _cart (لا تحرير موضعي للخلايا) — أبسط نموذجًا وأقل
        // إمساكًا بالحالة المتشابكة؛ جدول كاشير صغير يجعله مجاني الأداء.
        // الشارة: «N بند • M قطعة» — البنود والقطعات معلومات مختلفتان
        // (الكاشير يهتم بالقطع عند الجرد، والزبون بالبنود عند الفحص).
        // لون الصافي: رمادي عند الفراغ («لا شيء يُدفع») وAccent عند الحياة.
        // ثم التغذية الحية لشاشة الزبون الثاني (إن وجدت).
        private void RefreshCart()
        {
            _gridCart.Rows.Clear();
            foreach (var item in _cart)
                _gridCart.Rows.Add(item.ProductName, UiHelpers.Money(item.Price), "−", item.Quantity, "+", UiHelpers.Money(item.Total), "حذف");

            int totalUnits = _cart.Sum(i => i.Quantity);
            _lblItemsBadge.Text = _cart.Count == 0 ? "لا توجد بنود" : $"{_cart.Count} بند • {totalUnits} قطعة";
            _lblNet.BackColor = _cart.Count == 0 ? AppTheme.TextMuted : AppTheme.Accent;

            RefreshTotals();

            // 📖 شاشة الزبون: كل تغيير بالسلة يصل للعرض الثاني لحظيًا
            // (الترحيب يختبئ فور أول بند — داخل CustomerDisplayForm)
            NotifyCustomerDisplay();
        }

        // 📖 معادلة التسعير (تفحصها المحاسبة — لا تعدلها بتفاهم):
        //  الصافي = (المجموع − المجموع × نسبةالخصم) × (1 + نسبةالضريبة/100)
        //  الترتيب مقصود: الخصم على المجموع أولًا، والضريبة على الصافي
        //  بعد الخصم (الطريقة الأشهر في المتاجر — لا ضريبة على مبلغ
        //  لم يُطلب أصلًا). ParsePercent: يرفض السالب ويقص فوق 100.
        //  كل القيم decimal (لا double أبدًا — فاصلة عائمة نقود = خطيئة).
        private decimal Subtotal => _cart.Sum(i => i.Total);

        private decimal DiscountPercent => ParsePercent(_txtDiscount.Text);

        private decimal TaxPercent => ParsePercent(_txtTax.Text);

        // 📖 قيم العرض لشاشة الزبون: الخصم/الضريبة بالعملة (لا بالنسبة)
        // — الزبون يفهم «−12.50» ولا يفهم «خصم 5%».
        private decimal DiscountAmountForDisplay =>
            Math.Round(Subtotal * DiscountPercent / 100m, 2);

        private decimal TaxAmountForDisplay
        {
            get
            {
                decimal afterDiscount = Subtotal - Subtotal * DiscountPercent / 100m;
                return Math.Round(afterDiscount * TaxPercent / 100m, 2);
            }
        }

        private static decimal ParsePercent(string? text) =>
            decimal.TryParse(text?.Trim(), out var v) && v > 0 ? Math.Min(v, 100m) : 0m;

        private decimal NetTotal
        {
            get
            {
                decimal afterDiscount = Subtotal - Subtotal * DiscountPercent / 100m;
                return afterDiscount + afterDiscount * TaxPercent / 100m;
            }
        }

        private void RefreshTotals()
        {
            _lblSubtotal.Text = $"الإجمالي: {UiHelpers.Money(Subtotal)}";
            _lblNet.Text = $"الصافي: {UiHelpers.Money(NetTotal)}";
        }

        private void ClearCart()
        {
            _cart.Clear();
            _customerId = null;
            _customerName = null;
            _lblCustomer.Text = "عميل مجهول";
            _txtDiscount.Text = "0";
            _txtTax.Text = "0";
            RefreshCart();
        }

        // ───────────────── العميل ─────────────────
        private void PickCustomer()
        {
            using var picker = new CustomerPickerForm();
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedCustomerId.HasValue)
            {
                _customerId = picker.SelectedCustomerId;
                _customerName = picker.SelectedCustomerName;
                _lblCustomer.Text = _customerName ?? "عميل";
            }
            _txtSearch.Focus(); // الماسح ينتظر: التركيز يعود لمربع البحث فور إغلاق النافذة
        }

        // ───────────────── الدفع / التعليق ─────────────────
        // ═══════════════════════════════════════════════
        // 📖 CheckoutAsync — المسار المالي الأهم في النظام:
        //
        //  مسار البيع (suspend=false):
        //    تأكيد → إشعار شاشة الزبون («جهّز المبلغ») → PaymentForm
        //    (دفعات مقسمة/دَين لعميل) → بناء DtoCheckoutRequest →
        //    ProcessCheckoutAsync (معاملة واحدة: فاتورة+دفعات+مخزون
        //    +حركة+أرصدة) → سؤال الطباعة → تصفير السلة → تحديث المخزون.
        //
        //  مسار التعليق (suspend=true — F3):
        //    «فاتورة مؤجلة»: تحجز مخزونها (لا يبيعها كاشير آخر) وتعود
        //    من شاشة الفواتير المعلقة للاستكمال أو الإلغاء (يحرر الحجز).
        //
        //  💡 نقطة الإلغاء الحساسة: إلغاء الدفع يعيد شاشة الزبون
        //     (checkoutRequested:false) — كل مسار خروج يُعلم الشاشة
        //     الثانية، وإلا بقيت «جهّز المبلغ» لزبون لم يشترِ.
        // ═══════════════════════════════════════════════
        private async Task CheckoutAsync(bool suspend)
        {
            HideSearchPopup();

            if (_cart.Count == 0)
            {
                UiHelpers.ShowError(this, "السلة فارغة. أضف منتجات أولًا.");
                return;
            }

            // VULN-09: لا بيع بلا جلسة — فشل فتح الجلسة كان صامتًا فكان يوم
            // كامل مبيعات بـ SessionId=null لا تراها أي تقارير Z (نقد درج بلا
            // رقابة). الآن: نحاول فتحها؛ وإن تعذّر نمنع البيع برسالة صريحة.
            if (!_sessionId.HasValue)
            {
                await OpenSessionAsync();
                if (!_sessionId.HasValue)
                {
                    UiHelpers.ShowError(this,
                        "لا توجد جلسة بيع مفتوحة وتعذر فتح واحدة.\n" +
                        "أغلق البرنامج وأعد تشغيله أو راجع الاتصال بقاعدة البيانات — " +
                        "البيع بلا جلسة يخرج كل النقد من تقارير التسوية.");
                    _txtSearch.Focus();
                    return;
                }
            }

            List<DtoPaymentInfo> payments = new();

            if (!suspend)
            {
                bool confirmSale = ConfirmationDialog.Show(this, "إتمام البيع",
                    $"الصافي المطلوب: {UiHelpers.Money(NetTotal)}. هل تريد إتمام عملية البيع؟", dangerStyle: false);
                if (!confirmSale)
                    return;

                // 📖 شاشة الزبون: لحظة بدء الدفع تظهر «يرجى تجهيز المبلغ»
                // تحت الإجمالي — سطر صامت ثابت بلا وميض (الزبون يستعد للدفع)
                NotifyCustomerDisplay(checkoutRequested: true);

                using var payForm = new PaymentForm(NetTotal, hasCustomer: _customerId.HasValue);
                if (payForm.ShowDialog(this) != DialogResult.OK || payForm.PaymentsList.Count == 0)
                {
                    // أُلغي الدفع — شاشة الزبون تعود للعرض العادي
                    NotifyCustomerDisplay(checkoutRequested: false);
                    return;
                }
                payments = payForm.PaymentsList;
            }
            else
            {
                bool confirm = ConfirmationDialog.Show(this, "تعليق الفاتورة",
                    "سيتم تعليق الفاتورة وحجز المخزون. هل أنت متأكد؟");
                if (!confirm)
                    return;
            }

            var request = new DtoCheckoutRequest
            {
                CustomerId = _customerId,
                UserId = SessionState.CurrentUser?.UserId ?? 0,
                SessionId = _sessionId,
                Items = _cart.Select(i => new DtoCheckoutItem
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    UnitPrice = i.Price // VULN-14: snapshot سعر السلة — سعر لحظة الإضافة
                }).ToList(),
                DiscountPercent = DiscountPercent,
                DiscountAmount = 0,
                TaxPercent = TaxPercent,
                Payments = payments,
                Notes = null
            };

            Result<int> result;
            using (var scope = Program.Services.CreateScope())
            {
                var checkoutService = scope.ServiceProvider.GetRequiredService<clsCheckoutService>();
                result = await checkoutService.ProcessCheckoutAsync(request, suspend);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشلت عملية البيع." : result.Message);
                _txtSearch.Focus();
                return;
            }

            if (suspend)
            {
                UiHelpers.ShowInfo(this, $"تم تعليق الفاتورة رقم {result.Data}.");
                ClearCart();
                await RefreshStockAsync();
                return;
            }

            // VULN-35: الطباعة الحرارية التلقائية — كان إعداد «طباعة تلقائية بعد
            // كل بيع» محفوظًا ولا يُقرأ (طابعة كاملة من الكود الميت). الآن:
            // إن فعّلها المدير وضبط طابعة → ESC/POS مباشرة بعد كل بيع
            // (بلا نافذة PDF) — الإيصال يخرج لحظة اكتمال الفاتورة.
            await TryAutoThermalPrintAsync(result.Data);

            // اكتملت الفاتورة (دفع كامل أو دَين) — سؤال واحد بسيط: هل يريد الكاشير طباعتها؟
            // (يُسأل فقط لو لم تخرج حراريًا تلقائيًا — لا نكرر الطباعة مرتين)
            if (!_autoPrintedThisSale && ConfirmationDialog.Show(this, "طباعة الفاتورة",
                    "هل تريد طباعة الفاتورة؟", dangerStyle: false))
            {
                await InvoicePrintHelper.PrintAsync(result.Data, this);
            }
            _autoPrintedThisSale = false; // تصفير للفاتورة القادمة

            ClearCart();
            await RefreshStockAsync();
            _txtSearch.Focus(); // الجلسة التالية: الماسح جاهز فورًا
        }

        // ───────────────── طباعة الفاتورة بعد إتمامها ─────────────────
        // (PDF في Documents\Sayl_POS\Invoices ثم فتح نافذة الطباعة بالنسخة الجاهزة)

        /// <summary>VULN-35: الطباعة الحرارية التلقائية بعد البيع —
        /// يقرأ إعدادَي AutoPrintReceipt وReceiptPrinter (كانا يُحفظان ولا يُقرآن)
        /// ويطلق ReceiptPrinter.PrintInvoiceAsync (كان كودًا ميتًا) على Task.Run —
        /// الإرسال الخام winspool حظر فلا يلامس مؤشر الواجهة. أي فشل صامت:
        /// الإيصال تحسين، لا يوقف البيع، والسؤال اليدوي يبقى للأحوال.</summary>
        private bool _autoPrintedThisSale;
        private async Task TryAutoThermalPrintAsync(int invoiceId)
        {
            try
            {
                bool autoPrint;
                string printerName;
                InvoicePrintVM? invoice = null;
                using (var scope = Program.Services.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    autoPrint = await settings.GetBoolAsync(clsSettingsService.KeyAutoPrintReceipt, false);
                    if (!autoPrint) return;
                    printerName = await settings.GetAsync(clsSettingsService.KeyReceiptPrinter, "");
                    if (string.IsNullOrWhiteSpace(printerName)) return;

                    var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                    var res = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                    if (res.IsSuccess) invoice = res.Data;
                }

                if (invoice == null) return;

                var inv = invoice;
                var printer = printerName;
                await Task.Run(async () =>
                {
                    var (ok, _) = await ReceiptPrinter.PrintInvoiceAsync(inv, printer);
                });

                _autoPrintedThisSale = true; // خرجت حراريًا — لا نكرر بالسؤال
            }
            catch
            {
                // صمت مقصود: فشل الإيصال لا يعطل البيع — السؤال اليدوي يبقى بديلًا
            }
        }

        // ───────────────── المرتجع / النقد / إغلاق الجلسة ─────────────────
        // 📖 نوافذ الكاشير الجانبية — نمط موحد: ShowDialog (نافذة حوارية
        // توقف POS حتى تغلق — الكاشير لا يبيع ويعيد بنفس اللحظة)، ثم
        // «تحديث ما مسّته النافذة»: المرتجع/المعلقة تحدث المخزون، إغلاق
        // الجلسة يفتح جلسة جديدة، وكلها تعيد التركيز لمربع البحث —
        // الماسح أول ما ينتظر بعد أي نافذة (سلسلة عمل متصلة).
        private void ShowReturnForm()
        {
            // VULN-05: نمرر جلسة الدرج الحالية — استردادات اليوم تُنسب لتقرير Z اليوم
            using var form = new ReturnForm(_sessionId);
            form.ShowDialog(this);
            _ = RefreshStockAsync();
            _txtSearch.Focus();
        }

        private void ShowCashMovement()
        {
            if (!_sessionId.HasValue)
            {
                UiHelpers.ShowError(this, "لا توجد جلسة مفتوحة.");
                return;
            }

            using var form = new CashMovementForm(_sessionId.Value);
            form.ShowDialog(this);
            _txtSearch.Focus();
        }

        private async Task CloseSessionFlowAsync()
        {
            if (!_sessionId.HasValue)
            {
                UiHelpers.ShowError(this, "لا توجد جلسة مفتوحة.");
                return;
            }

            bool confirmed = ConfirmationDialog.Show(this, "إغلاق الجلسة",
                "سيتم عرض تقرير الجلسة وطلب عدّ النقد. متابعة؟");
            if (!confirmed)
                return;

            using var form = new CloseSessionForm(_sessionId.Value);
            form.ShowDialog(this);

            if (form.Closed)
            {
                ClearCart();
                await OpenSessionAsync();
            }
            _txtSearch.Focus();
        }

        private void ShowSuspendedInvoices()
        {
            // VULN-05: نمرر جلسة الدرج الحالية — دفعات الإكمال تُنسب لتقرير Z اليوم
            using var form = new SuspendedInvoicesForm(_sessionId);
            form.ShowDialog(this);
            _ = RefreshStockAsync();
            _txtSearch.Focus();
        }

        private async Task LogoutAsync()
        {
            bool confirmed = ConfirmationDialog.Show(this, "تسجيل الخروج",
                "هل أنت متأكد من رغبتك في تسجيل الخروج؟");
            if (!confirmed)
                return;

            try
            {
                int? userId = SessionState.CurrentUser?.UserId;
                if (userId.HasValue)
                {
                    using var scope = Program.Services.CreateScope();
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    await usersService.LogoutAsync(userId.Value);
                }
            }
            catch
            {
                // نتجاهل فشل تسجيل الخروج في السجل ونكمل الخروج من الواجهة.
            }

            SessionState.Clear();
            Program.Navigator.Show(Program.Services.GetRequiredService<LoginForm>());
        }
    }
}
