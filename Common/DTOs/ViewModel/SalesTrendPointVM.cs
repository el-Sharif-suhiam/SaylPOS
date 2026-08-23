using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class SalesTrendPointVM
    {
        public DateOnly PeriodStart { get; set; }
        public DateOnly PeriodEnd { get; set; }
        public int InvoiceCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal ReturnsAmount { get; set; }
        public decimal NetSales { get; set; }
    }
}
