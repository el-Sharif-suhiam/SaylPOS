using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoRole
    {
        public byte RoleId { get; set; }

        public string Name { get; set; } = null!;

        public int RoleValue { get; set; }
    }
}
