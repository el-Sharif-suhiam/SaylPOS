using System;

namespace Sayl.Core.Models
{
    /// <summary>
    /// سطر جرد فعلي لمنتج في جلسة جرد واحدة: الكمية الدفترية لحظة البدء (Snapshot)
    /// والكمية الدفترية لحظة العد الفعلي لكل سطر، والمُعدّاة فعليًا،
    /// والفرق وتكلفته — يُرحَّل عبر AdjustStockAsync.
    /// </summary>
    public partial class StockCount
    {
        public int CountId { get; set; }

        /// <summary>معرف جلسة الجرد — كل الجلسة تاريخ واحد (تُولَّد من CountId الأول أو بتاريخ اليوم).</summary>
        public int CountSessionId { get; set; }

        public int ProductId { get; set; }

        public string ProductName { get; set; } = null!;

        /// <summary>الكمية الدفترية لحظة بدء الجرد (Snapshot — لا تتغير بعده).</summary>
        public int BookQuantity { get; set; }

        /// <summary>الكمية المعدودة فعليًا — nullable حتى يُعدّ المنتج.</summary>
        public int? CountedQuantity { get; set; }

        public decimal UnitCost { get; set; }

        /// <summary>
        /// الفرق = المعدود − الدفتري عند لحظة العد الفعلي (BookQuantityAtCount).
        /// يحافظ على أي حركات حصلت بعد العد في الرصيد النهائي.
        /// </summary>
        public int? Difference => CountedQuantity - (BookQuantityAtCount ?? BookQuantity);

        public decimal DifferenceCost => (Difference ?? 0) * UnitCost;

        public bool IsPosted { get; set; }

        public int CountedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>لحظة العد الفعلي لهذا السطر — null إن لم يُعد بعد.</summary>
        public DateTime? CountedAt { get; set; }

        /// <summary>
        /// الكمية الدفترية لحظة العد الفعلي (snapshot). تُستخدم لحساب الفرق عند الترحيل —
        /// لا لقطة قديمة من بدء الجلسة. null يعني أنه لم يُعد بعد.
        /// </summary>
        public int? BookQuantityAtCount { get; set; }

        public virtual Product Product { get; set; } = null!;
    }
}
