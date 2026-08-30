using Microsoft.Win32;
using System;
using System.Security.Cryptography;
using System.Text;

namespace PosLicensing
{
    public static class ActivationManager
    {
        private const string RegistryPath = @"SOFTWARE\MyPos\License";
        private const string ValueName = "Data";

        internal static string PublicKeyXml = "<RSAKeyValue><Modulus>ضع هنا المفتاح العام</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

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
