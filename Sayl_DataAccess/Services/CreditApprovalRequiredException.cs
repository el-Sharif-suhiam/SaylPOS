using Common.DTOs;

namespace Sayl.Core.Services
{
    /// <summary>رفض أعمال مقصود داخل معاملة البيع: تجاوز حد ائتمان العميل —
    /// يتطلب موافقة المدير (لا فشل تقني). ترث InvalidOperationException لتبقى ضمن
    /// عائلة «رفض تحقق أعمال» عند حدود العمليات، مع حمل حمولة منظمة
    /// (CreditApprovalRequiredInfo) تُحوَّل لنتيجة CreditApprovalRequiredResult
    /// في حد المعاملة — فتبقى النتيجة قابلة للتمييز آليًا عن فشل عام.
    /// الرمي داخل المعاملة يعيد تراجعها بالكامل: لا فاتورة ولا دَين ولا حركة مخزون.</summary>
    public class CreditApprovalRequiredException : InvalidOperationException
    {
        public CreditApprovalRequiredInfo Info { get; }

        public CreditApprovalRequiredException(CreditApprovalRequiredInfo info, string message)
            : base(message)
        {
            Info = info;
        }
    }
}
