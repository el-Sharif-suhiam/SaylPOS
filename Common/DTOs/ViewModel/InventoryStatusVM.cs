using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class InventoryStatusVM
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string? Unit { get; set; }
        public int QuantityAvailable { get; set; }
        public int QuantityReserved { get; set; }
        public int ReorderLevel { get; set; }
        public bool IsLowStock { get; set; }
        public DateTime? LastCountedAt { get; set; }
    }
}
