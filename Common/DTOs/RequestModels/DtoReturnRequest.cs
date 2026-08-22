using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoReturnRequest
    {
        public int OriginalInvoiceId { get; set; }
        public int UserId { get; set; }
        public List<DtoReturnItem> Items { get; set; } = new();
        public string? Notes { get; set; }
    }

    public class DtoReturnItem
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public int? BatchId { get; set; }
    }
}
