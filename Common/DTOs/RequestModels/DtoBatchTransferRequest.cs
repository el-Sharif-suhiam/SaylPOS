using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoBatchTransferRequest
    {
        public int SourceBatchId { get; set; }
        public string TargetBatchNumber { get; set; } = null!;
        public int Quantity { get; set; }
        public DateOnly? TargetExpiryDate { get; set; }
        public int UserId { get; set; }
    }
}
