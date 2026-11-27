using System;
using System.Threading.Tasks;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>المرحلة 5: مساعد تحويل وحدات الإدخال إلى الوحدة الأساس —
    /// نقطة التحويل الوحيدة لكل سطر (حد الخدمة). الواجهة تحسب المثل نفسه
    /// للعرض فقط؛ الواجهة ليست حد الثقة: الخدمة تُعيد الحساب وتُرفض أي
    /// انحراف. الوحدة الأساس (UnitName فارغ/null) تتجاوز كل شيء: لا لقطة.
    /// اللقطة تُلتقط مرة واحدة عند إنشاء السطر ولا تُعاد كتابتها أبدًا.</summary>
    internal static class UnitConversion
    {
        /// <summary>سماحية مطابقة الكمية المُعادة الحساب مع الكمية المُرسلة —
        /// 0.0005 تحت منزلة التقريب الثالثة (سياسة الكميات العشرية 10,3).</summary>
        public const decimal QuantityTolerance = 0.0005m;

        /// <summary>لقطة وحدة الإدخال لسطر معاملة — تُلتقط مرة واحدة عند
        /// الإنشاء من تكوين ProductUnits النشط لحظتها، وتُخزن نصيًا/رقميًا
        /// على السطر؛ العمليات اللاحقة تقرأها ولا تُعيد اشتقاقها أبدًا.
        /// ProductUnitId مؤشر تيسيري فقط (ليس محمَّلًا) — العرض يقرأ اللقطة
        /// النصية أولًا ولا ينضم للجدول أبدًا.</summary>
        public sealed class UnitSnapshot
        {
            public int? ProductUnitId { get; init; }
            public string UnitName { get; init; } = null!;
            public decimal UnitFactorToBase { get; init; }
            public decimal EnteredQuantity { get; init; }
        }

        /// <summary>نتيجة التحويل: الكمية بالوحدة الأساس + اللقطة (null
        /// لإدخالات الوحدة الأساس — السلوك القديم بلا لقطة).</summary>
        public sealed class ConversionResult
        {
            public decimal BaseQuantity { get; init; }
            public UnitSnapshot? Snapshot { get; init; }
        }

        /// <summary>حلّ وحدة الإدخال من التكوين الحالي — نشطة فقط.
        /// UnitName فارغ/null = وحدة أساس (تتجاوز: Ok بلا صف). وحدة غير
        /// معروفة أو غير نشطة = Fail (الواجهة ليست حد الثقة).</summary>
        public static async Task<Result<ProductUnit?>> ResolveUnitAsync(
            SaylDbContext dbContext, int productId, string? unitName)
        {
            if (string.IsNullOrWhiteSpace(unitName))
                return Result<ProductUnit?>.Ok(null);

            string name = unitName.Trim();

            var unit = await dbContext.ProductUnits
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.ProductId == productId && u.Name == name);

            if (unit == null || !unit.IsActive)
                return Result<ProductUnit?>.Fail($"وحدة الإدخال «{unitName}» غير معروفة أو غير نشطة لهذا المنتج.");

            if (unit.FactorToBase <= 0)
                return Result<ProductUnit?>.Fail($"معامل تحويل الوحدة «{unit.Name}» غير صالح.");

            return Result<ProductUnit?>.Ok(unit);
        }

        /// <summary>التحويل مرة واحدة لكل سطر عند حد الخدمة: تُحلّ الوحدة،
        /// ويُتحقق الكمية المُدخلة (&gt; 0) ومعاملها المُرسل، ثم يُعاد حساب
        /// الأساس = Math.Round(EnteredQuantity × المعامل، 3، AwayFromZero)
        /// ويُشترط |الأساس − baseQuantity| &lt; 0.0005 وإلا فشل. الإدخال
        /// بوحدة الأساس (UnitName فارغ) يمر عبر قاعدة الكمية فقط — لا لقطة.
        /// baseQuantity هو الكمية بالوحدة الأساس المُرسلة من الواجهة
        /// (الحقيقة المخزنة المرشحة).</summary>
        public static async Task<Result<ConversionResult>> ValidateAndConvertAsync(
            SaylDbContext dbContext, int productId,
            decimal? enteredQuantity, string? unitName, decimal? unitFactorToBase,
            decimal baseQuantity)
        {
            // ── وحدة أساس: السلوك القديم — الكمية نفسها، بلا لقطة ──
            if (string.IsNullOrWhiteSpace(unitName))
            {
                if (enteredQuantity.HasValue && enteredQuantity.Value <= 0)
                    return Result<ConversionResult>.Fail("الكمية يجب أن تكون أكبر من صفر.");

                decimal baseQty = Math.Round(baseQuantity, 3, MidpointRounding.AwayFromZero);
                if (baseQty <= 0)
                    return Result<ConversionResult>.Fail("الكمية يجب أن تكون أكبر من صفر.");

                return Result<ConversionResult>.Ok(new ConversionResult
                {
                    BaseQuantity = baseQty,
                    Snapshot = null
                });
            }

            // ── وحدة إدخال: حلّ من التكوين النشط — الرفض خادميًا ──
            var unitResult = await ResolveUnitAsync(dbContext, productId, unitName);
            if (!unitResult.IsSuccess)
                return Result<ConversionResult>.Fail(unitResult.Message);

            var unit = unitResult.Data!;
            string name = unit.Name;

            if (!enteredQuantity.HasValue || enteredQuantity.Value <= 0)
                return Result<ConversionResult>.Fail($"الكمية المُدخلة بوحدة «{name}» يجب أن تكون أكبر من صفر.");

            if (!unitFactorToBase.HasValue || unitFactorToBase.Value <= 0)
                return Result<ConversionResult>.Fail($"معامل تحويل الوحدة «{name}» مطلوب وأكبر من صفر.");

            // المعامل المُرسل يجب أن يطابق تكوين الوحدة الحالي (الواجهة ليست حد الثقة)
            if (Math.Abs(unitFactorToBase.Value - unit.FactorToBase) >= QuantityTolerance)
                return Result<ConversionResult>.Fail(
                    $"معامل تحويل الوحدة «{name}» المُرسل ({unitFactorToBase.Value:0.######}) لا يطابق التكوين الحالي ({unit.FactorToBase:0.######}).");

            // التحويل مرة واحدة بالضبط — على المعامل المخزن في التكوين
            decimal recomputedBase = Math.Round(
                enteredQuantity.Value * unit.FactorToBase, 3, MidpointRounding.AwayFromZero);

            if (Math.Abs(baseQuantity - recomputedBase) >= QuantityTolerance)
                return Result<ConversionResult>.Fail(
                    $"الكمية بالوحدة الأساس ({baseQuantity:0.###}) لا تطابق الكمية المُدخلة " +
                    $"({enteredQuantity.Value:0.###} × {unit.FactorToBase:0.######} = {recomputedBase:0.###}) لوحدة «{name}».");

            return Result<ConversionResult>.Ok(new ConversionResult
            {
                BaseQuantity = recomputedBase,
                Snapshot = new UnitSnapshot
                {
                    ProductUnitId = unit.ProductUnitId,
                    UnitName = name,
                    UnitFactorToBase = unit.FactorToBase,
                    EnteredQuantity = Math.Round(enteredQuantity.Value, 3, MidpointRounding.AwayFromZero)
                }
            });
        }

        /// <summary>تحويل تكلفة وحدة الإدخال إلى تكلفة الوحدة الأساس —
        /// مثال الحافة: 115 كرتونًا بمعامل 12 → 9.5833… → 9.58 (خانتان،
        /// AwayFromZero). تُخزن تكلفة الأساس فقط على الدفعة/السطر.</summary>
        public static decimal ConvertCostToBase(decimal enteredUnitCost, decimal factorToBase)
        {
            if (factorToBase <= 0)
                throw new ArgumentOutOfRangeException(nameof(factorToBase), "معامل التحويل يجب أن يكون أكبر من صفر.");
            return Math.Round(enteredUnitCost / factorToBase, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>سعر وحدة الإدخال للعرض فقط = Math.Round(سعر الأساس ×
        /// المعامل، 2، AwayFromZero) — الحقيقة المخزنة تبقى سعر الأساس.</summary>
        public static decimal DisplayPricePerUnit(decimal basePrice, decimal factorToBase)
        {
            if (factorToBase <= 0)
                throw new ArgumentOutOfRangeException(nameof(factorToBase), "معامل التحويل يجب أن يكون أكبر من صفر.");
            return Math.Round(basePrice * factorToBase, 2, MidpointRounding.AwayFromZero);
        }
    }
}
