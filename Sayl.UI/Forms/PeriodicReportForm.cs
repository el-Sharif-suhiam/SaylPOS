using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة التقرير الدوري: اختيار نوع التقرير (ربع سنوي / نصف سنوي / سنوي) وفترة زمنية،
    /// عرض ملخص الفترات في جدول، وزر "تصدير PDF" لتوليد ملف احترافي قابل للطباعة.
    /// يعتمد على تحققٍ صارم من المدخلات قبل التنفيذ.
    /// </summary>
    public class PeriodicReportForm : DialogForm
    {
        private ComboBox _cmbType = null!;
        private DateTimePicker _dtpFrom = null!;
        private DateTimePicker _dtpTo = null!;

        private TabControl _tabs = null!;
        private DataGridView _grid = null!;
        private DataGridView _gridDaily = null!;
        private DataGridView _gridSessions = null!;
        private Label _lblSummary = null!;
        private Label _lblProfitExplain = null!;
        private Button _btnGenerate = null!;
        private Button _btnPdf = null!;

        private PeriodicReportVM? _current;

        public PeriodicReportForm()
        {
            Text = "تقرير الأرباح (ربع سنوي / نصف سنوي / سنوي)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1440, 880);
            MinimumSize = new Size(1080, 700);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            CornerRadius = 18;

            BuildUi();
            Load += async (_, _) => await RunAsync(quiet: true);
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — «الجذر المجزّأ» بستة صفوف + TabControl
        // (الأساسيات في PayrollForm — هنا خصوصيات هذه الشاشة):
        //
        //   صف0 (86): شريط عنوان كحلي
        //   صف1 (60): شريط الفلاتر (نوع/من/إلى/عرض)
        //   صف2 (72%): TabControl بثلاثة جداول (الفترات/اليومي/الجلسات)
        //   صف3 (86): لوحة شرح حساب الربح
        //   صف4 (52): شريط الإجمالي
        //   صف5 (86): الأزرار (تصدير PDF يمينًا / إغلاق يسارًا)
        //
        // نقطتان جديدتان هنا:
        // 1) التبويبات: TabControl بجداول مستقلة — شرح النمط في ShiftsForm
        //    («نافذة مصغرة داخل تبويب»).
        // 2) شرح الربح Label واحد بسطر طويل — يتحدث من SetProfitExplanation.
        //    (لو أردت أسطرًا منفصلة: عمود TLP بدل سطر واحد متصل.)
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = AppTheme.Bg
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));

            // ── الترويسة ──
            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Primary };
            var lblTitle = new Label
            {
                Text = "التقرير الدوري للأرباح",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = AppTheme.FontTitle
            };
            header.Controls.Add(lblTitle);
            root.Controls.Add(header, 0, 0);

            // ── شريط الفلاتر ──
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(16, 10, 16, 10) };

            var lblType = new Label { Text = "نوع التقرير", Dock = DockStyle.Right, Width = 92, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbType = new ComboBox { Dock = DockStyle.Right, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };
            _cmbType.Items.AddRange(new object[] { "ربع سنوي", "نصف سنوي", "سنوي" });
            _cmbType.SelectedIndex = 1;

            var lblFrom = new Label { Text = "من", Dock = DockStyle.Right, Width = 30, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 120, Format = DateTimePickerFormat.Short, Value = new DateTime(AppClock.Today.Year, 1, 1) };

            var lblTo = new Label { Text = "إلى", Dock = DockStyle.Right, Width = 30, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 120, Format = DateTimePickerFormat.Short, Value = AppClock.NowLocal.Date };

            _btnGenerate = AppTheme.MakeButton("عرض التقرير", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnGenerate.Dock = DockStyle.Right;
            _btnGenerate.Width = 150;
            _btnGenerate.Click += async (_, _) => await RunAsync(quiet: false);

            bar.Controls.Add(_btnGenerate);
            bar.Controls.Add(_dtpTo);
            bar.Controls.Add(lblTo);
            bar.Controls.Add(_dtpFrom);
            bar.Controls.Add(lblFrom);
            bar.Controls.Add(_cmbType);
            bar.Controls.Add(lblType);
            root.Controls.Add(bar, 0, 1);

            _cmbType.SelectedIndexChanged += async (_, _) => await RunAsync(quiet: true);

            // ── التبويبات: ملخص الفترات / الإيرادات اليومية / ملخص الجلسات ──
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                RightToLeft = RightToLeft.Yes,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold)
            };

            _grid = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            UiHelpers.StyleGrid(_grid);
            var pagePeriods = MakeTabPage("ملخص الفترات", _grid);

            _gridDaily = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            UiHelpers.StyleGrid(_gridDaily);
            var pageDaily = MakeTabPage("الإيرادات اليومية", _gridDaily);

            _gridSessions = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            UiHelpers.StyleGrid(_gridSessions);
            var pageSessions = MakeTabPage("ملخص الجلسات (Z)", _gridSessions);

            _tabs.TabPages.Add(pagePeriods);
            _tabs.TabPages.Add(pageDaily);
            _tabs.TabPages.Add(pageSessions);
            root.Controls.Add(_tabs, 0, 2);

            // ── لوحة شرح حساب الربح ──
            _lblProfitExplain = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                ForeColor = AppTheme.TextPrimary,
                Font = new Font("Segoe UI", 10.5F),
                Padding = new Padding(16, 8, 16, 8),
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(_lblProfitExplain, 0, 3);

            // ── شريط الإجمالي (معلومات) ──
            _lblSummary = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.PrimaryDark,
                Font = AppTheme.FontHeading,
                Text = ""
            };
            root.Controls.Add(_lblSummary, 0, 4);

            // ── شريط الأزرار ──
            var actions = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(16, 12, 16, 14) };

            var btnClose = AppTheme.MakeButton("إغلاق", Color.FromArgb(229, 231, 235), foreColor: AppTheme.TextPrimary, hoverColor: Color.FromArgb(209, 213, 219));
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 120;
            btnClose.Click += (_, _) => Close();

            _btnPdf = AppTheme.MakeButton("تصدير PDF", AppTheme.Accent, hoverColor: AppTheme.AccentDark);
            _btnPdf.Dock = DockStyle.Right;
            _btnPdf.Width = 180;
            _btnPdf.Enabled = false;
            _btnPdf.Click += async (_, _) => await ExportPdfAsync();

            actions.Controls.Add(_btnPdf);
            actions.Controls.Add(btnClose);
            root.Controls.Add(actions, 0, 5);

            Controls.Add(root);
        }

        private static TabPage MakeTabPage(string title, Control body)
        {
            return new TabPage(title) { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Controls = { body } };
        }

        private void SetProfitExplanation(PeriodicReportVM r)
        {
            _lblProfitExplain.Text =
                "شرح حساب الربح:  " +
                $"الإيراد {UiHelpers.Money(r.TotalSales)}  −  المرتجعات/المخصومات {UiHelpers.Money(r.TotalReturns)}  =  صافي الإيراد {UiHelpers.Money(r.TotalNetRevenue)}  →  " +
                $"الربح الإجمالي (بعد المخصومات فقط) = صافي الإيراد − COGS {UiHelpers.Money(r.TotalCogsNet)} = {UiHelpers.Money(r.TotalGrossProfit)}  →  " +
                $"صافي هامش الربح = الربح الإجمالي − المصروفات {UiHelpers.Money(r.TotalExpenses)} = {UiHelpers.Money(r.TotalNetProfit)}";
        }

        private enPeriodReportType SelectedType => _cmbType.SelectedIndex switch
        {
            0 => enPeriodReportType.Quarterly,
            1 => enPeriodReportType.HalfYearly,
            _ => enPeriodReportType.Yearly
        };

        /// <summary>تحقق صارم من المدخلات قبل توليد التقرير.</summary>
        private bool ValidateInputs(out DateOnly from, out DateOnly to, bool quiet)
        {
            from = DateOnly.FromDateTime(_dtpFrom.Value.Date);
            to = DateOnly.FromDateTime(_dtpTo.Value.Date);

            if (_cmbType.SelectedIndex < 0)
            {
                if (!quiet) UiHelpers.ShowError(this, "يرجى اختيار نوع التقرير.");
                return false;
            }

            if (_dtpFrom.Value > _dtpTo.Value)
            {
                if (!quiet) UiHelpers.ShowError(this, "تاريخ البداية يجب أن يكون قبل أو يساوي تاريخ النهاية.");
                return false;
            }

            if ((to.Year - from.Year) > 10)
            {
                if (!quiet) UiHelpers.ShowError(this, "الفترة الزمنية لا يمكن أن تتجاوز 10 سنوات.");
                return false;
            }

            if (_dtpFrom.Value.Date > AppClock.NowLocal.Date.AddYears(1))
            {
                if (!quiet) UiHelpers.ShowError(this, "تاريخ البداية في المستقبل غير مسموح به.");
                return false;
            }

            return true;
        }

        private async Task RunAsync(bool quiet)
        {
            if (!ValidateInputs(out var from, out var to, quiet))
                return;

            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _gridDaily.Rows.Clear();
            _gridDaily.Columns.Clear();
            _gridSessions.Rows.Clear();
            _gridSessions.Columns.Clear();
            Cursor = Cursors.WaitCursor;
            try
            {
                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var result = await reportService.GetPeriodicReportAsync(from, to, SelectedType, includeInvoiceDetails: true);
                if (!result.IsSuccess || result.Data == null)
                {
                    if (!quiet) UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _current = result.Data;
                BindGrid(_current);
                BindDailyGrid(_current);
                BindSessionGrid(_current);
                SetProfitExplanation(_current);

                _lblSummary.Text =
                    $"صافي الإيراد: {UiHelpers.Money(_current.TotalNetRevenue)}    —    " +
                    $"الربح الإجمالي: {UiHelpers.Money(_current.TotalGrossProfit)}    —    " +
                    $"صافي الربح (هامش الربح): {UiHelpers.Money(_current.TotalNetProfit)}";

                _btnPdf.Enabled = _current.Periods.Count > 0;
            }
            catch
            {
                if (!quiet) UiHelpers.ShowError(this, "تعذر توليد التقرير الدوري.");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void BindGrid(PeriodicReportVM r)
        {
            _grid.Columns.Clear();
            _grid.Columns.Add("colPeriod", "الفترة");
            _grid.Columns.Add("colCount", "الفواتير");
            _grid.Columns.Add("colSales", "الإيراد");
            _grid.Columns.Add("colReturns", "المرتجعات");
            _grid.Columns.Add("colNet", "صافي الإيراد");
            _grid.Columns.Add("colCogs", "COGS صافٍ");
            _grid.Columns.Add("colGross", "الربح الإجمالي");
            _grid.Columns.Add("colExp", "المصروفات");
            _grid.Columns.Add("colProfit", "صافي الربح");

            string[] names = { "colPeriod", "colCount", "colSales", "colReturns", "colNet", "colCogs", "colGross", "colExp", "colProfit" };
            double[] weights = { 22, 8, 10, 10, 11, 10, 10, 10, 11 };
            for (int i = 0; i < names.Length; i++)
                _grid.Columns[names[i]].FillWeight = (float)weights[i];

            _grid.Rows.Clear();
            foreach (var p in r.Periods)
            {
                _grid.Rows.Add(
                    $"{p.FromDate:dd/MM/yyyy} — {p.ToDate:dd/MM/yyyy}",
                    p.InvoiceCount,
                    UiHelpers.Money(p.SalesRevenue),
                    UiHelpers.Money(p.ReturnsAmount),
                    UiHelpers.Money(p.NetRevenue),
                    UiHelpers.Money(p.CogsNet),
                    UiHelpers.Money(p.GrossProfit),
                    UiHelpers.Money(p.Expenses),
                    UiHelpers.Money(p.NetProfit));
            }

            _grid.Rows.Add("الإجمالي",
                r.Periods.Sum(p => p.InvoiceCount),
                UiHelpers.Money(r.TotalSales),
                UiHelpers.Money(r.TotalReturns),
                UiHelpers.Money(r.TotalNetRevenue),
                UiHelpers.Money(r.TotalCogsNet),
                UiHelpers.Money(r.TotalGrossProfit),
                UiHelpers.Money(r.TotalExpenses),
                UiHelpers.Money(r.TotalNetProfit));

            int totalRow = _grid.Rows.Count - 1;
            _grid.Rows[totalRow].DefaultCellStyle.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            _grid.Rows[totalRow].DefaultCellStyle.BackColor = AppTheme.PrimaryLight;
        }

        private void BindDailyGrid(PeriodicReportVM r)
        {
            _gridDaily.Columns.Clear();
            _gridDaily.Columns.Add("cD", "اليوم");
            _gridDaily.Columns.Add("cC", "الفواتير");
            _gridDaily.Columns.Add("cG", "إجمالي البيع");
            _gridDaily.Columns.Add("cR", "المرتجعات");
            _gridDaily.Columns.Add("cN", "صافي الإيراد");
            _gridDaily.Columns.Add("cK", "COGS صافٍ");
            _gridDaily.Columns.Add("cP", "الربح الإجمالي");

            foreach (var d in r.DailyLines)
            {
                _gridDaily.Rows.Add(
                    $"{d.Date:dd/MM/yyyy}",
                    d.InvoiceCount,
                    UiHelpers.Money(d.GrossSales),
                    UiHelpers.Money(d.ReturnsAmount),
                    UiHelpers.Money(d.NetSales),
                    UiHelpers.Money(d.CogsNet),
                    UiHelpers.Money(d.GrossProfit));
            }

            decimal totalCount = r.DailyLines.Sum(x => x.InvoiceCount);
            _gridDaily.Rows.Add("الإجمالي",
                totalCount,
                UiHelpers.Money(r.DailyLines.Sum(x => x.GrossSales)),
                UiHelpers.Money(r.DailyLines.Sum(x => x.ReturnsAmount)),
                UiHelpers.Money(r.DailyLines.Sum(x => x.NetSales)),
                UiHelpers.Money(r.DailyLines.Sum(x => x.CogsNet)),
                UiHelpers.Money(r.DailyLines.Sum(x => x.GrossProfit)));

            int totalRow = _gridDaily.Rows.Count - 1;
            _gridDaily.Rows[totalRow].DefaultCellStyle.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            _gridDaily.Rows[totalRow].DefaultCellStyle.BackColor = AppTheme.PrimaryLight;
        }

        private void BindSessionGrid(PeriodicReportVM r)
        {
            _gridSessions.Columns.Clear();
            _gridSessions.Columns.Add("cS", "الجلسة");
            _gridSessions.Columns.Add("cU", "الكاشير");
            _gridSessions.Columns.Add("cO", "الافتتاحي");
            _gridSessions.Columns.Add("cT", "مبيعات نقدي");
            _gridSessions.Columns.Add("cRt", "مرتجعات نقدي");
            _gridSessions.Columns.Add("cIn", "قبض نقد");
            _gridSessions.Columns.Add("cOut", "صرف نقد");
            _gridSessions.Columns.Add("cE", "النقد المتوقع");
            _gridSessions.Columns.Add("cN", "صافي المبيعات");
            _gridSessions.Columns.Add("cI", "الافتتاح");

            foreach (var s in r.SessionLines)
            {
                _gridSessions.Rows.Add(
                    $"#{s.SessionId}",
                    s.UserName,
                    UiHelpers.Money(s.OpeningAmount),
                    UiHelpers.Money(s.CashSales),
                    UiHelpers.Money(s.CashReturns),
                    UiHelpers.Money(s.CashIn),
                    UiHelpers.Money(s.CashOut),
                    UiHelpers.Money(s.ExpectedCash),
                    UiHelpers.Money(s.NetSales),
                    $"{s.ClosedAt:dd/MM/yyyy HH:mm}");
            }
        }

        // 📖 التصدير PDF — مسار async كامل:
        // التوليد داخل Task.Run وإلا جمُدت النافذة كاملة (نفس علة FiscalYearCloseForm القديمة)
        //private async Task ExportPdfAsync()
        //{
        //    if (_current == null || _current.Periods.Count == 0)
        //    {
        //        UiHelpers.ShowError(this, "لا يوجد تقرير مُولَّد لتصديره. أعدّ العرض أولًا.");
        //        return;
        //    }

        //    using var save = new SaveFileDialog
        //    {
        //        Title = "حفظ تقرير PDF",
        //        Filter = "ملفات PDF (*.pdf)|*.pdf",
        //        FileName = $"تقرير_{_current.ReportTypeName}_{_current.FromDate:yyyy-MM-dd}_{_current.ToDate:yyyy-MM-dd}.pdf"
        //    };

        //    if (save.ShowDialog(this) != DialogResult.OK)
        //        return;

        //    _btnPdf.Enabled = false;
        //    Cursor = Cursors.WaitCursor;
        //    try
        //    {
        //        string storeName = "سيل";
        //        try
        //        {
        //            using var scope = Program.Services.CreateScope();
        //            var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
        //            storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, "سيل");
        //        }
        //        catch { /* الترويسة الافتراضية كافية */ }

        //        // التوليد خارج مؤشر الواجهة — التقرير السنوي قد يحوي مئات الصفوف
        //        var report = _current;
        //        await Task.Run(() => PdfService.ExportPeriodicReport(report, storeName, save.FileName));

        //        UiHelpers.ShowInfo(this, $"تم إنشاء ملف PDF بنجاح:\n{save.FileName}");
        //    }
        //    catch (Exception ex)
        //    {
        //        UiHelpers.ShowError(this, "تعذر إنشاء ملف PDF: " + ex.Message);
        //    }
        //    finally
        //    {
        //        _btnPdf.Enabled = true;
        //        Cursor = Cursors.Default;
        //    }
        //}

        private async Task ExportPdfAsync()
        {
            if (_current == null || _current.Periods.Count == 0)
            {
                UiHelpers.ShowError(this, "لا يوجد تقرير مُولَّد لتصديره. أعدّ العرض أولًا.");
                return;
            }

            // 1) إنشاء المسار تلقائياً: المستندات/Sayl POS
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string saylDir = Path.Combine(baseDir, "Sayl_POS");
            string periodicDir = Path.Combine(saylDir, "PeriodicReports");
            try
            {
                Directory.CreateDirectory(periodicDir);
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر إنشاء مجلد الحفظ تلقائيًا.");
                return;
            }

            // 2) تكوين اسم الملف بنفس الصيغة السابقة
            string fileName = $"تقرير_{_current.ReportTypeName}_{_current.FromDate:yyyy-MM-dd}_{_current.ToDate:yyyy-MM-dd}.pdf";
            string fullPath = Path.Combine(periodicDir, fileName);

            _btnPdf.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                string storeName = "سيل";
                try
                {
                    using var scope = Program.Services.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, "سيل");
                }
                catch { /* الترويسة الافتراضية كافية */ }

                var report = _current;
                await Task.Run(() => PdfService.ExportPeriodicReport(report, storeName, fullPath));

                UiHelpers.ShowInfo(this, $"تم حفظ التقرير في:\n{fullPath}");
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "تعذر إنشاء ملف PDF: " + ex.Message);
            }
            finally
            {
                _btnPdf.Enabled = true;
                Cursor = Cursors.Default;
            }
        }
    }
}
