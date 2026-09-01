using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    /// <summary>ربحية منتج واحد: الإيراد والتكلفة والربح والهامش وحصة الربح الكلية.</summary>
    public class ProductProfitVM
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
        public decimal Cost { get; set; }
        public decimal Profit { get; set; }
        /// <summary>هامش الربح: الربح ÷ الإيراد × 100.</summary>
        public decimal MarginPercent { get; set; }
        /// <summary>حصة هذا المنتج من إجمالي أرباح الفترة × 100.</summary>
        public decimal ProfitSharePercent { get; set; }
    }

    /// <summary>أفضل عميل في فترة: فواتيره وصافي إيراده وربحه وهامشه.</summary>
    public class CustomerProfitVM
    {
        public int CustomerId { get; set; }
        public string CustomerName { get; set; } = null!;
        public int InvoiceCount { get; set; }
        public decimal NetRevenue { get; set; }
        public decimal Profit { get; set; }
        public decimal MarginPercent { get; set; }
    }

    /// <summary>ديون عميل موزعة على شرائح العمر (لدى منه دين غير مسدد).</summary>
    public class DebtAgeingVM
    {
        public int CustomerId { get; set; }
        public string CustomerName { get; set; } = null!;
        public string? Phone { get; set; }
        public decimal Balance { get; set; }
        /// <summary>دين نشأ خلال آخر 30 يومًا من تاريخ التقرير.</summary>
        public decimal Age0to30 { get; set; }
        /// <summary>دين عمره 31-60 يومًا.</summary>
        public decimal Age31to60 { get; set; }
        /// <summary>دين عمره 61-90 يومًا.</summary>
        public decimal Age61to90 { get; set; }
        /// <summary>دين عمره أكثر من 90 يومًا — الأعلى خطورة للتحصيل.</summary>
        public decimal Age90plus { get; set; }
    }

    /// <summary>حركة الصندوق لفترة: كل ما دخل وخرج نقدًا فعليًا.</summary>
    public class CashFlowVM
    {
        public decimal CashSales { get; set; }
        public decimal CashReturns { get; set; }
        public decimal CustomerPayments { get; set; }
        public decimal SupplierPayments { get; set; }
        public decimal CashIn { get; set; }
        public decimal CashOut { get; set; }
        public decimal ExpensesPaid { get; set; }
        public decimal SalariesPaid { get; set; }
        /// <summary>داخل − خارج: صافي ما زاد في الصندوق خلال الفترة.</summary>
        public decimal NetMovement => CashSales - CashReturns + CustomerPayments - SupplierPayments
                                      + CashIn - CashOut - ExpensesPaid - SalariesPaid;
    }
}
