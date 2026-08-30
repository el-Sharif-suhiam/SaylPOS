using System;
using System.Collections.Generic;

namespace Common.DTOs.ViewModel
{
    /// <summary>
    /// فترة واحدة داخل التقرير الدوري (شهر / ربع / نصف سنة / سنة) بانتفاصيلها المالية.
    /// </summary>
    public class PeriodicPeriodVM
    {
        public string PeriodLabel { get; set; } = null!;
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }

        public int InvoiceCount { get; set; }
        public decimal SalesRevenue { get; set; }
        public decimal ReturnsAmount { get; set; }
        public decimal NetRevenue { get; set; }

        public decimal CogsSales { get; set; }
        public decimal CogsReturns { get; set; }
        public decimal CogsNet { get; set; }

        public decimal GrossProfit { get; set; }
        public decimal Expenses { get; set; }
        public decimal NetProfit { get; set; }

        public decimal Collected { get; set; }
        public decimal Outstanding { get; set; }
    }

    /// <summary>
    /// بند مالي مفصل يعكس حركة صندوق فعلي (دفعات) في فترة محدد في التقرير.
    /// </summary>
    public class PeriodCashLineVM
    {
        public DateOnly Date { get; set; }
        public string Type { get; set; } = null!;
        public string PartyName { get; set; } = null!;
        public decimal Amount { get; set; }
        public string Method { get; set; } = null!;
        public string? Notes { get; set; }
    }

    /// <summary>
    /// التقرير الدوري الكامل: مدة + نوع + ملخصات الفترات + تفصيل الفواتير المفصل.
    /// </summary>
    public class PeriodicReportVM
    {
        public string ReportTypeName { get; set; } = null!;
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }

        public List<PeriodicPeriodVM> Periods { get; set; } = new();

        // إجماليات التقرير
        public decimal TotalSales { get; set; }
        public decimal TotalReturns { get; set; }
        public decimal TotalNetRevenue { get; set; }
        public decimal TotalCogsNet { get; set; }
        public decimal TotalGrossProfit { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal TotalNetProfit { get; set; }
        public decimal TotalCollected { get; set; }
        public decimal TotalOutstanding { get; set; }

        // قائمة الفواتير التفصيلية داخل المدة كاملا (للتصدير التفصيلي)
        public List<InvoicePrintVM> Invoices { get; set; } = new();

        // تجميع الإيرادات اليومية (بدل سرد كل فاتورة على حدة)
        public List<PeriodDailyLineVM> DailyLines { get; set; } = new();

        // ملخص الجلسات المكتملة داخل المدة (مثل تقرير Z)
        public List<PeriodSessionLineVM> SessionLines { get; set; } = new();

        // التدفق النقدي (الدفعات) داخل المدة
        public List<PeriodCashLineVM> CashLines { get; set; } = new();
    }

    /// <summary>ملخص إيرادات يوم واحد (تجميع الجلسات/الفواتير لليوم).</summary>
    public class PeriodDailyLineVM
    {
        public DateOnly Date { get; set; }
        public int InvoiceCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal ReturnsAmount { get; set; }
        public decimal NetSales { get; set; }
        public decimal CogsNet { get; set; }
        public decimal GrossProfit { get; set; }
    }

    /// <summary>ملخص جلسة مكتملة (نظير تقرير Z عند إغلاق الجلسة).</summary>
    public class PeriodSessionLineVM
    {
        public int SessionId { get; set; }
        public string UserName { get; set; } = null!;
        public decimal OpeningAmount { get; set; }
        public decimal CashSales { get; set; }
        public decimal CashReturns { get; set; }
        public decimal CashIn { get; set; }
        public decimal CashOut { get; set; }
        public decimal ExpectedCash { get; set; }
        public int InvoiceCount { get; set; }
        public decimal NetSales { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
    }

    /// <summary>أنواع التقسيم الزمني للتقرير الدوري.</summary>
    public enum enPeriodReportType
    {
        Quarterly = 4,     // ربع سنوي
        HalfYearly = 6,    // نصف سنوي
        Yearly = 12        // سنوي
    }
}
