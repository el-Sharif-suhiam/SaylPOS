using Common.Utils;
using System.Diagnostics;

namespace Sayl.Tests;

/// <summary>
/// ساعة الاختبار المركزية (Task ساعة SQL الموثقة — بنية الاختبار):
///
/// 📖 لماذا؟ بعد إلغاء الاحتياطي الإرثي (+3) أصبح ProgramClock فشلًا مغلقًا
/// قبل نشر التوقيت والمرساة — لكن ~17 ملف اختبار مساعدًا يقرأ
/// ProgramClock.Today بلا إقلاع ساعة (عبر مساعدات Today الخاصة بها).
/// هذا النوع ينشر حالة اختبار موحدة محددة (deterministic) لكل الرَن:
///  • التوقيت: Test/FixedPlus3 — توقيت مخصص ثابت (+3، بلا توقيت صيفي)
///    فيعطي ToLocal/FromUtc/Today القيم نفسها التي كان الاحتياطي الإرثي
///    يعطيها — أمان الترحيل: اختبارات الخدمات القديمة تمر بلا تغيير.
///  • المرساة: لحظة UTC الحالية للجهاز + لقطة Stopwatch لحظة النشر —
///    فتتقدم الساعة الرتيبة مع الزمن الحقيقي (قيم «اليوم» صحيحة طوال الرَن).
/// بلا اعتماد على توقيت OS للجهاز — آمنة على CI.
/// </summary>
public static class TestClockConfig
{
    /// <summary>معرّف توقيت الاختبار الثابت (+3 — مطابق للاحتياطي الإرثي الملغى).</summary>
    public const string FixedPlus3Id = "Test/FixedPlus3";

    /// <summary>التوقيت الثابت للاختبارات: +3 بلا DST — محدد تمامًا
    /// (بلا اعتماد على قواعد ويندوز أو توقيت جهاز CI).</summary>
    public static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone(
        FixedPlus3Id, TimeSpan.FromHours(3), FixedPlus3Id, FixedPlus3Id);

    /// <summary>ضبط الحالة المعيارية لساعة الاختبار: التوقيت الثابت + مرساة
    /// UTC جديدة بلقطة Stopwatch لحظة النشر. يُنادى مرة من DatabaseFixture
    /// عند تهيئة الرَن، ومن كتل finally في StoreClockTests لاستعادة الحالة
    /// المعيارية (لا null أبدًا — إعادة التصفير تُفشل اختبارات المساعدين).</summary>
    public static void Configure()
        => ProgramClock.ConfigureForTests(Tz, DateTime.UtcNow, Stopwatch.GetTimestamp());
}
