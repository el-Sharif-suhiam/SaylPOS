using System.Globalization;
using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// فاتورة شراء جديدة: مورد + بنود (منتج مخزني، دفعة، كمية، تكلفة، انتهاء اختياري)
    /// + خصم وضريبة + دفع كلي/جزئي. تُنشئ الفاتورة عبر clsPurchaseService داخل معاملة واحدة
    /// فتُحدَّث الدفعات والمخزون وحركة المخزون وتكلفة المنتج ورصيد المورد تلقائيًا في الخدمة.
    /// </summary>
    public class PurchaseForm : DialogForm
    {
        private TextBox _txtSupplierSearch = null!;
        private ListBox _lstSuppliers = null!;
        private List<Supplier> _suppliers = new();

        private TextBox _txtProductSearch = null!;
        private ListBox _lstProducts = null!;
        private List<DtoProduct> _products = new();
        private Button _btnAddProduct = null!;

        private DataGridView _grid = null!;
        private TextBox _txtDiscount = null!;
        private TextBox _txtTaxPercent = null!;
        private TextBox _txtPaid = null!;
        private ComboBox _cmbMethod = null!;
        private TextBox _txtNotes = null!;

        private Label _lblSubtotal = null!;
        private Label _lblNet = null!;
        private Label _lblRemaining = null!;
        private Button _btnSave = null!;

        private decimal _net;
        private bool _saved;
        private NumericUpDown _nudQty = null!;
        public PurchaseForm()
        {
            Text = "فاتورة شراء جديدة";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 760);
            MinimumSize = new Size(880, 660);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();
            Load += async (_, _) =>
            {
                await LoadSuppliersAsync("");
                await LoadProductsAsync("");
                await LoadDefaultTaxAsync();
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «الجذر المجزّأ» بأربعة صفوف (راجع PayrollForm):
        //
        //   ┌──────────────────────────────────────┐
        //   │ صف0 (Absolute 60): الترويسة + زر ×   │
        //   ├──────────────────┬───────────────────┤
        //   │ صف1 (Absolute 170): المورد | المنتجات │ ← عمودان 50/50
        //   ├──────────────────┴───────────────────┤
        //   │ صف2 (Percent 100): جدول البنود        │ ← يتمدد مع النافذة
        //   ├──────────────────┬───────────────────┤
        //   │ صف3 (Absolute 290): دفع | إجماليات    │ ← عمودان 55/45
        //   └──────────────────┴───────────────────┘
        //
        // أبرز ما هنا — نمط «ماستر-ديتيل» المصغّر:
        // أعلى: قائمتا بحث (مورد يمينًا، منتج يسارًا) — تختار منهما،
        // وسط: جدول البنود يتراكم، أسفل: المبالغ تتحدث حيًا (RecalcTotals).
        //
        // ⚠️ علامة * في «المورد *» و«رقم الدفعة *» = إلزامي (لا حفظ بدونه).
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── الحاوية الرئيسية: عمود واحد + 4 صفوف (المقاسات فوق) ──
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));    // الترويسة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));   // الموردين والمنتجات
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // الجدول (يتمدد)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 290));   // الإجماليات والأزرار

            // ── الترويسة: عنوان وسط + زر × دائري أعلى اليسار ──
            // الزر الدائري فوق Label بـ Dock=Fill — Bounds يدوي داخل اللوحة.
            // Anchor=Top|Left: يبقى بالزاوية عند تغيير حجم النافذة (نافذة
            // قابلة للتحجيم هنا — Anchor يحافظ على موقع الزر النسبي).
            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            var lblTitle = new Label
            {
                Text = "فاتورة شراء جديدة",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };
            var btnCloseTop = AppTheme.MakeCircleButton("×", 44,
                back: AppTheme.BorderColor, hoverBack: AppTheme.Danger,
                fore: AppTheme.TextPrimary, parentBack: AppTheme.CardBg, fontSize: 15f);
            btnCloseTop.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnCloseTop.Bounds = new Rectangle(16, 8, 44, 44);
            btnCloseTop.Click += (_, _) => Close();
            header.Controls.Add(lblTitle);
            header.Controls.Add(btnCloseTop);

            // ── منطقة الاختيار: عمودان 50/50 — جدول داخلي داخل صف الجذر ──
            // مع RightToLeft.Yes: العمود 0 يظهر يمينًا — لذلك نضع المورد
            // (الأساس في الشراء) في العمود 1... انظر Controls.Add أسفل:
            // المورد أُضيف للعمود 1 (يظهر يمينًا)، المنتجات للعمود 0 (يسارًا).
            var selectionPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(24, 0, 24, 0)
            };
            selectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            selectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // المورد (يمين): تسمية + صف بحث أفقي + قائمة تملأ الباقي
            var supplierPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
            var lblSupplier = MakeCaption("المورد *", new Rectangle(0, 0, 200, 22));
            // FlowLayoutPanel بـ FlowDirection=RightToLeft: صف أفقي يرتص
            // من اليمين — أول Controls.Add يظهر أقصى اليمين.
            // WrapContents=false: لا التفاف للسطر التالي مهما ضاقت المساحة.
            // Height=40: ارتفاع الصف ثابت (زر البحث 32 + هوامش).
            var supplierSearchRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Height = 40
            };
            // Enter داخل مربع البحث = زر البحث (SuppressKeyPress يمنع «بيب»)
            _txtSupplierSearch = new TextBox { Width = 180, Height = 30 };
            _txtSupplierSearch.PlaceholderText = "بحث...";
            _txtSupplierSearch.KeyDown += async (_, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    await LoadSuppliersAsync(_txtSupplierSearch.Text.Trim());
                }
            };
            var btnSupplierSearch = AppTheme.MakeButton("بحث", AppTheme.PrimaryLight,
                foreColor: AppTheme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 32);
            btnSupplierSearch.Width = 80;
            btnSupplierSearch.Click += async (_, _) => await LoadSuppliersAsync(_txtSupplierSearch.Text.Trim());
            // الترتيب داخل Flow (RTL): أول Add أقصى اليمين — الزر يمين المربع
            supplierSearchRow.Controls.Add(btnSupplierSearch);
            supplierSearchRow.Controls.Add(_txtSupplierSearch);

            // ListBox: قائمة الموردين — Dock=Fill تملأ ما تبقى تحت صف البحث.
            // IntegralHeight=false: لا «يقفز» الارتفاع لعرض صفوف كاملة فقط.
            _lstSuppliers = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            // ⚠️ ترتيب الإرساء داخل supplierPanel (آخر Add يُرسّى أولًا):
            // 1) القائمة Fill أولًا — ترسو أخيرة وتملأ الباقي،
            // 2) صف البحث Top — يأخذ حافته قبلها،
            // 3) التسمية Bounds يدوية فلا تشارك بالتراصف.
            supplierPanel.Controls.Add(_lstSuppliers);
            supplierPanel.Controls.Add(supplierSearchRow);
            supplierPanel.Controls.Add(lblSupplier);

            // المنتجات (يسار): نفس بنية المورد تمامًا + كمية وزر إضافة
            var productPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
            var lblProduct = MakeCaption("المنتج المخزني (بحث بالاسم أو الباركود)", new Rectangle(0, 0, 200, 22));
            var productSearchRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Height = 40
            };
            _txtProductSearch = new TextBox { Width = 180, Height = 30 };
            _txtProductSearch.PlaceholderText = "بحث...";
            _txtProductSearch.KeyDown += async (_, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    await LoadProductsAsync(_txtProductSearch.Text.Trim());
                }
            };
            var btnProductSearch = AppTheme.MakeButton("بحث", AppTheme.PrimaryLight,
                foreColor: AppTheme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 32);
            btnProductSearch.Width = 80;
            btnProductSearch.Click += async (_, _) => await LoadProductsAsync(_txtProductSearch.Text.Trim());

            // زر الإضافة معطّل حتى يُختار منتج (SelectedIndexChanged يشغّله)
            _btnAddProduct = AppTheme.MakeButton("إضافة للفاتورة", AppTheme.Accent,
                hoverColor: AppTheme.AccentDark, height: 32);
            _btnAddProduct.Width = 130;
            _btnAddProduct.Enabled = false;
            _btnAddProduct.Click += (_, _) => AddSelectedItem();

            var lblQty = new Label
            {
                Text = "الكمية:",
                AutoSize = true,
                ForeColor = AppTheme.TextMuted,
                Margin = new Padding(4, 6, 2, 0) // هامش صغير يحاذي الارتفاع داخل صف 40
            };

            // NumericUpDown بدل TextBox للكمية: أسهم + حد أدنى/أقصى مدمج —
            // Minimum=1 يمنع الصفر والسالب بلا تحقق يدوي.
            _nudQty = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10000,
                Value = 1,
                Width = 60,
                Height = 30,
                Font = AppTheme.FontBody,
                Margin = new Padding(2, 2, 4, 0)
            };

            // ترتيب صف منتجات البحث (RTL): بحث ← المربع ← الكمية ← الرقم ← إضافة
            productSearchRow.Controls.Add(btnProductSearch);
            productSearchRow.Controls.Add(_txtProductSearch);
            productSearchRow.Controls.Add(lblQty);
            productSearchRow.Controls.Add(_nudQty);
            productSearchRow.Controls.Add(_btnAddProduct);

            _lstProducts = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            // نقر مزدوج على منتج = إضافته (اختصار الماسح الذكي للمستخدم)
            _lstProducts.DoubleClick += (_, _) => AddSelectedItem();
            // تفعيل زر الإضافة فقط عند تحديد صالح — حرسة واجهة قبل الحفظ
            _lstProducts.SelectedIndexChanged += (_, _) =>
                _btnAddProduct.Enabled = _lstProducts.SelectedIndex >= 0
                                      && _lstProducts.SelectedIndex < _products.Count;

            productPanel.Controls.Add(_lstProducts);
            productPanel.Controls.Add(productSearchRow);
            productPanel.Controls.Add(lblProduct);

            // الجذر مقلوب بسبب RTL: العمود 1 = يمين (المورد)، العمود 0 = يسار
            selectionPanel.Controls.Add(supplierPanel, 1, 0);
            selectionPanel.Controls.Add(productPanel, 0, 0);

            // ── جدول البنود: المرن — يتراكم فيه ما أُضيف من القائمتين ──
            // Margin أفقي 24: يباعد الجدول عن حواف النافذة ويحاذي منطقة الاختيار.
            // ReadOnly=false هنا فقط (خلاف StyleGrid الافتراضي): الدفعة/الكمية/
            // التكلفة/الانتهاء تُحرَّر داخل الجدول مباشرة — أما colProduct
            // وcolTotal وcolDelete فأعمدة عرض فقط (ReadOnly=true لكل منها تحت).
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(24, 0, 24, 0)
            };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add("colProduct", "المنتج");
            _grid.Columns.Add("colBatch", "رقم الدفعة *");
            _grid.Columns.Add("colQty", "الكمية");
            _grid.Columns.Add("colCost", "التكلفة");
            _grid.Columns.Add("colExpiry", "الانتهاء yyyy-mm-dd");
            _grid.Columns.Add("colTotal", "الإجمالي");
            _grid.Columns.Add("colDelete", "");
            _grid.Columns["colProduct"].ReadOnly = true;
            _grid.Columns["colProduct"].FillWeight = 30;
            _grid.Columns["colBatch"].FillWeight = 16;
            _grid.Columns["colQty"].FillWeight = 9;
            _grid.Columns["colQty"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colCost"].FillWeight = 11;
            _grid.Columns["colCost"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colExpiry"].FillWeight = 13;
            _grid.Columns["colExpiry"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colTotal"].ReadOnly = true;
            _grid.Columns["colTotal"].FillWeight = 12;
            _grid.Columns["colDelete"].ReadOnly = true;
            _grid.Columns["colDelete"].FillWeight = 9;
            // CellEndEdit: بعد انتهاء تحرير أي خلية تُعاد الحسابات — إجمالي
            // السطر وكل الملخصات أسفل النافذة يتحدثان فورًا.
            _grid.CellEndEdit += (_, _) => RefreshRowTotals();
            // CellClick مع فلترة عمود: نقر «حذف» فقط يحذف السطر —
            // args.ColumnIndex نقارن بفهرس العمود الاسمي لا برقم ثابت.
            _grid.CellClick += (_, args) =>
            {
                if (args.RowIndex < 0 || args.ColumnIndex != _grid.Columns["colDelete"].Index) return;
                if (args.RowIndex < _grid.Rows.Count && !_grid.Rows[args.RowIndex].IsNewRow)
                    _grid.Rows.RemoveAt(args.RowIndex);
                RecalcTotals();
            };

            // ── الصف السفلي: عمودان — دفع (يمين) | إجماليات (يسار) ──
            var bottomPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(24, 8, 24, 12)
            };
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            // الجهة اليمنى: 4 صفوف × عمودان (تسمية | إدخال) — خصم/ضريبة/مدفوع/طريقة
            var paymentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(0)
            };
            paymentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            paymentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var lblDiscount = MakeCaption("الخصم", new Rectangle(0, 0, 100, 22));
            _txtDiscount = new TextBox
            {
                Dock = DockStyle.Fill,
                TextAlign = HorizontalAlignment.Center,
                Text = "0",
                Margin = new Padding(6)
            };
            // TextChanged = إعادة حساب حية: كل تعديل خصم يحدّث الصافي فورًا
            _txtDiscount.TextChanged += (_, _) => RecalcTotals();

            var lblTax = MakeCaption("الضريبة %", new Rectangle(0, 0, 100, 22));
            _txtTaxPercent = new TextBox
            {
                Dock = DockStyle.Fill,
                TextAlign = HorizontalAlignment.Center,
                Text = "0",
                Margin = new Padding(6)
            };
            _txtTaxPercent.TextChanged += (_, _) => RecalcTotals();

            var lblPaid = MakeCaption("المدفوع الآن (0 = آجل)", new Rectangle(0, 0, 100, 22));
            _txtPaid = new TextBox
            {
                Dock = DockStyle.Fill,
                TextAlign = HorizontalAlignment.Center,
                Text = "0",
                Margin = new Padding(6)
            };
            _txtPaid.TextChanged += (_, _) => UpdateRemaining();

            var lblMethod = MakeCaption("طريقة الدفع", new Rectangle(0, 0, 100, 22));
            _cmbMethod = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(6)
            };
            _cmbMethod.Items.AddRange(new object[] { "نقدي", "بطاقة" });
            _cmbMethod.SelectedIndex = 0;

            // عمود TLP بـ RTL: العمود 0 يمين (التسميات) والعمود 1 يسار (الإدخال)
            paymentPanel.Controls.Add(lblDiscount, 0, 0);
            paymentPanel.Controls.Add(_txtDiscount, 1, 0);
            paymentPanel.Controls.Add(lblTax, 0, 1);
            paymentPanel.Controls.Add(_txtTaxPercent, 1, 1);
            paymentPanel.Controls.Add(lblPaid, 0, 2);
            paymentPanel.Controls.Add(_txtPaid, 1, 2);
            paymentPanel.Controls.Add(lblMethod, 0, 3);
            paymentPanel.Controls.Add(_cmbMethod, 1, 3);

            // الجهة اليسرى: الإجماليات (تتحدث حيًا) + ملاحظات + صف أزرار
            var totalsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(6)
            };
            // ثلاثة Labels بـ Dock=Top داخل عمود واحد: تتراصف فوق بعضها
            // بترتيب الإضافة (المجموع ثم الصافي الأبرز ثم المتبقي).
            _lblSubtotal = new Label
            {
                Text = "المجموع: 0.00",
                Dock = DockStyle.Top,
                ForeColor = AppTheme.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblNet = new Label
            {
                Text = "الصافي: 0.00",
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 12.5F, FontStyle.Bold), // الأبرز — رقم القرار
                ForeColor = AppTheme.PrimaryDark,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblRemaining = new Label
            {
                Text = "المتبقي على المورد: 0.00",
                Dock = DockStyle.Top,
                ForeColor = AppTheme.Warning,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var lblNotes = MakeCaption("ملاحظات", new Rectangle(0, 0, 100, 22));
            _txtNotes = new TextBox
            {
                Dock = DockStyle.Fill,     // يملأ المساحة بين التسمية وصف الأزرار
                Multiline = true
            };

            // صف الأزرار: FlowPanel بـ Dock=Bottom — يُرسّى أولًا (آخر Add)
            // فيحجز قاع العمود، والملاحظات Fill فوقه.
            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Height = 60
            };
            _btnSave = AppTheme.MakeButton("حفظ الفاتورة", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            _btnSave.Width = 180;
            _btnSave.Height = 50;
            _btnSave.Click += async (_, _) => await SaveAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Width = 160;
            btnClose.Height = 50;
            // DialogResult=Cancel: Esc يغلق — رغم أن زر × أعلى موجود أيضًا
            btnClose.DialogResult = DialogResult.Cancel;

            // RTL Flow: أول Add (حفظ) أقصى اليمين
            btnRow.Controls.Add(_btnSave);
            btnRow.Controls.Add(btnClose);

            // ⚠️ عمود TLP بتراصف رأسي: التسمية/الأزرار بـ Top/Bottom تُرسّى
            // حسب ترتيب الإضافة المعكوس — Dock=Fill (الملاحظات) يُضاف قبلها
            // هنا كي يبقى آخر من يرسو بعد حجز Top/Bottom.
            totalsPanel.Controls.Add(_lblSubtotal);
            totalsPanel.Controls.Add(_lblNet);
            totalsPanel.Controls.Add(_lblRemaining);
            totalsPanel.Controls.Add(lblNotes);
            totalsPanel.Controls.Add(_txtNotes);
            totalsPanel.Controls.Add(btnRow);

            // bottomPanel بـ RTL: العمود 1 (الدفع) يمين، العمود 0 (الإجماليات) يسار
            bottomPanel.Controls.Add(paymentPanel, 1, 0);
            bottomPanel.Controls.Add(totalsPanel, 0, 0);

            // ── تجميع صفوف الجذر — الترتيب يطابق RowStyles أعلاه ──
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(selectionPanel, 0, 1);
            root.Controls.Add(_grid, 0, 2);
            root.Controls.Add(bottomPanel, 0, 3);

            Controls.Add(root);
            CancelButton = btnClose;
        }
        // 📖 MakeCaption: تسمية رمادية موحدة (نص، Bounds، محاذاة يمين) —
        // Bounds هنا تُتجاهل داخل TLP (TLP يوزع بنفسه) — بقيت للتوافق.
        private static Label MakeCaption(string text, Rectangle bounds)
        {
            return new Label
            {
                Text = text,
                Bounds = bounds,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
        }

        // ── التحميل ──
        private async Task LoadSuppliersAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var supplierService = scope.ServiceProvider.GetRequiredService<clsSupplierService>();
                _suppliers = await supplierService.GetSupplierListAsync(search);

                _lstSuppliers.Items.Clear();
                foreach (var s in _suppliers)
                    _lstSuppliers.Items.Add($"{s.Party.Name}  —  رصيد: {UiHelpers.Money(s.Balance ?? 0)}");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الموردين.");
            }
        }

        private async Task LoadProductsAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                _products = await productService.GetProductsListAsync(search, type: "stock");

                _lstProducts.Items.Clear();
                foreach (var p in _products)
                    _lstProducts.Items.Add($"{p.Name}  [{p.Barcode ?? "بدون باركود"}]");

                if (_products.Count == 0)
                    _lstProducts.Items.Add("لا توجد منتجات مطابقة.");
                else if (_products.Count == 1)
                    _lstProducts.SelectedIndex = 0;
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل المنتجات.");
            }
        }

        private async Task LoadDefaultTaxAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                decimal tax = await settings.GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0);
                _txtTaxPercent.Text = tax.ToString("0.##");
            }
            catch
            {
                // الضريبة الافتراضية ليست حرجة — نبقيها صفر عند الفشل
            }
        }

        // ── إضافة بند ──
        //private void AddSelectedItem()
        //{
        //    if (_lstProducts.SelectedIndex < 0 || _lstProducts.SelectedIndex >= _products.Count)
        //    {
        //        UiHelpers.ShowInfo(this, "اختر منتجًا من القائمة أولًا (ابحث ثم انقر نقرًا مزدوجًا على المنتج أو استخدم زر الإضافة).");
        //        return;
        //    }

        //    var p = _products[_lstProducts.SelectedIndex];
        //    int rowIndex = _grid.Rows.Add(p.Name, string.Empty, "1",
        //        p.CostPrice.ToString("0.##"), string.Empty, string.Empty, "حذف");
        //    _grid.Rows[rowIndex].Tag = p.ProductId;
        //    _grid.Rows[rowIndex].Cells["colDelete"].Style.ForeColor = AppTheme.Danger;
        //    _grid.CurrentCell = _grid.Rows[rowIndex].Cells["colBatch"];
        //    RecalcTotals();
        //}


        private void AddSelectedItem()
        {
            if (_lstProducts.SelectedIndex < 0 || _lstProducts.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowInfo(this, "اختر منتجًا من القائمة أولًا.");
                return;
            }

            var p = _products[_lstProducts.SelectedIndex];
            int productId = p.ProductId;
            int requestedQty = (int)_nudQty.Value;   // الكمية المطلوبة

            // 1) ابحث إذا كان المنتج موجودًا مسبقًا
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                if (row.Tag is int id && id == productId)
                {
                    int currentQty = (int)ParseDecimal(row.Cells["colQty"].Value);
                    row.Cells["colQty"].Value = (currentQty + requestedQty).ToString();
                    _grid.CurrentCell = row.Cells["colQty"];
                    RecalcTotals();
                    return;
                }
            }

            // 2) لم يوجد، أضف سطرًا جديدًا بالكمية المطلوبة
            int rowIndex = _grid.Rows.Add(
                p.Name,
                string.Empty,
                requestedQty.ToString(),
                p.CostPrice.ToString("0.##"),
                string.Empty,
                string.Empty,
                "حذف");

            _grid.Rows[rowIndex].Tag = p.ProductId;
            _grid.Rows[rowIndex].Cells["colDelete"].Style.ForeColor = AppTheme.Danger;
            _grid.CurrentCell = _grid.Rows[rowIndex].Cells["colBatch"];
            RecalcTotals();
        }
        // ── الحسابات ──
        private void RefreshRowTotals()
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                decimal qty = ParseDecimal(row.Cells["colQty"].Value);
                decimal cost = ParseDecimal(row.Cells["colCost"].Value);
                row.Cells["colTotal"].Value = UiHelpers.Money(qty * cost);
            }
            RecalcTotals();
        }

        private void RecalcTotals()
        {
            decimal total = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                total += ParseDecimal(row.Cells["colQty"].Value) * ParseDecimal(row.Cells["colCost"].Value);
            }

            decimal discount = Math.Max(0, ParseDecimal(_txtDiscount.Text));
            decimal taxPercent = Math.Max(0, ParseDecimal(_txtTaxPercent.Text));
            decimal afterDiscount = total - discount;
            if (afterDiscount < 0) afterDiscount = 0;
            decimal tax = taxPercent > 0 ? afterDiscount * taxPercent / 100 : 0;
            _net = afterDiscount + tax;

            _lblSubtotal.Text = "المجموع: " + UiHelpers.Money(total);
            _lblNet.Text = "الصافي: " + UiHelpers.Money(_net);
            UpdateRemaining();
        }

        private void UpdateRemaining()
        {
            decimal paid = Math.Max(0, ParseDecimal(_txtPaid.Text));
            decimal remaining = _net - paid;
            if (remaining < 0) remaining = 0;
            _lblRemaining.Text = "المتبقي على المورد: " + UiHelpers.Money(remaining);
        }

        private static decimal ParseDecimal(object? value)
        {
            return decimal.TryParse(value?.ToString()?.Trim(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out decimal d) ? d : 0;
        }

        private static bool TryParseExpiry(string? text, out DateOnly? value, out string error)
        {
            value = null;
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (DateOnly.TryParseExact(text.Trim(),
                    new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                value = parsed;
                return true;
            }

            error = text.Trim();
            return false;
        }

        // ── الحفظ ──
        private async Task SaveAsync()
        {
            if (_lstSuppliers.SelectedIndex < 0 || _lstSuppliers.SelectedIndex >= _suppliers.Count)
            {
                UiHelpers.ShowError(this, "اختر المورد من القائمة.");
                return;
            }

            if (SessionState.CurrentUser == null)
            {
                UiHelpers.ShowError(this, "لا يوجد مستخدم مسجل.");
                return;
            }

            var items = new List<DtoPurchaseItem>();
            decimal subtotal = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow || row.Tag == null) continue;

                string productName = row.Cells["colProduct"].Value?.ToString() ?? "?";
                string batch = row.Cells["colBatch"].Value?.ToString()?.Trim() ?? "";

                if (batch.Length == 0)
                {
                    UiHelpers.ShowError(this, $"رقم الدفعة مطلوب للمنتج: {productName}");
                    _grid.CurrentCell = row.Cells["colBatch"];
                    return;
                }

                decimal qtyRaw = ParseDecimal(row.Cells["colQty"].Value);
                int qty = (int)qtyRaw;
                if (qtyRaw <= 0 || qtyRaw != qty)
                {
                    UiHelpers.ShowError(this, $"الكمية يجب أن تكون عددًا صحيحًا أكبر من صفر للمنتج: {productName}");
                    _grid.CurrentCell = row.Cells["colQty"];
                    return;
                }

                decimal cost = ParseDecimal(row.Cells["colCost"].Value);
                if (cost < 0)
                {
                    UiHelpers.ShowError(this, $"التكلفة يجب أن تكون صفرًا أو أكثر للمنتج: {productName}");
                    _grid.CurrentCell = row.Cells["colCost"];
                    return;
                }

                if (!TryParseExpiry(row.Cells["colExpiry"].Value?.ToString(), out DateOnly? expiry, out string bad))
                {
                    UiHelpers.ShowError(this, $"تاريخ الانتهاء غير صالح ({bad}) للمنتج: {productName}\nاستخدم الصيغة yyyy-mm-dd");
                    _grid.CurrentCell = row.Cells["colExpiry"];
                    return;
                }

                subtotal += qty * cost;
                items.Add(new DtoPurchaseItem
                {
                    ProductId = (int)row.Tag,
                    BatchNumber = batch,
                    Quantity = qty,
                    UnitCost = cost,
                    ExpiryDate = expiry
                });
            }

            if (items.Count == 0)
            {
                UiHelpers.ShowError(this, "أضف بندًا واحدًا على الأقل إلى الفاتورة.");
                return;
            }

            decimal discount = Math.Max(0, ParseDecimal(_txtDiscount.Text));
            if (discount > subtotal)
            {
                UiHelpers.ShowError(this, $"الخصم يتجاوز مجموع الفاتورة ({UiHelpers.Money(subtotal)}).");
                _txtDiscount.Focus();
                _txtDiscount.SelectAll();
                return;
            }

            decimal taxPercent = Math.Max(0, ParseDecimal(_txtTaxPercent.Text));
            decimal afterDiscount = subtotal - discount;
            decimal tax = taxPercent > 0 ? afterDiscount * taxPercent / 100 : 0;
            decimal net = afterDiscount + tax;

            decimal paid = Math.Max(0, ParseDecimal(_txtPaid.Text));
            if (paid > net)
            {
                UiHelpers.ShowError(this, $"المدفوع الآن أكبر من صافي الفاتورة ({UiHelpers.Money(net)}).");
                _txtPaid.Focus();
                _txtPaid.SelectAll();
                return;
            }

            var request = new DtoPurchaseRequest
            {
                SupplierId = _suppliers[_lstSuppliers.SelectedIndex].PartyId,
                UserId = SessionState.CurrentUser.UserId,
                Items = items,
                DiscountAmount = discount,
                TaxPercent = taxPercent,
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
                Payment = paid > 0
                    ? new DtoPaymentInfo
                    {
                        Amount = paid,
                        PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                        Method = _cmbMethod.SelectedIndex == 1 ? "card" : "cash"
                    }
                    : null
            };

            _btnSave.Enabled = false;
            try
            {
                Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var purchaseService = scope.ServiceProvider.GetRequiredService<clsPurchaseService>();
                    result = await purchaseService.CreatePurchaseInvoiceAsync(request);
                }

                UiHelpers.ShowResult(this, result,
                    $"تم إنشاء فاتورة الشراء رقم {result.Data} بنجاح.");

                if (result.IsSuccess)
                {
                    _saved = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر الاتصال بقاعدة البيانات.");
            }
            finally
            {
                _btnSave.Enabled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // إغلاق عبر X أو Esc بعد نجاح الحفظ لا يجب أن يبدو إلغاءً
            if (_saved) DialogResult = DialogResult.OK;
            base.OnFormClosing(e);
        }
    }
}
