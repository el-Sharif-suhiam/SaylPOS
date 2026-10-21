using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة «ملصقات باركود» من شاشة الكاشير: بحث حي بالاسم أو الباركود،
    /// تحديد المنتج والكمية والمقاس والطابعة واللغة (ZPL / ESC-POS) —
    /// المحتوى: الباركود نفسه + رقمه تحته فقط. الطباعة RAW عبر Task.Run
    /// فلا تجميد واجهة. آخر اختيارات الطابعة/اللغة/المقاس تُحفظ إعدادات.
    /// </summary>
    public class LabelPrintForm : DialogForm
    {
        private TextBox _txtSearch = null!;
        private ListBox _lst = null!;
        private NumericUpDown _numQty = null!;
        private ComboBox _cmbSize = null!;
        private ComboBox _cmbPrinter = null!;
        private ComboBox _cmbLang = null!;
        private Button _btnPrint = null!;

        private List<DtoProduct> _products = new();
        private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
        private int _searchSeq;

        private const string KeyPrinter = "LabelPrinter";
        private const string KeyLanguage = "LabelPrinterLanguage";
        private const string KeySize = "LabelPrinterSize";

        public LabelPrintForm()
        {
            Text = AppLang.T("Label.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 620);
            MinimumSize = new Size(520, 500);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();

            _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchAsync(); };
            Load += async (_, _) => { await LoadPersistedAsync(); await SearchAsync(); };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));    // البحث
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // القائمة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));   // الخيارات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));    // الأزرار

            // ── البحث ──
            var searchRow = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg };
            var lblSearch = new Label
            {
                Text = AppLang.T("Label.Search.Label"),
                AutoSize = true,
                ForeColor = AppTheme.TextPrimary,
                Anchor = AnchorStyles.Right
            };
            _txtSearch = new TextBox
            {
                Font = new Font("Segoe UI", 11F),
                Width = 320,
                Anchor = AnchorStyles.Right
            };
            searchRow.Controls.Add(_txtSearch);
            searchRow.Controls.Add(lblSearch);
            searchRow.Resize += (_, _) => LayoutCenteredRow(searchRow, lblSearch, _txtSearch, offset: 12);
            LayoutCenteredRow(searchRow, lblSearch, _txtSearch, offset: 12);
            _txtSearch.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };

            // ── القائمة (List: أداء ممتاز للنتائج) ──
            _lst = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 36,
                IntegralHeight = false,
                BackColor = AppTheme.CardBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10F)
            };
            _lst.DrawItem += DrawProductItem;
            _lst.DoubleClick += async (_, _) => await PrintSelectedAsync();

            // ── الخيارات ──
            var options = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                BackColor = AppTheme.Bg
            };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _numQty = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = 10, Anchor = AnchorStyles.Left, Width = 130 };
            var optQty = MakeOptionRow(AppLang.T("Label.Options.Qty"), _numQty);

            _cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Anchor = AnchorStyles.Left };
            foreach (var s in LabelPrinter.PresetSizes) _cmbSize.Items.Add(s.Caption);
            _cmbSize.SelectedIndex = Math.Min(1, _cmbSize.Items.Count - 1); // الافتراضي 40×25 (حد 25مم)
            var optSize = MakeOptionRow(AppLang.T("Label.Options.Size"), _cmbSize);

            _cmbPrinter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 240 };
            try
            {
                foreach (var p in LabelPrinter.GetInstalledPrinters())
                if (IsRealPrinter(p)) _cmbPrinter.Items.Add(p);
            }
            catch { /* تعداد فاشل = قائمة فارغة */ }
            var optPrinter = MakeOptionRow(AppLang.T("Label.Options.Printer"), _cmbPrinter);

            _cmbLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left, Width = 150 };
            _cmbLang.Items.Add(AppLang.T("Label.Lang.Zpl"));
            _cmbLang.Items.Add(AppLang.T("Label.Lang.EscPos"));
            _cmbLang.SelectedIndex = 0;
            var optLang = MakeOptionRow(AppLang.T("Label.Options.Language"), _cmbLang);

            options.Controls.Add(optQty, 0, 0);
            options.Controls.Add(optSize, 1, 0);
            options.Controls.Add(optPrinter, 0, 1);
            options.Controls.Add(optLang, 1, 1);

            // ── الأزرار ──
            var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(2, 6, 2, 2) };
            _btnPrint = AppTheme.MakeButton(AppLang.T("Label.Button.Print"), AppTheme.Primary,
                hoverColor: AppTheme.PrimaryDark, height: 48);
            _btnPrint.Dock = DockStyle.Left;
            _btnPrint.Width = 180;
            _btnPrint.Click += async (_, _) => await PrintSelectedAsync();

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 110;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(btnClose);
            bottomBar.Controls.Add(_btnPrint);

            root.Controls.Add(searchRow, 0, 0);
            root.Controls.Add(_lst, 0, 1);
            root.Controls.Add(options, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);

            Controls.Add(root);
        }

        private static Panel MakeOptionRow(string caption, Control editor)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(8, 12, 8, 4) };
            var lbl = new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = AppTheme.TextMuted,
                Anchor = AnchorStyles.Right
            };
            p.Controls.Add(editor);
            p.Controls.Add(lbl);
            p.Resize += (_, _) => LayoutOptionRow(p, lbl, editor);
            LayoutOptionRow(p, lbl, editor);
            return p;
        }

        private static void LayoutOptionRow(Panel p, Label lbl, Control editor)
        {
            int h = p.ClientSize.Height;
            lbl.SetBounds(lbl.PreferredWidth + 16, (h - lbl.Height) / 2, lbl.PreferredWidth + 12, lbl.Height);
            editor.SetBounds(Math.Max(0, lbl.Left - editor.Width - 6), (h - editor.Height) / 2, editor.Width, editor.Height);
        }

        private static void LayoutCenteredRow(Panel row, Label lblCaption, Control editor, int offset)
        {
            int h = row.ClientSize.Height;
            lblCaption.SetBounds(row.ClientSize.Width - offset - lblCaption.PreferredWidth - 4, (h - lblCaption.Height) / 2,
                lblCaption.PreferredWidth + 10, lblCaption.Height);
            editor.SetBounds(Math.Max(offset, lblCaption.Left - editor.Width - 8), (h - editor.Height) / 2,
                editor.Width, editor.Height);
        }

        private void DrawProductItem(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _products.Count) return;

            var p = _products[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var color = selected ? AppTheme.Primary : AppTheme.TextPrimary;
            string text = $"{p.Name}   |   {p.Barcode ?? "-"}   |   {UiHelpers.Money(p.BasePrice)}";

            TextRenderer.DrawText(e.Graphics, text, e.Font!, e.Bounds, color,
                TextFormatFlags.RightToLeft | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            e.DrawFocusRectangle();
        }

        // ───────────────── التحميل والبحث ─────────────────

        private async Task LoadPersistedAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                string printer = await settings.GetAsync(KeyPrinter);
                if (!string.IsNullOrWhiteSpace(printer) && _cmbPrinter.Items.Contains(printer))
                    _cmbPrinter.SelectedItem = printer;
                else if (_cmbPrinter.Items.Count > 0)
                    _cmbPrinter.SelectedIndex = 0;

                string lang = await settings.GetAsync(KeyLanguage);
                _cmbLang.SelectedIndex = lang == "escpos" ? 1 : 0;

                string size = await settings.GetAsync(KeySize);
                int idx = LabelPrinter.PresetSizes.ToList().FindIndex(s => s.Caption == size);
                _cmbSize.SelectedIndex = idx >= 0 ? idx : Math.Min(1, _cmbSize.Items.Count - 1);
            }
            catch
            {
                // فشل الإعدادات المحفوظة = الافتراضي — النافذة تعمل دائمًا
                if (_cmbPrinter.Items.Count > 0 && _cmbPrinter.SelectedIndex < 0)
                    _cmbPrinter.SelectedIndex = 0;
            }
        }

        private async Task SearchAsync()
        {
            int seq = ++_searchSeq;
            try
            {
                using var scope = Program.Services.CreateScope();
                var products = scope.ServiceProvider.GetRequiredService<clsProductService>();
                var list = await products.GetProductsListAsync(_txtSearch.Text?.Trim() ?? "", take: 60);
                if (seq != _searchSeq) return; // نتيجة قديمة أضافت بعد بحث أحدث — تجاهلها

                _products = list;
                _lst.BeginUpdate();
                _lst.Items.Clear();
                foreach (var p in _products) _lst.Items.Add("");
                _lst.EndUpdate();
            }
            catch
            {
                _products.Clear();
                _lst.Items.Clear();
            }
        }

        // ───────────────── طباعة ─────────────────

        /// <summary>الطابعة الاسمها الحقيقي (فوق التسمي المعروض) — Print To PDF/FAX استبعاد.</summary>
        private static bool IsRealPrinter(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            !name.Contains("PDF", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Fax", StringComparison.OrdinalIgnoreCase);

        private async Task PrintSelectedAsync()
        {
            if (_lst.SelectedIndex < 0 || _lst.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Label.Msg.PickProduct"));
                return;
            }

            var product = _products[_lst.SelectedIndex];
            if (string.IsNullOrWhiteSpace(product.Barcode))
            {
                UiHelpers.ShowError(this, AppLang.T("Label.Msg.NoBarcode", product.Name));
                return;
            }

            if (_cmbPrinter.SelectedIndex < 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Label.Msg.NoPrinter"));
                return;
            }

            var size = LabelPrinter.PresetSizes[_cmbSize.SelectedIndex];
            var lang = _cmbLang.SelectedIndex == 1 ? LabelPrinterLanguage.EscPos : LabelPrinterLanguage.Zpl;
            int qty = (int)_numQty.Value;
            string printer = (string)_cmbPrinter.SelectedItem!;

            var (ok, message) = await LabelPrinter.PrintProductBarcodeAsync(product.Barcode!, qty, size, printer, lang);

            // حفظ آخر الاختيارات — نجحت الطباعة أو بها باركود فعلي (إعداد تجربة الطابعة)
            await PersistChoicesAsync(printer, lang, size);

            if (ok) UiHelpers.ShowInfo(this, message);
            else UiHelpers.ShowError(this, message);
        }

        private static async Task PersistChoicesAsync(string printer, LabelPrinterLanguage lang, LabelSize size)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                await settings.SetAsync(KeyPrinter, printer);
                await settings.SetAsync(KeyLanguage, lang == LabelPrinterLanguage.EscPos ? "escpos" : "zpl");
                await settings.SetAsync(KeySize, size.Caption);
            }
            catch
            {
                // الحفظ تحسين — الطباعة لا تتوقف بفشله
            }
        }
    }
}
