using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class PosSessionVM
    {
        public int SessionId { get; set; }
        public int UserId { get; set; }
        public string? UserName { get; set; }
        public decimal OpeningAmount { get; set; }
        public decimal? ClosingAmount { get; set; }
        public decimal? ExpectedAmount { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string Status { get; set; } = null!;
    }
}
