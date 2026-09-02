using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
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
            // الترتيب البصري النهائي (من اليمين): النوع ← من ← إلى ← عرض ← PDF ← Excel ← الدوري.
            var filterBar = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = AppTheme.CardBg, Padding = new Padding(16, 14, 16, 10) };

            var lblType = new Label { Text = "نوع التقرير", Dock = DockStyle.Right, Width = 78, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbReportType = new ComboBox { Dock = DockStyle.Right, Width = 172, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };
            _cmbReportType.Items.AddRange(new object[]
            {
                "ملخص الأرباح",
                "الأكثر مبيعًا",
                "حالة المخزون",
                "المبيعات اليومية",
                "قرب انتهاء الصلاحية",
                "ربحية المنتجات",
                "أفضل العملاء",
                "أعمار الديون",
                "حركة الصندوق",
                "الرواتب",
                "آخر جرد فعلي"
            });
            _cmbReportType.SelectedIndex = 0;
            // إصلاح العلة القديمة: تغيير النوع يعيد تشغيل التقرير فورًا (كان يخفي الحقول فقط)
            _cmbReportType.SelectedIndexChanged += async (_, _) =>
            {
                ToggleDateFields();
                await RunReportAsync();
            };

            var lblFrom = new Label { Text = "من", Dock = DockStyle.Right, Width = 38, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _lblFrom = lblFrom;
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 126, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddMonths(-1), RightToLeftLayout = false };
            _dtpFrom.ValueChanged += async (_, _) => await RunReportAsync();

            var lblTo = new Label { Text = "إلى", Dock = DockStyle.Right, Width = 38, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _lblTo = lblTo;
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 126, Format = DateTimePickerFormat.Short, Value = DateTime.Today, RightToLeftLayout = false };
            _dtpTo.ValueChanged += async (_, _) => await RunReportAsync();

            _btnRefresh = AppTheme.MakeButton("عرض", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 42);
            _btnRefresh.Dock = DockStyle.Right;
            _btnRefresh.Width = 88;
            _btnRefresh.Click += async (_, _) => await RunReportAsync();

            _btnPdf = AppTheme.MakeButton("PDF", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 42);
            _btnPdf.Dock = DockStyle.Right;
            _btnPdf.Width = 82;
            _btnPdf.Click += (_, _) => ExportPdf();

            _btnExcel = AppTheme.MakeButton("Excel", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 42);
            _btnExcel.Dock = DockStyle.Right;
            _btnExcel.Width = 86;
            _btnExcel.Click += (_, _) => ExportExcel();

            _btnPeriodic = AppTheme.MakeButton("تقرير دوري", AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 42);
            _btnPeriodic.Dock = DockStyle.Right;
            _btnPeriodic.Width = 118;
            _btnPeriodic.Margin = new Padding(10, 0, 0, 0);
            _btnPeriodic.Click += (_, _) =>
            {
                using var form = new PeriodicReportForm();
                form.ShowDialog(FindForm());
            };

            // ترتيب الإرساء (RTL): الدوري ← Excel ← PDF ← عرض ← إلى ← من ← النوع
            filterBar.Controls.Add(_btnPeriodic);
            filterBar.Controls.Add(_btnExcel);
            filterBar.Controls.Add(_btnPdf);
            filterBar.Controls.Add(_btnRefresh);
            filterBar.Controls.Add(_dtpTo);
            filterBar.Controls.Add(_lblTo);
            filterBar.Controls.Add(_dtpFrom);
            filterBar.Controls.Add(lblFrom);
            filterBar.Controls.Add(_cmbReportType);
            filterBar.Controls.Add(lblType);

            _grid = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(12) };
            UiHelpers.StyleGrid(_grid);

            Controls.Add(_grid);
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
                return idx == ReportInventory ? "لقطة حالية" : "خلال 90 يومًا";
            if (idx == ReportDailySales)
                return DateOnly.FromDateTime(_dtpFrom.Value).ToString("yyyy-MM-dd");
            return $"{DateOnly.FromDateTime(_dtpFrom.Value):yyyy-MM-dd} ← {DateOnly.FromDateTime(_dtpTo.Value):yyyy-MM-dd}";
        }

        private async Task RunReportAsync()
        {
            DateOnly from = DateOnly.FromDateTime(_dtpFrom.Value.Date);
            DateOnly to = DateOnly.FromDateTime(_dtpTo.Value.Date);
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _currentRows.Clear();

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
                        _currentTitle = "ملخص الأرباح";
                        AddColumns(("البند", 60), ("القيمة", 40));
                        AddRow("إيراد المبيعات", UiHelpers.Money(d.SalesRevenue));
                        AddRow("المرتجعات", UiHelpers.Money(d.ReturnsAmount));
                        AddRow("صافي الإيراد", UiHelpers.Money(d.NetRevenue));
                        AddRow("تكلفة البضاعة المباعة", UiHelpers.Money(d.COGS));
                        AddRow("الربح الإجمالي", UiHelpers.Money(d.GrossProfit));
                        AddRow("المصروفات", UiHelpers.Money(d.Expenses));
                        AddRow("صافي الربح", UiHelpers.Money(d.NetProfit));
                        break;
                    }
                    case ReportTopProducts:
                    {
                        var r = await reportService.GetTopSellingProductsAsync(from, to, 20);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "الأكثر مبيعًا";
                        AddColumns(("المنتج", 50), ("الكمية المباعة", 25), ("الإيراد", 25));
                        foreach (var p in r.Data)
                            AddRow(p.ProductName, p.TotalQuantitySold.ToString(), UiHelpers.Money(p.TotalRevenue));
                        break;
                    }
                    case ReportInventory:
                    {
                        var r = await reportService.GetInventoryStatusAsync(lowStockOnly: false);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "حالة المخزون";
                        AddColumns(("المنتج", 40), ("المتاح", 15), ("محجوز", 15), ("حد الطلب", 15), ("الحالة", 15));
                        foreach (var i in r.Data)
                            AddRow(i.ProductName, i.QuantityAvailable.ToString(), i.QuantityReserved.ToString(),
                                i.ReorderLevel.ToString(), i.IsLowStock ? "منخفض" : "جيد");
                        break;
                    }
                    case ReportDailySales:
                    {
                        var r = await reportService.GetDailySalesAsync(from);
                        if (!CheckResult(r) || r.Data == null) return;
                        var d = r.Data;
                        _currentTitle = "المبيعات اليومية";
                        AddColumns(("البند", 60), ("القيمة", 40));
                        AddRow("عدد الفواتير", d.InvoiceCount.ToString());
                        AddRow("إجمالي قبل الخصم", UiHelpers.Money(d.TotalGross));
                        AddRow("الخصومات", UiHelpers.Money(d.TotalDiscount));
                        AddRow("الضرائب", UiHelpers.Money(d.TotalTax));
                        AddRow("صافي المبيعات (بعد المرتجعات)", UiHelpers.Money(d.TotalNet));
                        AddRow("المحصل", UiHelpers.Money(d.TotalCollected));
                        AddRow("غير المحصل", UiHelpers.Money(d.TotalOutstanding));
                        break;
                    }
                    case ReportExpiry:
                    {
                        var r = await reportService.GetExpiringBatchesAsync(90);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "قرب انتهاء الصلاحية";
                        AddColumns(("المنتج", 30), ("الدفعة", 18), ("انتهاء الصلاحية", 18), ("الكمية", 12), ("متبقٍ (يوم)", 12), ("الحالة", 10));
                        foreach (var b in r.Data)
                            AddRow(b.ProductName, b.BatchNumber, b.ExpiryDate.ToString("yyyy-MM-dd"),
                                b.Quantity.ToString(), b.DaysUntilExpiry.ToString(),
                                b.IsExpired ? "منتهية!" : b.DaysUntilExpiry <= 30 ? "قريب جدًا" : "قريب");
                        break;
                    }
                    case ReportProductProfit:
                    {
                        var r = await reportService.GetProductProfitReportAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "ربحية المنتجات";
                        AddColumns(("المنتج", 28), ("الكمية", 10), ("الإيراد", 14), ("التكلفة", 14), ("الربح", 14), ("الهامش %", 10), ("حصة الربح %", 10));
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
                        _currentTitle = "أفضل العملاء";
                        AddColumns(("العميل", 34), ("عدد الفواتير", 14), ("صافي الإيراد", 17), ("الربح", 17), ("الهامش %", 18));
                        foreach (var c in r.Data)
                            AddRow(c.CustomerName, c.InvoiceCount.ToString(), UiHelpers.Money(c.NetRevenue),
                                UiHelpers.Money(c.Profit), c.MarginPercent.ToString("0.#"));
                        break;
                    }
                    case ReportAgeing:
                    {
                        var r = await reportService.GetDebtAgeingReportAsync(to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "أعمار الديون";
                        AddColumns(("العميل", 30), ("الرصيد", 14), ("0-30 يوم", 14), ("31-60", 14), ("61-90", 14), ("90+ يوم", 14));
                        foreach (var a in r.Data)
                            AddRow(a.CustomerName, UiHelpers.Money(a.Balance), UiHelpers.Money(a.Age0to30),
                                UiHelpers.Money(a.Age31to60), UiHelpers.Money(a.Age61to90), UiHelpers.Money(a.Age90plus));
                        break;
                    }
                    case ReportCashFlow:
                    {
                        var r = await reportService.GetCashFlowReportAsync(from, to);
                        if (!CheckResult(r) || r.Data == null) return;
                        _currentTitle = "حركة الصندوق";
                        AddColumns(("البند", 60), ("القيمة", 40));
                        AddRow("مبيعات نقدية", UiHelpers.Money(r.Data.CashSales));
                        AddRow("مرتجعات نقدية", UiHelpers.Money(r.Data.CashReturns));
                        AddRow("قبض من العملاء", UiHelpers.Money(r.Data.CustomerPayments));
                        AddRow("سداد للموردين", UiHelpers.Money(r.Data.SupplierPayments));
                        AddRow("صرف نقد (جلسات)", UiHelpers.Money(r.Data.CashOut));
                        AddRow("نقد داخلي (جلسات)", UiHelpers.Money(r.Data.CashIn));
                        AddRow("مصروفات مدفوعة نقديًا", UiHelpers.Money(r.Data.ExpensesPaid));
                        AddRow("رواتب مصروفة", UiHelpers.Money(r.Data.SalariesPaid));
                        AddRow("صافي حركة النقد", UiHelpers.Money(r.Data.NetMovement));
                        break;
                    }
                    case ReportPayrolls:
                    {
                        var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                        var list = await payrollService.GetPayrollsAsync(from, to, null);
                        _currentTitle = "الرواتب";
                        AddColumns(("الموظف", 22), ("الفترة", 16), ("النوع", 10), ("أيام/ساعات", 12), ("الإجمالي", 13), ("خصم", 9), ("إضافة", 9), ("الصافي", 13));
                        foreach (var p in list)
                            AddRow(p.EmployeeName,
                                $"{p.PeriodFrom:MM-dd} ← {p.PeriodTo:MM-dd}",
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
                        _currentTitle = "آخر جرد فعلي (مرحّل)";
                        AddColumns(("المنتج", 32), ("الدفتري", 12), ("المعدود", 12), ("الفرق", 12), ("تكلفة الفرق", 16), ("الحالة", 16));
                        if (lines.Count == 0)
                        {
                            AddRow("لا يوجد جرد مرحّل بعد", "", "", "", "", "");
                        }
                        else
                        {
                            foreach (var l in lines)
                                AddRow(l.ProductName, l.BookQuantity.ToString(), l.CountedQuantity?.ToString() ?? "-",
                                    l.Difference > 0 ? "+" + l.Difference : l.Difference?.ToString() ?? "-",
                                    l.DifferenceCost.ToString("N2"),
                                    l.Difference is > 0 ? "فائض" : l.Difference is < 0 ? "عجز" : "مطابق");
                        }
                        break;
                    }
                }

                _currentPeriod = CurrentPeriodText();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر توليد التقرير.");
            }
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
        private async void ExportPdf()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0)
            {
                UiHelpers.ShowInfo(this, "لا توجد بيانات معروضة للتصدير.");
                return;
            }

            using var save = new SaveFileDialog
            {
                Title = "حفظ تقرير PDF",
                Filter = "ملفات PDF (*.pdf)|*.pdf",
                FileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

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

                // التوليد خارج مؤشر الواجهة — قد يستهلك لحظة مع آلاف الصفوف
                string title = _currentTitle, period = _currentPeriod,
                       user = SessionState.CurrentUser?.UserName ?? "", path = save.FileName;
                await Task.Run(() =>
                    PdfService.ExportTableReport(storeName, title, period, user, headers, rows, path));

                UiHelpers.ShowInfo(this, $"تم إنشاء ملف PDF بنجاح:\n{save.FileName}");
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "تعذر إنشاء ملف PDF: " + ex.Message);
            }
        }

        private void ExportExcel()
        {
            var (headers, rows) = CurrentTable();
            if (rows.Count == 0)
            {
                UiHelpers.ShowInfo(this, "لا توجد بيانات معروضة للتصدير.");
                return;
            }

            using var save = new SaveFileDialog
            {
                Title = "حفظ تقرير Excel",
                Filter = "ملفات Excel (*.xlsx)|*.xlsx",
                FileName = $"{_currentTitle}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                ExcelService.ExportTable(_currentTitle, _currentPeriod, headers, rows, save.FileName);
                UiHelpers.ShowInfo(this, $"تم إنشاء ملف Excel بنجاح:\n{save.FileName}");
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, "تعذر إنشاء ملف Excel: " + ex.Message);
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
            _currentRows.Add(snapshot);   // للتصدير
            var values = new List<object>();
            for (int i = 0; i < _grid.Columns.Count; i++)
                values.Add(i < snapshot.Length ? snapshot[i] : "");
            _grid.Rows.Add(values.ToArray());
        }
    }
}
