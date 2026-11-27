using System;
using System.Collections.Generic;
using System.Text;

namespace Common.DTOs.ViewModel
{
    public class InventoryStatusVM
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string? Unit { get; set; }
        public decimal QuantityAvailable { get; set; }
        public decimal QuantityReserved { get; set; }
        /// <summary>المتاح للبيع فعليًا (قاعدة GetNetAvailableQuantityAsync):
        /// مجموع دفعات غير منتهية الصلاحية وغير محجوبة − المحجوز، وغير سالب —
        /// أقل من QuantityAvailable (المخزون الفعلي) بقدر المنتهي/المحجوب/المحجوز.
        /// مؤشر المخزون المنخفض (IsLowStock) يُقرأ من هذا الحقل لا من الفعلي.</summary>
        public decimal NetAvailable { get; set; }
        public decimal ReorderLevel { get; set; }
        /// <summary>عتبة الانخفاض على المتاح للبيع (NetAvailable ≤ حد الطلب) —
        /// نفس القاعدة ≤ الموجودة سابقًا على المخزون الفعلي.</summary>
        public bool IsLowStock { get; set; }
        public DateTime? LastCountedAt { get; set; }
    }
}
