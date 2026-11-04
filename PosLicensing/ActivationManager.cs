using Microsoft.Win32;
using System;
using System.Security.Cryptography;
using System.Text;

namespace PosLicensing
{
    /// <summary>
    /// نتيجة فحص التفعيل — تميّز صراحةً بين «غير مفعّل» و«منتهي» و«ساعة مرجوعة»
    /// و«تعذّر التحقق الآن» حتى لا تُعرض كل الحالات للمستخدم وكأنها تلاعب.
    /// </summary>
    public enum LicenseCheckResult
    {
        /// <summary>رخصة صالحة لهذا الجهاز وسارية.</summary>
        Activated,
        /// <summary>لا توجد رخصة مخزَّنة أو أنها غير قابلة للفك/التحقق.</summary>
        NotActivated,
        /// <summary>رخصة موجودة وصحيحة لكن تاريخ انتهائها فات.</summary>
        Expired,
        /// <summary>مرساة الزمن تؤكد رجوع ساعة الجهاز (أو حُذفت المرساة) — رفض.</summary>
        ClockRolledBack,
        /// <summary>تعذّر الوصول لبيانات المرساة هذه الجولة (خلل مؤقت في النظام) — لا يُعتبر حالة آمنة.</summary>
        ValidationUnavailable
    }

    /// <summary>سبب فشل التفعيل — تُترجمه الواجهة للغة المستخدم.</summary>
    public enum ActivationFailure
    {
        None,
        /// <summary>نص الرخصة تالف أو توقيعه غير صحيح.</summary>
        InvalidLicense,
        /// <summary>الرخصة موقَّعة لكنها لجهاز آخر.</summary>
        WrongDevice,
        /// <summary>الرخصة صحيحة لكن تاريخ انتهائها فات.</summary>
        Expired,
        /// <summary>ساعة الجهاز مرجوعة — يجب ضبط التاريخ/الوقت أولًا.</summary>
        ClockRolledBack
    }

    public static class ActivationManager
    {
        private const string RegistryPath = @"SOFTWARE\Sayl\License";
        private const string ValueName = "Data";

        /// <summary>تجاوز مسار الريجستري للاختبارات فقط — null في الإنتاج.</summary>
        internal static string RegistryPathOverride = null;

        private static string EffectiveRegistryPath => RegistryPathOverride ?? RegistryPath;

internal static string PublicKeyXml = "<RSAKeyValue><Modulus>oRTF2J1LgULwWHnd3LctxJVPPs5cHPpdCYcJDusopCj6X+GFS3iAPpBOPoIYLzvF7JXiILoABoJSp+yY7f8kMLXmKe4gx2UDl2XbWNVypXFUMoXSQGsrpReQU20/y/+OdOyV4Xs3n8HX8Zy5kn8+HJZNvIq7HxHuUcYNbZ3IzAmk9KzJ5V2UhP2YkZ0IUhxb8UrN8DVLNf9MNrJTLRxYnMgjSEEUIRJrLshz9OhMfI1aqEIyM6a3vjqSm0H48YQA51gjmTun81xiY2KFow2pnrTSydH9kUOe6tYIsn6XVWUJPSWkzpDy2/P66LcV0Wptvlbzzhj55JW0t3QcFNKbPQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        public static string GetDeviceCode()
        {
            return MachineFingerprint.GetDeviceCode();
        }

        public static bool IsActivated()
        {
            return ValidateActivation() == LicenseCheckResult.Activated;
        }

        /// <summary>
        /// الفحص الكامل مع تمييز الحالات (يُستدعى عند إقلاع البرنامج وقبل
        /// العمليات الحساسة — سياسة التوثيق §11: فحوص دورية لا نداء وحيد).
        /// الحالة ValidationUnavailable تفشل بالإغلاق (لا تُعامل كحالة آمنة)
        /// لكنها تُعرض للمستخدم كخلل مؤقت لا كتلاعب.
        /// </summary>
        public static LicenseCheckResult ValidateActivation()
        {
            string stored = LoadStoredLicense();
            if (stored == null) return LicenseCheckResult.NotActivated;

            LicensePayload payload;
            if (!LicenseCrypto.TryVerify(stored, PublicKeyXml, out payload))
                return LicenseCheckResult.NotActivated;

            if (payload.ExpiryTicksUtc < DateTime.UtcNow.Ticks)
                return LicenseCheckResult.Expired;

            // VULN-27: مرساة زمنية — رجوع الساعة لا يُحيي رخصة منتهية.
            switch (EvaluateClockAnchor())
            {
                case ClockAnchorStatus.Ok:
                    break;
                case ClockAnchorStatus.RolledBack:
                case ClockAnchorStatus.Missing:
                    return LicenseCheckResult.ClockRolledBack;
                case ClockAnchorStatus.Unavailable:
                    // فشل الوصول لبيانات التلاعب ≠ حالة آمنة — إغلاق صريح مميَّز
                    return LicenseCheckResult.ValidationUnavailable;
            }

            string currentHwid = MachineFingerprint.Normalize(MachineFingerprint.GetDeviceCode());
            return string.Equals(payload.Hwid, currentHwid, StringComparison.Ordinal)
                ? LicenseCheckResult.Activated
                : LicenseCheckResult.NotActivated;
        }

        public static bool Activate(string licenseText, out string error)
        {
            error = null;
            if (!TryActivate(licenseText, out ActivationFailure failure))
            {
                // رسائل عربية للتوافق مع الاستدعاءات القديمة؛ الواجهة تُفضِّل TryActivate + AppLang
                error = failure switch
                {
                    ActivationFailure.InvalidLicense => "الرخصة غير صالحة أو تم التلاعب بها",
                    ActivationFailure.WrongDevice => "هذه الرخصة مخصصة لجهاز آخر ولا يمكن تفعيلها هنا",
                    ActivationFailure.Expired => "انتهت صلاحية هذه الرخصة",
                    ActivationFailure.ClockRolledBack => "يبدو أن تاريخ/وقت هذا الجهاز مضبوط بشكل خاطئ — صحّح التاريخ ثم أعد المحاولة",
                    _ => "فشل التفعيل"
                };
                return false;
            }
            return true;
        }

        /// <summary>التفعيل مع سبب فشل مرمَّز — الواجهة تترجمه بالعربية/الإنجليزية.</summary>
        public static bool TryActivate(string licenseText, out ActivationFailure failure)
        {
            failure = ActivationFailure.None;

            LicensePayload payload;
            if (!LicenseCrypto.TryVerify(licenseText, PublicKeyXml, out payload))
            {
                failure = ActivationFailure.InvalidLicense;
                return false;
            }

            string currentHwid = MachineFingerprint.Normalize(MachineFingerprint.GetDeviceCode());
            if (!string.Equals(payload.Hwid, currentHwid, StringComparison.Ordinal))
            {
                failure = ActivationFailure.WrongDevice;
                return false;
            }

            if (payload.ExpiryTicksUtc < DateTime.UtcNow.Ticks)
            {
                failure = ActivationFailure.Expired;
                return false;
            }

            // VULN-27: نفس المرساة تُطبَّق عند التفعيل — وإلا استُؤنفت رخصة
            // منتهية بإرجاع الساعة ثم إعادة إدخالها (تحايل على الفحص الدوري).
            switch (EvaluateClockAnchor())
            {
                case ClockAnchorStatus.RolledBack:
                case ClockAnchorStatus.Missing when HasStoredLicense():
                    failure = ActivationFailure.ClockRolledBack;
                    return false;
                case ClockAnchorStatus.Unavailable:
                    // لا نخزّن رخصة وفوق بيانات مرساة تعذّر التحقق منها
                    failure = ActivationFailure.ClockRolledBack;
                    return false;
            }

            SaveStoredLicense(licenseText.Trim());
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(EffectiveRegistryPath);
                key.SetValue(LastSeenValueName, DateTime.UtcNow.Ticks.ToString());
            }
            catch { }
            return true;
        }

        public static void Deactivate()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(EffectiveRegistryPath, true))
            {
                if (key != null) key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// يقرأ الرخصة المخزَّنة ويعيد بياناتها للعرض فقط (نافذة «حول»).
        /// لا يغيّر أي شيء؛ والفشل يعني ببساطة عدم وجود رخصة صالحة مخزَّنة.
        /// </summary>
        public static bool TryGetLicenseInfo(out string customer, out DateTime? expiryUtc)
        {
            customer = null;
            expiryUtc = null;

            string stored = LoadStoredLicense();
            if (stored == null) return false;

            LicensePayload payload;
            if (!LicenseCrypto.TryVerify(stored, PublicKeyXml, out payload)) return false;

            customer = string.IsNullOrEmpty(payload.Customer) ? null : payload.Customer;
            expiryUtc = payload.ExpiryTicksUtc >= DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay
                ? (DateTime?)null
                : new DateTime(payload.ExpiryTicksUtc, DateTimeKind.Utc);
            return true;
        }

        // ─────────────────────────────────────────────────
        // VULN-27: مرساة زمنية ضد رجوع الساعة
        // كان الفحص يعتمد على ساعة الجهاز وحدها — رجوع الساعة أيامًا يعيد
        // إحياء رخصة منتهية. الآن: كل فحص ناجح يخزن «آخر وقت رؤية» (UTC)
        // في قيمة Registry منفصلة؛ وإن رُصد الوقت الحالي أقدم من آخر رؤية
        // بأكثر من يوم واحد كامل (سماح ضبط يدوي طفيف) تُعتبر الساعة مرجوعة
        // والرخصة مرفوضة — الترخيص التجريبي لا يُمدد بإرجاع عقارب الساعة.
        // ─────────────────────────────────────────────────
        private const string LastSeenValueName = "LastSeenUtc";
        private static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromDays(1);

        /// <summary>حالة المرساة الزمنية — تمييز ثلاثي صريح.</summary>
        internal enum ClockAnchorStatus
        {
            /// <summary>المرساة موجودة، الساعة سليمة، وتم تحديث المرساة.</summary>
            Ok,
            /// <summary>المرساة تثبت رجوع الساعة ما وراء السماح.</summary>
            RolledBack,
            /// <summary>قيمة المرساة مفقودة/تالفة على جهاز عليه رخصة مخزَّنة (حذف مقصود لإحياء رخصة) — رفض.</summary>
            Missing,
            /// <summary>تعذّر الوصول للريجستري هذه الجولة (خلل مؤقت) — لا يُعتبر حالة آمنة ولا تلاعبًا مؤكدًا.</summary>
            Unavailable
        }

        internal static ClockAnchorStatus EvaluateClockAnchor()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(EffectiveRegistryPath, true)
                                        ?? Registry.CurrentUser.CreateSubKey(EffectiveRegistryPath);
                return EvaluateClockAnchorCore(key.GetValue(LastSeenValueName) as string, DateTime.UtcNow.Ticks);
            }
            catch
            {
                // تعذّر الوصول للمرساة — لا نكتبيها كنجاح ولا كتلاعب مؤكد؛
                // المتصل (ValidateActivation/TryActivate) يفشل بالإغلاق بحالة مميَّزة
                // والمستخدم يرى «خلل مؤقت — أعد المحاولة» لا رسالة تلاعب.
                return ClockAnchorStatus.Unavailable;
            }
        }

        /// <summary>منطق المرساة الصرف (قابل للاختبار بلا ريجستري):
        /// raw = قيمة آخر رؤية المخزَّنة، nowTicks = وقت UTC الحالي.</summary>
        internal static ClockAnchorStatus EvaluateClockAnchorCore(string raw, long nowTicks)
        {
            if (!long.TryParse(raw, out long lastSeenTicks))
                return ClockAnchorStatus.Missing;

            var lastSeen = new DateTime(lastSeenTicks, DateTimeKind.Utc);
            var now = new DateTime(nowTicks, DateTimeKind.Utc);

            if (now < lastSeen - ClockRollbackTolerance)
                return ClockAnchorStatus.RolledBack; // الساعة رُجعت أيامًا — رفض

            return ClockAnchorStatus.Ok; // تقدم طبيعي أو رجوع ضمن السماح
        }

        private static bool HasStoredLicense()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(EffectiveRegistryPath);
                string stored = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(stored);
            }
            catch
            {
                return false;
            }
        }

        private static void SaveStoredLicense(string license)
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(license),
                null,
                DataProtectionScope.LocalMachine);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(EffectiveRegistryPath))
            {
                key.SetValue(ValueName, Convert.ToBase64String(encrypted));
            }
        }

        private static string LoadStoredLicense()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(EffectiveRegistryPath))
            {
                string stored = key == null ? null : key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(stored)) return null;

                try
                {
                    byte[] decrypted = ProtectedData.Unprotect(
                        Convert.FromBase64String(stored),
                        null,
                        DataProtectionScope.LocalMachine);
                    return Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
