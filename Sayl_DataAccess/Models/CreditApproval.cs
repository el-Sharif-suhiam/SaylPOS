using System;

namespace Sayl.Core.Models;

/// <summary>اعتماد المدير لبيع ائتماني يتجاوز حد ائتمان العميل (سجل دائم قابل للتدقيق).
/// صف واحد لكل اعتماد: لقطة موقف العميل الائتماني لحظة الاعتماد (الرصيد الحالي/الحد/
/// الجديد/المتوقع/الفائض) + سبب إلزامي + هوية المدير المعتمِد، وحالة الاستهلاك:
///   granted  = مُنح لم يُستهلك بعد (ربطه لعملية بيع واحدة بالربط الصارم أدناه)
///   consumed = استُهلك بالضبط مرة واحدة بفاتورة ائتمان واحدة (ConsumedByInvoiceId)
///   burned   = أُحرق بلا استهلاك (فشلت عملية البيع بعد الاعتماد) — يبقى سجل تدقيق
///              بأن الاعتماد مُنح ولم يقابله بيع، ولا يصلح لعملية مستقبلية.
/// الإنفاذ لا يعتمد على سجل نصي: الانتقال الذري granted→consumed داخل معاملة البيع
/// (CAS UPDATE … WHERE Status=N'granted' مع مطابقة الربط) هو الآلية التنفيذية.
/// الاعتماد لا يعدّل CreditLimit دائمًا أبدًا — تفويض لعملية واحدة فقط.</summary>
public partial class CreditApproval
{
    public int ApprovalId { get; set; }

    public int CustomerId { get; set; }

    /// <summary>المدير المعتمِد — حساب مدير نظام (AdminRoleValue) تحقق على الخادم بكلمة المرور.</summary>
    public int ManagerUserId { get; set; }

    /// <summary>فاتورة الائتمان المستهلكة للاعتماد — null حتى الاستهلاك (وللـ burned).</summary>
    public int? ConsumedByInvoiceId { get; set; }

    /// <summary>الرصيد (الدَّين) الحالي على العميل لحظة الاعتماد — دفتر Customers.Balance.</summary>
    public decimal CurrentOutstanding { get; set; }

    /// <summary>حد الائتمان المعتمد للعميل لحظة الاعتماد.</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>مبلغ الدين الجديد الذي وافق عليه الاعتماد (آجل الفاتورة المقصودة).</summary>
    public decimal NewCreditAmount { get; set; }

    /// <summary>CurrentOutstanding + NewCreditAmount لحظة الاعتماد.</summary>
    public decimal ProjectedOutstanding { get; set; }

    /// <summary>ProjectedOutstanding − CreditLimit (الفائض الذي اعتمده المدير).</summary>
    public decimal ExcessAmount { get; set; }

    /// <summary>سبب الاعتماد — إلزامي غير فارغ (2..500 حرفًا بعد التشذيب).</summary>
    public string Reason { get; set; } = null!;

    /// <summary>granted / consumed / burned — قيد CHECK على القيم الثلاث.</summary>
    public string Status { get; set; } = "granted";

    /// <summary>لحظة منح الاعتماد (UTC).</summary>
    public DateTime ApprovedAtUtc { get; set; }

    /// <summary>لحظة الاستهلاك داخل معاملة البيع (UTC) — null قبلها.</summary>
    public DateTime? ConsumedAtUtc { get; set; }

    public virtual Customer Customer { get; set; } = null!;

    public virtual User Manager { get; set; } = null!;

    public virtual Invoice? ConsumedByInvoice { get; set; }
}
