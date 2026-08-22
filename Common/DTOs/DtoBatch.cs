using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoBatch
    {
        public int BatchId { get; set; }

        public int ProductId { get; set; }

        public string BatchNumber { get; set; } = null!;

        public DateOnly? ExpiryDate { get; set; }

        public int BatchQuantity { get; set; }

        public DateTime? CreatedAt { get; set; }
    }
}
