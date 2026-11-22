using System;

namespace Sayl.Core.Models;

/// <summary>عدّاد الترقيم التجاري السنوي: صف واحد لكل سنة برنامجية —
/// LastIssuedNumber هو آخر تسلسل صدر في تلك السنة عبر كل أنواع الفواتير
/// الأربعة (sale/purchase/customer_return/supplier_return) بلا فجوات مُعاد
/// استخدامها. يقرؤه المولّد اللاحق (CAS/WITH (UPDLOCK)) ليسند التالي،
/// وكتبه Upgrade_19 بترحيل الفواتير القائمة (القاعدة = أعلى لاحقة محفوظة).</summary>
public partial class InvoiceNumberCounter
{
    /// <summary>السنة البرمجية (UTC+3 — اتفاقية ProgramClock) — المفتاح.</summary>
    public int NumberYear { get; set; }

    /// <summary>آخر تسلسل صدر في هذه السنة (لا يُخفَّض أبدًا).</summary>
    public long LastIssuedNumber { get; set; }

    /// <summary>لحظة آخر تحديث للعدّاد (UTC — اتفاقية Upgrade_15).</summary>
    public DateTime LastUpdatedAtUtc { get; set; }
}
