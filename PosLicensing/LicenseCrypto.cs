using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PosLicensing
{
    public class LicensePayload
    {
        public string Hwid;
        public long ExpiryTicksUtc;
        public string Customer;

        internal string Serialize()
        {
            string customer = Customer == null ? "" : Customer.Replace('|', ' ').Trim();
            return Hwid + "|" + ExpiryTicksUtc.ToString(CultureInfo.InvariantCulture) + "|" + customer;
        }

        internal static bool TryDeserialize(string s, out LicensePayload payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('|');
            if (parts.Length < 2 || parts.Length > 3) return false;
            long ticks;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                return false;
            payload = new LicensePayload
            {
                Hwid = parts[0],
                ExpiryTicksUtc = ticks,
                Customer = parts.Length == 3 ? parts[2] : ""
            };
            return true;
        }
    }

    public static class LicenseCrypto
    {
        public static string Sign(LicensePayload payload, string privateKeyXml)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (string.IsNullOrEmpty(payload.Hwid)) throw new ArgumentException("Hwid is required");

            string normalized = MachineFingerprint.Normalize(payload.Hwid);
            payload.Hwid = normalized;

            string body = payload.Serialize();
            byte[] data = Encoding.UTF8.GetBytes(body);

            using (RSA rsa = RSA.Create(2048))
            {
                rsa.FromXmlString(privateKeyXml);
                byte[] signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(data) + "." + Convert.ToBase64String(signature);
            }
        }

        public static bool TryVerify(string licenseText, string publicKeyXml, out LicensePayload payload)
        {
            payload = null;
            if (string.IsNullOrWhiteSpace(licenseText)) return false;

            string[] parts = licenseText.Trim().Split('.');
            if (parts.Length != 2) return false;

            try
            {
                byte[] data = Convert.FromBase64String(parts[0]);
                byte[] signature = Convert.FromBase64String(parts[1]);

                using (RSA rsa = RSA.Create())
                {
                    rsa.FromXmlString(publicKeyXml);
                    bool valid = rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    if (!valid) return false;
                }

                string body = Encoding.UTF8.GetString(data);
                if (!LicensePayload.TryDeserialize(body, out payload)) return false;
                return !string.IsNullOrEmpty(payload.Hwid);
            }
            catch
            {
                return false;
            }
        }
    }
}
