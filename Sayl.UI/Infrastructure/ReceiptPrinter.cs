using System.Runtime.InteropServices;
using System.Text;
using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// طباعة إيصال حراري ESC/POS (طابعات الكاشير) مع دعم العربية (Codepage 1256).
    /// ترسل بايتات خام إلى الطابعة عبر خدمة الطباعة في ويندوز.
    /// </summary>
    internal static class ReceiptPrinter
    {
        private const decimal DefaultPaperWidthColumns = 42m; // طابعة 80مم

        [DllImport("winspool.Drv", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        public static IEnumerable<string> GetInstalledPrinters()
        {
            // قد يفشل تعداد الطابعات إذا كانت خدمة الطباعة متوقفة (RPC unavailable) — لا نعطل الواجهة.
            try
            {
                return System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>إرسال بايتات خام إلى طابعة.</summary>
        private static bool SendBytesToPrinter(string printerName, byte[] bytes)
        {
            if (!OpenPrinter(printerName.Normalize(), out IntPtr hPrinter, IntPtr.Zero))
                return false;

            var di = new DOCINFOA
            {
                pDocName = "Sayl Receipt",
                pOutputFile = null!,
                pDataType = "RAW"
            };

            bool success = false;
            if (StartDocPrinter(hPrinter, 1, ref di))
            {
                if (StartPagePrinter(hPrinter))
                {
                    IntPtr pUnmanaged = Marshal.AllocCoTaskMem(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, pUnmanaged, bytes.Length);
                        success = WritePrinter(hPrinter, pUnmanaged, bytes.Length, out _);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pUnmanaged);
                    }
                    EndPagePrinter(hPrinter);
                }
                EndDocPrinter(hPrinter);
            }
            ClosePrinter(hPrinter);
            return success;
        }

        /// <summary>بناء إيصال ESC/POS وطباعته على الطابعة المحددة.</summary>
        public static async Task<(bool ok, string message)> PrintInvoiceAsync(
            InvoicePrintVM invoice, string printerName)
        {
            try
            {
                string storeName = "سيل", storePhone = "", vatNumber = "", footer = "";
                using (var scope = Program.Services.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, "سيل");
                    storePhone = await settings.GetAsync(clsSettingsService.KeyStorePhone);
                    vatNumber = await settings.GetAsync(clsSettingsService.KeyVatNumber);
                    footer = await settings.GetAsync(clsSettingsService.KeyReceiptFooter);
                }

                var encoding = Encoding.GetEncoding(1256);
                var bytes = new List<byte>();

                // تهيئة + ترميز عربي CP1256
                bytes.AddRange(new byte[] { 0x1B, 0x40 });          // ESC @ init
                bytes.AddRange(new byte[] { 0x1B, 0x74, 0x17 });    // ESC t 23 → CP1256

                void Line(string text) => bytes.AddRange(encoding.GetBytes(text + "\n"));

                void Centered(string text)
                {
                    int pad = Math.Max(0, ((int)DefaultPaperWidthColumns - text.Length) / 2);
                    Line(new string(' ', pad) + text);
                }

                void Divider() => Line(new string('-', (int)DefaultPaperWidthColumns));

                // الترويسة
                bytes.AddRange(new byte[] { 0x1B, 0x61, 0x01 });    // توسيط
                bytes.AddRange(new byte[] { 0x1D, 0x21, 0x11 });    // خط كبير
                Line(storeName);
                bytes.AddRange(new byte[] { 0x1D, 0x21, 0x00 });    // حجم عادي
                if (!string.IsNullOrWhiteSpace(storePhone)) Line($"هاتف: {storePhone}");
                if (!string.IsNullOrWhiteSpace(vatNumber)) Line($"الرقم الضريبي: {vatNumber}");
                bytes.AddRange(new byte[] { 0x1B, 0x61, 0x00 });    // محاذاة يمين
                Divider();
                Line($"فاتورة: {invoice.InvoiceId}    {Sayl.UI.Infrastructure.AppClock.ToLocal(invoice.CreatedAt ?? DateTime.UtcNow):yyyy-MM-dd HH:mm}");
                Line($"الكاشير: {invoice.CashierName}");
                if (!string.IsNullOrWhiteSpace(invoice.PartyName)) Line($"العميل: {invoice.PartyName}");
                Divider();

                // البنود
                foreach (var item in invoice.Items)
                {
                    Line(item.ProductName);
                    Line($"  {item.Quantity} × {item.ItemPrice:N2} = {item.ItemPrice * item.Quantity:N2}");
                }

                Divider();
                Line($"الإجمالي: {invoice.TotalAmount:N2}");
                if (invoice.DiscountAmount > 0) Line($"الخصم: {invoice.DiscountAmount:N2}");
                if (invoice.TaxAmount > 0) Line($"الضريبة: {invoice.TaxAmount:N2}");
                bytes.AddRange(new byte[] { 0x1B, 0x21, 0x20 });    // خط عريض
                Line($"الصافي: {invoice.NetAmount:N2}");
                bytes.AddRange(new byte[] { 0x1B, 0x21, 0x00 });
                Line($"المدفوع: {invoice.PaidAmount:N2}");
                if (invoice.RemainingAmount > 0)
                    Line($"المتبقي: {invoice.RemainingAmount:N2}");
                Divider();

                // المدفوعات
                foreach (var p in invoice.Payments)
                    Line($"{TranslateMethod(p.PaymentMethod)}: {p.Amount:N2}");

                Centered("شكرًا لتعاملكم معنا!");
                if (!string.IsNullOrWhiteSpace(footer)) Centered(footer);

                // قص الورق
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 });

                bool ok = SendBytesToPrinter(printerName, bytes.ToArray());
                return ok
                    ? (true, "تمت الطباعة.")
                    : (false, $"تعذر الوصول للطابعة '{printerName}'.");
            }
            catch (Exception ex)
            {
                return (false, $"خطأ في الطباعة: {ex.Message}");
            }
        }

        private static string TranslateMethod(string method) => method switch
        {
            "cash" => "نقدي",
            "card" => "شبكة",
            "bank_transfer" => "تحويل بنكي",
            _ => method
        };
    }
}
