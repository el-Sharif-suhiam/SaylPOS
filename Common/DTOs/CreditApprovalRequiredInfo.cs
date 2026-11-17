using System;

namespace Common.DTOs
{
    /// <summary>حمولة نتيجة «تجاوز حد الائتمان — يتطلب موافقة المدير».
    /// تُرفق بنتيجة رفض منظمة (Result/Result&lt;T&gt;) ليميّزها المستدعي آليًا عن
    /// فشل عام، وتحمل ما يحتاجه سير عمل اعتماد المدير (المهمة التالية):
    /// هوية العميل والرصيد الحالي والحد ومبلغ الدين الجديد والمتوقع والفائض.
    /// كل المبالغ ريال وفّات لا كسور — مصدر الرصيد دفتر Customers.Balance.</summary>
    public class CreditApprovalRequiredInfo
    {
        public int CustomerId { get; set; }
        /// <summary>الرصيد (الدَّين) الحالي المحمَّل على العميل قبل هذه العملية.</summary>
        public decimal CurrentOutstanding { get; set; }
        /// <summary>حد الائتمان المعتمد للعميل.</summary>
        public decimal CreditLimit { get; set; }
        /// <summary>مبلغ الدين الجديد الذي طلبت هذه العملية إضافته.</summary>
        public decimal NewCreditAmount { get; set; }
        /// <summary>CurrentOutstanding + NewCreditAmount.</summary>
        public decimal ProjectedOutstanding { get; set; }
        /// <summary>ProjectedOutstanding − CreditLimit (الفائض الذي يتطلب الاعتماد).</summary>
        public decimal ExcessAmount { get; set; }
    }
}
