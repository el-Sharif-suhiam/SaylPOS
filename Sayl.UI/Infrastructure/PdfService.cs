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

        // ═════════════════════════════════════════════════
        // إيصال حراري 80mm — مواصفة 9 أقسام، صفحة واحدة، RTL
        // ═════════════════════════════════════════════════

        /// <summary>
        /// يولّد إيصال فاتورة 80mm بصفحة واحدة متكيفة: الترويسة (اسم المحل الأقوى) ← المعرّفات ←
        /// الكاشير إلزامي ← العميل («عميل نقدي» عند الغياب) ← جدول 4 أعمدة ← عدّاد الأسطر/الوحدات ←
        /// ملخص مالي (الصفر يختفي) ← الإجمالي هو الأبرز ← الدفع/الباقي/المتبقي ← تذييل باسم النظام.
        /// أرقام ثابتة المحاذاة، فاصل متقطع متوافق حرارياً، حتمي (نفس الفاتورة = نفس الإيصال).
        /// </summary>
        public static void ExportInvoiceReceipt(InvoicePrintVM inv, string storeName, string storePhone,
            string vatNumber, string? receiptFooter, string filePath)
        {
            EnsureLicense();

            // حساب الارتفاع المطلوب ديناميكيًا (صفحة واحدة — لا تقسيم صفحات)
            int itemsLines = inv.Items.Count + 2;                       // بنود + ترويسة الجدول
            int paymentLines = 1 + (inv.RemainingAmount > 0 ? 1 : 0)
                                 + (inv.Payments.Count > 0 ? 1 : 0);
            int headerExtra = (string.IsNullOrWhiteSpace(storePhone) ? 0 : 1)
                            + (string.IsNullOrWhiteSpace(vatNumber) ? 0 : 1);
            double heightMm = Math.Max(60,
                30 + headerExtra * 4 + itemsLines * 5.2 + paymentLines * 5.0 + 22);

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(80, (float)heightMm, Unit.Millimetre);
                    page.Margin(3, Unit.Millimetre);
                    page.DefaultTextStyle(x => x
                        .FontSize(8)
                        .FontFamily("Segoe UI")
                        .DirectionFromRightToLeft());

                    page.Content().Column(col =>
                    {
                        void Separator() => col.Item()
                            .PaddingVertical(1.5f)
                            .BorderBottom(0.4f).BorderColor(Colors.Grey.Darken2);

                        // 1) ترويسة المحل — الاسم هو الأقوى بصريًا
                        col.Item().AlignCenter().Text(storeName).FontSize(14).Bold();
                        if (!string.IsNullOrWhiteSpace(storePhone))
                            col.Item().AlignCenter().Text($"هاتف: {storePhone}").FontSize(8);
                        if (!string.IsNullOrWhiteSpace(vatNumber))
                            col.Item().AlignCenter().Text($"الرقم الضريبي: {vatNumber}").FontSize(8);
                        Separator();

                        // 2) معرّف الفاتورة + 3) الكاشير (إلزامي فوق البنود) + العميل
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"{TranslateType(inv.Type)} رقم: {inv.InvoiceId}").FontSize(9).Bold();
                            r.ConstantItem(52).AlignLeft().Text(inv.CreatedAt?.ToString("dd/MM/yyyy") ?? "-").FontSize(8);
                        });
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"الوقت: {inv.CreatedAt:HH:mm}").FontSize(8);
                        });
                        col.Item().Text($"الكاشير: {inv.CashierName}").FontSize(8);
                        col.Item().Text(inv.Type == "purchase"
                            ? $"المورد: {inv.PartyName ?? "-"}"
                            : $"العميل: {inv.PartyName ?? "عميل نقدي"}").FontSize(8);
                        if (!string.IsNullOrWhiteSpace(inv.Notes))
                            col.Item().Text($"ملاحظات: {inv.Notes}").FontSize(7.5f);
                        Separator();

                        // 4) جدول البنود — 4 أعمدة، الأرقام ثابتة المحاذاة
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(5);    // الصنف
                                c.RelativeColumn(1.6f); // الكمية
                                c.RelativeColumn(2.2f); // سعر الوحدة
                                c.RelativeColumn(2.4f); // الإجمالي
                            });
                            table.Header(h =>
                            {
                                h.Cell().Text("الصنف").FontSize(8).Bold();
                                h.Cell().AlignCenter().Text("كمية").FontSize(8).Bold();
                                h.Cell().AlignLeft().Text("سعر").FontSize(8).Bold();
                                h.Cell().AlignLeft().Text("إجمالي").FontSize(8).Bold();
                            });
                            foreach (var item in inv.Items)
                            {
                                table.Cell().Text(item.ProductName).FontSize(8);
                                table.Cell().AlignCenter().Text(item.Quantity.ToString()).FontSize(8);
                                table.Cell().AlignLeft().Text(Money(item.ItemPrice)).FontSize(8);
                                table.Cell().AlignLeft().Text(Money(item.ItemPrice * item.Quantity)).FontSize(8);
                            }
                        });

                        // 5) عدّاد الأسطر/الوحدات
                        int units = inv.Items.Sum(i => i.Quantity);
                        col.Item().Text($"البنود: {inv.Items.Count} أسطر / {units} وحدة").FontSize(7.5f);

                        Separator();

                        // 6) الملخص المالي — الحقول الصفرية تختفي
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text("المجموع").FontSize(9);
                            r.ConstantItem(52).AlignLeft().Text(Money(inv.TotalAmount)).FontSize(9);
                        });
                        if (inv.DiscountAmount > 0)
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("الخصم").FontSize(9);
                                r.ConstantItem(52).AlignLeft().Text(Money(inv.DiscountAmount)).FontSize(9);
                            });
                        if (inv.TaxAmount > 0)
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("الضريبة").FontSize(9);
                                r.ConstantItem(52).AlignLeft().Text(Money(inv.TaxAmount)).FontSize(9);
                            });

                        // 7) الإجمالي — الأقوى ماليًا
                        col.Item().PaddingTop(1).BorderTop(0.4f).BorderColor(Colors.Grey.Darken2);
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text("الإجمالي").FontSize(11).Bold();
                            r.ConstantItem(58).AlignLeft().Text(Money(inv.NetAmount)).FontSize(11).Bold();
                        });

                        // 8) الدفع — الباقي/المتبقي عند وجودهما فقط
                        if (inv.PaidAmount > 0)
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("المدفوع").FontSize(9);
                                r.ConstantItem(52).AlignLeft().Text(Money(inv.PaidAmount)).FontSize(9);
                            });
                        if (inv.Payments.Count > 0)
                        {
                            var methods = string.Join(" + ", inv.Payments
                                .GroupBy(p => TranslateMethod(p.PaymentMethod))
                                .Select(g => $"{g.Key} {Money(g.Sum(x => x.Amount))}"));
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("طريقة الدفع").FontSize(8);
                                r.ConstantItem(52).AlignLeft().Text(methods).FontSize(8);
                            });
                        }
                        decimal change = inv.PaidAmount - inv.NetAmount;
                        if (change > 0)
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("الباقي للعميل").FontSize(9).Bold();
                                r.ConstantItem(52).AlignLeft().Text(Money(change)).FontSize(9).Bold();
                            });
                        if (inv.RemainingAmount > 0)
                            col.Item().Row(r =>
                            {
                                r.RelativeItem().Text("المتبقي (دَين)").FontSize(9).Bold();
                                r.ConstantItem(52).AlignLeft().Text(Money(inv.RemainingAmount)).FontSize(9).Bold();
                            });

                        // 9) التذييل — نص قابل للضبط + اسم النظام
                        col.Item().PaddingTop(4).AlignCenter().Text(
                                string.IsNullOrWhiteSpace(receiptFooter) ? "شكرًا لتعاملكم معنا" : receiptFooter!)
                            .FontSize(9).Bold();
                        col.Item().AlignCenter().Text("سيل POS — نظام نقاط البيع").FontSize(7.5f);
                    });
                });
            });

            document.GeneratePdf(filePath);
        }


        private static string TranslateType(string type) => type switch
        {
            "sale" => "فاتورة بيع",
            "purchase" => "فاتورة شراء",
            "customer_return" => "مرتجع عميل",
            "supplier_return" => "مرتجع مورد",
            _ => type
        };

        private static string TranslateMethod(string method) => method switch
        {
            "cash" => "نقدي",
            "card" => "شبكة",
            "bank_transfer" => "تحويل بنكي",
            _ => method
        };


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
