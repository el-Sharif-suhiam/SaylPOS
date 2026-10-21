using System.Text;
using Common.DTOs;

namespace Sayl.UI.Infrastructure
{
    /// <summary>لغة طابعة الملصقات — الموديل يعمم بلا ربط بجهاز:
    ///  • Zpl: عائلة Zebra وTSC وXprinter وGodex (الأوسع انتشارًا)
    ///  • EscPos: الطابعات الرخيصة بوضع الملصقات (نفس عائلة طابعة الإيصال)
    /// كلاهما يولّد الباركود داخل الطابعة نفسها — لا صور ولضغط، المطفا حادة.</summary>
    internal enum LabelPrinterLanguage { Zpl, EscPos }

    /// <summary>مقاس ملصق معد مسبقًا (مم) — المحتوى: الباركود + رقمه تحته فقط.</summary>
    internal record LabelSize(int WidthMm, int HeightMm)
    {
        public string Caption => $"{WidthMm}×{HeightMm}mm";
    }

    /// <summary>
    /// طباعة ملصقات باركود للمنتجات — باركود المنتج الحقيقي (ما دخلته عند
    /// الإضافة) + رقمه المقروء تحته. الإرسال RAW عبر Winspool (Task.Run)
    /// بنفس تقنية طابعة الإيصals. أي فشل يعود رسالة صريحة لا صمت.
    /// </summary>
    internal static class LabelPrinter
    {
        /// <summary>مقاسات جاهزة تغطي لفائف السوق — الافتراضي 40×25 (حد 25مم التالي طلب المالك).</summary>
        public static readonly LabelSize[] PresetSizes =
        [
            new(30, 20),
            new(40, 25),
            new(50, 30),
            new(58, 40),
        ];

        public static IEnumerable<string> GetInstalledPrinters() => ReceiptPrinter.GetInstalledPrinters();

        /// <summary>طباعة كمية ملصقات لباركود منتج — تعود (نجاح، رسالة).</summary>
        public static async Task<(bool ok, string message)> PrintProductBarcodeAsync(
            string barcode, int copies, LabelSize size, string printerName, LabelPrinterLanguage lang)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return (false, AppLang.T("Label.Msg.NoBarcode"));
            if (copies < 1) copies = 1;

            return await Task.Run(() =>
            {
                try
                {
                    byte[] bytes = lang == LabelPrinterLanguage.Zpl
                        ? BuildZpl(barcode.Trim(), copies, size.WidthMm, size.HeightMm)
                        : BuildEscPos(barcode.Trim(), copies);

                    bool ok = ReceiptPrinter.SendBytesToPrinter(printerName, bytes, docName: "Sayl Label");
                    return ok
                        ? (true, AppLang.T("Label.Msg.Printed", copies))
                        : (false, AppLang.T("Label.Msg.PrinterUnavailable", printerName));
                }
                catch (Exception ex)
                {
                    return (false, AppLang.T("Label.Msg.PrintError", ex.Message));
                }
            });
        }

        // ───────────────────────── ZPL (203dpi افتراضي: 8 نقاط/مم) ─────────────────────────
        // مثال الطباعة: ^BC Code128 داخل الطابعة بخط معرف مقروء (Y) — الأرقام تُطبع تحته تلقائيًا.
        private static byte[] BuildZpl(string code, int copies, int wMm, int hMm)
        {
            int nW = wMm * 8, nH = hMm * 8;
            int barHeight = Math.Max(40, (int)(nH * 0.55));

            // تقدير عرض Code128: ~11 وحدات×2 نقطة لكل حرف + حدود — توسيط تقريبي يحمي الحواف
            int estWidth = (code.Length + 2) * 22;
            int x = Math.Max(4, (nW - Math.Min(estWidth, nW - 8)) / 2);

            string zpl = $"^XA^FWN^CI27" +
                         $"^PW{nW}^LL{nH}^LH0,0^LT0" +
                         // sidebar margins صفرية — الكود وسط الملصق تقريبيًا
                         $"^FO{x},4" +
                         $"^BY2,2.5,{barHeight}" +
                         $"^BCN,{barHeight},Y,N,N" +
                         $"^FD{code}^FS" +
                         $"^PQ{copies},0,0,N" +
                         $"^XZ";

            return Encoding.ASCII.GetBytes(zpl);
        }

        // ───────────────────────── ESC/POS وضع الملصقات ─────────────────────────
        // الطابعة توزّع ملصقًا لكل «باركود + FF» — يعتمد معايرة فجوة اللوحة (إعداد الطابعة).
        // HRI تحت الباركود (GS H 2) = الرقم المقروء تلقائيًا — نفس محتوى ZPL.
        private static byte[] BuildEscPos(string code, int copies)
        {
            var b = new List<byte>();
            b.AddRange([0x1B, 0x40]);                 // ESC @ init
            b.AddRange([0x1D, 0x48, 0x02]);           // HRI تحته
            b.AddRange([0x1D, 0x77, 0x02]);           // عرض الوحدة Ç2
            b.AddRange([0x1D, 0x68, 0x50]);           // ارتفاع 80 نقطة

            bool ean13 = code.Length == 13 && code.All(char.IsDigit);
            for (int i = 0; i < copies; i++)
            {
                if (ean13)
                {
                    // EAN-13 أصلي: GS k 2 13 + الرقم — أسرع مسار مقروء الدرجات
                    b.AddRange([0x1D, 0x6B, 0x02, 0x0D]);
                    b.AddRange(code.Select(c => (byte)c));
                }
                else
                {
                    // CODE128 (م=73): "{B" + كود ثم NUL — أي أرقام/رموز مخصصة
                    byte[] data = Encoding.ASCII.GetBytes("{B" + code + "\0");
                    b.AddRange([(byte)0x1D, (byte)0x6B, 0x73, (byte)data.Length]);
                    b.AddRange(data);
                }
                b.Add(0x0C);                          // FF: إخراج/تقديم ملصق واحد
            }
            return [.. b];
        }
    }
}
