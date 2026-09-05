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
    }
}
