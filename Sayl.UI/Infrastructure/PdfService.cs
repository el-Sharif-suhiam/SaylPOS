using Common.DTOs.ViewModel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// خدمة توليد تقارير PDF احترافية بالتنسيق العربي للواجهة.
    /// تستخدم QuestPDF لإنشاء مستند منسّق (ترويسة، جدول، تذييل) وتنزيله كملف.
    /// </summary>
    internal static class PdfService
    {
        // ضبط الترخيص للمشاريع غير التجارية/الصغيرة (مطلوب من إصدار 2024.3+)
        public static void EnsureLicense() => QuestPDF.Settings.License = LicenseType.Community;

        /// <summary>
        /// يولّد ملف PDF لتقرير دوري (ربع سنوي / نصف سنوي / سنوي) ويحفظه في المسار المحدد.
        /// </summary>
        public static void ExportPeriodicReport(PeriodicReportVM report, string filePath)
        {
            EnsureLicense();

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI"));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("سيل — نظام إدارة نقاط البيع")
                            .FontSize(16).Bold()
                            .FontColor(Colors.Blue.Darken3);
                        col.Item().Text($"{report.ReportTypeName} — من {report.FromDate:yyyy-MM-dd} إلى {report.ToDate:yyyy-MM-dd}")
                            .FontSize(12)
                            .FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                    });

                    // ملخص عام
                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        col.Item().Text("الملخص المالي").FontSize(13).Bold();

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                            });
                            table.Header(h =>
                            {
                                h.Cell().Element(CellStyle).Text("صافي الإيراد");
                                h.Cell().Element(CellStyle).Text("الربح الإجمالي");
                                h.Cell().Element(CellStyle).Text("صافي الربح");
                            });
                            table.Cell().Element(CellStyle).Text(Money(report.TotalNetRevenue));
                            table.Cell().Element(CellStyle).Text(Money(report.TotalGrossProfit));
                            table.Cell().Element(CellStyle).Text(Money(report.TotalNetProfit));
                        });

                        col.Item().PaddingTop(12).Text("تفصيل الفترات").FontSize(13).Bold();

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2);
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                            });

                            table.Header(h =>
                            {
                                h.Cell().Element(CellStyle).Text("الفترة");
                                h.Cell().Element(CellStyle).Text("الإيراد");
                                h.Cell().Element(CellStyle).Text("المرتجعات");
                                h.Cell().Element(CellStyle).Text("صافي الإيراد");
                                h.Cell().Element(CellStyle).Text("COGS صافٍ");
                                h.Cell().Element(CellStyle).Text("الربح الإجمالي");
                                h.Cell().Element(CellStyle).Text("المصروفات");
                                h.Cell().Element(CellStyle).Text("صافي الربح");
                            });

                            foreach (var p in report.Periods)
                            {
                                table.Cell().Element(CellStyle).Text(ToArabicDate(p.PeriodLabel));
                                table.Cell().Element(CellStyle).Text(Money(p.SalesRevenue));
                                table.Cell().Element(CellStyle).Text(Money(p.ReturnsAmount));
                                table.Cell().Element(CellStyle).Text(Money(p.NetRevenue));
                                table.Cell().Element(CellStyle).Text(Money(p.CogsNet));
                                table.Cell().Element(CellStyle).Text(Money(p.GrossProfit));
                                table.Cell().Element(CellStyle).Text(Money(p.Expenses));
                                table.Cell().Element(CellStyle).Text(Money(p.NetProfit));
                            }

                            // سطر الإجمالي
                            table.Cell().Element(CellStyleBold).Text("الإجمالي");
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalSales));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalReturns));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalNetRevenue));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalCogsNet));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalGrossProfit));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalExpenses));
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalNetProfit));
                        });

                        // شرح حساب الربح المتدرج
                        col.Item().PaddingTop(12).Text("شرح حساب الربح").FontSize(13).Bold();
                        col.Item().PaddingTop(4).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn(3);
                            });
                            table.Cell().Element(CellStyle).Text("1) الإيراد الإجمالي (المبيعات)");
                            table.Cell().Element(CellStyle).Text(Money(report.TotalSales));
                            table.Cell().Element(CellStyle).Text("2) المخصومات (المرتجعات / خصومات الفواتير)");
                            table.Cell().Element(CellStyle).Text("-" + Money(report.TotalReturns));
                            table.Cell().Element(CellStyleBold).Text("= صافي الإيراد");
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalNetRevenue));
                            table.Cell().Element(CellStyle).Text("3) تكلفة البضاعة المباعة (COGS صافٍ)");
                            table.Cell().Element(CellStyle).Text("-" + Money(report.TotalCogsNet));
                            table.Cell().Element(CellStyleBold).Text("= الربح الإجمالي (بعد المخصومات فقط)");
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalGrossProfit));
                            table.Cell().Element(CellStyle).Text("4) المصروفات التشغيلية");
                            table.Cell().Element(CellStyle).Text("-" + Money(report.TotalExpenses));
                            table.Cell().Element(CellStyleBold).Text("= صافي هامش الربح");
                            table.Cell().Element(CellStyleBold).Text(Money(report.TotalNetProfit));
                        });

                        // الإيرادات اليومية (تجمع الفواتير بدل سرد كل فاتورة)
                        if (report.DailyLines.Count > 0)
                        {
                            col.Item().PaddingTop(12).Text("الإيرادات اليومية").FontSize(13).Bold();

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                });
                                table.Header(h =>
                                {
                                    h.Cell().Element(CellStyle).Text("اليوم");
                                    h.Cell().Element(CellStyle).Text("الفواتير");
                                    h.Cell().Element(CellStyle).Text("إجمالي البيع");
                                    h.Cell().Element(CellStyle).Text("المرتجعات");
                                    h.Cell().Element(CellStyle).Text("صافي الإيراد");
                                    h.Cell().Element(CellStyle).Text("COGS صافٍ");
                                    h.Cell().Element(CellStyle).Text("الربح الإجمالي");
                                });

                                foreach (var d in report.DailyLines)
                                {
                                    table.Cell().Element(CellStyle).Text(d.Date.ToString("dd/MM/yyyy"));
                                    table.Cell().Element(CellStyle).Text(d.InvoiceCount.ToString());
                                    table.Cell().Element(CellStyle).Text(Money(d.GrossSales));
                                    table.Cell().Element(CellStyle).Text(Money(d.ReturnsAmount));
                                    table.Cell().Element(CellStyle).Text(Money(d.NetSales));
                                    table.Cell().Element(CellStyle).Text(Money(d.CogsNet));
                                    table.Cell().Element(CellStyle).Text(Money(d.GrossProfit));
                                }

                                table.Cell().Element(CellStyleBold).Text("الإجمالي");
                                table.Cell().Element(CellStyleBold).Text(report.DailyLines.Sum(x => x.InvoiceCount).ToString());
                                table.Cell().Element(CellStyleBold).Text(Money(report.DailyLines.Sum(x => x.GrossSales)));
                                table.Cell().Element(CellStyleBold).Text(Money(report.DailyLines.Sum(x => x.ReturnsAmount)));
                                table.Cell().Element(CellStyleBold).Text(Money(report.DailyLines.Sum(x => x.NetSales)));
                                table.Cell().Element(CellStyleBold).Text(Money(report.DailyLines.Sum(x => x.CogsNet)));
                                table.Cell().Element(CellStyleBold).Text(Money(report.DailyLines.Sum(x => x.GrossProfit)));
                            });
                        }

                        // ملخص الجلسات المكتملة (نظير تقرير Z)
                        if (report.SessionLines.Count > 0)
                        {
                            col.Item().PaddingTop(12).Text("ملخص الجلسات المكتملة (Z)").FontSize(13).Bold();

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                    c.RelativeColumn();
                                });
                                table.Header(h =>
                                {
                                    h.Cell().Element(CellStyle).Text("الجلسة");
                                    h.Cell().Element(CellStyle).Text("الكاشير");
                                    h.Cell().Element(CellStyle).Text("الافتتاحي");
                                    h.Cell().Element(CellStyle).Text("مبيعات نقدي");
                                    h.Cell().Element(CellStyle).Text("مرتجعات نقدي");
                                    h.Cell().Element(CellStyle).Text("قبض نقد");
                                    h.Cell().Element(CellStyle).Text("صرف نقد");
                                    h.Cell().Element(CellStyle).Text("النقد المتوقع");
                                    h.Cell().Element(CellStyle).Text("صافي المبيعات");
                                    h.Cell().Element(CellStyle).Text("الإغلاق");
                                });

                                foreach (var s in report.SessionLines)
                                {
                                    table.Cell().Element(CellStyle).Text("#" + s.SessionId);
                                    table.Cell().Element(CellStyle).Text(s.UserName);
                                    table.Cell().Element(CellStyle).Text(Money(s.OpeningAmount));
                                    table.Cell().Element(CellStyle).Text(Money(s.CashSales));
                                    table.Cell().Element(CellStyle).Text(Money(s.CashReturns));
                                    table.Cell().Element(CellStyle).Text(Money(s.CashIn));
                                    table.Cell().Element(CellStyle).Text(Money(s.CashOut));
                                    table.Cell().Element(CellStyle).Text(Money(s.ExpectedCash));
                                    table.Cell().Element(CellStyle).Text(Money(s.NetSales));
                                    table.Cell().Element(CellStyle).Text(s.ClosedAt?.ToString("dd/MM/yyyy HH:mm") ?? "-");
                                }
                            });
                        }

                        col.Item().PaddingTop(10).Text($"صافي الإيراد: {Money(report.TotalNetRevenue)} — المحصل: {Money(report.TotalCollected)} — غير المحصل: {Money(report.TotalOutstanding)}")
                            .FontSize(11).Bold();
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.Span("سيل POS — تقرير ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });

            document.GeneratePdf(filePath);
        }

        private static IContainer CellStyle(IContainer c) => c
            .Border(0.5f).BorderColor(Colors.Grey.Lighten2)
            .Padding(4)
            .DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken3));

        private static IContainer CellStyleBold(IContainer c) => c
            .Border(0.5f).BorderColor(Colors.Grey.Darken1)
            .Background(Colors.Grey.Lighten3)
            .Padding(4)
            .DefaultTextStyle(x => x.FontSize(9).Bold().FontColor(Colors.Blue.Darken3));

        internal static string Money(decimal value) => value.ToString("N2");

        /// <summary>تحويل صيغة "yyyy-MM-dd ← yyyy-MM-dd" إلى صيغة تاريخ عربية مختصرة.</summary>
        private static string ToArabicDate(string label)
        {
            if (string.IsNullOrEmpty(label)) return label;
            var parts = label.Split("←");
            if (parts.Length != 2) return label;
            var from = parts[0].Trim();
            var to = parts[1].Trim();
            string Fmt(string s)
            {
                if (DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
                    return d.ToString("dd/MM/yyyy");
                return s;
            }
            return $"{Fmt(from)} — {Fmt(to)}";
        }
    }
}
