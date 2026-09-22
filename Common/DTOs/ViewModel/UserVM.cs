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

        /// <summary>معرف الدور — الحل الضعف القديم: مطابقة الاسم النصي كانت تنكسر عند إعادة التسمية.</summary>
        public byte RoleId { get; set; }

        /// <summary>قناع بتات صلاحيات الدور (من جدول الأدوار عند الدخول).</summary>
        public int RoleValue { get; set; }

        public bool IsActive { get; set; }

        /// <summary>إجبار تغيير كلمة المرور قبل الدخول — تُملأ من جدول المستخدمين عند تسجيل الدخول فقط.</summary>
        public bool MustChangePassword { get; set; }

    }
}
