using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoParty
    {
        public int PartyId { get; set; }

        public char PartyType { get; set; }

        public string Name { get; set; } = null!;

        public string? Phone1 { get; set; }

        public string? Phone2 { get; set; }

        public string? Email { get; set; }

        public string? AddressLine1 { get; set; }

        public string? AddressLine2 { get; set; }

        public string? Notes { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
      
    }
}
