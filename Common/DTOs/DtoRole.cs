using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs
{
    public class DtoRole
    {
        public byte RoleId { get; set; }

        public string Name { get; set; } = null!;

        /// <summary>قناع بتات الشاشات — انظر Common\Utils\AppPermissions.cs.</summary>
        public int RoleValue { get; set; }

        public bool IsActive { get; set; }
    }
}
