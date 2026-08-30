using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
using IOPath = System.IO.Path;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// مسار طباعة موحّد للفواتير: جلب بيانات الفاتورة + إعدادات المحل (async كامل)
    /// ثم توليد إيصال 80mm PDF في Documents\Sayl_POS\Invoices وفتح نافذة الطباعة
    /// بالنسخة الجاهزة. يستخدمه POS بعد إتمام البيع وشاشة تفاصيل الفاتورة.
    /// </summary>
    internal static class InvoicePrintHelper
    {
        /// <summary>طباعة فاتورة بالمعرف — مسار كامل async لا يعلّق الواجهة.</summary>
        public static async Task PrintAsync(int invoiceId, IWin32Window? owner)
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
                string dir = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Sayl_POS", "Invoices",
                    InvoiceDetailsForm.TranslateType(invoice.Type));
                System.IO.Directory.CreateDirectory(dir);

                string fileName = $"Invoice_{invoice.InvoiceId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                filePath = IOPath.Combine(dir, fileName);

                var inv = invoice;
                await Task.Run(() =>
                {
                    PdfService.EnsureLicense();
                    PdfService.ExportInvoiceReceipt(inv, storeName, storePhone, vatNumber, receiptFooter, filePath);
                });
            }
            catch
            {
                UiHelpers.ShowError(owner, "تعذر توليد ملف PDF.");
                return;
            }

            // فتح نافذة الطباعة على النسخة الجاهزة (الفعل "print" — المعالج الافتراضي للنظام)
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    Verb = "print",
                    UseShellExecute = true
                });
            }
            catch
            {
                // لا يوجد معالج "print" — نفتح الملف للعرض ويطبع المستخدم من العارض
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
}
