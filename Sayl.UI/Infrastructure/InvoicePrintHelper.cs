using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
using IOPath = System.IO.Path;

namespace Sayl.UI.Infrastructure
{
    /// <summary>صيغة الطباعة المطلوبة لفاتورة معينة.</summary>
    internal enum InvoicePrintFormat
    {
        /// <summary>إيصال حراري 80mm — الافتراضي لفواتير الكاشير السريعة (بيع/مرتجع عميل).</summary>
        ThermalReceipt,

        /// <summary>فاتورة A4 احترافية مفصلة — للطباعة الرسمية (بيع/شراء/مرتجعات).</summary>
        A4Detailed
    }

    /// <summary>
    /// مسار طباعة موحّد للفواتير: جلب بيانات الفاتورة + إعدادات المحل (async كامل)
    /// ثم توليد PDF في Documents\Sayl_POS\Invoices وفتحه للعرض/الطباعة.
    /// يميّز بين صيغتين متمايزتين:
    ///  • ThermalReceipt (80mm) — لفواتير الكاشير اليومية السريعة.
    ///  • A4Detailed (A4) — الفاتورة الرسمية المفصلة بالتوقيعات للاحتياجات الرسمية.
    /// التوليد داخل Task.Run دائمًا — لا تجميد للواجهة مهما كبرت الفاتورة.
    /// </summary>
    internal static class InvoicePrintHelper
    {
        /// <summary>طباعة فاتورة بالمعرف (إيصال حراري افتراضيًا) — مسار async كامل لا يعلّق الواجهة.</summary>
        public static Task PrintAsync(int invoiceId, IWin32Window? owner)
            => PrintAsync(invoiceId, owner, InvoicePrintFormat.ThermalReceipt);

        /// <summary>طباعة فاتورة بالمعرف بصيغة محددة — مسار async كامل لا يعلّق الواجهة.</summary>
        public static async Task PrintAsync(int invoiceId, IWin32Window? owner, InvoicePrintFormat format)
        {
            InvoicePrintVM? invoice = null;
            string storeName = "سيل";
            string storePhone = "";
            string vatNumber = "";
            string? receiptFooter = null;

            try
            {
                using (var scope = Program.Services.CreateScope())
                {
                    var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                    var result = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                    if (result.IsSuccess)
                        invoice = result.Data;

                    var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await settings.GetAsync(clsSettingsService.KeyStoreName, "سيل");
                    storePhone = await settings.GetAsync(clsSettingsService.KeyStorePhone, "");
                    vatNumber = await settings.GetAsync(clsSettingsService.KeyVatNumber, "");
                    receiptFooter = await settings.GetAsync(clsSettingsService.KeyReceiptFooter, "");
                }
            }
            catch
            {
                UiHelpers.ShowError(owner, "تعذر تحميل بيانات الفاتورة.");
                return;
            }

            if (invoice == null)
            {
                UiHelpers.ShowError(owner, "الفاتورة غير موجودة.");
                return;
            }

            string? filePath = null;
            try
            {
                // مجلد منفصل لكل صيغة — الحرارية عن الرسمية A4
                string formatFolder = format == InvoicePrintFormat.A4Detailed ? "A4" : "Thermal";
                string dir = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Sayl_POS", "Invoices", formatFolder,
                    InvoiceDetailsForm.TranslateType(invoice.Type));
                System.IO.Directory.CreateDirectory(dir);

                string fileName = $"Invoice_{invoice.InvoiceId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                filePath = IOPath.Combine(dir, fileName);

                var inv = invoice;
                await Task.Run(() =>
                {
                    PdfService.EnsureLicense();
                    if (format == InvoicePrintFormat.A4Detailed)
                        PdfService.ExportInvoiceA4(inv, storeName, storePhone, vatNumber, receiptFooter, filePath);
                    else
                        PdfService.ExportInvoiceReceipt(inv, storeName, storePhone, vatNumber, receiptFooter, filePath);
                });
            }
            catch
            {
                UiHelpers.ShowError(owner, "تعذر توليد ملف PDF.");
                return;
            }

            // 📖 فتح الملف للعرض (لا فعل "print" — بعض معالجات PDF تعلّق العملية كلها
            // بحوارات طباعة خارج نافذتنا). المستخدم يطبع من العارض بضغطة واحدة.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                UiHelpers.ShowInfo(owner, $"الملف محفوظ في:\n{filePath}");
            }
        }
    }
}
