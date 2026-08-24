using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    /// <summary>دفعة قريبة من انتهاء الصلاحية أو منتهية.</summary>
    public class ExpiringBatchVM
    {
        public int BatchId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string BatchNumber { get; set; } = null!;
        public DateOnly ExpiryDate { get; set; }
        public int Quantity { get; set; }
        public int DaysUntilExpiry { get; set; }
        public bool IsExpired { get; set; }
    }
}
