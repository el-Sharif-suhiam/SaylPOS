using Microsoft.Win32;
using System;
using System.Security.Cryptography;
using System.Text;

namespace PosLicensing
{
    public static class ActivationManager
    {
        private const string RegistryPath = @"SOFTWARE\Sayl\License";
        private const string ValueName = "Data";

internal static string PublicKeyXml = "<RSAKeyValue><Modulus>oRTF2J1LgULwWHnd3LctxJVPPs5cHPpdCYcJDusopCj6X+GFS3iAPpBOPoIYLzvF7JXiILoABoJSp+yY7f8kMLXmKe4gx2UDl2XbWNVypXFUMoXSQGsrpReQU20/y/+OdOyV4Xs3n8HX8Zy5kn8+HJZNvIq7HxHuUcYNbZ3IzAmk9KzJ5V2UhP2YkZ0IUhxb8UrN8DVLNf9MNrJTLRxYnMgjSEEUIRJrLshz9OhMfI1aqEIyM6a3vjqSm0H48YQA51gjmTun81xiY2KFow2pnrTSydH9kUOe6tYIsn6XVWUJPSWkzpDy2/P66LcV0Wptvlbzzhj55JW0t3QcFNKbPQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        public static string GetDeviceCode()
        {
            return MachineFingerprint.GetDeviceCode();
        }

        public static bool IsActivated()
        {
            string stored = LoadStoredLicense();
            if (stored == null) return false;
            return ValidateAgainstThisDevice(stored);
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

        private static bool ClockWasRolledBack()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, true)
                                        ?? Registry.CurrentUser.CreateSubKey(RegistryPath);
                string raw = key.GetValue(LastSeenValueName) as string;
                if (!long.TryParse(raw, out long lastSeenTicks))
                {
                    // تثبيت مفعَّل بلا مرساة = عبث (حذف القيمة لإحياء رخصة) — رفض
                    return true;
                }

                var lastSeen = new DateTime(lastSeenTicks, DateTimeKind.Utc);
                var now = DateTime.UtcNow;

                if (now < lastSeen - ClockRollbackTolerance)
                    return true; // الساعة رُجعت أيامًا — رفض

                // تحديث المرساة (تقدم طبيعي أو رجوع ضمن السماح)
                key.SetValue(LastSeenValueName, now.Ticks.ToString());
                return false;
            }
            catch
            {
                // Registry معطوب؟ لا نعطل البرنامج بالخطأ — سلوك التحقق الأصلي يكفي
                return false;
            }
        }

        public static bool Activate(string licenseText, out string error)
        {
            error = null;

            LicensePayload payload;
            if (!LicenseCrypto.TryVerify(licenseText, PublicKeyXml, out payload))
            {
                error = "الرخصة غير صالحة أو تم التلاعب بها";
                return false;
            }

            string currentHwid = MachineFingerprint.Normalize(MachineFingerprint.GetDeviceCode());
            if (!string.Equals(payload.Hwid, currentHwid, StringComparison.Ordinal))
            {
                error = "هذه الرخصة مخصصة لجهاز آخر ولا يمكن تفعيلها هنا";
                return false;
            }

            if (payload.ExpiryTicksUtc < DateTime.UtcNow.Ticks)
            {
                error = "انتهت صلاحية هذه الرخصة";
                return false;
            }

            SaveStoredLicense(licenseText.Trim());
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key.SetValue(LastSeenValueName, DateTime.UtcNow.Ticks.ToString());
            }
            catch { }
            return true;
        }

        public static void Deactivate()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, true))
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

        private static bool ValidateAgainstThisDevice(string licenseText)
        {
            LicensePayload payload;
            if (!LicenseCrypto.TryVerify(licenseText, PublicKeyXml, out payload)) return false;
            if (payload.ExpiryTicksUtc < DateTime.UtcNow.Ticks) return false;
            // VULN-27: مرساة زمنية — رجوع الساعة لا يُحيي رخصة منتهية
            if (ClockWasRolledBack()) return false;

            string currentHwid = MachineFingerprint.Normalize(MachineFingerprint.GetDeviceCode());
            return string.Equals(payload.Hwid, currentHwid, StringComparison.Ordinal);
        }

        private static void SaveStoredLicense(string license)
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(license),
                null,
                DataProtectionScope.LocalMachine);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue(ValueName, Convert.ToBase64String(encrypted));
            }
        }

        private static string LoadStoredLicense()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath))
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
