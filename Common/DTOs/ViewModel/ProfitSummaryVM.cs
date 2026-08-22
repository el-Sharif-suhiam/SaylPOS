using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class ProfitSummaryVM
    {
        public DateOnly FromDate { get; set; }
        public DateOnly ToDate { get; set; }
        public decimal SalesRevenue { get; set; }
        public decimal ReturnsAmount { get; set; }
        public decimal NetRevenue { get; set; }
        public decimal COGS { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal Expenses { get; set; }
        public decimal NetProfit { get; set; }
    }
}
