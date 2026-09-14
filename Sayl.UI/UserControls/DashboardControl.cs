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

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «لوحة معلومات متداخلة» (أعقد بنية TLP بالنظام):
        //
        //   main (عمودان 62/38):
        //   ┌──────────────────────────────┬────────────────────┐
        //   │ بطاقات KPI (Flow, 150px)      │ الأكثر مبيعًا (42%) │
        //   │ [شريط الفترة 46px]            │                    │
        //   │ [المخطط البياني (مرن)]        │ المخزون المنخفض(58%)│
        //   └──────────────────────────────┴────────────────────┘
        //
        // أربع أفكار تستحق التأمل (كلها قابلة للنسخ):
        // 1) بطاقات KPI داخل FlowLayoutPanel WrapContents=true — تلتف
        //    للصف التالي تلقائيًا حسب عرض النافذة (استجابية مجانية!)
        //    — البطاقات تُبنى برمجيًا في LoadSummaryAsync لا هنا.
        // 2) المخطط LiveCharts (CartesianChart) عنصر WinForms عادي:
        //    Dock=Fill وسط الصف المرن — بياناته تُحقن من RefreshTrendAsync.
        // 3) عمود RTL: main بعمودين — «left» المتغير (البطاقات+المخطط)
        //    أُضيف للعمود 0 فيظهر يمينًا، و«right» (القائمتان) عمود 1 يسارًا.
        // 4) كل «بطاقة» (topPanel/lowPanel) = Panel + عنوان Top + محتوى
        //    Fill — نمط «بطاقة بعنوان» يتكرر في كل لوحات النظام.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── الجذر: عمودان — 62% للرئيسي (KPI+مخطط)، 38% للقوائم ──
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

            // ── العمود الأيمن (يظهر يمينًا مع RTL): المؤشرات + المخطط ──
            // ثلاثة صفوف: بطاقات 150 / شريط فترة 46 / مخطط مرن.
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // FlowPanel يلف البطاقات: WrapContents=true التفاف حر حسب العرض —
            // شاشة عريضة = صف واحد طويل، ضيقة = صفان. بلا أي كود استجابة.
            _cardsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                BackColor = AppTheme.Bg,
                Padding = new Padding(0)
            };

            // شريط الفترة: تسمية يمين + قائمة يسارها (Dock=Right داخل RTL —
            // أول Add أقصى اليمين). تغيير الفترة يعيد المخطط فقط (لا الكل).
            var periodBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(4, 8, 4, 4) };
            var lblPeriod = new Label
            {
                Text = AppLang.T("Dashboard.Period"),
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
            _cmbPeriod.Items.AddRange(new object[]
            {
                AppLang.T("Dashboard.Period.Week"),
                AppLang.T("Dashboard.Period.Month"),
                AppLang.T("Dashboard.Period.Quarter"),
                AppLang.T("Dashboard.Period.Year")
            });
            _cmbPeriod.SelectedIndex = 1;
            _cmbPeriod.SelectedIndexChanged += async (_, _) => await RefreshTrendAsync();
            periodBar.Controls.Add(_cmbPeriod);
            periodBar.Controls.Add(lblPeriod);

            // المخطط: عنصر LiveCharts بذات قواعد أي عنصر — Dock=Fill
            // داخل الصف المرن. BackColor فقط هنا؛ السلاسل والألوان
            // والنصوص تُضبط في RefreshTrendAsync.
            _chart = new CartesianChart
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg
            };

            left.Controls.Add(_cardsPanel);
            left.Controls.Add(periodBar);
            left.Controls.Add(_chart);

            // ── العمود الأيسر: بطاقتا قوائم (42% / 58%) ──
            // كل بطاقة: Panel CardBg بعنوان Dock=Top ومحتوى Dock=Fill —
            // ⚠️ المحتوى يُضاف أولًا (يُرسّى أخيرًا) ثم العنوان Top.
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

            var topPanel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(10) };
            var lblTop = new Label
            {
                Text = AppLang.T("Dashboard.TopProducts"),
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
                Text = AppLang.T("Dashboard.LowStock"),
                Dock = DockStyle.Top,
                Height = 30,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Danger // أحمر: تنبيه طلب شراء
            };
            _gridLowStock = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridLowStock);
            _gridLowStock.Columns.Add("colName", AppLang.T("Common.Label.Product"));
            _gridLowStock.Columns.Add("colQty", AppLang.T("Inventory.Label.Available"));
            _gridLowStock.Columns.Add("colReorder", AppLang.T("Common.Label.Reorder"));
            _gridLowStock.Columns["colName"].FillWeight = 55;
            _gridLowStock.Columns["colQty"].FillWeight = 22;
            _gridLowStock.Columns["colReorder"].FillWeight = 23;
            lowPanel.Controls.Add(_gridLowStock);
            lowPanel.Controls.Add(_lblLowStockHeader);

            right.Controls.Add(topPanel);
            right.Controls.Add(lowPanel);

            // RTL: العمود 0 يظهر يمينًا — لذا «left» (المخطط) في 0، «right» في 1
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
                var result = await reportService.GetDashboardSummaryAsync(AppClock.Today);

                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                DashboardSummaryVM s = result.Data;
                _cardsPanel.Controls.Clear();
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.TodayNet"), UiHelpers.Money(s.TodayNetSales), AppTheme.Primary));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.TodayCollected"), UiHelpers.Money(s.TodayCollected), AppTheme.Success));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.TodayInvoices"), s.TodayInvoiceCount.ToString(), AppTheme.SidebarBg));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.MonthNet"), UiHelpers.Money(s.MonthNetSales), AppTheme.Primary));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.MonthProfit"), UiHelpers.Money(s.MonthNetProfit), s.MonthNetProfit >= 0 ? AppTheme.Success : AppTheme.Danger));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.StockValue"), UiHelpers.Money(s.TotalStockValue), AppTheme.SidebarBg));
                _cardsPanel.Controls.Add(MakeCard(AppLang.T("Dashboard.Card.LowStockCount"), s.LowStockCount.ToString(), s.LowStockCount > 0 ? AppTheme.Warning : AppTheme.Success));

                _lstTopProducts.Items.Clear();
                if (s.TopProducts.Count == 0)
                    _lstTopProducts.Items.Add(AppLang.T("Dashboard.Msg.NoSales"));
                foreach (var p in s.TopProducts)
                    _lstTopProducts.Items.Add(AppLang.T("Dashboard.TopProduct.Line", p.ProductName, p.TotalQuantitySold, UiHelpers.Money(p.TotalRevenue)));

                _gridLowStock.Rows.Clear();
                foreach (var item in s.LowStockItems)
                    _gridLowStock.Rows.Add(item.ProductName, item.QuantityAvailable, item.ReorderLevel);

                _lblLowStockHeader.Text = s.LowStockCount > 0
                    ? AppLang.T("Dashboard.LowStock.Count", s.LowStockCount)
                    : AppLang.T("Dashboard.LowStock.None");
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Dashboard.Msg.LoadFailed"));
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

            DateOnly today = AppClock.Today;
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

        // 📖 RenderChart: تحقن بيانات LiveCharts في العنصر الذي بناه BuildUi —
        // Series = طبقات الرسم (أعمدة رمادية لعدد الفواتير + خط أزرق للمبيعات)،
        // XAxes/YAxes = المحاور (labels نصية والمحور Y يرقم تلقائيًا)،
        // SKColor = لون SkiaSharp — LiveCharts يرسم بـ Skia لا بـ GDI+.
        // LegendPosition.Bottom: مفتاح الألوان أسفل المخطط (لا يزاحم الرسم).
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
                    Name = AppLang.T("Dashboard.Series.InvoiceCount"),
                    Values = countValues,
                    Fill = new SolidColorPaint(new SKColor(148, 163, 184)),
                    MaxBarWidth = 26
                },
                new LineSeries<decimal>
                {
                    Name = AppLang.T("Dashboard.Series.NetSales"),
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

        // 📖 MakeCard: بطاقة KPI (205×130) — تُبنى برمجيًا وقت تحميل البيانات
        // (راجع LoadSummaryAsync) لا وقت بناء الواجهة. تركيبها:
        //   [شريط لون علوي 6px]  ← لون البطاقة يحدد حالتها (أخضر/أحمر/تحذيري)
        //   [القيمة — 16pt غامق]
        //   [العنوان — 9.5pt رمادي]
        // ⚠️ ترتيب الإضافة: Fill أولًا ثم Bottom ثم Top — الإرساء بالعكس
        //    (راجع LoginForm/§ passPanel). ApplyRoundedRegion يقص زواياها.
        // Margin (0,0,12,12): هو «مسافة» البطاقة داخل FlowPanel —
        //    FlowLayout يحترم Margin في التوزيع، لذا التباعد هنا لا Padding.
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
