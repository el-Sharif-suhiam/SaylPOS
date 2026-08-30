using System;
using System.IO;
using System.Security.Cryptography;

namespace PosLicensing.KeygenTool
{
    // أداة خاصة بك فقط، لا تُوزَّع على العملاء أبداً
    public static class Program
    {
        public static void Main()
        {
            Console.WriteLine("=== أداة توليد الرخص ===");
            Console.WriteLine("1) توليد زوج مفاتيح جديد (مرة واحدة فقط)");
            Console.WriteLine("2) إنشاء رخصة لعميل");
            Console.Write("اختر: ");
            string choice = Console.ReadLine();

            if (choice == "1")
            {
                GenerateKeyPair();
            }
            else if (choice == "2")
            {
                CreateLicense();
            }
        }

        private static void GenerateKeyPair()
        {
            using (RSA rsa = RSA.Create(2048))
            {
                File.WriteAllText("private_key.xml", rsa.ToXmlString(true));
                File.WriteAllText("public_key.xml", rsa.ToXmlString(false));
            }
            Console.WriteLine("تم إنشاء private_key.xml و public_key.xml");
            Console.WriteLine("انسخ محتوى public_key.xml إلى ActivationManager.PublicKeyXml في برنامج الـ POS");
        }

        private static void CreateLicense()
        {
            Console.Write("كود الجهاز الذي أرسله العميل: ");
            string hwid = MachineFingerprint.Normalize(Console.ReadLine());

            if (hwid.Length != 16)
            {
                Console.WriteLine("كود الجهاز غير صالح");
                return;
            }

            Console.Write("اسم العميل: ");
            string customer = Console.ReadLine() ?? "";

            Console.Write("مدة الصلاحية بالأيام (0 = دائم): ");
            int days;
            int.TryParse(Console.ReadLine(), out days);

            long expiry = days <= 0
                ? DateTime.MaxValue.Ticks
                : DateTime.UtcNow.AddDays(days).Ticks;

            var payload = new LicensePayload
            {
                Hwid = hwid,
                ExpiryTicksUtc = expiry,
                Customer = customer
            };

            string privateKey = File.ReadAllText("private_key.xml");
            string license = LicenseCrypto.Sign(payload, privateKey);

            Console.WriteLine();
            Console.WriteLine("الرخصة (أرسلها للعميل):");
            Console.WriteLine(license);
            Console.WriteLine();
            Console.WriteLine("كود الجهاز المربوط: " + hwid);
        }
    }
}
