using Common.DTOs.ViewModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WinForms;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;
using SkiaSharp;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// لوحة التحكم: مؤشرات اليوم/الشهر، مخطط المبيعات الزمني، الأكثر مبيعًا، وتنبيه المخزون المنخفض.
    /// </summary>
    internal class DashboardControl : UserControl
    {
        private enum TrendPeriod { Week = 7, Month = 30, Quarter = 90, Year = 365 }

        private FlowLayoutPanel _cardsPanel = null!;
        private ComboBox _cmbPeriod = null!;
        private CartesianChart _chart = null!;
        private ListBox _lstTopProducts = null!;
        private DataGridView _gridLowStock = null!;
        private Label _lblLowStockHeader = null!;

        public DashboardControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
            BuildUi();
            Load += async (_, _) => await RefreshDataAsync();
        }

        private void BuildUi()
        {
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

            // ── العمود الأيمن: المؤشرات + المخطط ──
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _cardsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                BackColor = AppTheme.Bg,
                Padding = new Padding(0)
            };

            var periodBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(4, 8, 4, 4) };
            var lblPeriod = new Label
            {
                Text = "الفترة:",
                Dock = DockStyle.Right,
                Width = 60,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };
            _cmbPeriod = new ComboBox
            {
                Dock = DockStyle.Right,
                Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = AppTheme.FontBody
            };
            _cmbPeriod.Items.AddRange(new object[] { "أسبوع", "شهر", "ربع سنة", "سنة" });
            _cmbPeriod.SelectedIndex = 1;
            _cmbPeriod.SelectedIndexChanged += async (_, _) => await RefreshTrendAsync();
            periodBar.Controls.Add(_cmbPeriod);
            periodBar.Controls.Add(lblPeriod);

            _chart = new CartesianChart
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg
            };

            left.Controls.Add(_cardsPanel);
            left.Controls.Add(periodBar);
            left.Controls.Add(_chart);

            // ── العمود الأيسر: الأكثر مبيعًا + المخزون المنخفض ──
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

            var topPanel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(10) };
            var lblTop = new Label
            {
                Text = "الأكثر مبيعًا (آخر 30 يومًا)",
                Dock = DockStyle.Top,
                Height = 30,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };
            _lstTopProducts = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            topPanel.Controls.Add(_lstTopProducts);
            topPanel.Controls.Add(lblTop);

            var lowPanel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(10) };
            _lblLowStockHeader = new Label
            {
                Text = "المخزون المنخفض",
                Dock = DockStyle.Top,
                Height = 30,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Danger
            };
            _gridLowStock = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridLowStock);
            _gridLowStock.Columns.Add("colName", "المنتج");
            _gridLowStock.Columns.Add("colQty", "المتاح");
            _gridLowStock.Columns.Add("colReorder", "حد الطلب");
            _gridLowStock.Columns["colName"].FillWeight = 55;
            _gridLowStock.Columns["colQty"].FillWeight = 22;
            _gridLowStock.Columns["colReorder"].FillWeight = 23;
            lowPanel.Controls.Add(_gridLowStock);
            lowPanel.Controls.Add(_lblLowStockHeader);

            right.Controls.Add(topPanel);
            right.Controls.Add(lowPanel);

            main.Controls.Add(left, 0, 0);
            main.Controls.Add(right, 1, 0);
            Controls.Add(main);
        }

        // ─────────────────────────────────────────────────
        // تحميل البيانات
        // ─────────────────────────────────────────────────
        private async Task RefreshDataAsync()
        {
            await LoadSummaryAsync();
            await RefreshTrendAsync();
        }

        private async Task LoadSummaryAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var result = await reportService.GetDashboardSummaryAsync(DateOnly.FromDateTime(DateTime.Today));

                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                DashboardSummaryVM s = result.Data;
                _cardsPanel.Controls.Clear();
                _cardsPanel.Controls.Add(MakeCard("صافي مبيعات اليوم", UiHelpers.Money(s.TodayNetSales), AppTheme.Primary));
                _cardsPanel.Controls.Add(MakeCard("المحصل اليوم", UiHelpers.Money(s.TodayCollected), AppTheme.Success));
                _cardsPanel.Controls.Add(MakeCard("فواتير اليوم", s.TodayInvoiceCount.ToString(), AppTheme.SidebarBg));
                _cardsPanel.Controls.Add(MakeCard("صافي مبيعات الشهر", UiHelpers.Money(s.MonthNetSales), AppTheme.Primary));
                _cardsPanel.Controls.Add(MakeCard("صافي ربح الشهر", UiHelpers.Money(s.MonthNetProfit), s.MonthNetProfit >= 0 ? AppTheme.Success : AppTheme.Danger));
                _cardsPanel.Controls.Add(MakeCard("قيمة المخزون", UiHelpers.Money(s.TotalStockValue), AppTheme.SidebarBg));
                _cardsPanel.Controls.Add(MakeCard("منتجات تحت حد الطلب", s.LowStockCount.ToString(), s.LowStockCount > 0 ? AppTheme.Warning : AppTheme.Success));

                _lstTopProducts.Items.Clear();
                if (s.TopProducts.Count == 0)
                    _lstTopProducts.Items.Add("لا توجد مبيعات بعد.");
                foreach (var p in s.TopProducts)
                    _lstTopProducts.Items.Add($"{p.ProductName} — {p.TotalQuantitySold} وحدة ({UiHelpers.Money(p.TotalRevenue)})");

                _gridLowStock.Rows.Clear();
                foreach (var item in s.LowStockItems)
                    _gridLowStock.Rows.Add(item.ProductName, item.QuantityAvailable, item.ReorderLevel);

                _lblLowStockHeader.Text = s.LowStockCount > 0
                    ? $"المخزون المنخفض ({s.LowStockCount} منتج)"
                    : "المخزون المنخفض — لا شيء";
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل بيانات لوحة التحكم.");
            }
        }

        private async Task RefreshTrendAsync()
        {
            TrendPeriod period = _cmbPeriod.SelectedIndex switch
            {
                0 => TrendPeriod.Week,
                1 => TrendPeriod.Month,
                2 => TrendPeriod.Quarter,
                _ => TrendPeriod.Year
            };

            DateOnly today = DateOnly.FromDateTime(DateTime.Today);
            DateOnly from = today.AddDays(-(int)period + 1);
            var groupBy = period switch
            {
                TrendPeriod.Quarter => Common.Enums.enTrendGroupBy.Week,
                TrendPeriod.Year => Common.Enums.enTrendGroupBy.Month,
                _ => Common.Enums.enTrendGroupBy.Day
            };

            try
            {
                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var result = await reportService.GetSalesTrendAsync(from, today, groupBy);
                if (!result.IsSuccess || result.Data == null)
                    return;

                RenderChart(result.Data);
            }
            catch
            {
                // تجاهل فشل تحديث المخطط (يبقى المخطط السابق معروضًا)
            }
        }

        private void RenderChart(List<SalesTrendPointVM> points)
        {
            var labels = points.Select(p =>
                p.PeriodStart == p.PeriodEnd
                    ? p.PeriodStart.ToString("MM-dd")
                    : p.PeriodStart.ToString("MM-dd"))
                .ToArray();

            var netValues = points.Select(p => p.NetSales).ToArray();
            var countValues = points.Select(p => (decimal)p.InvoiceCount).ToArray();

            _chart.Series = new ISeries[]
            {
                new ColumnSeries<decimal>
                {
                    Name = "عدد الفواتير",
                    Values = countValues,
                    Fill = new SolidColorPaint(new SKColor(148, 163, 184)),
                    MaxBarWidth = 26
                },
                new LineSeries<decimal>
                {
                    Name = "صافي المبيعات",
                    Values = netValues,
                    Stroke = new SolidColorPaint(new SKColor(37, 99, 235), 3),
                    GeometryStroke = new SolidColorPaint(new SKColor(37, 99, 235), 3),
                    GeometryFill = new SolidColorPaint(SKColors.White),
                    GeometrySize = 7,
                    Fill = null,
                    LineSmoothness = 0.3
                }
            };

            _chart.XAxes = new Axis[]
            {
                new Axis { Labels = labels, LabelsPaint = new SolidColorPaint(new SKColor(107, 114, 128)) }
            };
            _chart.YAxes = new Axis[]
            {
                new Axis { LabelsPaint = new SolidColorPaint(new SKColor(107, 114, 128)) }
            };

            _chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Bottom;
            _chart.LegendTextPaint = new SolidColorPaint(new SKColor(107, 114, 128));
        }

        private static Panel MakeCard(string title, string value, Color color)
        {
            var card = new Panel
            {
                Width = 205,
                Height = 130,
                Margin = new Padding(0, 0, 12, 12),
                BackColor = AppTheme.CardBg
            };
            AppTheme.ApplyRoundedRegion(card, 12);

            var colorBar = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = color };
            var lblValue = new Label
            {
                Text = value,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = color
            };
            var lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Bottom,
                Height = 40,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = AppTheme.TextMuted
            };

            card.Controls.Add(lblValue);
            card.Controls.Add(lblTitle);
            card.Controls.Add(colorBar);
            return card;
        }
    }
}
