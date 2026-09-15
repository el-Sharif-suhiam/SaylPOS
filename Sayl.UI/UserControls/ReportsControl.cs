using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// التقارير: ملخص الأرباح، الأكثر مبيعًا، المخزون، المبيعات اليومية، الصلاحية،
    /// ربحية المنتجات والأقسام، أعمار الديون، حركة الصندوق، الرواتب —
    /// مع تشغيل فوري عند تغيير النوع/الفترة وتصدير PDF/Excel لما يُعرض.
    /// </summary>
    internal class ReportsControl : UserControl
    {
        private ComboBox _cmbReportType = null!;
        private DateTimePicker _dtpFrom = null!;
        private DateTimePicker _dtpTo = null!;
        private Label _lblFrom = null!;
        private Label _lblTo = null!;
        private Button _btnRefresh = null!;
        private Button _btnPeriodic = null!;
        private Button _btnPdf = null!;
        private Button _btnExcel = null!;
        private DataGridView _grid = null!;

        // آخر تقرير معروض — التصدير يأخذ منه (نفس ما تراه العين بالضبط)
        private string _currentTitle = "";
        private string _currentPeriod = "";
        private List<string[]> _currentRows = new();
        private PagerControl _pager = null!;
        private int _currentPage = 1;
        private int _pageSize = 20;

        private const int ReportProfit = 0;
        private const int ReportTopProducts = 1;
        private const int ReportInventory = 2;
        private const int ReportDailySales = 3;
        private const int ReportExpiry = 4;
        private const int ReportProductProfit = 5;
        private const int ReportTopCustomers = 6;
        private const int ReportAgeing = 7;
        private const int ReportCashFlow = 8;
        private const int ReportPayrolls = 9;
        private const int ReportLastCount = 10;

        // ═══════════════════════════════════════════════════════════════
        // 📖 قسم التقارير — نمط «UserControl بشريط فلترة + جدول»:
        //
        // لإضافة تقرير جديد (الطريق الكامل):
        //  1. أضف ثابتًا هنا: private const int ReportX = 11; (رقم متسلسل)
        //  2. أضف اسمه العربي في Items.AddRange للقائمة أدناه — بنفس الترتيب!
        //     (الترتيب يربط فهرس القائمة بالثابت — أي خلل = تقرير خاطئ)
        //  3. أضف case جديد في RunReportAsync: استدعاء خدمة + AddColumns + AddRow
        //     — ولأن التصدير يقرأ الجدول نفسه، يحصل تقريرك على PDF/Excel مجانًا.
        //  4. هل يحتاج فترة تاريخ؟ راجع ToggleDateFields لإخفاء/إظهار الحقول.
        //  ⚠️ ملاحظة: كل تغيير (نوع/تاريخ) يعيد التشغيل فورًا — لا زر إضافي مطلوب.
        // ═══════════════════════════════════════════════════════════════
        public ReportsControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
            BuildUi();
            Load += async (_, _) => await RunReportAsync();
        }

        private void BuildUi()
        {
            // ── شريط الفلترة (أعلى القسم، Dock=Top بارتفاع 70) ──
            // كل عناصره بـ Dock=Right داخل RTL: أول Add أقصى اليمين.
            // الترتيب البصري النهائي (من اليمين): النوع ← من ← إلى ← عرض ← PDF ← Excel ← الدوري
            // الأزرار الأربعة مفصولة بفواصل Panel (8px) — الـ Margin لا يعمل مع Dock.
            var filterBar = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = AppTheme.CardBg, Padding = new Padding(16, 14, 16, 10) };

            var lblType = new Label { Text = AppLang.T("Reports.Label.Type"), Dock = DockStyle.Right, Width = 78, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbReportType = new ComboBox { Dock = DockStyle.Right, Width = 172, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };
            _cmbReportType.Items.AddRange(new object[]
            {
                AppLang.T("Reports.Type.Profit"),
                AppLang.T("Reports.Type.TopProducts"),
                AppLang.T("Reports.Type.Inventory"),
                AppLang.T("Reports.Type.DailySales"),
                AppLang.T("Reports.Type.Expiry"),
                AppLang.T("Reports.Type.ProductProfit"),
                AppLang.T("Reports.Type.TopCustomers"),
                AppLang.T("Reports.Type.Ageing"),
                AppLang.T("Reports.Type.CashFlow"),
                AppLang.T("Reports.Type.Payrolls"),
                AppLang.T("Reports.Type.LastCount")
            });
            _cmbReportType.SelectedIndex = 0;
            // إصلاح العلة القديمة: تغيير النوع يعيد تشغيل التقرير فورًا (كان يخفي الحقول فقط)
            _cmbReportType.SelectedIndexChanged += async (_, _) =>
            {
                ToggleDateFields();
                await RunReportAsync();
            };

            var lblFrom = new Label { Text = AppLang.T("PeriodicReport.Label.From"), Dock = DockStyle.Right, Width = 38, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _lblFrom = lblFrom;
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 126, Format = DateTimePickerFormat.Short, Value = AppClock.NowLocal.Date.AddMonths(-1), RightToLeftLayout = false };
            _dtpFrom.ValueChanged += async (_, _) => await RunReportAsync();

            var lblTo = new Label { Text = AppLang.T("PeriodicReport.Label.To"), Dock = DockStyle.Right, Width = 38, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _lblTo = lblTo;
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 126, Format = DateTimePickerFormat.Short, Value = AppClock.NowLocal.Date, RightToLeftLayout = false };
            _dtpTo.ValueChanged += async (_, _) => await RunReportAsync();

            // 📖 أزرار الإجراءات — نفس وصفة AddToolbarButton في الأقسام الأخرى
            // لكن بـ Dock=Right لأن شريط الفلترة مبني بالإرساء اليميني.
            // ⚠️ الفاصل: الـ Margin لا يُطبَّق على العناصر المرساة بـ Dock
            // (فجوة صفر دائمًا — نفس سبب اختراع AddToolbarGap في الأقسام
            // الأخرى): كل زرين يفصل بينهما فاصل Panel شفاف حقيقي بعرض 8.
            Button MakeToolButton(string text, Color back, Color hover, int width)
            {
                var btn = AppTheme.MakeButton(text, back, hoverColor: hover, height: 42);
                btn.Dock = DockStyle.Right;
                btn.Width = width;
                btn.Padding = new Padding(4, 0, 4, 0);
                return btn;
            }

            Panel MakeGap(int width = 8) => new()
            {
                Dock = DockStyle.Right,
                Width = width,
                BackColor = Color.Transparent
            };

            _btnRefresh = MakeToolButton(AppLang.T("Reports.Button.Show"), AppTheme.Primary, AppTheme.PrimaryDark, 110);
            _btnRefresh.Click += async (_, _) => await RunReportAsync();

            _btnPdf = MakeToolButton("PDF", AppTheme.Danger, Color.FromArgb(185, 28, 28), 100);
            _btnPdf.Click += (_, _) => ExportPdf();

            _btnExcel = MakeToolButton("Excel", AppTheme.Success, Color.FromArgb(21, 128, 61), 100);
            _btnExcel.Click += (_, _) => ExportExcel();

            _btnPeriodic = MakeToolButton(AppLang.T("Reports.Button.Periodic"), AppTheme.Accent, AppTheme.AccentDark, 140);
            _btnPeriodic.Click += (_, _) =>
            {
                using var form = new PeriodicReportForm();
                form.ShowDialog(FindForm());
            };

            // فاصل أكبر (16) يفصل مجموعة الأزرار عن حقلي التاريخ بعدها
            Panel MakeGroupGap() => MakeGap(16);

            // ترتيب الإرساء (RTL): الدوري ← فاصل ← Excel ← فاصل ← PDF ← فاصل ← عرض ← فاصل-مجموعة ← إلى ← من ← النوع
            filterBar.Controls.Add(_btnPeriodic);
            filterBar.Controls.Add(MakeGap());
            filterBar.Controls.Add(_btnExcel);
            filterBar.Controls.Add(MakeGap());
            filterBar.Controls.Add(_btnPdf);
            filterBar.Controls.Add(MakeGap());
            filterBar.Controls.Add(_btnRefresh);
            filterBar.Controls.Add(MakeGroupGap());
            filterBar.Controls.Add(_dtpTo);
            filterBar.Controls.Add(_lblTo);
            filterBar.Controls.Add(_dtpFrom);
            filterBar.Controls.Add(lblFrom);
            filterBar.Controls.Add(_cmbReportType);
            filterBar.Controls.Add(lblType);

            _grid = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(12) };
            UiHelpers.StyleGrid(_grid);

            // شريط الترقيم: التصدير يبقى على «كل الصفوف» (CurrentTable) —
            // التنقل يعيد رسم الجدول من الشريحة فقط (RefreshDisplay).
            _pager = new PagerControl();
            _pager.PageRequested += page =>
            {
                _currentPage = page;
                RefreshDisplay();
            };
            _pager.PageSizeSelected += size =>
            {
                _pageSize = size;
                _currentPage = 1;
                RefreshDisplay();
            };

            Controls.Add(_grid);
            Controls.Add(_pager);
            Controls.Add(filterBar);
        }

        private void ToggleDateFields()
        {
            bool inventory = _cmbReportType.SelectedIndex == ReportInventory;
            bool daily = _cmbReportType.SelectedIndex == ReportDailySales;
            bool expiry = _cmbReportType.SelectedIndex == ReportExpiry;

            _dtpTo.Visible = !inventory && !daily && !expiry;
            _lblTo.Visible = _dtpTo.Visible;
            _dtpFrom.Visible = !inventory && !expiry;
            _lblFrom.Visible = _dtpFrom.Visible;
        }

        private string CurrentPeriodText()
        {
            int idx = _cmbReportType.SelectedIndex;
            if (idx == ReportInventory || idx == ReportExpiry)
                return idx == ReportInventory ? AppLang.T("Reports.Period.Current") : AppLang.T("Reports.Period.Next90");
            if (idx == ReportDailySales)
                return DateOnly.FromDateTime(_dtpFrom.Value).ToString("yyyy-MM-dd");
            return AppLang.T("Reports.Period.Range",
                DateOnly.FromDateTime(_dtpFrom.Value), DateOnly.FromDateTime(_dtpTo.Value));
        }

        private async Task RunReportAsync()
        {
            DateOnly from = DateOnly.FromDateTime(_dtpFrom.Value.Date);
            DateOnly to = DateOnly.FromDateTime(_dtpTo.Value.Date);
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _currentRows.Clear();
            _currentPage = 1; // تقرير جديد = من أول صفحة

            try
            {
                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();

                switch (_cmbReportType.SelectedIndex)
                {
                    case ReportProfit:
                    {
                        var r = await reportService.GetProfitSummaryAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        var d = r.Data;
                        _currentTitle = AppLang.T("Reports.Type.Profit");
                        AddColumns((AppLang.T("Common.Report.Item"), 60), (AppLang.T("Common.Report.Value"), 40));
                        AddRow(AppLang.T("Reports.Profit.SalesRevenue"), UiHelpers.Money(d.SalesRevenue));
                        AddRow(AppLang.T("Reports.Profit.Returns"), UiHelpers.Money(d.ReturnsAmount));
                        AddRow(AppLang.T("Reports.Profit.NetRevenue"), UiHelpers.Money(d.NetRevenue));
                        AddRow(AppLang.T("Reports.Profit.Cogs"), UiHelpers.Money(d.COGS));
                        AddRow(AppLang.T("Reports.Profit.GrossProfit"), UiHelpers.Money(d.GrossProfit));
                        AddRow(AppLang.T("Reports.Profit.Expenses"), UiHelpers.Money(d.Expenses));
                        AddRow(AppLang.T("Reports.Profit.NetProfit"), UiHelpers.Money(d.NetProfit));
                        break;
                    }
                    case ReportTopProducts:
                    {
                        var r = await reportService.GetTopSellingProductsAsync(from, to, 20);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.TopProducts");
                        AddColumns((AppLang.T("Common.Label.Product"), 50), (AppLang.T("Reports.Top.SoldQty"), 25), (AppLang.T("Reports.Top.Revenue"), 25));
                        foreach (var p in r.Data)
                            AddRow(p.ProductName, p.TotalQuantitySold.ToString(), UiHelpers.Money(p.TotalRevenue));
                        break;
                    }
                    case ReportInventory:
                    {
                        var r = await reportService.GetInventoryStatusAsync(lowStockOnly: false);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.Inventory");
                        AddColumns((AppLang.T("Common.Label.Product"), 40), (AppLang.T("Inventory.Label.Available"), 15), (AppLang.T("Inventory.Label.Reserved"), 15), (AppLang.T("Common.Label.Reorder"), 15), (AppLang.T("Common.Label.Status"), 15));
                        foreach (var i in r.Data)
                            AddRow(i.ProductName, i.QuantityAvailable.ToString(), i.QuantityReserved.ToString(),
                                i.ReorderLevel.ToString(),
                                i.IsLowStock ? AppLang.T("Common.Status.Low") : AppLang.T("Common.Status.Good"));
                        break;
                    }
                    case ReportDailySales:
                    {
                        var r = await reportService.GetDailySalesAsync(from);
                        if (!CheckResult(r) || r.Data == null) return;
                        var d = r.Data;
                        _currentTitle = AppLang.T("Reports.Type.DailySales");
                        AddColumns((AppLang.T("Common.Report.Item"), 60), (AppLang.T("Common.Report.Value"), 40));
                        AddRow(AppLang.T("Dashboard.Series.InvoiceCount"), d.InvoiceCount.ToString());
                        AddRow(AppLang.T("Reports.Daily.Gross"), UiHelpers.Money(d.TotalGross));
                        AddRow(AppLang.T("Reports.Daily.Discounts"), UiHelpers.Money(d.TotalDiscount));
                        AddRow(AppLang.T("Reports.Daily.Taxes"), UiHelpers.Money(d.TotalTax));
                        AddRow(AppLang.T("Reports.Daily.NetSales"), UiHelpers.Money(d.TotalNet));
                        AddRow(AppLang.T("Reports.Daily.Collected"), UiHelpers.Money(d.TotalCollected));
                        AddRow(AppLang.T("Reports.Daily.Outstanding"), UiHelpers.Money(d.TotalOutstanding));
                        break;
                    }
                    case ReportExpiry:
                    {
                        var r = await reportService.GetExpiringBatchesAsync(90);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.Expiry");
                        AddColumns((AppLang.T("Common.Label.Product"), 30), (AppLang.T("Reports.Expiry.Batch"), 18), (AppLang.T("Reports.Expiry.ExpiryDate"), 18), (AppLang.T("Common.Label.Qty"), 12), (AppLang.T("Reports.Expiry.DaysLeft"), 12), (AppLang.T("Common.Label.Status"), 10));
                        foreach (var b in r.Data)
                            AddRow(b.ProductName, b.BatchNumber, b.ExpiryDate.ToString("yyyy-MM-dd"),
                                b.Quantity.ToString(), b.DaysUntilExpiry.ToString(),
                                b.IsExpired ? AppLang.T("Reports.Expiry.Expired")
                                    : b.DaysUntilExpiry <= 30 ? AppLang.T("Reports.Expiry.VeryClose") : AppLang.T("Reports.Expiry.Close"));
                        break;
                    }
                    case ReportProductProfit:
                    {
                        var r = await reportService.GetProductProfitReportAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.ProductProfit");
                        AddColumns((AppLang.T("Common.Label.Product"), 28), (AppLang.T("Reports.ProductProfit.Qty"), 10), (AppLang.T("Reports.Top.Revenue"), 14), (AppLang.T("Common.Label.Cost"), 14), (AppLang.T("Reports.ProductProfit.Profit"), 14), (AppLang.T("Reports.ProductProfit.Margin"), 10), (AppLang.T("Reports.ProductProfit.Share"), 10));
                        foreach (var p in r.Data)
                            AddRow(p.ProductName, p.Quantity.ToString(), UiHelpers.Money(p.Revenue),
                                UiHelpers.Money(p.Cost), UiHelpers.Money(p.Profit),
                                p.MarginPercent.ToString("0.#"), p.ProfitSharePercent.ToString("0.#"));
                        break;
                    }
                    case ReportTopCustomers:
                    {
                        var r = await reportService.GetTopCustomersReportAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.TopCustomers");
                        AddColumns((AppLang.T("Common.Label.Customer"), 34), (AppLang.T("Reports.TopCustomers.Invoices"), 14), (AppLang.T("Reports.TopCustomers.NetRevenue"), 17), (AppLang.T("Reports.ProductProfit.Profit"), 17), (AppLang.T("Reports.ProductProfit.Margin"), 18));
                        foreach (var c in r.Data)
                            AddRow(c.CustomerName, c.InvoiceCount.ToString(), UiHelpers.Money(c.NetRevenue),
                                UiHelpers.Money(c.Profit), c.MarginPercent.ToString("0.#"));
                        break;
                    }
                    case ReportAgeing:
                    {
                        var r = await reportService.GetDebtAgeingReportAsync(to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.Ageing");
                        AddColumns((AppLang.T("Common.Label.Customer"), 30), (AppLang.T("Reports.Ageing.Balance"), 14), (AppLang.T("Reports.Ageing.0to30"), 14), (AppLang.T("Reports.Ageing.31to60"), 14), (AppLang.T("Reports.Ageing.61to90"), 14), (AppLang.T("Reports.Ageing.90plus"), 14));
                        foreach (var a in r.Data)
                            AddRow(a.CustomerName, UiHelpers.Money(a.Balance), UiHelpers.Money(a.Age0to30),
                                UiHelpers.Money(a.Age31to60), UiHelpers.Money(a.Age61to90), UiHelpers.Money(a.Age90plus));
                        break;
                    }
                    case ReportCashFlow:
                    {
                        var r = await reportService.GetCashFlowReportAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = AppLang.T("Reports.Type.CashFlow");
                        AddColumns((AppLang.T("Common.Report.Item"), 60), (AppLang.T("Common.Report.Value"), 40));
                        AddRow(AppLang.T("Reports.CashFlow.CashSales"), UiHelpers.Money(r.Data.CashSales));
                        AddRow(AppLang.T("Reports.CashFlow.CashReturns"), UiHelpers.Money(r.Data.CashReturns));
                        AddRow(AppLang.T("Reports.CashFlow.CustomerPayments"), UiHelpers.Money(r.Data.CustomerPayments));
                        AddRow(AppLang.T("Reports.CashFlow.SupplierPayments"), UiHelpers.Money(r.Data.SupplierPayments));
                        AddRow(AppLang.T("Reports.CashFlow.SessionCashOut"), UiHelpers.Money(r.Data.CashOut));
                        AddRow(AppLang.T("Reports.CashFlow.SessionCashIn"), UiHelpers.Money(r.Data.CashIn));
                        AddRow(AppLang.T("Reports.CashFlow.ExpensesPaid"), UiHelpers.Money(r.Data.ExpensesPaid));
                        AddRow(AppLang.T("Reports.CashFlow.SalariesPaid"), UiHelpers.Money(r.Data.SalariesPaid));
                        AddRow(AppLang.T("Reports.CashFlow.NetMovement"), UiHelpers.Money(r.Data.NetMovement));
                        break;
                    }
                    case ReportPayrolls:
                    {
                        var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                        var list = await payrollService.GetPayrollsAsync(from, to, null);
                        _currentTitle = AppLang.T("Reports.Type.Payrolls");
                        AddColumns((AppLang.T("Reports.Payroll.Employee"), 22), (AppLang.T("Reports.Payroll.Period"), 16), (AppLang.T("Common.Label.Type"), 10), (AppLang.T("Reports.Payroll.WorkedUnits"), 12), (AppLang.T("Reports.Payroll.Gross"), 13), (AppLang.T("Reports.Payroll.Deduction"), 9), (AppLang.T("Reports.Payroll.Addition"), 9), (AppLang.T("Common.Label.Net"), 13));
                        foreach (var p in list)
                            AddRow(p.EmployeeName,
                                AppLang.T("Payroll.Period.Line", p.PeriodFrom, p.PeriodTo),
                                p.SalaryType.ToString(),
                                p.WorkedUnits.ToString("0.##"),
                                UiHelpers.Money(p.Gross),
                                p.Deductions.ToString("0.##"),
                                p.Additions.ToString("0.##"),
                                UiHelpers.Money(p.Net));
                        break;
                    }
                    case ReportLastCount:
                    {
                        var countService = scope.ServiceProvider.GetRequiredService<clsStockCountService>();
                        var lines = await countService.GetLastPostedSessionAsync();
                        _currentTitle = AppLang.T("StockCount.List.Posted");
                        AddColumns((AppLang.T("Reports.Count.Product"), 32), (AppLang.T("Reports.Count.Book"), 12), (AppLang.T("Reports.Count.Counted"), 12), (AppLang.T("Reports.Count.Difference"), 12), (AppLang.T("Reports.Count.DiffCost"), 16), (AppLang.T("Common.Label.Status"), 16));
                        if (lines.Count == 0)
                        {
                            AddRow(AppLang.T("Reports.Count.None"), "", "", "", "", "");
                        }
                        else
                        {
                            foreach (var l in lines)
                                AddRow(l.ProductName, l.BookQuantity.ToString(), l.CountedQuantity?.ToString() ?? "-",
                                    l.Difference > 0 ? "+" + l.Difference : l.Difference?.ToString() ?? "-",
                                    l.DifferenceCost.ToString("N2"),
                                    l.Difference is > 0 ? AppLang.T("Reports.Count.Surplus")
                                        : l.Difference is < 0 ? AppLang.T("Reports.Count.Shortage")
                                        : AppLang.T("Reports.Count.Match"));
                        }
                        break;
                    }
                }

                _currentPeriod = CurrentPeriodText();
                RefreshDisplay();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Reports.Msg.GenerateFailed"));
            }
        }

        /// <summary>
        /// يرسم شريحة الصفحة الحالية من _currentRows (الذي يحتفظ بكل
        /// الصفوف للتصدير) — التنقل لا يمس بيانات التقرير أبدًا.
        /// </summary>
        private void RefreshDisplay()
        {
            _grid.Rows.Clear();
            int totalPages = Math.Max(1, (int)Math.Ceiling(_currentRows.Count / (double)Math.Max(1, _pageSize)));
            _currentPage = Math.Clamp(_currentPage, 1, totalPages);

            foreach (var row in _currentRows.Skip((_currentPage - 1) * _pageSize).Take(_pageSize))
            {
                var values = new List<object>();
                for (int i = 0; i < _grid.Columns.Count; i++)
                    values.Add(i < row.Length ? row[i] : "");
                _grid.Rows.Add(values.ToArray());
            }

            _pager.SetNavigation(_currentRows.Count, _currentPage);
        }

        // ───────────────── التصدير: نفس ما يُعرض بالضبط ─────────────────
        private (string[] headers, List<string[]> rows) CurrentTable()
        {
            var headers = _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray();
            return (headers, _currentRows);
        }

        // 📖 التصدير PDF — مسار async كامل:
        // أي نداء قاعدة بيانات (اسم المتجر) بـ await، والتوليد نفسه داخل
        // Task.Run — GetAwaiter().GetResult() على مؤشر الواجهة يعني تجمّدًا
        // كاملاً (نفس مرض ExportPdf القديم الذي عولج في InvoicePrintHelper).
        //private async void ExportPdf()
        //{
        //    var (headers, rows) = CurrentTable();
        //    if (rows.Count == 0)
        //    {
        //        UiHelpers.ShowInfo(this, "لا توجد بيانات معروضة للتصدير.");
        //        return;
        //    }

        //    using var save = new SaveFileDialog
        //    {
        //        Title = "حفظ تقرير PDF",
        //        Filter = "ملفات PDF (*.pdf)|*.pdf",
        //        FileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
        //    };
        //    if (save.ShowDialog(this) != DialogResult.OK) return;

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

        //        // التوليد خارج مؤشر الواجهة — قد يستهلك لحظة مع آلاف الصفوف
        //        string title = _currentTitle, period = _currentPeriod,
        //               user = SessionState.CurrentUser?.UserName ?? "", path = save.FileName;
        //        await Task.Run(() =>
        //            PdfService.ExportTableReport(storeName, title, period, user, headers, rows, path));

        //        UiHelpers.ShowInfo(this, $"تم إنشاء ملف PDF بنجاح:\n{save.FileName}");
        //    }
        //    catch (Exception ex)
        //    {
        //        UiHelpers.ShowError(this, "تعذر إنشاء ملف PDF: " + ex.Message);
        //    }
        //}

        private async void ExportPdf()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Reports.Msg.NoData"));
                return;
            }

            // 1) إنشاء مسار الحفظ تلقائياً
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string saylDir = Path.Combine(baseDir, "Sayl_POS");
            string reportsDir = Path.Combine(saylDir, "Reports");
            try
            {
                Directory.CreateDirectory(reportsDir);
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Reports.Msg.DirFailed"));
                return;
            }

            // 2) اسم الملف بنفس الصيغة السابقة
            string fileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";
            string fullPath = Path.Combine(reportsDir, fileName);

            try
            {
                string storeName = AppLang.T("App.Store.Default");
                try
                {
                    using var scope = Program.Services.CreateScope();
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, AppLang.T("App.Store.Default"));
                }
                catch { /* الترويسة الافتراضية كافية */ }

                // التوليد خارج مؤشر الواجهة — قد يستهلك لحظة مع آلاف الصفوف
                string title = _currentTitle, period = _currentPeriod,
                       user = SessionState.CurrentUser?.UserName ?? "", path = fullPath;
                await Task.Run(() =>
                    PdfService.ExportTableReport(storeName, title, period, user, headers, rows, path));

                UiHelpers.ShowInfo(this, AppLang.T("Reports.Msg.PdfDone", fullPath));
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, AppLang.T("Reports.Msg.PdfFailed", ex.Message));
            }
        }

        //private void ExportExcel()
        //{
        //    var (headers, rows) = CurrentTable();
        //    if (rows.Count == 0)
        //    {
        //        UiHelpers.ShowInfo(this, "لا توجد بيانات معروضة للتصدير.");
        //        return;
        //    }

        //    using var save = new SaveFileDialog
        //    {
        //        Title = "حفظ تقرير Excel",
        //        Filter = "ملفات Excel (*.xlsx)|*.xlsx",
        //        FileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
        //    };
        //    if (save.ShowDialog(this) != DialogResult.OK) return;

        //    try
        //    {
        //        ExcelService.ExportTable(_currentTitle, _currentPeriod, headers, rows, save.FileName);
        //        UiHelpers.ShowInfo(this, $"تم إنشاء ملف Excel بنجاح:\n{save.FileName}");
        //    }
        //    catch (Exception ex)
        //    {
        //        UiHelpers.ShowError(this, "تعذر إنشاء ملف Excel: " + ex.Message);
        //    }
        //}
        private void ExportExcel()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Reports.Msg.NoData"));
                return;
            }

            // 1) إنشاء مسار الحفظ تلقائياً
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string saylDir = Path.Combine(baseDir, "Sayl_POS");
            string reportsDir = Path.Combine(saylDir, "Reports");
            try
            {
                Directory.CreateDirectory(reportsDir);
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Reports.Msg.DirFailed"));
                return;
            }

            // 2) اسم الملف بنفس الصيغة السابقة
            string fileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            string fullPath = Path.Combine(reportsDir, fileName);

            try
            {
                ExcelService.ExportTable(_currentTitle, _currentPeriod, headers, rows, fullPath);
                UiHelpers.ShowInfo(this, AppLang.T("Reports.Msg.ExcelDone", fullPath));
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, AppLang.T("Reports.Msg.ExcelFailed", ex.Message));
            }
        }
        private bool CheckResult<T>(Common.Results.Result<T> r)
        {
            if (r.IsSuccess) return true;
            UiHelpers.ShowError(this, r.Message);
            return false;
        }

        private void AddColumns(params (string name, int weight)[] cols)
        {
            foreach (var (name, weight) in cols)
            {
                var col = _grid.Columns.Add(name, name);
                _grid.Columns[col].FillWeight = weight;
            }
        }

        private void AddRow(params string[] cells)
        {
            var snapshot = cells.ToArray();
            _currentRows.Add(snapshot);   // للتصدير — الجدول يرسمه RefreshDisplay (ترقيم)
        }
    }
}
