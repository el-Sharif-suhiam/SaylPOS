using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// الجرد الفعلي: فتح جلسة بلقطة دفترية لكل منتج مخزني → إدخال المعدود مباشرة
    /// بالشبكة → ملخص الفروق الحي → ترحيل عبر تسويات المخزون القائمة.
    /// طباعة قائمة العد PDF/Excel للفرق وللجرد الورقي.
    /// </summary>
    public class StockCountForm : DialogForm
    {
        private DataGridView _grid = null!;
        private Label _lblSummary = null!;
        private Button _btnStart = null!;
        private Button _btnPost = null!;
        private Button _btnPrint = null!;
        private Button _btnExcel = null!;

        private List<StockCountVM> _lines = new();
        private bool _sessionOpen;
        private PagerControl _pager = null!;
        private int _currentPage = 1;
        private int _pageSize = 20;

        // فهرس السطر الكامل لصف الشبكة المعروض (ترقيم العرض — الإدخال يحفظ فوريًا للقاعدة)
        private int VisibleLineIndex(int gridRow) => (_currentPage - 1) * _pageSize + gridRow;

        public StockCountForm()
        {
            Text = AppLang.T("StockCount.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 680);
            MinimumSize = new Size(760, 520);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();
            Load += async (_, _) => await LoadSessionAsync();
        }

        private void BuildUi()
        {
            // ══ جذر: شريط علوي + جدول + ملخص/أزرار سفلي مطلق ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));    // أزرار القيادة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // الجدول
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));     // الترقيم
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));     // الملخص + الترحيل

            // ═══════════════════════════════════════════════════════════════
            // 📖 شاشة الجرد — نفس «الجذر المجزّأ» (راجع PayrollForm للشرح الكامل)
            // مع لمسات خاصة بشاشات العمل اليدوية:
            //  • عمود «المعدود» بخلفية FieldBg: ينعكس بصريًا كحقق قابل للتحرير.
            //  • الصفوف غير المعدودة تُصفر بخلفية صفراء فاتحة (كود RefreshGrid)
            //    — لوحة إشارة بصرية «هذا لم يُعد بعد» بلا نصوص مزعجة.
            //  • خلية الفرق تتلون أخضر (فائض) / أحمر (عجز) في RefreshGrid.
            //  • السطر الإرشادي (hint) يملأ المساحة المتبقية من الشريط
            //    — استبدله بتعليمات متجرك الخاصة بحرية.
            // ═══════════════════════════════════════════════════════════════
            // ── الشريط العلوي ──
            var topBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(2) };

            _btnStart = AppTheme.MakeButton(AppLang.T("StockCount.Button.Start"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnStart.Dock = DockStyle.Right;
            _btnStart.Width = 150;
            _btnStart.Click += async (_, _) => await StartAsync();

            _btnPrint = AppTheme.MakeButton(AppLang.T("StockCount.Button.Print"), AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 44);
            _btnPrint.Dock = DockStyle.Right;
            _btnPrint.Width = 135;
            _btnPrint.Click += async (_, _) => await PrintList();

            _btnExcel = AppTheme.MakeButton("Excel", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 44);
            _btnExcel.Dock = DockStyle.Right;
            _btnExcel.Width = 95;
            _btnExcel.Click += (_, _) => ExportExcel();

            var hint = new Label
            {
                Text = AppLang.T("StockCount.Hint"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Padding = new Padding(0, 0, 10, 0)
            };

            topBar.Controls.Add(_btnExcel);
            topBar.Controls.Add(_btnPrint);
            topBar.Controls.Add(_btnStart);
            topBar.Controls.Add(hint);

            // ── الجدول ──
            _grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

            _grid.Columns.Add("colProduct", AppLang.T("StockCount.Col.Product"));
            _grid.Columns["colProduct"].FillWeight = 34;
            _grid.Columns["colProduct"].ReadOnly = true;
            _grid.Columns.Add("colBook", AppLang.T("StockCount.Col.Book"));
            _grid.Columns["colBook"].FillWeight = 13;
            _grid.Columns["colBook"].ReadOnly = true;
            var colCounted = new DataGridViewTextBoxColumn { Name = "colCounted", HeaderText = AppLang.T("StockCount.Col.Counted"), FillWeight = 13 };
            colCounted.DefaultCellStyle.BackColor = AppTheme.FieldBg;
            colCounted.DefaultCellStyle.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            _grid.Columns.Add(colCounted);
            _grid.Columns.Add("colDiff", AppLang.T("StockCount.Col.Diff"));
            _grid.Columns["colDiff"].FillWeight = 11;
            _grid.Columns["colDiff"].ReadOnly = true;
            _grid.Columns.Add("colCost", AppLang.T("StockCount.Col.DiffCost"));
            _grid.Columns["colCost"].FillWeight = 14;
            _grid.Columns["colCost"].ReadOnly = true;

            _grid.CellEndEdit += async (_, args) => await CountedEditedAsync(args);
            _grid.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) args.SuppressKeyPress = true; };

            _pager = new PagerControl();
            _pager.PageRequested += page =>
            {
                _currentPage = page;
                RefreshGrid();
            };
            _pager.PageSizeSelected += size =>
            {
                _pageSize = size;
                _currentPage = 1;
                RefreshGrid();
            };

            // ── الشريط السفلي ──
            var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(2, 8, 2, 8) };

            _lblSummary = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                ForeColor = AppTheme.PrimaryDark
            };

            _btnPost = AppTheme.MakeButton(AppLang.T("StockCount.Button.Post"), AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 48);
            _btnPost.Dock = DockStyle.Left;
            _btnPost.Width = 150;
            _btnPost.Click += async (_, _) => await PostAsync();

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 110;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(_lblSummary);
            bottomBar.Controls.Add(_btnPost);
            bottomBar.Controls.Add(btnClose);

            root.Controls.Add(topBar, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(_pager, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);

            Controls.Add(root);
        }

        // ───────────────── التحميل ─────────────────
        private async Task LoadSessionAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();

                _sessionOpen = await countService.HasOpenSessionAsync();
                _lines = _sessionOpen ? await countService.GetOpenSessionAsync() : new List<StockCountVM>();

                _currentPage = 1; // جلسة جديدة = من أول صفحة
                RefreshGrid();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.LoadFailed"));
            }
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();

            int totalPages = Math.Max(1, (int)Math.Ceiling(_lines.Count / (double)Math.Max(1, _pageSize)));
            _currentPage = Math.Clamp(_currentPage, 1, totalPages);

            foreach (var l in _lines.Skip((_currentPage - 1) * _pageSize).Take(_pageSize))
            {
                int row = _grid.Rows.Add(l.ProductName, l.BookQuantity.ToString(),
                    l.CountedQuantity?.ToString() ?? "",
                    l.Difference > 0 ? "+" + l.Difference : l.Difference?.ToString() ?? "",
                    l.CountedQuantity.HasValue ? UiHelpers.Money(l.DifferenceCost) : "");
                _grid.Rows[row].Tag = l;

                if (l.Difference.HasValue)
                {
                    var color = l.Difference > 0 ? AppTheme.Success : l.Difference < 0 ? AppTheme.Danger : AppTheme.TextMuted;
                    _grid.Rows[row].Cells["colDiff"].Style.ForeColor = color;
                    _grid.Rows[row].Cells["colCost"].Style.ForeColor = color;
                }
                if (!l.CountedQuantity.HasValue)
                    _grid.Rows[row].Cells["colCounted"].Style.BackColor = Color.FromArgb(254, 243, 199); // لم يُعد — تمييز بصري
            }

            _pager.SetNavigation(_lines.Count, _currentPage);
            UpdateSummary();
            _btnStart.Enabled = !_sessionOpen;
            _btnPost.Enabled = _sessionOpen;
            _btnPrint.Enabled = _lines.Count > 0;
            _btnExcel.Enabled = _lines.Count > 0;
        }

        private void UpdateSummary()
        {
            if (!_sessionOpen)
            {
                _lblSummary.Text = AppLang.T("StockCount.NoSession");
                _lblSummary.ForeColor = AppTheme.TextMuted;
                return;
            }

            int counted = _lines.Count(l => l.CountedQuantity.HasValue);
            int uncounted = _lines.Count - counted;
            int diffs = _lines.Count(l => l.Difference is > 0 or < 0);
            decimal cost = _lines.Where(l => l.Difference.HasValue).Sum(l => l.DifferenceCost);

            _lblSummary.ForeColor = uncounted > 0 ? AppTheme.Warning : AppTheme.PrimaryDark;
            _lblSummary.Text = uncounted > 0
                ? AppLang.T("StockCount.Summary.Counting", counted, _lines.Count, uncounted, diffs)
                : AppLang.T("StockCount.Summary.Done", counted, diffs, UiHelpers.Money(cost));
        }

        // ───────────────── إدخال المعدود ─────────────────
        private async Task CountedEditedAsync(DataGridViewCellEventArgs args)
        {
            int pi = VisibleLineIndex(args.RowIndex);
            if (args.RowIndex < 0 || pi < 0 || pi >= _lines.Count)
                return;

            var line = _lines[pi];
            string? raw = _grid.Rows[args.RowIndex].Cells["colCounted"].Value?.ToString()?.Trim();

            int? counted = null;
            if (!string.IsNullOrEmpty(raw))
            {
                if (!int.TryParse(raw, out int value) || value < 0)
                {
                    UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.CountedInvalid"));
                    await LoadSessionAsync();
                    return;
                }
                counted = value;
            }

            try
            {
                using var scope = Program.Services.CreateScope();
                var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();
                var r = await countService.SetCountedAsync(line.CountId, counted);
                if (!r.IsSuccess)
                {
                    UiHelpers.ShowError(this, r.Message);
                    await LoadSessionAsync();
                    return;
                }

                line.CountedQuantity = counted;
                RefreshGrid();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.SaveCountFailed"));
            }
        }

        // ───────────────── بدء / ترحيل ─────────────────
        private async Task StartAsync()
        {
            if (_sessionOpen) return;

            if (!ConfirmationDialog.Show(this, AppLang.T("StockCount.Start.Title"),
                    AppLang.T("StockCount.Start.Confirm"), dangerStyle: false))
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();
                var r = await countService.StartSessionAsync(SessionState.CurrentUser?.UserId ?? 0);
                UiHelpers.ShowResult(this, r);
                await LoadSessionAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Start.Failed"));
            }
        }

        private async Task PostAsync()
        {
            if (!_sessionOpen) return;

            using (var scope = Program.Services.CreateScope())
            {
                var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();
                var summary = await countService.GetSummaryAsync();
                if (summary.UncountedLines > 0)
                {
                    UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.UncountedLeft", summary.UncountedLines));
                    return;
                }
            }

            if (!ConfirmationDialog.Show(this, AppLang.T("StockCount.Post.Title"),
                    AppLang.T("StockCount.Post.Confirm"), dangerStyle: true))
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();
                var r = await countService.PostSessionAsync(SessionState.CurrentUser?.UserId ?? 0);
                UiHelpers.ShowResult(this, r);
                if (r.IsSuccess)
                {
                    _sessionOpen = false;
                    _lines = new List<StockCountVM>();
                    RefreshGrid();
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Post.Failed"));
            }
        }

        // ───────────────── التصدير ─────────────────
        private (string[] headers, List<string[]> rows) CurrentTable()
        {
            string[] headers = { AppLang.T("StockCount.Col.Product"), AppLang.T("StockCount.Col.Book"), AppLang.T("StockCount.Col.Counted"), AppLang.T("StockCount.Col.Diff"), AppLang.T("StockCount.Col.DiffCost") };
            var rows = _lines.Select(l => new[]
            {
                l.ProductName,
                l.BookQuantity.ToString(),
                l.CountedQuantity?.ToString() ?? "-",
                l.Difference > 0 ? "+" + l.Difference : l.Difference?.ToString() ?? "-",
                l.CountedQuantity.HasValue ? l.DifferenceCost.ToString("N2") : "-"
            }).ToList();
            return (headers, rows);
        }

        private async Task PrintList()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0) return;

            using var save = new SaveFileDialog
            {
                Title = AppLang.T("StockCount.Save.PdfTitle"),
                Filter = AppLang.T("StockCount.Save.PdfFilter"),
                FileName = AppLang.T("StockCount.Save.FileName", DateTime.Now) + ".pdf"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                // مسار async كامل — منع التجميد (نفس إصلاح التقارير)
                string storeName = AppLang.T("Common.Default.Store");
                try
                {
                    using var scope = Program.Services.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, AppLang.T("Common.Default.Store"));
                }
                catch { }

                string title = _sessionOpen ? AppLang.T("StockCount.List.Title") : AppLang.T("StockCount.List.TitleShort"),
                       period = Sayl.UI.Infrastructure.AppClock.NowLocal.ToString("yyyy-MM-dd HH:mm"),
                       user = SessionState.CurrentUser?.UserName ?? "", path = save.FileName;
                await Task.Run(() =>
                    PdfService.ExportTableReport(storeName, title, period, user, headers, rows, path));

                UiHelpers.ShowInfo(this, AppLang.T("StockCount.Msg.PdfDone", save.FileName));
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.PdfFailed", ex.Message));
            }
        }

        private void ExportExcel()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0) return;

            using var save = new SaveFileDialog
            {
                Title = AppLang.T("StockCount.Save.ExcelTitle"),
                Filter = AppLang.T("StockCount.Save.ExcelFilter"),
                FileName = AppLang.T("StockCount.Save.FileName", DateTime.Now) + ".xlsx"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                ExcelService.ExportTable(AppLang.T("StockCount.List.TitleShort"), Sayl.UI.Infrastructure.AppClock.NowLocal.ToString("yyyy-MM-dd HH:mm"), headers, rows, save.FileName);
                UiHelpers.ShowInfo(this, AppLang.T("StockCount.Msg.ExcelDone", save.FileName));
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, AppLang.T("StockCount.Msg.ExcelFailed", ex.Message));
            }
        }
    }
}
