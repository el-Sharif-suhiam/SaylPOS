using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoStockReceiveRequest
    {
        public int ProductId { get; set; }
        public string BatchNumber { get; set; } = null!;
        public int Quantity { get; set; }
        public DateOnly? ExpiryDate { get; set; }
        public int? InvoiceId { get; set; }
    }
}
