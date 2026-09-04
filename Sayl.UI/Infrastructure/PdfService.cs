using Common.DTOs.ViewModel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Elements.Table;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// خدمة توليد مستندات PDF بالهوية البصرية الموحدة لسيل — عائلات متمايزة عن قصد:
    /// 1) ExportInvoiceReceipt : إيصال حراري 80mm لفواتير الكاشير السريعة — صفحة واحدة
    ///                          متكيفة الارتفاع وفواصل شرطات متوافقة حراريًا.
    /// 2) ExportInvoiceA4      : فاتورة A4 احترافية مفصلة (بيع / شراء / مرتجعات) — ترويسة
    ///                          رسمية وبطاقات بيانات وجدول بنود مرقّم وصندوق إجماليات وتوقيعات.
    /// 3) ExportTableReport    : تقرير جدولي عام موحّد الهوية لكل تقارير النظام.
    /// 4) ExportPeriodicReport : التقرير الدوري للأرباح ببطاقات مؤشرات وجداول تفصيلية.
    /// ⚠️ كل الطرق متزامنة — يجب استدعاؤها خارج مؤشر الواجهة (Task.Run) وإلا جمُدت الواجهة.
    /// </summary>
    internal static class PdfService
    {
        // ═══ لوحة هوية سيل (كحلي/سماوي) — موحدة عبر كل المستندات ═══
        private const string Navy = "#002B54";
        private const string NavyMid = "#0B3B69";
        private const string Sky = "#93C5FD";
        private const string PanelBlue = "#E7EFFA";
        private const string BorderGrey = "#E2E8F0";
        private const string StripeGrey = "#F8FAFC";
        private const string MutedGrey = "#94A3B8";

        // ضبط الترخيص للمشاريع غير التجارية/الصغيرة (مطلوب من إصدار 2024.3+)
        public static void EnsureLicense() => QuestPDF.Settings.License = LicenseType.Community;

        /// <summary>
        /// يولّد التقرير الدوري كملف A4 احترافي: ترويسة كحلية تتكرر بكل صفحة ←
        /// بطاقات مؤشرات مالية (صافي الإيراد / الربح الإجمالي / المصروفات / صافي الربح) ←
        /// جدول الفترات بإجمالي كحلي بارز ← سُلّم شرح الربح ← الإيرادات اليومية ←
        /// ملخص الجلسات (Z) ← سطر التحصيل.
        /// </summary>
        public static void ExportPeriodicReport(PeriodicReportVM report, string storeName, string filePath)
        {
            EnsureLicense();

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI").DirectionFromRightToLeft());

                    // ══ الترويسة (تتكرر بكل صفحة): شريط كحلي بهوية المحل + شريط سماوي تمييز ══
                    page.Header().Column(col =>
                    {
                        col.Item().Background(Navy).PaddingVertical(11).PaddingHorizontal(14).Row(r =>
                        {
                            r.RelativeItem().Column(c2 =>
                            {
                                c2.Item().Text(storeName).FontSize(19).Bold().FontColor("#FFFFFF");
                                c2.Item().Text($"التقرير الدوري — {report.ReportTypeName}")
                                    .FontSize(9.5f).FontColor(Sky);
                            });
                            r.ConstantItem(190).AlignLeft().Column(c2 =>
                            {
                                c2.Item().Text("سيل POS").FontSize(9.5f).Bold().FontColor(Sky);
                                c2.Item().Text($"من {report.FromDate:yyyy-MM-dd} إلى {report.ToDate:yyyy-MM-dd}")
                                    .FontSize(8.5f).FontColor(MutedGrey);
                            });
                        });
                        col.Item().Height(2.5f).Background(Sky);
                    });

                    page.Content().PaddingTop(12).Column(col =>
                    {
                        // ══ بطاقات المؤشرات المالية (KPI) ══
                        col.Item().Row(r =>
                        {
                            void Card(IContainer item, string label, decimal value, bool gap)
                            {
                                var card = gap ? item.PaddingLeft(8) : item;
                                card.Background(PanelBlue).Border(1).BorderColor("#C7D9F0").Padding(10).Column(c2 =>
                                {
                                    c2.Item().Text(label).FontSize(9.5f).FontColor("#37536E");
                                    c2.Item().PaddingTop(2).Text(Money(value)).FontSize(16).Bold().FontColor(Navy);
                                });
                            }

                            Card(r.RelativeItem(), "صافي الإيراد", report.TotalNetRevenue, gap: true);
                            Card(r.RelativeItem(), "الربح الإجمالي", report.TotalGrossProfit, gap: true);
                            Card(r.RelativeItem(), "المصروفات التشغيلية", report.TotalExpenses, gap: true);
                            Card(r.RelativeItem(), "صافي الربح", report.TotalNetProfit, gap: false);
                        });

                        col.Item().PaddingTop(12).Element(SectionTitle).Text("تفصيل الفترات");

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2.4f);   // الفترة
                                c.RelativeColumn(0.9f);  // الفواتير
                                c.RelativeColumn(1.2f);  // الإيراد
                                c.RelativeColumn(1.2f);  // المرتجعات
                                c.RelativeColumn(1.2f);  // صافي الإيراد
                                c.RelativeColumn(1.2f);  // COGS
                                c.RelativeColumn(1.2f);  // الربح الإجمالي
                                c.RelativeColumn(1.1f);  // المصروفات
                                c.RelativeColumn(1.2f);  // صافي الربح
                            });

                            table.Header(h =>
                            {
                                foreach (var head in new[]
                                {
                                    "الفترة", "الفواتير", "الإيراد", "المرتجعات", "صافي الإيراد",
                                    "COGS صافٍ", "الربح الإجمالي", "المصروفات", "صافي الربح"
                                })
                                    h.Cell().Element(HeaderCell).AlignCenter().Text(head).FontSize(8.2f);
                            });

                            bool stripe = false;
                            foreach (var p in report.Periods)
                            {
                                BodyRow(table, stripe, 8.2f,
                                    $"{p.FromDate:dd/MM/yyyy} — {p.ToDate:dd/MM/yyyy}",
                                    p.InvoiceCount.ToString(),
                                    Money(p.SalesRevenue), Money(p.ReturnsAmount), Money(p.NetRevenue),
                                    Money(p.CogsNet), Money(p.GrossProfit), Money(p.Expenses), Money(p.NetProfit));
                                stripe = !stripe;
                            }

                            TotalRow(table,
                                "الإجمالي", report.Periods.Sum(p => p.InvoiceCount).ToString(),
                                Money(report.TotalSales), Money(report.TotalReturns), Money(report.TotalNetRevenue),
                                Money(report.TotalCogsNet), Money(report.TotalGrossProfit), Money(report.TotalExpenses),
                                Money(report.TotalNetProfit));
                        });

                        // ══ سُلّم شرح حساب الربح (خطوة بخطوة) ══
                        col.Item().PaddingTop(12).Element(SectionTitle).Text("شرح حساب الربح (خطوة بخطوة)");
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(3);
                                c.RelativeColumn(1.4f);
                            });
                            table.Header(h =>
                            {
                                h.Cell().Element(HeaderCell).AlignRight().Text("البند");
                                h.Cell().Element(HeaderCell).AlignLeft().Text("القيمة");
                            });

                            void Ladder(string label, string value, bool result)
                            {
                                table.Cell().Element(result ? ResultCell : BodyCell)
                                    .AlignRight().Text(label).FontSize(9.5f);
                                table.Cell().Element(result ? ResultCell : BodyCell)
                                    .AlignLeft().Text(value).FontSize(9.5f);
                            }

                            Ladder("1) الإيراد الإجمالي (المبيعات)", Money(report.TotalSales), result: false);
                            Ladder("− المرتجعات والمخصومات", "-" + Money(report.TotalReturns), result: false);
                            Ladder("= صافي الإيراد", Money(report.TotalNetRevenue), result: true);
                            Ladder("− تكلفة البضاعة المباعة (COGS صافٍ)", "-" + Money(report.TotalCogsNet), result: false);
                            Ladder("= الربح الإجمالي (بعد المخصومات فقط)", Money(report.TotalGrossProfit), result: true);
                            Ladder("− المصروفات التشغيلية", "-" + Money(report.TotalExpenses), result: false);
                            Ladder("= صافي هامش الربح", Money(report.TotalNetProfit), result: true);
                        });

                        // ══ الإيرادات اليومية (تجمع الفواتير بدل سرد كل فاتورة) ══
                        if (report.DailyLines.Count > 0)
                        {
                            col.Item().PaddingTop(12).Element(SectionTitle).Text("الإيرادات اليومية");

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(1.5f);  // اليوم
                                    c.RelativeColumn(1);    // الفواتير
                                    c.RelativeColumn(1.3f); // إجمالي البيع
                                    c.RelativeColumn(1.3f); // المرتجعات
                                    c.RelativeColumn(1.3f); // صافي الإيراد
                                    c.RelativeColumn(1.3f); // COGS
                                    c.RelativeColumn(1.3f); // الربح الإجمالي
                                });
                                table.Header(h =>
                                {
                                    foreach (var head in new[]
                                        { "اليوم", "الفواتير", "إجمالي البيع", "المرتجعات", "صافي الإيراد", "COGS صافٍ", "الربح الإجمالي" })
                                        h.Cell().Element(HeaderCell).AlignCenter().Text(head).FontSize(8.2f);
                                });

                                bool stripe = false;
                                foreach (var d in report.DailyLines)
                                {
                                    BodyRow(table, stripe, 8.2f,
                                        d.Date.ToString("dd/MM/yyyy"),
                                        d.InvoiceCount.ToString(),
                                        Money(d.GrossSales), Money(d.ReturnsAmount), Money(d.NetSales),
                                        Money(d.CogsNet), Money(d.GrossProfit));
                                    stripe = !stripe;
                                }

                                TotalRow(table,
                                    "الإجمالي", report.DailyLines.Sum(x => x.InvoiceCount).ToString(),
                                    Money(report.DailyLines.Sum(x => x.GrossSales)),
                                    Money(report.DailyLines.Sum(x => x.ReturnsAmount)),
                                    Money(report.DailyLines.Sum(x => x.NetSales)),
                                    Money(report.DailyLines.Sum(x => x.CogsNet)),
                                    Money(report.DailyLines.Sum(x => x.GrossProfit)));
                            });
                        }

                        // ملخص الجلسات المكتملة (نظير تقرير Z)
                        if (report.SessionLines.Count > 0)
                        // ══ ملخص الجلسات المكتملة (نظير تقرير Z) ══
                        if (report.SessionLines.Count > 0)
                        {
                            col.Item().PaddingTop(12).Element(SectionTitle).Text("ملخص الجلسات المكتملة (Z)");

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    for (int i = 0; i < 10; i++)
                                        c.RelativeColumn(i switch { 0 => 0.8f, 1 => 1.5f, 9 => 1.3f, _ => 1f });
                                });
                                table.Header(h =>
                                {
                                    foreach (var head in new[]
                                    {
                                        "الجلسة", "الكاشير", "الافتتاحي", "مبيعات نقدي", "مرتجعات نقدي",
                                        "قبض نقد", "صرف نقد", "النقد المتوقع", "صافي المبيعات", "الإغلاق"
                                    })
                                        h.Cell().Element(HeaderCell).AlignCenter().Text(head).FontSize(7.6f);
                                });

                                bool stripe = false;
                                foreach (var s in report.SessionLines)
                                {
                                    BodyRow(table, stripe, 7.6f,
                                        "#" + s.SessionId,
                                        s.UserName,
                                        Money(s.OpeningAmount), Money(s.CashSales), Money(s.CashReturns),
                                        Money(s.CashIn), Money(s.CashOut), Money(s.ExpectedCash),
                                        Money(s.NetSales),
                                        s.ClosedAt?.ToString("dd/MM/yyyy HH:mm") ?? "-");
                                    stripe = !stripe;
                                }
                            });
                        }

                        // ══ سطر التحصيل الختامي ══
                        col.Item().PaddingTop(12).Background(StripeGrey).Border(1).BorderColor(BorderGrey)
                            .Padding(10).Row(r =>
                            {
                                r.RelativeItem().AlignRight()
                                    .Text($"صافي الإيراد: {Money(report.TotalNetRevenue)}  —  المحصل: {Money(report.TotalCollected)}")
                                    .FontSize(11).Bold().FontColor(Navy);
                                r.ConstantItem(200).AlignLeft()
                                    .Text($"غير المحصل: {Money(report.TotalOutstanding)}")
                                    .FontSize(11).Bold().FontColor("#B91C1C");
                            });
                    });

                    // ══ التذييل: ترقيم + هوية ══
                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor("#CBD5E1");
                        col.Item().AlignCenter().Text(t =>
                        {
                            t.Span("سيل POS — نظام نقاط البيع").FontSize(8.5f).FontColor(MutedGrey);
                            t.Span("    |    صفحة ").FontSize(8.5f).FontColor(MutedGrey);
                            t.CurrentPageNumber().FontSize(8.5f).FontColor(MutedGrey);
                            t.Span(" من ").FontSize(8.5f).FontColor(MutedGrey);
                            t.TotalPages().FontSize(8.5f).FontColor(MutedGrey);
                        });
                    });
                });
            });

            document.GeneratePdf(filePath);
        }

        // ═════════════════════════════════════════════════
        // فاتورة A4 احترافية مفصلة — الطباعة الرسمية للفواتير
        // ═════════════════════════════════════════════════

        /// <summary>
        /// يولّد فاتورة A4 احترافية (بيع / شراء / مرتجعات): ترويسة كحلية رسمية (المحل والهاتف
        /// والرقم الضريبي يمينًا، نوع الفاتورة ورقمها يسارًا) ← بطاقتا بيانات (الطرف / الفاتورة) ←
        /// جدول بنود مرقّم بالدفعة والكمية والأسعار ← سطر إجمالي كحلي ← صندوق إجماليات بارز
        /// بالصافي كحليًا ← سجل المدفوعات ← الملاحظات ← خطّا التوقيع.
        /// </summary>
        public static void ExportInvoiceA4(InvoicePrintVM inv, string storeName, string storePhone,
            string vatNumber, string? receiptFooter, string filePath)
        {
            EnsureLicense();

            string typeLabel = TranslateType(inv.Type);
            bool isSupplierSide = inv.Type is "purchase" or "supplier_return";
            string partyLabel = isSupplierSide ? "المورد" : "العميل";
            string partyName = inv.PartyName ?? (isSupplierSide ? "-" : "عميل نقدي");

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI").DirectionFromRightToLeft());

                    // ══ الترويسة الرسمية: شريط كحلي — المحل يمينًا، عنوان الفاتورة يسارًا ══
                    page.Header().Column(col =>
                    {
                        col.Item().Background(Navy).PaddingVertical(14).PaddingHorizontal(16).Row(r =>
                        {
                            r.RelativeItem().Column(c2 =>
                            {
                                c2.Item().Text(storeName).FontSize(21).Bold().FontColor("#FFFFFF");
                                if (!string.IsNullOrWhiteSpace(storePhone))
                                    c2.Item().PaddingTop(2).Text($"هاتف: {storePhone}").FontSize(9f).FontColor(Sky);
                                if (!string.IsNullOrWhiteSpace(vatNumber))
                                    c2.Item().Text($"الرقم الضريبي: {vatNumber}").FontSize(9f).FontColor(Sky);
                            });
                            r.ConstantItem(230).AlignLeft().Column(c2 =>
                            {
                                c2.Item().Text(typeLabel).FontSize(17).Bold().FontColor("#FFFFFF");
                                c2.Item().PaddingTop(2).Text($"رقم الفاتورة: {inv.InvoiceId}")
                                    .FontSize(10f).FontColor(Sky);
                                c2.Item().Text(inv.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-")
                                    .FontSize(9f).FontColor(MutedGrey);
                            });
                        });
                        col.Item().Height(3).Background(Sky);
                    });

                    page.Content().PaddingTop(12).Column(col =>
                    {
                        // ══ بطاقتا البيانات: الطرف (يمين) + بيانات الفاتورة (يسار) ══
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().PaddingLeft(6).Border(1).BorderColor(BorderGrey).Padding(10).Column(c2 =>
                            {
                                c2.Item().PaddingBottom(4).Text(partyLabel).FontSize(11).Bold().FontColor(NavyMid);
                                c2.Item().Text(partyName).FontSize(11).Bold();
                                c2.Item().Text($"الهاتف: {inv.PartyPhone ?? "-"}").FontSize(9.5f).FontColor("#475569");
                                c2.Item().Text($"العنوان: {inv.PartyAddress ?? "-"}").FontSize(9.5f).FontColor("#475569");
                            });
                            r.ConstantItem(255).Border(1).BorderColor(BorderGrey).Padding(10).Column(c2 =>
                            {
                                c2.Item().PaddingBottom(4).Text("بيانات الفاتورة").FontSize(11).Bold().FontColor(NavyMid);
                                c2.Item().Text($"الحالة: {TranslateStatus(inv.Status)}   |   الدفع: {TranslatePayment(inv.PaymentStatus)}")
                                    .FontSize(9.5f).FontColor("#475569");
                                c2.Item().Text($"الكاشير: {inv.CashierName}").FontSize(9.5f).FontColor("#475569");
                                if (inv.SessionId != null)
                                    c2.Item().Text($"الجلسة: #{inv.SessionId}").FontSize(9.5f).FontColor("#475569");
                                if (inv.OriginalInvoiceId != null)
                                    c2.Item().Text($"مرتجع على فاتورة: #{inv.OriginalInvoiceId}")
                                        .FontSize(9.5f).FontColor("#B91C1C");
                            });
                        });

                        // ══ جدول البنود: م / الصنف / الدفعة / الكمية / السعر / الإجمالي ══
                        col.Item().PaddingTop(14).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(24);      // م
                                c.RelativeColumn(4.5f);   // الصنف
                                c.RelativeColumn(1.8f);   // الدفعة
                                c.ConstantColumn(46);     // الكمية
                                c.RelativeColumn(1.9f);   // سعر الوحدة
                                c.RelativeColumn(2);      // الإجمالي
                            });

                            table.Header(h =>
                            {
                                h.Cell().Element(HeaderCell).AlignCenter().Text("م").FontSize(9f);
                                h.Cell().Element(HeaderCell).AlignCenter().Text("الصنف").FontSize(9.5f);
                                h.Cell().Element(HeaderCell).AlignCenter().Text("الدفعة").FontSize(9.5f);
                                h.Cell().Element(HeaderCell).AlignCenter().Text("الكمية").FontSize(9.5f);
                                h.Cell().Element(HeaderCell).AlignLeft().Text("سعر الوحدة").FontSize(9.5f);
                                h.Cell().Element(HeaderCell).AlignLeft().Text("الإجمالي").FontSize(9.5f);
                            });

                            int no = 0;
                            bool stripe = false;
                            foreach (var item in inv.Items)
                            {
                                no++;
                                BodyRow(table, stripe, 9.5f,
                                    no.ToString(),
                                    item.ProductName,
                                    item.BatchNumber ?? "-",
                                    item.Quantity.ToString(),
                                    Money(item.ItemPrice),
                                    Money(item.ItemPrice * item.Quantity));
                                stripe = !stripe;
                            }

                            // سطر الإجمالي — كحلي بارز
                            table.Cell().ColumnSpan(5).Element(TotalCell).AlignRight()
                                .Text($"إجمالي البنود ({inv.Items.Count}) — {inv.Items.Sum(i => i.Quantity)} وحدة");
                            table.Cell().Element(TotalCell).AlignLeft()
                                .Text(Money(inv.TotalAmount)).FontColor(Sky);
                        });

                        // ══ صندوق الإجماليات — على حافة الأرقام (يسار) ══
                        col.Item().PaddingTop(14).AlignLeft().Width(315)
                            .Background("#F1F5F9").Border(1).BorderColor("#CBD5E1").Padding(10).Column(c2 =>
                            {
                                void MoneyLine(string label, string value)
                                {
                                    c2.Item().PaddingVertical(1.5f).Row(rr =>
                                    {
                                        rr.RelativeItem().Text(label).FontSize(10f);
                                        rr.ConstantItem(130).AlignLeft().Text(value).FontSize(10.5f).Bold();
                                    });
                                }

                                MoneyLine("المجموع (قبل الخصم)", Money(inv.TotalAmount));
                                if (inv.DiscountAmount > 0)
                                    MoneyLine("الخصم", "-" + Money(inv.DiscountAmount));
                                if (inv.TaxAmount > 0)
                                    MoneyLine("الضريبة", Money(inv.TaxAmount));

                                // الصافي — الأبرز في الفاتورة كلها
                                c2.Item().PaddingTop(6).Background(Navy).Padding(9).Row(rr =>
                                {
                                    rr.RelativeItem().Text("الصافي المستحق").FontSize(11).Bold().FontColor("#FFFFFF");
                                    rr.ConstantItem(130).AlignLeft().Text(Money(inv.NetAmount))
                                        .FontSize(12.5f).Bold().FontColor(Sky);
                                });

                                if (inv.PaidAmount > 0)
                                    MoneyLine("المدفوع", Money(inv.PaidAmount));
                                decimal change = inv.PaidAmount - inv.NetAmount;
                                if (change > 0)
                                    MoneyLine("الباقي للعميل", Money(change));
                                if (inv.RemainingAmount > 0)
                                    c2.Item().PaddingVertical(1.5f).Row(rr =>
                                    {
                                        rr.RelativeItem().Text("المتبقي (دَين)").FontSize(10f).FontColor("#B91C1C");
                                        rr.ConstantItem(130).AlignLeft().Text(Money(inv.RemainingAmount))
                                            .FontSize(10.5f).Bold().FontColor("#B91C1C");
                                    });
                            });

                        // ══ سجل المدفوعات ══
                        if (inv.Payments.Count > 0)
                        {
                            col.Item().PaddingTop(14).Element(SectionTitle).Text("سجل المدفوعات");
                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(2);
                                    c.RelativeColumn(2);
                                    c.RelativeColumn(1.4f);
                                });

                                table.Header(h =>
                                {
                                    h.Cell().Element(HeaderCell).AlignCenter().Text("التاريخ");
                                    h.Cell().Element(HeaderCell).AlignCenter().Text("طريقة الدفع");
                                    h.Cell().Element(HeaderCell).AlignLeft().Text("المبلغ");
                                });

                                bool stripe = false;
                                foreach (var p in inv.Payments)
                                {
                                    BodyRow(table, stripe, 9.5f,
                                        p.PaymentDate.ToString("yyyy-MM-dd"),
                                        TranslateMethod(p.PaymentMethod),
                                        Money(p.Amount));
                                    stripe = !stripe;
                                }
                            });
                        }

                        // ══ الملاحظات ══
                        if (!string.IsNullOrWhiteSpace(inv.Notes))
                            col.Item().PaddingTop(10).Background("#FFFBEB").Border(1).BorderColor("#FDE68A")
                                .Padding(8).Text($"ملاحظات: {inv.Notes}").FontSize(9.5f);

                        // ══ خطّا التوقيع ══
                        col.Item().PaddingTop(34).Row(r =>
                        {
                            r.RelativeItem().PaddingRight(24).Column(c2 =>
                            {
                                c2.Item().AlignCenter().Text("توقيع البائع (الكاشير)").FontSize(9f).FontColor("#475569");
                                c2.Item().PaddingTop(16).LineHorizontal(0.7f).LineColor(MutedGrey);
                            });
                            r.RelativeItem().PaddingLeft(24).Column(c2 =>
                            {
                                c2.Item().AlignCenter().Text($"توقيع {partyLabel}").FontSize(9f).FontColor("#475569");
                                c2.Item().PaddingTop(16).LineHorizontal(0.7f).LineColor(MutedGrey);
                            });
                        });
                    });

                    // ══ التذييل: شكر + هوية + ترقيم ══
                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor("#CBD5E1");
                        col.Item().AlignCenter()
                            .Text(string.IsNullOrWhiteSpace(receiptFooter) ? "شكرًا لتعاملكم معنا" : receiptFooter!)
                            .FontSize(9f).FontColor("#475569");
                        col.Item().AlignCenter().Text(t =>
                        {
                            t.Span("سيل POS — نظام نقاط البيع    |    صفحة ").FontSize(8f).FontColor(MutedGrey);
                            t.CurrentPageNumber().FontSize(8f).FontColor(MutedGrey);
                            t.Span(" / ").FontSize(8f).FontColor(MutedGrey);
                            t.TotalPages().FontSize(8f).FontColor(MutedGrey);
                        });
                    });
                });
            });

            document.GeneratePdf(filePath);
        }

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

            // حساب الارتفاع ديناميكيًا مع هامش التفاف أسماء الأصناف (عمود الصنف ≈ 20 حرفًا/سطر)
            int itemsLines = inv.Items.Sum(i =>
                Math.Max(1, (int)Math.Ceiling((i.ProductName?.Length ?? 0) / 20.0)));
            int paymentLines = 1 + (inv.RemainingAmount > 0 ? 1 : 0)
                                 + (inv.Payments.Count > 0 ? 2 : 0);
            int headerExtra = (string.IsNullOrWhiteSpace(storePhone) ? 0 : 1)
                            + (string.IsNullOrWhiteSpace(vatNumber) ? 0 : 1);
            double heightMm = 34
                + headerExtra * 4.2
                + (string.IsNullOrWhiteSpace(inv.Notes) ? 0 : 5)
                + itemsLines * 5.3
                + paymentLines * 5.2
                + (inv.Status != "completed" ? 5 : 0)
                + 26;
            // هامش أمان 15% — اللفافة الحرارية تتحمل الورق الزائد، والاستثناء أغلى من الهامش
            heightMm = Math.Max(62, heightMm * 1.15);

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
                        // فاصل شرطات نصي — التوافق الأمثل مع الرؤوس الحرارية عند إعادة الطباعة كصورة
                        void Separator() => col.Item()
                            .PaddingVertical(1)
                            .AlignCenter().Text(new string('-', 64)).FontColor("#6B7280");

                        // 1) ترويسة المحل — الاسم هو الأقوى بصريًا
                        col.Item().AlignCenter().Text(storeName).FontSize(15).Bold();
                        if (!string.IsNullOrWhiteSpace(storePhone))
                            col.Item().AlignCenter().Text($"هاتف: {storePhone}").FontSize(8);
                        if (!string.IsNullOrWhiteSpace(vatNumber))
                            col.Item().AlignCenter().Text($"الرقم الضريبي: {vatNumber}").FontSize(8);
                        Separator();

                        // 2) معرّف الفاتورة + شارة الحالة غير المكتملة + المرتجع على فاتورة أصلية
                        if (inv.Status != "completed")
                            col.Item().AlignCenter().Text($"*** {TranslateStatus(inv.Status)} ***")
                                .FontSize(8.5f).Bold();
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"{TranslateType(inv.Type)} رقم: {inv.InvoiceId}").FontSize(9.5f).Bold();
                            r.ConstantItem(52).AlignLeft().Text(inv.CreatedAt?.ToString("dd/MM/yyyy") ?? "-").FontSize(8);
                        });
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"الوقت: {inv.CreatedAt:HH:mm}").FontSize(8);
                            if (inv.OriginalInvoiceId != null)
                                r.ConstantItem(140).AlignLeft().Text($"مرتجع على: #{inv.OriginalInvoiceId}").FontSize(7.5f);
                        });

                        // 3) الكاشير (إلزامي فوق البنود) + الطرف (مورد للشراء، عميل لغيره)
                        col.Item().Text($"الكاشير: {inv.CashierName}").FontSize(8);
                        col.Item().Text(inv.Type is "purchase" or "supplier_return"
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


        // ═════════════════════════════════════════════════
        // مصدّر جدول عام لكل تقارير النظام (PDF) — ترويسة موحدة + RTL
        // ═════════════════════════════════════════════════

        /// <summary>
        /// يولّد PDF احترافيًا موحّد الهوية لأي تقرير جدولي (A4):
        /// شريط ترويسة كحلي باسم المحل والتاريخ ← لوحة عنوان التقرير ←
        /// جدول بحدود وأنماط متناوبة وصفوف رقمية بمحاذاة ثابتة ←
        /// سطر السجلات ← تذييل ترقيم مع المستخدم.
        /// (منفصل عن الإيصاع الحرارية 80mm — لكل نوع فلسفة عرضه.)
        /// </summary>
        public static void ExportTableReport(string storeName, string title, string period,
            string userName, string[] headers, List<string[]> rows, string filePath)
        {
            EnsureLicense();

            // كشف تلقائي: هل آخر عمود رقمي (مبالغ)؟ — يستحق صف إجمالي بالأسفل
            // VULN-41: فحص بثقافتين — الجهاز أولًا (الشاشات تنسق بها) ثم
            // الثابتة — فلا يفوت صف الإجمالي على جهاز عربي الفواصل.
            bool ParseMoneyCell(string? s, out decimal v) =>
                decimal.TryParse(s?.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out v) ||
                decimal.TryParse(s?.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out v);

            bool hasAmounts = headers.Length >= 2 && rows.Count > 0 && rows.All(r =>
                r.Length >= headers.Length && ParseMoneyCell(r[^1], out _));

            var document = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI").DirectionFromRightToLeft());

                    // ══ الترويسة: شريط كحلي بهوية سيل (اسم يسار الشريط، تاريخ يمينه) ══
                    page.Header().Column(col =>
                    {
                        col.Item().Background("#002B54").PaddingVertical(10).PaddingHorizontal(14).Row(r =>
                        {
                            r.RelativeItem().AlignRight().Text(storeName)
                                .FontSize(19).Bold().FontColor("#FFFFFF");
                            r.ConstantItem(180).AlignLeft().Column(c2 =>
                            {
                                c2.Item().Text("سيل POS").FontSize(9).Bold().FontColor("#93C5FD");
                                c2.Item().Text(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                                    .FontSize(8.5f).FontColor("#94A3B8");
                            });
                        });
                        // لوحة العنوان: اسم التقرير + الفترة على خلفية فاتحة
                        col.Item().Background("#E7EFFA").BorderBottom(1).BorderColor("#002B54").PaddingVertical(6).PaddingHorizontal(10).Row(r =>
                        {
                            r.RelativeItem().AlignRight().Text(title)
                                .FontSize(13.5f).Bold().FontColor("#00335F");
                            r.ConstantItem(300).AlignLeft().Column(c2 =>
                            {
                                c2.Item().Text(period).FontSize(9.5f).FontColor("#004080");
                                if (!string.IsNullOrWhiteSpace(userName))
                                    c2.Item().Text($"أنشأه: {userName}").FontSize(8.5f).FontColor("#64748B");
                            });
                        });
                    });

                    // ══ المتن: الجدول ══
                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                for (int i = 0; i < headers.Length; i++)
                                    c.RelativeColumn(i == headers.Length - 1 && headers.Length > 3 ? 1.3f : 1f);
                            });

                            // رأس الجدول: شريط كحلي بحد سفلي أزرق
                            table.Header(h =>
                            {
                                foreach (var head in headers)
                                    h.Cell().Background("#0B3B69").PaddingVertical(5).PaddingHorizontal(6).BorderBottom(1)
                                        .BorderColor("#002B54")
                                        .AlignCenter().Text(head).FontSize(9.5f).Bold().FontColor("#FFFFFF");
                            });

                            bool stripe = false;
                            foreach (var row in rows)
                            {
                                bool isStriped = stripe;
                                for (int i = 0; i < headers.Length; i++)
                                {
                                    string value = i < row.Length ? row[i] ?? "" : "";
                                    var cell = table.Cell().PaddingVertical(4).PaddingHorizontal(5).BorderBottom(1)
                                        .BorderColor("#E2E8F0");

                                    if (isStriped)
                                        cell.Background("#F8FAFC");

                                    bool isNumeric = value.Length > 0 &&
                                        value.All(ch => char.IsDigit(ch) || ".,-−%() ".Contains(ch));
                                    if (i > 0 && isNumeric)
                                        cell.AlignLeft().Text(value).FontSize(9f);
                                    else
                                        cell.AlignRight().Text(value).FontSize(9f);
                                }
                                stripe = !stripe;
                            }

                            // صف الإجمالي: للتقارير المالية (آخر عمود أرقام) —
                            // أبرز صف بالجدول: خلفية كحلية ونص أبيض
                            if (hasAmounts)
                            {
                                table.Cell().ColumnSpan((uint)(headers.Length - 1))
                                    .Background("#002B54").PaddingVertical(5).PaddingHorizontal(6)
                                    .AlignRight().Text("الإجمالي").FontSize(10.5f).Bold()
                                    .FontColor("#FFFFFF");

                                decimal total = 0;
                                foreach (var r in rows)
                                    if (ParseMoneyCell(r[^1], out var v)) // VULN-41: ثقافتان
                                        total += v;

                                table.Cell().Background("#002B54").PaddingVertical(5).PaddingHorizontal(6)
                                    .AlignLeft().Text(total.ToString("N2"))
                                    .FontSize(10.5f).Bold().FontColor("#93C5FD");
                            }
                        });

                        col.Item().PaddingTop(8).Row(r =>
                        {
                            r.RelativeItem().AlignRight().Text($"عدد السجلات: {rows.Count}")
                                .FontSize(8.5f).FontColor("#475569");
                            r.ConstantItem(240).AlignLeft().Text($"سيل POS — نظام نقاط البيع")
                                .FontSize(8.5f).FontColor("#94A3B8");
                        });
                    });

                    // ══ التذييل: خط فاصل + ترقيم + سطر النظام ══
                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor("#CBD5E1");
                        col.Item().AlignCenter().Text(t =>
                        {
                            t.Span("صفحة ").FontSize(8.5f).FontColor("#64748B");
                            t.CurrentPageNumber().FontSize(8.5f).FontColor("#64748B");
                            t.Span(" من ").FontSize(8.5f).FontColor("#64748B");
                            t.TotalPages().FontSize(8.5f).FontColor("#64748B");
                        });
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

        private static string TranslateStatus(string status) => status switch
        {
            "completed" => "مكتملة",
            "suspended" => "معلقة",
            "cancelled" => "ملغاة",
            _ => status
        };

        private static string TranslatePayment(string status) => status switch
        {
            "paid" => "مدفوعة",
            "partial" => "جزئية",
            "unpaid" => "غير مدفوعة",
            _ => status
        };

        internal static string Money(decimal value) => value.ToString("N2");

        // ═════════════════════════════════════════════════
        // مكوّنات الهوية الموحدة — رؤوس أقسام، خلايا جدول، صفوف جاهزة
        // ═════════════════════════════════════════════════

        /// <summary>عنوان قسم: خط كحلي يميني ونص كحلي غامق فوق كل جدول.</summary>
        private static IContainer SectionTitle(IContainer c) => c
            .PaddingBottom(5)
            .BorderBottom(1.2f)
            .BorderColor(Navy)
            .DefaultTextStyle(x => x.FontSize(12).Bold().FontColor(Navy));

        /// <summary>خلية رأس جدول: خلفية كحلية بنص أبيض.</summary>
        private static IContainer HeaderCell(IContainer c) => c
            .Background(NavyMid)
            .PaddingVertical(5).PaddingHorizontal(6)
            .BorderBottom(1).BorderColor(Navy)
            .DefaultTextStyle(x => x.FontSize(9).Bold().FontColor("#FFFFFF"));

        /// <summary>خلية بيانات عادية بحد سفلي خفيف.</summary>
        private static IContainer BodyCell(IContainer c) => c
            .PaddingVertical(4).PaddingHorizontal(6)
            .BorderBottom(1).BorderColor(BorderGrey)
            .DefaultTextStyle(x => x.FontSize(9).FontColor("#1E293B"));

        /// <summary>خلية نتيجة (نتائج سُلّم الربح): خلفية زرقاء فاتحة وخط غامق.</summary>
        private static IContainer ResultCell(IContainer c) => c
            .Background(PanelBlue)
            .PaddingVertical(4).PaddingHorizontal(6)
            .BorderBottom(1).BorderColor(Navy)
            .DefaultTextStyle(x => x.FontSize(9).Bold().FontColor(Navy));

        /// <summary>خلية صف الإجمالي: شريط كحلي بنص أبيض وسماوي.</summary>
        private static IContainer TotalCell(IContainer c) => c
            .Background(Navy)
            .PaddingVertical(5).PaddingHorizontal(6)
            .DefaultTextStyle(x => x.FontSize(9.5f).Bold().FontColor("#FFFFFF"));

        /// <summary>
        /// يضيف صف بيانات كاملًا للجدول: أول خلية نص (يمين) والباقي أرقام (يسار، ثابتة العرض)
        /// مع تظليل متناوب رمادي فاتح.
        /// </summary>
        private static void BodyRow(TableDescriptor table, bool striped, float fontSize, params string[] cells)
        {
            // سلسلة واحدة متصلة لكل خلية — إعادة استخدام المتغير نفسه في سلسلتين
            // ترفع DocumentComposeException (single-child container)
            IContainer Style(IContainer c) => striped ? c.Background(StripeGrey) : c;

            for (int i = 0; i < cells.Length; i++)
            {
                var content = table.Cell().Element(Style).Element(BodyCell)
                    .DefaultTextStyle(x => x.FontSize(fontSize).FontColor("#1E293B"));

                if (i == 0)
                    content.AlignRight().Text(cells[i]);
                else
                    content.AlignLeft().Text(cells[i]);
            }
        }

        /// <summary>يضيف صف الإجمالي: «الإجمالي» يمينًا ثم القيم يسارًا — كحلي بارز.</summary>
        private static void TotalRow(TableDescriptor table, params string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                var content = table.Cell().Element(TotalCell);

                if (i == 0)
                    content.AlignRight().Text(cells[i]);
                else
                    content.AlignLeft().Text(cells[i]).FontColor(i == cells.Length - 1 ? Sky : "#FFFFFF");
            }
        }
    }
}
