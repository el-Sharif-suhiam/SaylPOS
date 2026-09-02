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

        public StockCountForm()
        {
            Text = "الجرد الفعلي للمخزون";
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

            _btnStart = AppTheme.MakeButton("بدء جرد جديد", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnStart.Dock = DockStyle.Right;
            _btnStart.Width = 150;
            _btnStart.Click += async (_, _) => await StartAsync();

            _btnPrint = AppTheme.MakeButton("طباعة (PDF)", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 44);
            _btnPrint.Dock = DockStyle.Right;
            _btnPrint.Width = 135;
            _btnPrint.Click += (_, _) => PrintList();

            _btnExcel = AppTheme.MakeButton("Excel", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 44);
            _btnExcel.Dock = DockStyle.Right;
            _btnExcel.Width = 95;
            _btnExcel.Click += (_, _) => ExportExcel();

            var hint = new Label
            {
                Text = "افتح جلسة → اطبع القائمة → عُدّ البضاعة → أدخل الكميات المعدودة → ترحّل الفروق",
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

            _grid.Columns.Add("colProduct", "المنتج");
            _grid.Columns["colProduct"].FillWeight = 34;
            _grid.Columns["colProduct"].ReadOnly = true;
            _grid.Columns.Add("colBook", "الدفتري");
            _grid.Columns["colBook"].FillWeight = 13;
            _grid.Columns["colBook"].ReadOnly = true;
            var colCounted = new DataGridViewTextBoxColumn { Name = "colCounted", HeaderText = "المعدود", FillWeight = 13 };
            colCounted.DefaultCellStyle.BackColor = AppTheme.FieldBg;
            colCounted.DefaultCellStyle.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            _grid.Columns.Add(colCounted);
            _grid.Columns.Add("colDiff", "الفرق");
            _grid.Columns["colDiff"].FillWeight = 11;
            _grid.Columns["colDiff"].ReadOnly = true;
            _grid.Columns.Add("colCost", "تكلفة الفرق");
            _grid.Columns["colCost"].FillWeight = 14;
            _grid.Columns["colCost"].ReadOnly = true;

            _grid.CellEndEdit += async (_, args) => await CountedEditedAsync(args);
            _grid.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) args.SuppressKeyPress = true; };

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

            _btnPost = AppTheme.MakeButton("ترحيل الفروق", AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 48);
            _btnPost.Dock = DockStyle.Left;
            _btnPost.Width = 150;
            _btnPost.Click += async (_, _) => await PostAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
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
            root.Controls.Add(bottomBar, 0, 2);

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

                RefreshGrid();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل جلسة الجرد.");
            }
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            foreach (var l in _lines)
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
                _lblSummary.Text = "لا توجد جلسة جرد مفتوحة — ابدأ جردًا جديدًا.";
                _lblSummary.ForeColor = AppTheme.TextMuted;
                return;
            }

            int counted = _lines.Count(l => l.CountedQuantity.HasValue);
            int uncounted = _lines.Count - counted;
            int diffs = _lines.Count(l => l.Difference is > 0 or < 0);
            decimal cost = _lines.Where(l => l.Difference.HasValue).Sum(l => l.DifferenceCost);

            _lblSummary.ForeColor = uncounted > 0 ? AppTheme.Warning : AppTheme.PrimaryDark;
            _lblSummary.Text = uncounted > 0
                ? $"عُدّ {counted} من {_lines.Count} — متبقٍ {uncounted} منتجًا (صفوف صفراء) — فروق: {diffs}"
                : $"اكتمل العد ({counted}) — فروق: {diffs} منتجًا بتكلفة صافية {UiHelpers.Money(cost)}";
        }

        // ───────────────── إدخال المعدود ─────────────────
        private async Task CountedEditedAsync(DataGridViewCellEventArgs args)
        {
            if (args.RowIndex < 0 || args.RowIndex >= _lines.Count)
                return;

            var line = _lines[args.RowIndex];
            string? raw = _grid.Rows[args.RowIndex].Cells["colCounted"].Value?.ToString()?.Trim();

            int? counted = null;
            if (!string.IsNullOrEmpty(raw))
            {
                if (!int.TryParse(raw, out int value) || value < 0)
                {
                    UiHelpers.ShowError(this, "أدخل كمية صحيحة (رقم صفر أو أكثر) — أو اترك الخانة فارغة = لم يُعد.");
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
                UiHelpers.ShowError(this, "تعذر حفظ العدد.");
            }
        }

        // ───────────────── بدء / ترحيل ─────────────────
        private async Task StartAsync()
        {
            if (_sessionOpen) return;

            if (!ConfirmationDialog.Show(this, "بدء جرد جديد",
                    "سيتم التقاط صورة دفترية لكل منتج مخزني نشط الآن.\nأكمل العد قبل أي عمليات بيع/شراء كبيرة لتقليل التشويش.", dangerStyle: false))
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
                UiHelpers.ShowError(this, "تعذر بدء الجرد.");
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
                    UiHelpers.ShowError(this, $"{summary.UncountedLines} منتجًا لم يُعد بعد — أكمل العد أولًا.");
                    return;
                }
            }

            if (!ConfirmationDialog.Show(this, "ترحيل فروق الجرد",
                    $"سيتم تعديل المخزون بكل الفروق عبر تسويات المخزون القائمة (مع سجلاتها).\nهذا لا يمكن التراجع عنه إلا بتسويات عكسية.", dangerStyle: true))
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
                UiHelpers.ShowError(this, "تعذر الترحيل.");
            }
        }

        // ───────────────── التصدير ─────────────────
        private (string[] headers, List<string[]> rows) CurrentTable()
        {
            string[] headers = { "المنتج", "الدفتري", "المعدود", "الفرق", "تكلفة الفرق" };
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

        private void PrintList()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0) return;

            using var save = new SaveFileDialog
            {
                Title = "حفظ قائمة الجرد PDF",
                Filter = "ملفات PDF (*.pdf)|*.pdf",
                FileName = $"قائمة_الجرد_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string storeName = "سيل";
                try
                {
                    using var scope = Program.Services.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = settings.GetAsync(clsSettingsService.KeyStoreName, "سيل").GetAwaiter().GetResult();
                }
                catch { }

                PdfService.ExportTableReport(storeName, _sessionOpen ? "قائمة الجرد (للعد الفعلي)" : "قائمة الجرد",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm"), SessionState.CurrentUser?.UserName ?? "", headers, rows, save.FileName);
                UiHelpers.ShowInfo(this, $"تم إنشاء القائمة:\n{save.FileName}");
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "تعذر إنشاء PDF: " + ex.Message);
            }
        }

        private void ExportExcel()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0) return;

            using var save = new SaveFileDialog
            {
                Title = "حفظ قائمة الجرد Excel",
                Filter = "ملفات Excel (*.xlsx)|*.xlsx",
                FileName = $"قائمة_الجرد_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                ExcelService.ExportTable("قائمة الجرد", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), headers, rows, save.FileName);
                UiHelpers.ShowInfo(this, $"تم إنشاء الملف:\n{save.FileName}");
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "تعذر إنشاء Excel: " + ex.Message);
            }
        }
    }
}
