using System;
using System.Collections.Generic;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace PosLicensing
{
    public static class MachineFingerprint
    {
        public static string GetDeviceCode()
        {
            string cpu = JoinValues(WmiValues("Win32_Processor", "ProcessorId"));
            string disks = JoinValues(WmiValues("Win32_DiskDrive", "SerialNumber"));
            string board = JoinValues(WmiValues("Win32_BaseBoard", "SerialNumber"));
            string bios = JoinValues(WmiValues("Win32_BIOS", "SerialNumber"));

            string raw = cpu + "|" + disks + "|" + board + "|" + bios;

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                string hex = BitConverter.ToString(hash, 0, 8).Replace("-", "");
                StringBuilder sb = new StringBuilder(19);
                for (int i = 0; i < hex.Length; i++)
                {
                    sb.Append(hex[i]);
                    if (i % 4 == 3 && i < hex.Length - 1)
                        sb.Append('-');
                }
                return sb.ToString();
            }
        }

        public static string Normalize(string code)
        {
            if (code == null) return "";
            return code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        }

        private static string JoinValues(List<string> values)
        {
            return string.Join(",", values.ToArray());
        }

        private static List<string> WmiValues(string wmiClass, string propertyName)
        {
            List<string> result = new List<string>();
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT " + propertyName + " FROM " + wmiClass))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string v = obj[propertyName] as string;
                        if (!string.IsNullOrWhiteSpace(v))
                            result.Add(v.Trim());
                    }
                }
            }
            catch
            {
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }
}
