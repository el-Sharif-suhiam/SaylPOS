# مواصفات نظام الترخيص والتفعيل لنظام POS — وثيقة شاملة

> **الغرض من هذه الوثيقة**: توثيق كل تفصيلة في نظام تفعيل البرنامج (Licensing/Activation System) الموجود في مجلد `LicenseSystem/`، بحيث يستطيع أي مطوّر أو وكيل ذكاء اصطناعي آخر فهم الفكرة، والبنية، والأمان، وطريقة التشغيل، والتطوير المستقبلي دون أي سياق إضافي.

---

## 1. السياق والمتطلبات

| البند | القيمة |
|---|---|
| البرنامج | نظام نقاط بيع (POS) يعمل على ويندوز |
| اللغة/التقنية | C# / .NET Framework (كود متوافق مع C# 7.3 وما بعده، يعمل على 4.6+) |
| نوع التطبيق | Windows Desktop (يفترض WinForms/WPF) |
| المتطلب الأساسي | كل مفتاح/رخصة تُفعَّل على **جهاز واحد فقط** |
| القيد المختار | **Offline بالكامل** — لا يوجد سيرفر ولا اتصال إنترنت وقت التفعيل |
| التخزين | Windows Registry مشفّراً بـ DPAPI |

### قرار التصميم الجوهري
المستخدم طلب أصلاً "برنامج يصنع أكواد (Serial Keys)". لكن تم توضيحه على أن **المفتاح العشوائي الأعمى لا يمكن ضمانه Offline**، لأن أي شخص ينقل نفس المفتاح لجهاز آخر سيعمل عنده أيضاً — لا توجد جهة مركزية تعرف أن المفتاح استُخدم من قبل.

**الحل المعتمد**: لا نولّد "سيريالات عمياء"، بل نولّد **رخصة رقمياً موقّعة تحتوي بداخلها بصمة جهاز العميل (Hardware Fingerprint)**. التوقيع بـ RSA-2048 بمفتاح خاص يملكه البائع وحده. هذا هو الضمانة الوحيدة القوية Offline.

---

## 2. المبدأ الأمني (سلسلة الثقة)

```
[البائع] يملك زوج مفاتيح RSA-2048:
    - private_key.xml  ← سرّ مطلق، لا يخرج من جهاز البائع أبداً
    - public_key.xml   ← مضمّن داخل كود برنامج الـ POS (لا يسرّ شيئاً)

[رخصة العميل] = base64( البيانات ) . base64( التوقيع )
    البيانات    = بصمة_الجهاز | تاريخ_انتهاء | اسم_العميل
    التوقيع     = RSA-SHA256-PKCS1 على بايتات البيانات بالمفتاح الخاص

[برنامج POS عند التفعيل]:
    1) يتحقق من التوقيع بالمفتاح العام المضمّن  → الرخصة صادرة عني فعلاً وغير معدَّلة
    2) يقارن البصمة المدفونة داخل الرخصة مع بصمة الجهاز الحالي → رخصة غيري تُرفض
    3) يتحقق من عدم انتهاء الصلاحية
    4) يخزّن الرخصة مشفّرة بـ DPAPI في الريجستري
```

**لماذا هذا يضمن جهازاً واحداً؟**
- تعديل البصمة داخل الرخصة (لتتوافق مع جهاز آخر) يكسر التوقيع فوراً.
- إعادة توقيع رخصة جديدة تتطلب المفتاح الخاص الذي لدى البائع فقط.
- نسخ قيمة الريجستري من جهاز لآخر تفشل لأن DPAPI (`LocalMachine`) مرتبط بالجهاز نفسه.
- حتى لو حُذفت الريجستري، إعادة إدخال نفس الرخصة تنجح **على نفس الجهاز فقط**.

**القيد الوحيد المقبول**: لا يمكن "إلغاء/سحب" رخصة بعد بيعها (لا يوجد اتصال). العلاج: اعتماد صلاحية محدودة (مثلاً سنة) والتجديد السنوي.

---

## 3. مكونات النظام (4 ملفات)

| الملف | المساحة (Namespace) | أين يُستخدم | السرية |
|---|---|---|---|
| `MachineFingerprint.cs` | `PosLicensing` | مشترك (POS + Keygen) | عام |
| `LicenseCrypto.cs` | `PosLicensing` | مشترك (POS + Keygen) | عام |
| `ActivationManager.cs` | `PosLicensing` | **داخل مشروع الـ POS فقط** | عام (يحتوي المفتاح العام فقط) |
| `KeygenTool.cs` | `PosLicensing.KeygenTool` | **أداة مستقلة خاصة بالبائع** | ⛔ سرّي تماماً، لا يُوزَّع |

**بنية المشاريع:**
- **مشروع POS (WinForms/WPF)**: يشمل `MachineFingerprint.cs` + `LicenseCrypto.cs` + `ActivationManager.cs` + المراجع `System.Management` و `System.Security`.
- **مشروع KeygenTool (Console App منفصل)**: يشمل `MachineFingerprint.cs` + `LicenseCrypto.cs` + `KeygenTool.cs`. يُبنى EXE منفصل يبقى لدى البائع.

---

## 4. توليد بصمة الجهاز — `MachineFingerprint.cs`

### المصادر (WMI)
| الفئة | الخاصية |
|---|---|
| `Win32_Processor` | `ProcessorId` |
| `Win32_DiskDrive` | `SerialNumber` (جميع الأقراص) |
| `Win32_BaseBoard` | `SerialNumber` |
| `Win32_BIOS` | `SerialNumber` |

### الخوارزمية خطوة بخطوة
1. تُجمع قيم كل مصدر في قائمة، **تُرتَّب أبجدياً (Ordinal)** — لضمان ثبات النتيجة مهما اختلف ترتيب تعداد الأجهزة.
2. تُدمج القيم بفواصل `,` داخل كل مجموعة، ثم المجموعات بـ `|`:
   `cpu|disks|board|bios`
3. تُمرَّر النص إلى SHA256، ويؤخذ **أول 8 بايتات** → 16 خانة Hex.
4. تُنسَّق: `XXXX-XXXX-XXXX-XXXX` (مثال: `A3F2-9B01-C7D4-E520`).

### الدوال العامة
- `string GetDeviceCode()` — يعيد كود الجهاز المنسق.
- `string Normalize(string code)` — يحذف الشرطات والمسافات ويحوّل لحروف كبيرة؛ تُستخدم قبل المقارنة أو التوقيع دائماً.

### ملاحظات استقرار مهمة
- الدالة `WmiValues` تبتلع الاستثناءات وتتجاهل القيم الفارغة (بعض الألواح OEM ترجع Serial فارغاً) — البصمة تبقى صالحة من المصادر المتبقية.
- **إذا غيّر العميل قطعة أساسية (معالج/قرص النظام/لوحة)** ستتغير البصمة والرخصة تُرفض → السياسة التجارية: البائع يصدر رخصة جديدة (يُنصح بسياسة رسمية لهذه الحالة).
- **الأجهزة الافتراضية**: استنساخ VM بنفس العتاد الافتراضي يعطي نفس البصمة. إذا أردت، أضف لاحقاً كشف VM.
- **إعادة تركيب ويندوز** على نفس العتاد: البصمة تبقى كما هي غالباً والرخصة تعمل.

---

## 5. بنية الرخصة — `LicensePayload` + `LicenseCrypto.cs`

### نص الحمولة (Payload)
```
{HWID}|{ExpiryTicksUtc}|{Customer}
```
- `HWID`: 16 خانة Hex بدون شرطات (بعد `Normalize`).
- `ExpiryTicksUtc`: `DateTime.Ticks` بتوقيت UTC. الصلاحية الدائمة = `DateTime.MaxValue.Ticks` (9223372036854775807).
- `Customer`: اسم حر، ويُستبدل فيه أي حرف `|` بمسافة لمنع كسر التنسيق.
- إذا لم يوجد اسم عميل: الجزء الثالث اختياري (2 أو 3 أجزاء مقبولة عند التحليل).

### التوقيع والتحقق
- **التوقيع**: بايتات UTF8 للحمولة → `RSA.SignData(..., SHA256, RSASignaturePadding.Pkcs1)` بالمفتاح الخاص.
- **الصيغة النهائية**: `Convert.ToBase64String(payloadBytes) + "." + Convert.ToBase64String(signature)`
- **التحقق**: فصل عند `.` → `RSA.VerifyData` بالمفتاح العام → تحليل الحمولة → يجب أن يكون `Hwid` غير فارغ وإلا تُعتبر الرخصة فاسدة.
- أي استثناء أثناء التحليل/التحويل ⇒ النتيجة `false` (لا رميات خارجية).

### الدوال العامة
- `string Sign(LicensePayload payload, string privateKeyXml)` — يطبّع HWID ثم يوقّع ويعيد نص الرخصة. يرمي `ArgumentNullException/ArgumentException` عند نقص البيانات.
- `bool TryVerify(string licenseText, string publicKeyXml, out LicensePayload payload)` — تحقق صامت (out pattern).

---

## 6. إدارة التفعيل داخل POS — `ActivationManager.cs`

### الثوابت والإعدادات
- مسار الريجستري: `HKEY_CURRENT_USER\SOFTWARE\MyPos\License` وقيمة اسمها `Data`.
- `internal static string PublicKeyXml` — **يجب استبدال محتواه** بمحتوى ملف `public_key.xml` بعد توليد المفاتيح (حالياً Placeholder).

### الواجهة العامة (API)
| الدالة | الوظيفة |
|---|---|
| `string GetDeviceCode()` | يعيد كود الجهاز لعرضه للعميل في شاشة التفعيل |
| `bool IsActivated()` | يقرأ المخزن، يفك DPAPI، يتحقق من التوقيع + الصلاحية + البصمة. يُستدعى عند كل تشغيل |
| `bool Activate(string licenseText, out string error)` | التفعيل الكامل مع رسالة خطأ عربية بالسبب |
| `void Deactivate()` | حذف القيمة المخزنة |

### تسلسل `Activate`
1. `TryVerify` بالتوقيع → فشل: `"الرخصة غير صالحة أو تم التلاعب بها"`.
2. مقارنة `payload.Hwid` مع بصمة الجهاز الحالي بعد `Normalize` وبمقارنة `Ordinal` → اختلاف: `"هذه الرخصة مخصصة لجهاز آخر ولا يمكن تفعيلها هنا"`.
3. `payload.ExpiryTicksUtc < DateTime.UtcNow.Ticks` → `"انتهت صلاحية هذه الرخصة"`.
4. النجاح: تخزين نص الرخصة كاملاً بعد `Protect` بـ DPAPI (`DataProtectionScope.LocalMachine`) ثم Base64 في الريجستري.

### تسلسل `IsActivated` (عند كل تشغيل)
1. قراءة القيمة وفكها بـ `Unprotect` — أي فشل ⇒ غير مفعّل (قيمة تالفة أو منسوخة من جهاز آخر).
2. تحقق التوقيع + الصلاحية + تطابق البصمة (نفس منطق Activate دون رسائل).

### طبقتا الحماية في التخزين (Defense-in-Depth)
- **التوقيع داخل الرخصة** يربطها بالجهاز منطقياً.
- **DPAPI LocalMachine** يربط التشفير بالجهاز فيزيائياً: فك التشفير ينجح على نفس الجهاز (أي مستخدم)، ويفشل على أي جهاز آخر حتى لو نُسخت قيمة الريجستري حرفياً.

### ملاحظة تشغيلية (HKCU)
التخزين تحت `CurrentUser` يعني أن **كل حساب ويندوز** على نفس الجهاز له مخزنه؛ لو دخّل الكاشير بحساب ويندوز مختلف سيطلب البرنامج إعادة إدخال نفس الرخصة (وستنجح لأن البصمة نفسها). إن أردت مخزناً واحداً لكل الجهاز: انقل القراءة/الكتابة إلى `HKLM` مع مراعاة صلاحيات Admin وقت التثبيت فقط.

---

## 7. أداة البائع — `KeygenTool.cs` (⛔ لا تُوزَّع)

Console App تفاعلية بقائمة:
1. **توليد زوج مفاتيح جديد** (مرة واحدة فقط في عمر المنتج):
   - `RSA.Create(2048)` → يكتب `private_key.xml` (خاص + عام عبر `ToXmlString(true)`) و `public_key.xml` (عام فقط `ToXmlString(false)`).
   - بعدها: انسخ محتوى `public_key.xml` كاملاً إلى `ActivationManager.PublicKeyXml` وأعد بناء الـ POS.
2. **إنشاء رخصة لعميل**:
   - يطلب: كود الجهاز (يتحقق أنه 16 خانة بعد Normalize)، اسم العميل، مدة الأيام (0 = دائمة).
   - يقرأ `private_key.xml` من مجلد التشغيل ويطبع نص الرخصة للنسخ والإرسال للعميل.
   - يطبع أيضاً كود الجهاز المربوط للتأكيد.

⚠️ لو ضاع/تسرب `private_key.xml` يجب توليد زوج جديد وإصدار تحديث للبرنامج بالمفتاح العام الجديد ورخص جديدة للعملاء.

---

## 8. الكود الكامل (المرجع الحرفي)

> الملفات موجودة فعلياً في مجلد `LicenseSystem/`. النسخ هنا للوثائقية.

### 8.1 MachineFingerprint.cs
```csharp
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
```

### 8.2 LicenseCrypto.cs
```csharp
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
```

### 8.3 ActivationManager.cs
```csharp
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
```

### 8.4 KeygenTool.cs
```csharp
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
```

---

## 9. سيناريو الاستخدام الكامل (End-to-End)

### أولاً: تجهيز (مرة واحدة)
1. ابنِ مشروع KeygenTool وشغّله واختر `1` → يتولد `private_key.xml` و `public_key.xml` بجانب الـ EXE.
2. افتح `public_key.xml` وانسخ محتواه بالكامل إلى `ActivationManager.PublicKeyXml`.
3. ابنِ برنامج POS وأدرج فيه الملفات الثلاثة + المراجع.

### ثانياً: بيع وترخيص (لكل عميل)
```
العميل                      البائع
------                      ------
يشغّل POS أول مرة
→ شاشة تفعيل تعرض
  "كود الجهاز: A3F2-9B01-C7D4-E520"
  وزر نسخ
يرسل الكود (واتساب)  ──────►  يشغّل KeygenTool → 2
                              يُدخل الكود + الاسم + المدة
                              ← ينسخ نص الرخصة
يستلم الرخصة         ◄──────  
يلصقها ويضغط تفعيل
→ Activate():
   ✔ توقيع سليم
   ✔ البصمة = بصمة جهازه
   ✔ الصلاحية سارية
   → تُخزَّن DPAPI+Registry
→ البرنامج يعمل، ومن بعدها
  IsActivated()==true مباشرة عند الإقلاع
```

### ثالثاً: محاولات فاشلة (سلوك النظام)
| المحاولة | النتيجة |
|---|---|
| إدخال رخصة معدّلة يدوياً | فشل التوقيع → "غير صالحة أو تم التلاعب بها" |
| أخذ رخصة صديقه (جهاز آخر) | "مخصصة لجهاز آخر" |
| نسخ قيمة الريجستري لجهاز ثاني | DPAPI يفشل في فك التشفير → غير مفعّل |
| استخدام رخصة منتهية | "انتهت صلاحية" |
| إعادة إدخال نفس الرخصة على نفس الجهاز بعد تنظيف الريجستري | تنجح (البصمة نفسها) — وهذا مقصود |

---

## 10. الدمج في واجهات POS (أمثلة جاهزة)

### عند إقلاع البرنامج (Program.cs / MainForm Load)
```csharp
if (!PosLicensing.ActivationManager.IsActivated())
{
    using (var f = new ActivationForm())
    {
        if (f.ShowDialog() != DialogResult.OK)
        {
            // اخرج من التطبيق أو افصل الوظائف الأساسية
            Application.Exit();
        }
    }
}
```

### شاشة التفعيل (ActivationForm)
```csharp
lblDeviceCode.Text = ActivationManager.GetDeviceCode();

btnCopy.Click += (s, e) => Clipboard.SetText(lblDeviceCode.Text);

btnActivate.Click += (s, e) =>
{
    string err;
    if (ActivationManager.Activate(txtLicense.Text, out err))
    {
        MessageBox.Show("تم التفعيل بنجاح، شكراً لك");
        DialogResult = DialogResult.OK;
        Close();
    }
    else
    {
        MessageBox.Show(err, "فشل التفعيل", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
};
```

### عرض معلومات الرخصة (اختياري — "حول البرنامج")
```csharp
// يمكن إضافة دالة تحليل للعرض فقط:
// LicensePayload p; LicenseCrypto.TryVerify(stored, PublicKeyXml, out p)
// ثم عرض p.Customer وتاريخ الانتهاء
```

---

## 11. التهديدات والثغرات المعروفة وخطة التحصين

### أ) الهندسة العكسية لـ .NET (الخطر الأول)
برامج .NET تُفكك بسهولة بأدوات مثل dnSpy/ILSpy؛ يمكن للمهاجم إيجاد `IsActivated` وجعلها تعيد `true` دائماً.

**التحصينات المطلوبة (لم تنفذ بعد — مهمة قادمة):**
1. Obfuscator إلزامي: ConfuserEx (مجاني) أو .NET Reactor (مدفوع + Anti-Tamper/Anti-Debug).
2. توزيع الفحص: لا تكتفِ بنداء واحد في البداية؛ نفّذ فحوصاً دورية أثناء التشغيل (مثلاً قبل العمليات الحرجة: حفظ الفاتورة، نهاية اليوم) وأغلق/عطّل عند الفشل بصمت.
3. تجنّب أسماء واضحة (`IsActivated`) بعد التشويش سيتم تغييرها تلقائياً.
4. اجعل فشل الترخيص يفسد سلوكاً بعيداً عن نقطة الفحص (Harder to patch).

### ب) تقليب ساعة النظام (Clock Rollback)
عميل لديه رخصة سنة قد يعيد الساعة للوراء لإطالة المدة.
**علاج مقترح (غير منفذ بعد):** خزّن في الريجستري آخر ظهور `DateTime.UtcNow.Ticks` عند كل تشغيل؛ إذا كان الوقت الحالي أقدم من المخزون بهامش كبير (مثلاً أكثر من 24 ساعة) ⇒ اعتبرها تلاعباً (اطلب تفعيلاً أو علّق الوظائف).

### ج) لا يوجد سحب/إبطال عن بعد
مقبول ضمن قيد Offline. **التوصية التجارية:** لا تصدر رخصاً دائمة (0)؛ أصدر سنة وأعد الترخيص سنوياً — حماية ودخل متكرر.

### د) تغيير عتاد العميل
الرخصة تموت. جهّز سياسة دعم مكتوبة (مثلاً: إعادة إصدار مجانية مرة واحدة سنوياً).

### هـ) تسرب private_key.xml
كارثية كاملة (توليد رخص مزيفة). احفظه في مكان آمن/مشفر ولا تتركه بجوار أداة التوليد على جهاز مكشوف.

### و) حدود DPAPI
`LocalMachine` يسمح لأي مستخدم على نفس الجهاز بفك التشفير — هذا مقبول لأن الهدف منع النقل بين الأجهزة وليس عزل مستخدمي نفس الجهاز. (استخدام `CurrentUser` بدلاً منه يعزل أكثر لكنه يعقّد تعدد حسابات الكاشير.)

---

## 12. المتطلبات التقنية للبناء

- .NET Framework 4.6+ (الكود Classic-style C# 7.3 متوافق مع المشاريع القديمة).
- References إضافية في مشروع POS:
  - `System.Management` (للـ WMI)
  - `System.Security` (لـ `ProtectedData`)
- `Microsoft.Win32.Registry` جزء من الإطار (mscorlib/…).
- لا اعتماديات NuGet خارجية حالياً.

---

## 13. قائمة تحقق سريعة (Checklist)

- [ ] توليد زوج المفاتيح مرة واحدة عبر KeygenTool
- [ ] حقن `public_key.xml` في `ActivationManager.PublicKeyXml`
- [ ] إضافة المرجعين `System.Management` و `System.Security`
- [ ] شاشة تفعيل تعرض كود الجهاز بزر نسخ وحقل لصق الرخصة
- [ ] نداء `IsActivated()` عند كل إقلاع
- [ ] بناء KeygenTool كـ EXE منفصل وإبعاده عن حزمة التوزيع
- [ ] تشويش/Obfuscate نسخة التوزيع النهائية
- [ ] اختبار على جهاز حقيقي + جهاز ثانٍ (يجب أن يُرفض)
- [ ] اختبار نسخ قيمة الريجستري بين جهازين (يجب أن تفشل)

---

## 14. توسعات مستقبلية ممكنة (Backlog)

1. **حقول إضافية في الحمولة**: قائمة ميزات (Features flags)، رقم الفرع/الترخيص الضريبي، نسخة السعر.
2. **وضع تجريبي (Trial)**: رخصة بصلاحية 7–14 يوم بدون أي كود إضافي — البنية تدعمها الآن عبر `ExpiryTicksUtc`.
3. **رقابة زمنية ضد تقليب الساعة** (كما في 11-ب).
4. **ترقية جزئية Online لاحقاً**: API صغيرة جداً للتحقق الاختياري عند توفر إنترنت (Blacklist، إحصاءات) مع بقاء العمل Offline كامل الوظيفة.
5. **تصدير/استيراد "طلب تفعيل" كملف** بدل قراءة كود الجهاز يدوياً، لتقليل أخطاء النسخ.
6. **نقل الرخصة بين أجهزة العميل**: إصدار "Deactivate Token" موقّع من العميل يُرجعه للبائع ليُصدر رخصة لجهاز جديد (نمط Transfer بدون سيرفر).

---

## 15. ملخص API (Cheat Sheet)

```csharp
using PosLicensing;

// كود الجهاز (للعرض)
string code = ActivationManager.GetDeviceCode();          // "A3F2-9B01-C7D4-E520"

// هل البرنامج مفعّل؟
bool ok = ActivationManager.IsActivated();

// التفعيل
string error;
bool done = ActivationManager.Activate(licenseText, out error);

// إزالة التفعيل
ActivationManager.Deactivate();

// (للبائع/الأدوات) توقيع وتحقق مباشر
string lic = LicenseCrypto.Sign(new LicensePayload { Hwid="A3F29B01C7D4E520", ExpiryTicksUtc=ticks, Customer="محل X" }, privateKeyXml);
LicensePayload p; bool valid = LicenseCrypto.TryVerify(lic, publicKeyXml, out p);
```

**نقطة واحدة يجب ألا ينساها أي وكيل يعمل على هذا المشروع**: أمان النظام كله يقوم على سرية `private_key.xml` لدى البائع وعلى تشويش نسخة التوزيع — الكود وحده بلا هاتين الإجرائين ليس حماية كافية ضد مستخدم محترف.
