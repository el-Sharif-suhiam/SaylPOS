using Microsoft.Data.SqlClient;

namespace Sayl.Core.Services
{
    /// <summary>
    /// باراميترات SQL صريحة النوع للكميات العشرية (المرحلة 4).
    /// لماذا: EF Core يُنشئ باراميتر قيمة decimal داخل ExecuteSqlInterpolatedAsync
    /// بنوعه الافتراضي DECIMAL(18,2) — فيُقرَّب 1.235 إلى 1.24 عند العميل قبل أن
    /// يراه SQL Server أصلًا، فيفسد «مجموع الدفعات = المخزون المتاح» بخطأ 0.005
    /// على أعمدة DECIMAL(10,3) (المخزون/الدفعات/الحركات). ربط الباراميتر بمقياس
    /// العمود (10,3) صراحةً يُمرر الكمية كما قُرِّبت مرة واحدة عند مدخل الخدمة.
    /// ملاحظة: قيم النقود DECIMAL(10,2) لا تتأثر (الافتراضي (18,2) يغطيها)،
    /// وExecuteUpdateAsync/كتابة الكيانات المتتبعة تستخدم خريطة العمود نفسها.
    /// كل استدعاء يصنع نسخة جديدة — باراميتر واحد لا يتكرر باسم واحد في أمر واحد.
    /// </summary>
    internal static class SqlQty
    {
        /// <summary>باراميتر كمية بمقياس أعمدة الكميات DECIMAL(10,3).</summary>
        public static SqlParameter Param(string name, decimal value) =>
            new(name, System.Data.SqlDbType.Decimal) { Precision = 10, Scale = 3, Value = value };
    }
}
