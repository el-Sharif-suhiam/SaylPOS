using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.RequestModels
{
    public class DtoPaymentRequest
    {
        public int? InvoiceId { get; set; }
        public char PartyType { get; set; }
        public int? PartyId { get; set; }
        public decimal Amount { get; set; }
        public DateOnly PaymentDate { get; set; }
        public string Method { get; set; } = "cash";
        public string? Notes { get; set; }

        /// <summary>VULN-05: جلسة الدرج التي استلمت النقد فعليًا —
        /// قد تختلف عن جلسة الفاتورة (تحصيل دَين قديم اليوم يخص درج اليوم).
        /// null = دفعة بلا توجيه درج (خلفية/توافق) — لا تُحمَّل على أي جلسة.</summary>
        public int? SessionId { get; set; }

        /// <summary>معرف الفاعل (المستخدم المسجل دخوله) — للتحقق من ملكية
        /// جلسة الدرج: لا دفع في درج مستخدم آخر إلا بمدير النظام.</summary>
        public int UserId { get; set; }
    }
}
