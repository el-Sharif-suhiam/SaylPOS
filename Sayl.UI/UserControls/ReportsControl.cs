using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// التقارير: ملخص الأرباح، الأكثر مبيعًا، حالة المخزون، والمبيعات اليومية — مع فترة زمنية.
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
        private DataGridView _grid = null!;

        private const int ReportProfit = 0;
        private const int ReportTopProducts = 1;
        private const int ReportInventory = 2;
        private const int ReportDailySales = 3;
        private const int ReportExpiry = 4;

        public ReportsControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
            BuildUi();
            Load += async (_, _) => await RunReportAsync();
        }

        private void BuildUi()
        {
            var filterBar = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = AppTheme.CardBg, Padding = new Padding(16, 14, 16, 10) };

            var lblType = new Label { Text = "نوع التقرير", Dock = DockStyle.Right, Width = 80, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbReportType = new ComboBox { Dock = DockStyle.Right, Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = AppTheme.FontBody };
            _cmbReportType.Items.AddRange(new object[] { "ملخص الأرباح", "الأكثر مبيعًا", "حالة المخزون", "المبيعات اليومية", "قرب انتهاء الصلاحية" });
            _cmbReportType.SelectedIndex = 0;
            _cmbReportType.SelectedIndexChanged += (_, _) => ToggleDateFields();

            var lblFrom = new Label { Text = "من", Dock = DockStyle.Right, Width = 30, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _lblFrom = lblFrom;
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 130, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddMonths(-1) };

            _lblTo = new Label { Text = "إلى", Dock = DockStyle.Right, Width = 30, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 130, Format = DateTimePickerFormat.Short, Value = DateTime.Today };

            _btnRefresh = AppTheme.MakeButton("عرض التقرير", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnRefresh.Dock = DockStyle.Right;
            _btnRefresh.Width = 140;
            _btnRefresh.Click += async (_, _) => await RunReportAsync();

            _btnPeriodic = AppTheme.MakeButton("تقرير دوري (PDF)", AppTheme.Accent, hoverColor: AppTheme.AccentDark);
            _btnPeriodic.Dock = DockStyle.Right;
            _btnPeriodic.Width = 170;
            _btnPeriodic.Click += (_, _) =>
            {
                using var form = new PeriodicReportForm();
                form.ShowDialog(FindForm());
            };

            // ترتيب الإرساء: الزر أولًا ثم التواريخ ثم النوع (آخر عنصر يُرسّى أولًا)
            filterBar.Controls.Add(_btnRefresh);
            filterBar.Controls.Add(_btnPeriodic);
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
            _lblFrom.Visible = !inventory && !expiry;
        }

        private async Task RunReportAsync()
        {
            DateOnly from = DateOnly.FromDateTime(_dtpFrom.Value.Date);
            DateOnly to = DateOnly.FromDateTime(_dtpTo.Value.Date);
            _grid.Rows.Clear();
            _grid.Columns.Clear();

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
                        AddColumns(("المنتج", 50), ("الكمية المباعة", 25), ("الإيراد", 25));
                        foreach (var p in r.Data)
                            AddRow(p.ProductName, p.TotalQuantitySold.ToString(), UiHelpers.Money(p.TotalRevenue));
                        break;
                    }
                    case ReportInventory:
                    {
                        var r = await reportService.GetInventoryStatusAsync(lowStockOnly: false);
                        if (!CheckResult(r) || r.Data == null) return;
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
                        AddColumns(("المنتج", 30), ("الدفعة", 18), ("انتهاء الصلاحية", 18), ("الكمية", 12), ("متبقٍ (يوم)", 12), ("الحالة", 10));
                        foreach (var b in r.Data)
                            AddRow(b.ProductName, b.BatchNumber, b.ExpiryDate.ToString("yyyy-MM-dd"),
                                b.Quantity.ToString(), b.DaysUntilExpiry.ToString(),
                                b.IsExpired ? "منتهية!" : b.DaysUntilExpiry <= 30 ? "قريب جدًا" : "قريب");
                        break;
                    }
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر توليد التقرير.");
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
            var values = new List<object>();
            for (int i = 0; i < _grid.Columns.Count; i++)
                values.Add(i < cells.Length ? cells[i] : "");
            _grid.Rows.Add(values.ToArray());
        }
    }
}
