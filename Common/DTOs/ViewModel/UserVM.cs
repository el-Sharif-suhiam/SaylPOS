using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class UserVM
    {
        public int UserId { get; set; }
        public string Name { get; set; }
        public string UserName { get; set; }

        public string RoleName { get; set; }
        public bool IsActive { get; set; }

    }
}
