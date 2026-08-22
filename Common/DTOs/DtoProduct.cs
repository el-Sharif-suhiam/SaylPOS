using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoProduct
    {
        public int ProductId { get; set; }

        public string Name { get; set; } = null!;

        public string? Barcode { get; set; }

        public string? Unit { get; set; }

        public string Type { get; set; } = null!;

        public decimal BasePrice { get; set; }

        public decimal CostPrice { get; set; }

        public int? ReorderLevel { get; set; }

        public bool IsActive { get; set; }

        public DateTime? CreatedAt { get; set; }
    }
}
