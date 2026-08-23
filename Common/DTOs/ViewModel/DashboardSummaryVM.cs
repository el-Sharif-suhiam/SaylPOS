using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class DashboardSummaryVM
    {
        public DateOnly Date { get; set; }

        // اليوم
        public int TodayInvoiceCount { get; set; }
        public decimal TodayNetSales { get; set; }
        public decimal TodayCollected { get; set; }
        public decimal TodayReturns { get; set; }
        public decimal TodayExpenses { get; set; }

        // الشهر حتى تاريخه
        public decimal MonthNetSales { get; set; }
        public decimal MonthExpenses { get; set; }
        public decimal MonthGrossProfit { get; set; }
        public decimal MonthNetProfit { get; set; }

        // المخزون والمنتجات
        public int LowStockCount { get; set; }
        public int TotalActiveProducts { get; set; }
        public decimal TotalStockValue { get; set; }

        // قوائم مساعدة للعرض
        public List<TopSellingProductVM> TopProducts { get; set; } = new();
        public List<InventoryStatusVM> LowStockItems { get; set; } = new();
    }
}
