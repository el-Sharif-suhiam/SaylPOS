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
            };
        }

        // ═══════════════════════════════════════════════
        // بناء الواجهة
        // ═══════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "سيل — نقاط البيع";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1150, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;

            BuildCartArea();     // Fill (يُرسّى أخيرًا)
            BuildActionPanel();  // Dock Right
            BuildTopBar();       // Dock Top (يمتد بكامل العرض)
            BuildSearchPopup();
        }

        // ───────────────── الشريط العلوي ─────────────────
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
                BackColor = Color.FromArgb(31, 41, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtSearch.PlaceholderText = "اكتب اسم المنتج للبحث الفوري أو امسح الباركود ثم Enter";
            _txtSearch.TextChanged += (_, _) => OnSearchTextChanged();
            _txtSearch.KeyDown += Search_KeyDown;
            searchHost.Controls.Add(_txtSearch);

            var btnLogout = AppTheme.MakeButton("خروج", Color.FromArgb(31, 41, 55),
                foreColor: Color.FromArgb(252, 165, 165), hoverColor: Color.FromArgb(55, 65, 81), height: 50);
            btnLogout.Dock = DockStyle.Left;
            btnLogout.Width = 92;
            btnLogout.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnLogout.Click += async (_, _) => await LogoutAsync();

            // ترتيب الإرساء المعكوس: الأخير يُرسّى أولًا
            top.Controls.Add(_txtSearch.Parent!);
            top.Controls.Add(_lblSession);
            top.Controls.Add(lblBrand);
            top.Controls.Add(btnLogout);

            Controls.Add(top);
        }

        // ───────────────── قائمة نتائج البحث الحية ─────────────────
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

        private async Task RunLiveSearchAsync()
        {
            string term = _txtSearch.Text?.Trim() ?? "";
            if (term.Length == 0)
            {
                HideSearchPopup();
                return;
            }

            int seq = ++_searchSeq;
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();

                // باركود مطابق تمامًا = إضافة فورية (سيناريو الماسح الضوئي)
                if (term.Length >= 6 && term.All(char.IsDigit))
                {
                    var byBarcode = await productService.GetProductByBarcodeAsync(term);
                    if (seq != _searchSeq) return;
                    if (byBarcode.IsSuccess && byBarcode.Data != null)
                    {
                        AddToCart(byBarcode.Data);
                        FinishSearchInput();
                        return;
                    }
                }

                var matches = (await productService.GetProductsListAsync(term))
                    .Take(8)
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

            string term = _txtSearch.Text?.Trim() ?? "";
            if (term.Length == 0) return;

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

                await RunLiveSearchAsync();
                if (_lstResults.Items.Count == 1)
                    AddSelectedResult();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر البحث عن المنتج.");
            }
        }

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

        private void FinishSearchInput()
        {
            _searchSeq++;
            _searchTimer.Stop();
            _txtSearch.Clear();
            HideSearchPopup();
            _txtSearch.Focus();
        }

        // ───────────────── منطقة السلة (المنتصف) ─────────────────
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

            var colQty = new DataGridViewTextBoxColumn { Name = "colQty", HeaderText = "الكمية", ReadOnly = true, FillWeight = 10 };
            colQty.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colQty.DefaultCellStyle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);

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
        }

        private void GridCart_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            HideSearchPopup();

            if (e.RowIndex < 0 || e.RowIndex >= _cart.Count)
                return;

            string? colName = _gridCart.Columns[e.ColumnIndex].Name;
            var item = _cart[e.RowIndex];

            switch (colName)
            {
                case "colPlus":
                    if (_stockByProduct.TryGetValue(item.ProductId, out int stock) &&
                        _stockByProduct[item.ProductId] > 0 && item.Quantity + 1 > stock)
                        ShowStockWarn($"تنبيه: المتاح من '{item.ProductName}' هو {stock} فقط");
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
        private void BuildActionPanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 388, BackColor = AppTheme.Bg, Padding = new Padding(4, 12, 12, 12) };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(0)
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
            var btnCustomer = AppTheme.MakeButton("اختيار العميل", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 44);
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
                BackColor = AppTheme.Primary
            };
            AddRow(layout, _lblNet, 62);

            // 4) زر الدفع الكبير
            _btnPay = AppTheme.MakeButton("دفع الآن", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 78);
            _btnPay.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            _btnPay.Click += async (_, _) => await CheckoutAsync(suspend: false);
            AddRow(layout, _btnPay, 82);

            // 5) تعليق + مسح (صفان متجاوران)
            _btnSuspend = AppTheme.MakeButton("تعليق الفاتورة", AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9), height: 54);
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
            var btnReturn = AppTheme.MakeButton("مرتجع بيع", Color.FromArgb(124, 58, 237), hoverColor: Color.FromArgb(91, 33, 182), height: 52);
            btnReturn.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            btnReturn.Margin = new Padding(0, 0, 4, 8);
            btnReturn.Click += (_, _) => ShowReturnForm();

            var btnCash = AppTheme.MakeButton("قبض وصرف", AppTheme.PrimaryLight, foreColor: AppTheme.TextPrimary, hoverColor: Color.FromArgb(191, 219, 254), height: 52);
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
        private void AddToCart(DtoProduct product)
        {
            var existing = _cart.FirstOrDefault(i => i.ProductId == product.ProductId);
            if (existing != null)
                existing.Quantity++;
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
        private void RefreshCart()
        {
            _gridCart.Rows.Clear();
            foreach (var item in _cart)
                _gridCart.Rows.Add(item.ProductName, UiHelpers.Money(item.Price), "−", item.Quantity, "+", UiHelpers.Money(item.Total), "حذف");

            int totalUnits = _cart.Sum(i => i.Quantity);
            _lblItemsBadge.Text = _cart.Count == 0 ? "لا توجد بنود" : $"{_cart.Count} بند • {totalUnits} قطعة";
            _lblNet.BackColor = _cart.Count == 0 ? AppTheme.TextMuted : AppTheme.Primary;

            RefreshTotals();
        }

        private decimal Subtotal => _cart.Sum(i => i.Total);

        private decimal DiscountPercent => ParsePercent(_txtDiscount.Text);

        private decimal TaxPercent => ParsePercent(_txtTax.Text);

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
        }

        // ───────────────── الدفع / التعليق ─────────────────
        private async Task CheckoutAsync(bool suspend)
        {
            HideSearchPopup();

            if (_cart.Count == 0)
            {
                UiHelpers.ShowError(this, "السلة فارغة. أضف منتجات أولًا.");
                return;
            }

            List<DtoPaymentInfo> payments = new();

            if (!suspend)
            {
                bool confirmSale = ConfirmationDialog.Show(this, "إتمام البيع",
                    $"الصافي المطلوب: {UiHelpers.Money(NetTotal)}. هل تريد إتمام عملية البيع؟", dangerStyle: false);
                if (!confirmSale)
                    return;

                using var payForm = new PaymentForm(NetTotal, hasCustomer: _customerId.HasValue);
                if (payForm.ShowDialog(this) != DialogResult.OK || payForm.PaymentsList.Count == 0)
                    return;
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
                    Quantity = i.Quantity
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
                return;
            }

            UiHelpers.ShowInfo(this, suspend
                ? $"تم تعليق الفاتورة رقم {result.Data}."
                : $"تمت عملية البيع بنجاح. رقم الفاتورة: {result.Data}");

            if (!suspend)
                _ = PrintReceiptIfEnabledAsync(result.Data);

            ClearCart();
            await RefreshStockAsync();
        }

        // ───────────────── طباعة الإيصال الحراري تلقائيًا ─────────────────
        private async Task PrintReceiptIfEnabledAsync(int invoiceId)
        {
            try
            {
                bool autoPrint;
                string printerName;
                using (var scope = Program.Services.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    autoPrint = await settings.GetBoolAsync(clsSettingsService.KeyAutoPrintReceipt, false);
                    printerName = await settings.GetAsync(clsSettingsService.KeyReceiptPrinter);
                }

                if (!autoPrint || string.IsNullOrWhiteSpace(printerName))
                    return;

                InvoicePrintVM? invoice = null;
                using (var scope = Program.Services.CreateScope())
                {
                    var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                    var result = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                    if (result.IsSuccess)
                        invoice = result.Data;
                }

                if (invoice == null)
                    return;

                var (ok, message) = await ReceiptPrinter.PrintInvoiceAsync(invoice, printerName);
                if (!ok)
                    UiHelpers.ShowError(this, message + " — يمكنك الطباعة من قسم فواتير البيع.");
            }
            catch
            {
                // فشل الطباعة لا يعطل سير البيع
            }
        }

        // ───────────────── المرتجع / النقد / إغلاق الجلسة ─────────────────
        private void ShowReturnForm()
        {
            using var form = new ReturnForm();
            form.ShowDialog(this);
            _ = RefreshStockAsync();
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
        }

        private void ShowSuspendedInvoices()
        {
            using var form = new SuspendedInvoicesForm();
            form.ShowDialog(this);
            _ = RefreshStockAsync();
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
