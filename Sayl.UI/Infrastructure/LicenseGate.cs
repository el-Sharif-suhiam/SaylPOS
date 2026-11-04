using PosLicensing;
using Sayl.UI.Forms;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// بوابة الترخيص الموحدة — نقطة فحص واحدة يعاد استدعاؤها عند حدود التطبيق:
    /// • عند إقلاع البرنامج (Program.Main).
    /// • قبل العملية المالية الأهم (إتمام/تعليق البيع في PosForm) — سياسة
    ///   توثيق الترخيص (LICENSE-SYSTEM-SPEC §11-أ) تشترط فحوصًا دورية قبل
    ///   العمليات الحرجة لا نداءً واحدًا عند الإقلاع فقط.
    /// الرسائل تميّز صراحةً بين: غير مفعّل / منتهي الصلاحية / ساعة مرجوعة /
    /// خلل تحقق مؤقت — فحالة «تعذّر التحقق الآن» لا تُعرض وكأنها تلاعب مؤكد،
    /// ومع ذلك تُغلق المسار (لا تُعامل كحالة آمنة) وتُتيح إعادة المحاولة.
    /// </summary>
    internal static class LicenseGate
    {
        /// <summary>يعيد true فقط عند تفعيل صالح — أي حالة أخرى تُغلق المسار بعد توضيح السبب.</summary>
        public static bool EnsureValidated(IWin32Window? owner)
        {
            while (true)
            {
                switch (ActivationManager.ValidateActivation())
                {
                    case LicenseCheckResult.Activated:
                        return true;

                    case LicenseCheckResult.ValidationUnavailable:
                        // خلل مؤقت في الوصول لبيانات التحقق — إعادة محاولة، وليس اتهامًا بالتلاعب
                        var choice = MessageBox.Show(
                            owner,
                            AppLang.T("License.Gate.ValidationUnavailable"),
                            AppLang.T("Common.Title.Error"),
                            MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button1,
                            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                        if (choice == DialogResult.Retry)
                            continue;
                        return false;

                    case LicenseCheckResult.ClockRolledBack:
                        // تلاعب مؤكد بالساعة (أو حذف المرساة) — نصٌّ واضح قبل شاشة التفعيل؛
                        // التفعيل نفسه سيرفض حتى تُضبط الساعة (نفس المرساة تُفحص عند التفعيل)
                        MessageBox.Show(
                            owner,
                            AppLang.T("License.Gate.ClockRolledBack"),
                            AppLang.T("Common.Title.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error,
                            MessageBoxDefaultButton.Button1,
                            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                        break;
                }

                // غير مفعّل / منتهي / ساعة مرجوعة → شاشة التفعيل
                using (var activation = new ActivationForm())
                {
                    if (activation.ShowDialog(owner) != DialogResult.OK)
                        return false;
                }
                // نجح التفعيل → يعاد الفحص للتأكد قبل السماح بالمرور
            }
        }
    }
}
