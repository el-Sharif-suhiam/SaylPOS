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

        internal static string PublicKeyXml = "<RSAKeyValue><Modulus>r0vh7zVhN/sQoh2yQ6J1XyRKRb7RCAQ3Ev/JTwd/ChECvZwyAArIsW8LHAztpqfw2EhTcO6nut0R3slG5TrqOiJRY7ZLUQLEl8u1ZX61GpLfSsXKR74iOuE7/wHoVLQHJsULYnVqvMVOAP7sdOJfUgbCbEEC4CezAWCQh64fd6k8I8Q7U+r++q2I4Xl96DQasJYsbNHZYWF48rdQwrwdv3LX48O/Yj8T4FkK1DLs7LJOUEyFqD2RKr3oQa9w6FdA0V2fYY1LIOkrkJ6idhsgOHieu+CFlJAmb9S56Y/b6+ejAi5oboXccUFeQkdvJemLFyuaDyxUbknMRfzKu14xDQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

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
