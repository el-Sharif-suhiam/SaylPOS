using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// إعدادات التطبيق (Key/Value) مع قيم افتراضية.
    /// المفاتيح: StoreName, StorePhone, VatNumber, ReceiptFooter, TaxPercent, BackupFolder, AutoPrintReceipt, LastBackupDate
    /// </summary>
    public class clsSettingsService
    {
        private readonly SaylDbContext _dbContext;

        public clsSettingsService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public const string KeyStoreName = "StoreName";
        public const string KeyStorePhone = "StorePhone";
        public const string KeyVatNumber = "VatNumber";
        public const string KeyReceiptFooter = "ReceiptFooter";
        public const string KeyWelcomeMessage = "WelcomeMessage";   // رسالة شاشة الزبون الثانية
        public const string KeyTaxPercent = "TaxPercent";
        public const string KeyBackupFolder = "BackupFolder";
        public const string KeyAutoPrintReceipt = "AutoPrintReceipt";
        public const string KeyLastBackupDate = "LastBackupDate";
        public const string KeyReceiptPrinter = "ReceiptPrinter";
        public const string KeyDefaultCreditLimit = "DefaultCreditLimit";

        /// <summary>حد الائتمان الافتراضي للعملاء الجدد عند غياب صف الإعداد —
        /// 1000 بعملة المتجر (متطلب Task 1: قيمة ابتدائية معتمدة).</summary>
        public const decimal DefaultCreditLimitValue = 1000m;

        /// <summary>الحد الأقصى المقبول للإعداد — سعة العمود Customers.CreditLimit
        /// DECIMAL(10,2) (99,999,999.99): قيمة أعلى من ذلك تُفشل إنشاء أي عميل
        /// بخطأ تجاوز سعة القاعدة، فنرفضها عند الإعداد بدل السماح بانفجار لاحق.</summary>
        public const decimal MaxCreditLimitValue = 99_999_999.99m;

        public async Task<string> GetAsync(string key, string defaultValue = "")
        {
            var setting = await _dbContext.Settings.FindAsync(key);
            if (setting == null || setting.SettingValue == null)
                return defaultValue;
            return setting.SettingValue;
        }

        public async Task<decimal> GetDecimalAsync(string key, decimal defaultValue)
        {
            var value = await GetAsync(key);
            return decimal.TryParse(value, out var v) ? v : defaultValue;
        }

        public async Task<bool> GetBoolAsync(string key, bool defaultValue)
        {
            var value = await GetAsync(key);
            return bool.TryParse(value, out var v) ? v : defaultValue;
        }

        public async Task SetAsync(string key, string value)
        {
            var setting = await _dbContext.Settings.FindAsync(key);
            if (setting == null)
            {
                _dbContext.Settings.Add(new Setting
                {
                    SettingKey = key,
                    SettingValue = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                setting.SettingValue = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync();
        }

        /// <summary>قراءة حد الائتمان الافتراضي للعملاء الجدد. غياب الصف أو قيمة
        /// غير رقمية أو سالبة (تلاعب يدوي بالقاعدة) يعود بالافتراضية المعتمدة
        /// (1000) — الإقلاع لا يفشل أبدًا على قيمة تالفة ولا يمرّر حدًّا سالبًا
        /// لعميل جديد. التخزين والقراءة بثقافة Invariant (عكس TaxPercent الذي
        /// يكتب/يقرأ بثقافة الجهاز) لأن كاتب هذا المفتاح هو SetDefaultCreditLimitAsync
        /// وحده — لا جدال ثقافي حول الفاصلة العشرية بعد إعادة التشغيل.</summary>
        public async Task<decimal> GetDefaultCreditLimitAsync()
        {
            string raw = await GetAsync(KeyDefaultCreditLimit);
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
                && v >= 0
                ? v
                : DefaultCreditLimitValue;
        }

        /// <summary>كتابة حد الائتمان الافتراضي مع التحقق الخدمي المرجعي:
        /// يُرفض السالب وما يتجاوز سعة العمود DECIMAL(10,2) — والصفر مقبول
        /// ومقصود (عميل جديد بلا آجل). تُخزَّن القيمة مقرّبةً لمنزلتين بثقافة
        /// Invariant كما ستمرّ على العمود لاحقًا (النوع decimal لا يعرف NaN/Infinity
        /// فلا فحص لعدم النهاية هنا — الشكل غير الرقمي يُرفض عند التحويل قبل الوصول).</summary>
        public async Task<Common.Results.Result> SetDefaultCreditLimitAsync(decimal value)
        {
            if (value < 0)
                return Common.Results.Result.Fail("لا يمكن أن يكون حد الائتمان الافتراضي سالبًا — أدخل 0 أو أكثر.");
            if (value > MaxCreditLimitValue)
                return Common.Results.Result.Fail($"حد الائتمان الافتراضي يتجاوز الحد الأقصى المسموح ({MaxCreditLimitValue.ToString("0.##", CultureInfo.InvariantCulture)}).");

            await SetAsync(KeyDefaultCreditLimit, Math.Round(value, 2, MidpointRounding.AwayFromZero)
                .ToString("0.##", CultureInfo.InvariantCulture));
            return Common.Results.Result.Ok("تم حفظ حد الائتمان الافتراضي.");
        }
    }
}
