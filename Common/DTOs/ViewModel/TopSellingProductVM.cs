using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class TopSellingProductVM
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public decimal TotalQuantitySold { get; set; }
        public decimal TotalRevenue { get; set; }
    }
}
