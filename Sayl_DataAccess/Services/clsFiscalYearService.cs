using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
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
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)) /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */;
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
        public async Task<FiscalCloseCheckVM> GetCloseReadinessAsync()
        {
            var vm = new FiscalCloseCheckVM
            {
                FiscalYearStart = await GetFiscalYearStartAsync()
            };

            // 1) جلسات POS مفتوحة — خطأ مانع
            vm.OpenSessions = await _dbContext.PosSessions.CountAsync(s => s.Status == enPosSessionStatusType.open);
            if (vm.OpenSessions > 0)
                vm.Errors.Add($"{vm.OpenSessions} جلسة بيع مفتوحة — أغلقها قبل الإغلاق السنوي.");

            // 2) فواتير معلقة قديمة (قبل السنة الجديدة) — خطأ مانع
            var fys = vm.FiscalYearStart;
            vm.OldSuspendedInvoices = await _dbContext.Invoices
                .CountAsync(i => i.Status == enInvoicesStatus.suspended && i.CreatedAt < fys.ToDateTime(TimeOnly.MinValue));
            if (vm.OldSuspendedInvoices > 0)
                vm.Errors.Add($"{vm.OldSuspendedInvoices} فاتورة معلقة قبل بداية السنة — أكملها أو ألغها.");

            // 3) كشوف رواتب معلقة داخل السنة — خطأ مانع (لا تُترك راتب بلا مصير)
            vm.PendingPayrolls = await _dbContext.PayrollRecords
                .CountAsync(p => p.Status == enPayrollStatus.pending && p.PeriodFrom >= fys);
            if (vm.PendingPayrolls > 0)
                vm.Errors.Add($"{vm.PendingPayrolls} كشف رواتب معلق — صرفها أو عكسها قبل الإغلاق.");

            // 4) جلسة جرد مفتوحة — خطأ مانع
            vm.OpenStockCount = await _dbContext.StockCounts.AnyAsync(sc => !sc.IsPosted);
            if (vm.OpenStockCount)
                vm.Errors.Add("توجد جلسة جرد مفتوحة — أكملها وترحّلها (الجرد ركن الإغلاق السنوي).");

            // 5) ديون شرائح عميقة (90+ يومًا) — تحذير فقط: تبقى للسنة الجديدة للمطالبة
            var oldDebtInvoices = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt < fys.ToDateTime(TimeOnly.MinValue))
                .Select(i => new { i.InvoiceId, i.NetAmount, i.CreatedAt })
                .ToListAsync();
            var paidByInv = await _dbContext.Payments
                .Where(p => p.InvoiceId != null)
                .GroupBy(p => p.InvoiceId!.Value)
                .Select(g => new { Inv = g.Key, Paid = g.Sum(p => p.Amount) })
                .ToDictionaryAsync(x => x.Inv, x => x.Paid);

            decimal oldDebt = 0;
            foreach (var inv in oldDebtInvoices)
            {
                decimal paid = paidByInv.TryGetValue(inv.InvoiceId, out var p) ? p : 0;
                if (inv.NetAmount - paid > 0.01m)
                    oldDebt += inv.NetAmount - paid;
            }
            vm.OldDebtCarried = Math.Round(oldDebt, 2);
            if (vm.OldDebtCarried > 0)
                vm.Warnings.Add($"ديون قديمة (قبل السنة الحالية) بمبلغ {vm.OldDebtCarried:N2} ستبقى للسنة الجديدة للمطالبة — تأكد أنك على علم بها.");

            // 6) فواتير السنة الجزئية (آجل بلا أي دفعة) — تحذير (استعلام قابل للترجمة)
            var yearInvoices = await _dbContext.Invoices
                .Where(i => i.Type == enInvoicesType.sale
                          && i.Status == enInvoicesStatus.completed
                          && i.PartyId != null
                          && i.CreatedAt >= fys.ToDateTime(TimeOnly.MinValue)
                          && i.CreatedAt < DateTime.UtcNow)
                .Select(i => new { i.InvoiceId, i.NetAmount })
                .ToListAsync();

            var paidTotals = await _dbContext.Payments
                .Where(p => p.InvoiceId != null)
                .GroupBy(p => p.InvoiceId!.Value)
                .Select(g => new { Inv = g.Key, Total = g.Sum(p => p.Amount) })
                .ToListAsync();
            var paidMap = paidTotals.ToDictionary(x => x.Inv, x => x.Total);

            vm.UnpaidThisYear = yearInvoices
                .Count(i => (i.NetAmount - (paidMap.TryGetValue(i.InvoiceId, out var paid) ? paid : 0m)) > 0.01m);
            if (vm.UnpaidThisYear > 0)
                vm.Warnings.Add($"{vm.UnpaidThisYear} فاتورة آجلة/جزئية في السنة الحالية — ستبقى ديونًا مفتوحة بعد الإغلاق.");

            vm.CanClose = vm.Errors.Count == 0;
            return vm;
        }

        /// <summary>
        /// قلب السنة: تحديث FiscalYearStart لليوم الجديد + تسجيل تاريخ الإغلاق.
        /// لا يُحذف ولا يُعدّل أي سجل بيانات — فقط مفتاحا Settings.
        /// يستدعيه معالج الإغلاق بعد النسخ الاحتياطي والتقارير الختامية.
        /// </summary>
        public async Task<Result> CloseYearAsync(int userId, DateOnly newYearStart)
        {
            var current = await GetFiscalYearStartAsync();
            if (newYearStart <= current)
                return Result.Fail($"تاريخ السنة الجديدة يجب أن يكون بعد {current:yyyy-MM-dd} (بداية السنة الحالية).");

            if (newYearStart > DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)) /* VULN-45: اليوم بتوقيت البرنامج UTC+3 */)
                return Result.Fail("لا يمكن بدء سنة مالية في المستقبل.");

            var readiness = await GetCloseReadinessAsync();
            if (!readiness.CanClose)
                return Result.Fail("فحوصات الجاهزية لم تكتمل:\n- " + string.Join("\n- ", readiness.Errors));

            await _settings.SetAsync(KeyFiscalYearStart, newYearStart.ToString("yyyy-MM-dd"));
            await _settings.SetAsync(KeyLastCloseDate, DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd"));

            clsAudit.Log(_dbContext, userId, "CloseFiscalYear",
                $"إغلاق السنة المالية: من {current:yyyy-MM-dd} إلى {newYearStart:yyyy-MM-dd} — ديون محمولة: {readiness.OldDebtCarried:N2}");
            await _dbContext.SaveChangesAsync();

            return Result.Ok($"تم إغلاق السنة المالية. السنة الجديدة تبدأ من {newYearStart:yyyy-MM-dd}.");
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
