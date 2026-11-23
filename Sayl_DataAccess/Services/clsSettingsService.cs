using System.Globalization;
using Common.Utils;
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

        /// <summary>معرّف توقيت ويندوز للجهاز المركزي (المصدر الموثوق لتوقيت المتجر) —
        /// يكتبه جهاز الخادم وحده عند كل إقلاع وتقرأه أجهزة البيع (clsStoreClockService).</summary>
        public const string KeyStoreTimeZoneId = "StoreTimeZoneId";

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
                    UpdatedAt = Common.Utils.ProgramClock.UtcNow
                });
            }
            else
            {
                setting.SettingValue = value;
                setting.UpdatedAt = Common.Utils.ProgramClock.UtcNow;
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

        /// <summary>
        /// كتابة نسبة الضريبة — بوابة الخدمة + ذرّية التغيير والتدقيق.
        /// كان المسار SetAsync الخام: أي كود يمسك سياقًا يغيّر ضريبة المتجر بصمت
        /// (بوابة الواجهة تجميل؛ الإنفاذ هنا). السياسة:
        /// • رفض الصلاحية (فاعل ≤ 0 / دور -1 / بلا بت الإعدادات 2048) → Result.Fail فقط
        ///   بلا تدقيق رفض — لا سجل لفشل محاولاته (متطلبات المهمة صريحة).
        /// • فشل النطاق (0–100) → فشل تحقق نطاقي فقط بلا تدقيق.
        /// • لا تغيير → نجاح برسالة «لا تغيير» بلا كتابة وبلا تدقيق.
        /// • التغيير + سجل التدقيق (TaxPercentChanged) داخل معاملة واحدة —
        ///   فشل أيٍّ منهما يتراجع عن الاثنين معًا.
        /// التخزين بثقافة الجهاز "0.##" كعهد المفتاح (عكس حد الائتمان Invariant).
        /// </summary>
        public async Task<Common.Results.Result> SetTaxPercentAsync(decimal value, int actorUserId)
        {
            // ══ بوابة الفاعل: الهوية من الجلسة، والدور يُشتق من القاعدة لا من الواجهة ══
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, actorUserId);
            if (actorUserId <= 0 || actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Settings))
                return Common.Results.Result.Fail(
                    "تعديل نسبة الضريبة يتطلب صلاحية شاشة الإعدادات (بت الإعدادات). راجع المدير.");

            // ══ تحقق النطاق (القاعدة المرجعية هنا — فحص الواجهة للراحة فقط) ══
            if (value < 0 || value > 100)
                return Common.Results.Result.Fail("نسبة الضريبة يجب أن تكون بين 0 و 100.");

            // ══ لا-تغيير: المقارنة رقميًا بنفس قراءة الجالب (ثقافة الجهاز) ══
            var current = await _dbContext.Settings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == KeyTaxPercent);
            if (current?.SettingValue != null &&
                decimal.TryParse(current.SettingValue, out decimal currentValue) &&
                currentValue == value)
            {
                return Common.Results.Result.Ok("نسبة الضريبة كما هي — لا تغيير.");
            }

            string newValueStr = value.ToString("0.##"); // ثقافة الجهاز — عهد المفتاح

            // ══ التغيير + التدقيق: معاملة واحدة أو تراجع عن الاثنين ══
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // القيمة القديمة الخام: يُقرأ من جديد داخل المعاملة (المصدر الموثوق لحظة الكتابة)
                var oldRow = await _dbContext.Settings.FindAsync(KeyTaxPercent);
                string? oldRaw = oldRow?.SettingValue;

                string oldDisplay = oldRaw == null
                    ? "(غير محددة)" // غياب الصف ≠ صفر — يُمثَّل بلا غموض
                    : decimal.TryParse(oldRaw, out decimal oldNum)
                        ? oldNum.ToString("0.##")
                        : $"«{oldRaw}» (غير رقمية)"; // قيمة تالفة تُعرض نصًا خامًا لا 0 مضللًا

                if (oldRow == null)
                {
                    _dbContext.Settings.Add(new Setting
                    {
                        SettingKey = KeyTaxPercent,
                        SettingValue = newValueStr,
                        UpdatedAt = Common.Utils.ProgramClock.UtcNow
                    });
                }
                else
                {
                    oldRow.SettingValue = newValueStr;
                    oldRow.UpdatedAt = Common.Utils.ProgramClock.UtcNow;
                }

                clsAudit.Log(_dbContext, actorUserId, "TaxPercentChanged",
                    $"تعديل نسبة الضريبة: القيمة القديمة = {oldDisplay}، القيمة الجديدة = {newValueStr}.");
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();
                return Common.Results.Result.Ok("تم حفظ نسبة الضريبة.");
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                throw; // لا إعداد بلا تدقيق ولا تدقيق لتغيير مُتراجَع — والفشل تقني يُعاد للمستدعي
            }
        }

        /// <summary>
        /// كتابة الرقم الضريبي (VatNumber) — نفس بوابة ونفس ذرّية SetTaxPercentAsync:
        /// الإعداد ضريبي (هوية الفاتورة ضريبيًا المطبوعة) فالسياسة موحّدة:
        /// • رفض الصلاحية (فاعل ≤ 0 / دور -1 / بلا بت الإعدادات 2048) → Result.Fail
        ///   بلا تدقيق رفض — لا سجل لمحاولات الرفض (متطلبات المهمة صريحة).
        /// • لا تحقق شكل جديد — القيمة نص حر (قد تكون فارغة لمتجر غير مسجّل) كعهد المسار الحالي؛
        ///   الوحيد المفروض هنا: null قادمة من المستدعي تُطبَّع إلى "" (نفس عهد SetAsync الخام).
        /// • لا تغيير (القيمة المخزنة = الجديدة بعد التطبيع) → نجاح بلا كتابة وبلا تدقيق
        ///   (غياب الصف ليس لا-تغييرًا — قيمته المرجعية "غير محددة").
        /// • التغيير + سجل التدقيق (VatNumberChanged: القديمة والجديدة صراحةً، والفارغة
        ///   تمثَّل بلا غموض) داخل معاملة واحدة — فشل أيٍّ يتراجع عن الاثنين.
        /// </summary>
        public async Task<Common.Results.Result> SetVatNumberAsync(string? value, int actorUserId)
        {
            // ══ بوابة الفاعل — مطابقة بوابة الضريبة ══
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, actorUserId);
            if (actorUserId <= 0 || actorRole < 0 || !AppPermissions.Has(actorRole, AppPermissions.Settings))
                return Common.Results.Result.Fail(
                    "تعديل الرقم الضريبي يتطلب صلاحية شاشة الإعدادات (بت الإعدادات). راجع المدير.");

            // ══ تطبيع القيمة: null → "" (نفس عهد مسار SetAsync الخام في الواجهة) ══
            string newValue = value?.Trim() ?? "";

            // ══ لا-تغيير: مقاربة نصية صريحة (الفراغ له معنى هنا) ══
            var current = await _dbContext.Settings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SettingKey == KeyVatNumber);
            if (current?.SettingValue == newValue)
                return Common.Results.Result.Ok("الرقم الضريبي كما هو — لا تغيير.");

            // ══ التغيير + التدقيق: معاملة واحدة أو تراجع عن الاثنين ══
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // القيمة القديمة الخام تُقرأ داخل المعاملة (المصدر الموثوق لحظة الكتابة)
                var oldRow = await _dbContext.Settings.FindAsync(KeyVatNumber);
                string? oldRaw = oldRow?.SettingValue;

                string oldDisplay = oldRaw == null
                    ? "(غير محددة)"   // غياب الصف ≠ نص فارغ — يُمثَّل بلا غموض
                    : oldRaw.Length == 0
                        ? "(فارغ)"    // صف موجود بقيمة "" — حالة مختلفة عن الغياب
                        : $"«{oldRaw}»";

                if (oldRow == null)
                {
                    _dbContext.Settings.Add(new Setting
                    {
                        SettingKey = KeyVatNumber,
                        SettingValue = newValue,
                        UpdatedAt = Common.Utils.ProgramClock.UtcNow
                    });
                }
                else
                {
                    oldRow.SettingValue = newValue;
                    oldRow.UpdatedAt = Common.Utils.ProgramClock.UtcNow;
                }

                string newDisplay = newValue.Length == 0 ? "(فارغ)" : $"«{newValue}»";
                clsAudit.Log(_dbContext, actorUserId, "VatNumberChanged",
                    $"تعديل الرقم الضريبي: القيمة القديمة = {oldDisplay}، القيمة الجديدة = {newDisplay}.");
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();
                return Common.Results.Result.Ok("تم حفظ الرقم الضريبي.");
            }
            catch
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                throw; // لا إعداد بلا تدقيق ولا تدقيق لتغيير مُتراجَع
            }
        }
    }
}
