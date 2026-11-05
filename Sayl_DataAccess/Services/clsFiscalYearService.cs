using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// السنة المالية: تاريخ البداية في Settings (تقارير «الحالية» تبدأ منه تلقائيًا)،
    /// فحوصات جاهزية الإغلاق، والقلب السنوي — محاكاة «البداية الجديدة» دون حذف سجل واحد:
    /// التاريخ كله يبقى قابلًا للقراءة والمقارنة، والتعديل قبله يُقفل.
    /// </summary>
    public class clsFiscalYearService
    {
        public const string KeyFiscalYearStart = "FiscalYearStart";
        public const string KeyLastCloseDate = "FiscalYearClosedOn";

        private readonly SaylDbContext _dbContext;
        private readonly clsSettingsService _settings;

        public clsFiscalYearService(SaylDbContext dbContext, clsSettingsService settings)
        {
            _dbContext = dbContext;
            _settings = settings;
        }

        /// <summary>بداية السنة المالية الحالية (تُستخدم كحد أدنى افتراضي للتقارير الجارية والقفل).</summary>
        public async Task<DateOnly> GetFiscalYearStartAsync()
        {
            var raw = await _settings.GetAsync(KeyFiscalYearStart, "");
            if (DateOnly.TryParse(raw, out var d))
                return d;

            // الافتراضي: بداية السنة الميلادية الجارية عند أول تشغيل
            var today = ProgramClock.Today; /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */
            return new DateOnly(today.Year, 1, 1);
        }

        /// <summary>تاريخ آخر إغلاق سنة (null إن لم يُغلق أبدًا).</summary>
        public async Task<DateOnly?> GetLastCloseDateAsync()
        {
            var raw = await _settings.GetAsync(KeyLastCloseDate, "");
            return DateOnly.TryParse(raw, out var d) ? d : null;
        }

        /// <summary>
        /// فحوصات جاهزية الإغلاق: أخطاء تمنع، وتحذيرات تسمح بالمتابعة.
        /// يُستدعى قبل خطوة الأرشفة في معالج الإغلاق.
        /// </summary>
        /// <param name="closingTo">
        /// نهاية الفترة التي تُغلق (بداية السنة الجديدة المرشحة). عند تمريرها تُفحص
        /// الفواتير المعلقة والعناصر المعلّقة كاملةً ضد هذا الحد — الفترة الفعلية
        /// المغلقة [FiscalYearStart, closingTo) — لا ضد حد افتراضي خاطئ.
        /// </param>
        public async Task<FiscalCloseCheckVM> GetCloseReadinessAsync(DateOnly? closingTo = null)
        {
            var vm = new FiscalCloseCheckVM
            {
                FiscalYearStart = await GetFiscalYearStartAsync()
            };

            DateOnly periodEnd = closingTo ?? ProgramClock.Today /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */;
            DateTime periodEndUtc = periodEnd.ToDateTime(TimeOnly.MinValue);

            // 1) جلسات POS مفتوحة — خطأ مانع
            vm.OpenSessions = await _dbContext.PosSessions.CountAsync(s => s.Status == enPosSessionStatusType.open);
            if (vm.OpenSessions > 0)
                vm.Errors.Add($"{vm.OpenSessions} جلسة بيع مفتوحة — أغلقها قبل الإغلاق السنوي.");

            // 2) فواتير معلقة داخل الفترة المغلقة [FiscalYearStart, periodEnd) — خطأ مانع
            //    (أي فاتورة معلقة ستنجز نتائجها المالية في السنة الجديدة بعد الإغلاق
            //     — يجب حسمها قبل الإغلاق: إكمالها إن كانت لسنة جارية، أو إلغاؤها)
            //    الحدان معًا: فاتورة معلقة من سنة أقدم مغلقة سلفًا (قبل FiscalYearStart)
            //    لا تعود لهذه الفترة — لا يصح أن تسدّ إغلاق سنة لا تعنيها.
            var fys = vm.FiscalYearStart;
            DateTime fysUtc = fys.ToDateTime(TimeOnly.MinValue);
            vm.OldSuspendedInvoices = await _dbContext.Invoices
                .CountAsync(i => i.Status == enInvoicesStatus.suspended
                              && i.CreatedAt >= fysUtc
                              && i.CreatedAt < periodEndUtc);
            if (vm.OldSuspendedInvoices > 0)
                vm.Errors.Add($"{vm.OldSuspendedInvoices} فاتورة معلقة داخل الفترة المغلقة — أكملها أو ألغها قبل الإغلاق.");

            // 3) كشوف رواتب معلقة تتقاطع مع الفترة المغلقة — خطأ مانع (لا تُترك راتب بلا مصير)
            //    التقاطع الفعلي [PeriodFrom, PeriodTo] ∩ [fys, periodEnd) ≠ ∅:
            //    • كشف داخل الفترة كلها — يُحسب.
            //    • كشف يمتد عبر أحد الحدين (بدأت قبل fys ولم تُحسم، أو بدأت داخلها
            //      وتمتد إلى ما بعد periodEnd) — يُحسب: جزء من نتائجه المالية في الفترة المغلقة.
            //    • كشف قبل fys أو يبدأ من periodEnd فصاعدًا (سنة جديدة) — لا يُحسب.
            //    الحدّان معًا (الشرطان) — حد واحد وحده يفلت كشوفًا عبر الحدود.
            vm.PendingPayrolls = await _dbContext.PayrollRecords
                .CountAsync(p => p.Status == enPayrollStatus.pending
                              && p.PeriodFrom < periodEnd
                              && p.PeriodTo >= fys);
            if (vm.PendingPayrolls > 0)
                vm.Errors.Add($"{vm.PendingPayrolls} كشف رواتب معلق — صرفها أو عكسها قبل الإغلاق.");

            // 4) جلسة جرد مفتوحة — خطأ مانع
            vm.OpenStockCount = await _dbContext.StockCounts.AnyAsync(sc => !sc.IsPosted);
            if (vm.OpenStockCount)
                vm.Errors.Add("توجد جلسة جرد مفتوحة — أكملها وترحّلها (الجرد ركن الإغلاق السنوي).");

            // 5) ديون شرائح عميقة (90+ يومًا) — تحذير فقط: تبقى للسنة الجديدة للمطالبة
            //    ══ التسوية الحتمية ══ «المتبقي» لكل فاتورة يُحسب عبر clsPaymentAllocation
            //    — نفس المعادلة التي يحكم بها سداد الفواتير (PayInvoiceAsync) وخطط
            //    الاسترداد (BuildRefundPlanAsync): الصافي بعد المرتجعات − الدفعات
            //    المرتبطة − نصيب الدفعات العامة (تخصيص الأقدم أولًا).
            //    كان الحساب يعتبر الدفعات المرتبطة مباشرة وحدها، ففاتورة سُددت عبر
            //    تحصيل عام (بلا فاتورة) أو سُددت بالمرتجع ظهرت «دينًا قديمًا» وهميًا —
            //    وتحذير مغلوط عند الإغلاق يعني قرار محاسبي مبني على أرقام خاطئة.
            var oldDebtInvoices = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt < fysUtc)
                .Select(i => new { i.InvoiceId, i.NetAmount, i.CreatedAt })
                .ToListAsync();
            var oldOutstanding = await clsPaymentAllocation.GetOutstandingForInvoicesAsync(
                _dbContext, oldDebtInvoices.Select(i => i.InvoiceId));

            decimal oldDebt = 0;
            foreach (var inv in oldDebtInvoices)
            {
                decimal outstanding = oldOutstanding.TryGetValue(inv.InvoiceId, out var o)
                    ? o : inv.NetAmount;
                if (outstanding > 0.01m)
                    oldDebt += outstanding;
            }
            vm.OldDebtCarried = Math.Round(oldDebt, 2);
            if (vm.OldDebtCarried > 0)
                vm.Warnings.Add($"ديون قديمة (قبل السنة الحالية) بمبلغ {vm.OldDebtCarried:N2} ستبقى للسنة الجديدة للمطالبة — تأكد أنك على علم بها.");

            // 6) فواتير السنة الجزئية (آجل بلا أي دفعة) — تحذير (استعلام قابل للترجمة)
            //    ══ التسوية الحتمية ══ نفس معادلة الفقرة 5: المتبقي عبر
            //    clsPaymentAllocation يشمل الدفعات العامة المخصَّصة والمرتجعات —
            //    فلا تُعد فاتورة سُددت بتحصيل عام أو غطاها مرتجع «آجلة» زائفة.
            var yearInvoices = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt >= fysUtc
                          && i.CreatedAt < periodEndUtc)
                .Select(i => new { i.InvoiceId, i.NetAmount })
                .ToListAsync();

            var yearOutstanding = await clsPaymentAllocation.GetOutstandingForInvoicesAsync(
                _dbContext, yearInvoices.Select(i => i.InvoiceId));

            vm.UnpaidThisYear = yearInvoices
                .Count(i => (yearOutstanding.TryGetValue(i.InvoiceId, out var outstanding)
                                ? outstanding : i.NetAmount) > 0.01m);
            if (vm.UnpaidThisYear > 0)
                vm.Warnings.Add($"{vm.UnpaidThisYear} فاتورة آجلة/جزئية في السنة الحالية — ستبقى ديونًا مفتوحة بعد الإغلاق.");

            vm.CanClose = vm.Errors.Count == 0;
            return vm;
        }

        /// <summary>
        /// قلب السنة: تحديث FiscalYearStart لليوم الجديد + تسجيل تاريخ الإغلاق.
        /// لا يُحذف ولا يُعدّل أي سجل بيانات — فقط مفتاحا Settings.
        /// يستدعيه معالج الإغلاق بعد النسخ الاحتياطي والتقارير الختامية.
        ///
        /// ══ الصلاحية ══ الإغلاق يتطلب صلاحية شاشة المخزون (الجرد ركن الإغلاق
        /// السنوي) — نفس بوابة الزر في شاشة الإعدادات. الفحص هنا في الخدمة لا
        /// يكتفي بإخفاء الزر في الواجهة (الإخفاء تجميل؛ البوابة خدمية).
        ///
        /// ══ الترتيب عند أول إغلاق ══ صف FiscalYearStart قد يكون غير موجود:
        /// GetFiscalYearStartAsync يعود حينها بالافتراضي (1 يناير للسنة الحالية)
        /// وهو فعليًا ما كانت تعتبره بقية الخدمات بداية للسنة. لذا تُحسب فحوصات
        /// الجاهزية للفترة السابقة [الافتراضي/المخزّن, newYearStart) قبل أي كتابة —
        /// الإدراج الخام لصف البداية الجديد قبل الفحص كان يجعل GetAsync (عبر
        /// FindAsync) يقرأ القيمة الجديدة لحظًا فيرى الفحص «الفترة السابقة» فارغة
        /// ويمرّ كل شيء دون فحص حقيقي. وبعد القلب يُفرَّغ ChangeTracker حتى لا
        /// تعيد قيم Settings المتتبعة (القديمة) الكتابة فوق القيم الخام الجديدة.
        ///
        /// ══ الذرية ══ كلا المفتاحين + سجل التدقيق يُحدَّثون داخل معاملة واحدة:
        /// تحديث FiscalYearStart يُنفَّذ بمقارنة-وتبديل ذرية (CAS) على القيمة المتوقعة،
        /// فمحاولة إغلاق متزامنة ثانية تفشل بأمان بدل أن تُغلق مرتين — وفشل أي
        /// خطوة لاحقة يتراجع عن كل شيء (لا حالة نصفية: سنة جديدة بلا تاريخ إغلاق).
        /// </summary>
        public async Task<Result> CloseYearAsync(int userId, DateOnly newYearStart)
        {
            // 0) بوابة الصلاحية: شاشة المخزون (الجرد ركن الإغلاق السنوي) —
            //    مطابقة لبوابة الزر في SettingsControl؛ المستخدم بلا دور نشط (-1) يُرفض.
            int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, userId);
            if (!AppPermissions.Has(actorRole, AppPermissions.Inventory))
                return Result.Fail("إغلاق السنة المالية يتطلب صلاحية شاشة المخزون (الجرد ركن الإغلاق السنوي).");

            var current = await GetFiscalYearStartAsync();
            if (newYearStart <= current)
                return Result.Fail($"تاريخ السنة الجديدة يجب أن يكون بعد {current:yyyy-MM-dd} (بداية السنة الحالية).");

            if (newYearStart > ProgramClock.Today /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */)
                return Result.Fail("لا يمكن بدء سنة مالية في المستقبل.");

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // 1) فحوصات الجاهزية أولًا — قبل أي كتابة للبداية الجديدة:
                //    الفترة المغلقة [current, newYearStart) حيث current هو المخزّن
                //    أو الافتراضي عند غياب الصف (أول إغلاق على الإطلاق). حسابها
                //    بعد الإدراج الخام كان يجعل FindAsync يقرأ البداية الجديدة
                //    فيفحص فترة فارغة. ولا يوجد متتبَّع لصف البداية قبل هذا الحد
                //    إلا بقيمته القديمة الصحيحة، فالقراءة هنا صحيحة في الحالتين.
                var readiness = await GetCloseReadinessAsync(newYearStart);
                if (!readiness.CanClose)
                    return Result.Fail("فحوصات الجاهزية لم تكتمل:\n- " + string.Join("\n- ", readiness.Errors));

                // 2) المقارنة-والتبديل الذرية: التحديث ينجح فقط إن كانت بداية السنة
                //    ما زالت القيمة المقروءة — من يصل أولًا يفوز، والمتأخر يرى صفر
                //    صفوف متأثرة فيفشل بأمان (لا إغلاق مزدوج).
                string currentStr = current.ToString("yyyy-MM-dd");
                string newStr = newYearStart.ToString("yyyy-MM-dd");
                // GETUTCDATE لا GETDATE: Settings.UpdatedAt منطقة زمنية واحدة (UTC) —
                // clsSettingsService يكتبها DateTime.UtcNow (VULN-45).
                int swapped = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Settings SET SettingValue = {newStr}, UpdatedAt = GETUTCDATE() WHERE SettingKey = {KeyFiscalYearStart} AND SettingValue = {currentStr}");

                bool startRowExists = await _dbContext.Settings
                    .AnyAsync(s => s.SettingKey == KeyFiscalYearStart);

                if (swapped == 0)
                {
                    if (startRowExists)
                        return Result.Fail("تغيّرت بداية السنة المالية أثناء الإغلاق (محاولة إغلاق متزامنة) — أُلغيت هذه المحاولة. أعد الفحص ثم أعد المحاولة.");

                    // أول إغلاق على الإطلاق: صف البداية غير موجود — يُدرج من الصفر
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO Settings (SettingKey, SettingValue, UpdatedAt) VALUES ({KeyFiscalYearStart}, {newStr}, GETUTCDATE())");
                }

                // 3) تفريغ المتتبع قبل أي كيان جديد: قيم Settings (وربما بيانات أخرى)
                //    صارت متتبعة من فحوصات الجاهزية بقيمها ما قبل القلب — بلا تفريغ
                //    يعيد FindAsync القيمة القديمة وSaveChanges قد يعيد كتابتها فوق
                //    القيم الخام المكتوبة للتو. كل ما سبق SQL خام، فلا يُفقد شيء.
                _dbContext.ChangeTracker.Clear();

                // 4) تاريخ الإغلاق — UPSERT داخل نفس المعاملة
                string closeDateStr = ProgramClock.Today.ToString("yyyy-MM-dd"); /* VULN-45: يوم البرنامج */
                int closeUpdated = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Settings SET SettingValue = {closeDateStr}, UpdatedAt = GETUTCDATE() WHERE SettingKey = {KeyLastCloseDate}");
                if (closeUpdated == 0)
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO Settings (SettingKey, SettingValue, UpdatedAt) VALUES ({KeyLastCloseDate}, {closeDateStr}, GETUTCDATE())");

                clsAudit.Log(_dbContext, userId, "CloseFiscalYear",
                    $"إغلاق السنة المالية: من {current:yyyy-MM-dd} إلى {newYearStart:yyyy-MM-dd} — ديون محمولة: {readiness.OldDebtCarried:N2}");
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();
                return Result.Ok($"تم إغلاق السنة المالية. السنة الجديدة تبدأ من {newYearStart:yyyy-MM-dd}.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result.Fail($"فشل إغلاق السنة المالية ({ex.GetType().Name}) — لم يُعدَّل أي شيء، أعد المحاولة.");
            }
        }

        /// <summary>
        /// حماية ما قبل السنة: هل التاريخ المطلوب (سجل/فترة قديمة) يقع قبل السنة الحالية؟
        /// تُستدعى من الخدمات الحساسة لرفض تعديل الماضي المؤرشف.
        /// </summary>
        public async Task<bool> IsBeforeCurrentFiscalYearAsync(DateOnly date)
        {
            return date < await GetFiscalYearStartAsync();
        }
    }
}
