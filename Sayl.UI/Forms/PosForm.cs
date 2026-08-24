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
    /// واجهة الكاشير: بحث سريع، سلة بيع، خصم/ضريبة، دفع، تعليق واستكمال الفواتير المعلقة، واستلام المخزون.
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

        private readonly List<CartItem> _cart = new();
        private int? _sessionId;

        private TextBox _txtSearch = null!;
        private Panel _searchResultsPanel = null!;
        private ListBox _lstResults = null!;
        private DataGridView _gridCart = null!;
        private Label _lblCustomer = null!;
        private Label _lblSubtotal = null!;
        private Label _lblNet = null!;
        private Label _lblSession = null!;
        private TextBox _txtDiscount = null!;
        private TextBox _txtTax = null!;
        private Button _btnPay = null!;
        private Button _btnSuspend = null!;

        private int? _customerId;
        private string? _customerName;

        public PosForm()
        {
            BuildUi();
            Load += async (_, _) => await OpenSessionAsync();
        }

        // ─────────────────────────────────────────────────
        // بناء الواجهة
        // ─────────────────────────────────────────────────
        private void BuildUi()
        {
            Text = "سيل — نقاط البيع";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1100, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            KeyPreview = true;

            BuildTopBar();
            // ترتيب الإرساء معكوس: Fill (الشبكة) يُضاف قبل لوحة الأوامر حتى تحجز اللوحة مكانها
            BuildCartGrid();
            BuildActionPanel();
        }

        private void BuildTopBar()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = AppTheme.SidebarBg, Padding = new Padding(16, 10, 16, 10) };

            _lblSession = new Label
            {
                Text = "جارٍ فتح الجلسة...",
                Dock = DockStyle.Left,
                Width = 200,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.TextMuted
            };

            var lblBrand = new Label
            {
                Text = "سيل | نقاط البيع",
                Dock = DockStyle.Left,
                Width = 170,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold)
            };

            _txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 14F),
                BackColor = AppTheme.FieldBg
            };
            _txtSearch.PlaceholderText = "امسح الباركود أو اكتب اسم المنتج ثم Enter";
            _txtSearch.KeyDown += Search_KeyDown;

            var btnLogout = new Button
            {
                Text = "خروج",
                Dock = DockStyle.Left,
                Width = 90,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(248, 113, 113),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnLogout.FlatAppearance.BorderSize = 1;
            btnLogout.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 81);
            btnLogout.Click += async (_, _) => await LogoutAsync();

            // ترتيب الإرساء (آخر عنصر يُرسّى أولًا): بحث Fill ثم الجلسة ثم الشعار ثم الخروج
            top.Controls.Add(_txtSearch);
            top.Controls.Add(_lblSession);
            top.Controls.Add(lblBrand);
            top.Controls.Add(btnLogout);

            // لوحة نتائج البحث العائمة
            _searchResultsPanel = new Panel
            {
                Visible = false,
                Width = 520,
                Height = 260,
                BackColor = AppTheme.CardBg,
                BorderStyle = BorderStyle.FixedSingle
            };
            _lstResults = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 12F),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            _lstResults.Click += (_, _) => AddSelectedResult();
            _searchResultsPanel.Controls.Add(_lstResults);
            _searchResultsPanel.BringToFront();

            Controls.Add(top);
            Controls.Add(_searchResultsPanel);

            // تثبيت لوحة النتائج أسفل مربع البحث عند تغيّر المقاس
            top.Resize += (_, _) => PositionResultsPanel(top);
            PositionResultsPanel(top);
        }

        private void PositionResultsPanel(Panel top)
        {
            _searchResultsPanel.Left = top.ClientSize.Width - 16 - _searchResultsPanel.Width;
            _searchResultsPanel.Top = top.Bottom + 4;
        }

        private void BuildCartGrid()
        {
            _gridCart = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg
            };
            UiHelpers.StyleGrid(_gridCart);
            _gridCart.ReadOnly = false;
            _gridCart.Columns.Add("colProduct", "المنتج");
            _gridCart.Columns.Add("colPrice", "السعر");

            var colQty = new DataGridViewTextBoxColumn
            {
                Name = "colQty",
                HeaderText = "الكمية",
                ReadOnly = false
            };
            _gridCart.Columns.Add(colQty);

            _gridCart.Columns.Add("colTotal", "الإجمالي");
            _gridCart.Columns["colProduct"].FillWeight = 45;
            _gridCart.Columns["colPrice"].FillWeight = 18;
            _gridCart.Columns["colQty"].FillWeight = 15;
            _gridCart.Columns["colTotal"].FillWeight = 15;

            var colRemove = new DataGridViewButtonColumn
            {
                Name = "colRemove",
                HeaderText = "",
                Text = "حذف",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat,
                FillWeight = 7
            };
            colRemove.DefaultCellStyle.ForeColor = AppTheme.Danger;
            _gridCart.Columns.Add(colRemove);

            _gridCart.CellEndEdit += (_, _) => RefreshCart();
            _gridCart.CellClick += GridCart_CellClick;

            Controls.Add(_gridCart);
        }

        private void BuildActionPanel()
        {
            var panel = new Panel { Dock = DockStyle.Right, Width = 310, BackColor = AppTheme.CardBg, Padding = new Padding(18, 14, 18, 14) };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _lblCustomer = new Label
            {
                Text = "عميل مجهول",
                Dock = DockStyle.Top,
                Height = 44,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = AppTheme.FieldBg,
                ForeColor = AppTheme.TextPrimary,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold)
            };

            var btnCustomer = AppTheme.MakeButton("اختيار العميل", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 46);
            btnCustomer.Click += (_, _) => PickCustomer();

            _lblSubtotal = new Label
            {
                Text = "الإجمالي: 0.00",
                Dock = DockStyle.Top,
                Height = 36,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary
            };

            var discountRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2 };
            discountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            discountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            var lblDiscount = new Label { Text = "خصم %", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtDiscount = new TextBox { Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, Text = "0" };
            _txtDiscount.TextChanged += (_, _) => RefreshTotals();
            discountRow.Controls.Add(lblDiscount, 0, 0);
            discountRow.Controls.Add(_txtDiscount, 1, 0);

            var taxRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 2 };
            taxRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            taxRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            var lblTax = new Label { Text = "ضريبة %", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtTax = new TextBox { Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, Text = "0" };
            _txtTax.TextChanged += (_, _) => RefreshTotals();
            taxRow.Controls.Add(lblTax, 0, 0);
            taxRow.Controls.Add(_txtTax, 1, 0);

            _lblNet = new Label
            {
                Text = "الصافي: 0.00",
                Dock = DockStyle.Top,
                Height = 52,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = AppTheme.Primary
            };

            _btnPay = AppTheme.MakeButton("دفع", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 60);
            _btnPay.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            _btnPay.Click += async (_, _) => await CheckoutAsync(suspend: false);

            _btnSuspend = AppTheme.MakeButton("تعليق الفاتورة", AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9), height: 54);
            _btnSuspend.Click += async (_, _) => await CheckoutAsync(suspend: true);

            var btnReturn = AppTheme.MakeButton("مرتجع بيع", Color.FromArgb(124, 58, 237), hoverColor: Color.FromArgb(91, 33, 182), height: 54);
            btnReturn.Click += (_, _) => ShowReturnForm();

            var btnCash = AppTheme.MakeButton("قبض / صرف نقد", AppTheme.PrimaryLight, foreColor: AppTheme.TextPrimary, hoverColor: Color.FromArgb(191, 219, 254), height: 54);
            btnCash.Click += (_, _) => ShowCashMovement();

            var btnCloseSession = AppTheme.MakeButton("إغلاق الجلسة (Z)", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 54);
            btnCloseSession.Click += async (_, _) => await CloseSessionFlowAsync();

            var btnSuspended = AppTheme.MakeButton("الفواتير المعلقة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 54);
            btnSuspended.Click += (_, _) => ShowSuspendedInvoices();

            var btnReceive = AppTheme.MakeButton("استلام مخزون", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 54);
            btnReceive.Visible = SessionState.CanReceiveStock;
            btnReceive.Click += (_, _) => Program.Services.GetRequiredService<ReceiveStockForm>().ShowDialog(this);

            var btnClear = AppTheme.MakeButton("مسح السلة", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClear.Click += (_, _) =>
            {
                if (_cart.Count == 0) return;
                if (ConfirmationDialog.Show(this, "مسح السلة", "هل تريد مسح جميع بنود الفاتورة؟"))
                    ClearCart();
            };

            Control[] rows =
            {
                _lblCustomer, btnCustomer, _lblSubtotal, discountRow, taxRow, _lblNet,
                _btnPay, _btnSuspend, btnReturn, btnCash, btnCloseSession, btnSuspended, btnReceive, btnClear
            };

            foreach (var c in rows)
            {
                c.Margin = new Padding(0, 0, 0, 10);
                layout.Controls.Add(c);
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            panel.Controls.Add(layout);
            Controls.Add(panel);
        }

        // ─────────────────────────────────────────────────
        // الجلسة
        // ─────────────────────────────────────────────────
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

        // ─────────────────────────────────────────────────
        // البحث وإضافة المنتجات
        // ─────────────────────────────────────────────────
        private void Search_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;
            _ = SearchAndAddAsync(_txtSearch.Text?.Trim() ?? "");
        }

        private async Task SearchAndAddAsync(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();

                // محاولة الباركود أولًا
                var byBarcode = await productService.GetProductByBarcodeAsync(term);
                if (byBarcode.IsSuccess && byBarcode.Data != null)
                {
                    AddToCart(byBarcode.Data);
                    _txtSearch.Clear();
                    return;
                }

                var matches = await productService.GetProductsListAsync(term);
                if (matches.Count == 0)
                {
                    UiHelpers.ShowInfo(this, $"لا يوجد منتج مطابق لـ '{term}'.");
                    return;
                }

                if (matches.Count == 1)
                {
                    AddToCart(matches[0]);
                    _txtSearch.Clear();
                    return;
                }

                _lstResults.Items.Clear();
                _lstResults.Tag = matches;
                foreach (var p in matches)
                    _lstResults.Items.Add($"{p.Name} — {UiHelpers.Money(p.BasePrice)}");
                _searchResultsPanel.Visible = true;
                _searchResultsPanel.BringToFront();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر البحث عن المنتج.");
            }
        }

        private void AddSelectedResult()
        {
            if (_lstResults.Tag is not List<Common.DTOs.DtoProduct> matches ||
                _lstResults.SelectedIndex < 0 ||
                _lstResults.SelectedIndex >= matches.Count)
                return;

            AddToCart(matches[_lstResults.SelectedIndex]);
            _searchResultsPanel.Visible = false;
            _txtSearch.Clear();
            _txtSearch.Focus();
        }

        private void AddToCart(Common.DTOs.DtoProduct product)
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

            RefreshCart();
        }

        // ─────────────────────────────────────────────────
        // السلة والإجماليات
        // ─────────────────────────────────────────────────
        private void GridCart_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != _gridCart.Columns["colRemove"].Index)
                return;

            if (e.RowIndex < _cart.Count)
            {
                _cart.RemoveAt(e.RowIndex);
                RefreshCart();
            }
        }

        private void RefreshCart()
        {
            // قراءة الكميات المعدلة من الشبكة قبل إعادة البناء
            for (int i = 0; i < _gridCart.Rows.Count && i < _cart.Count; i++)
            {
                if (int.TryParse(_gridCart.Rows[i].Cells["colQty"].Value?.ToString(), out int q) && q > 0)
                    _cart[i].Quantity = q;
            }

            _gridCart.Rows.Clear();
            foreach (var item in _cart)
                _gridCart.Rows.Add(item.ProductName, UiHelpers.Money(item.Price), item.Quantity, UiHelpers.Money(item.Total));

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

        // ─────────────────────────────────────────────────
        // العميل
        // ─────────────────────────────────────────────────
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

        // ─────────────────────────────────────────────────
        // الدفع / التعليق
        // ─────────────────────────────────────────────────
        private async Task CheckoutAsync(bool suspend)
        {
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
        }

        // ─────────────────────────────────────────────────
        // طباعة الإيصال الحراري تلقائيًا (حسب الإعدادات)
        // ─────────────────────────────────────────────────
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

                Common.DTOs.ViewModel.InvoicePrintVM? invoice = null;
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

        // ─────────────────────────────────────────────────
        // المرتجع / النقد / إغلاق الجلسة
        // ─────────────────────────────────────────────────
        private void ShowReturnForm()
        {
            using var form = new ReturnForm();
            form.ShowDialog(this);
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

        // ─────────────────────────────────────────────────
        // الفواتير المعلقة والخروج
        // ─────────────────────────────────────────────────
        private void ShowSuspendedInvoices()
        {
            using var form = new SuspendedInvoicesForm();
            form.ShowDialog(this);
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
