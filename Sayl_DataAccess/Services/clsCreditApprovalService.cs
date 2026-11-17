using Microsoft.EntityFrameworkCore;
using Common.DTOs;
using Common.Results;
using Common.Utils;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>خدمة اعتماد المدير لتجاوز حد الائتمان (سير Task 3).
    /// سياسة صاحب القرار: مدير نظام موثق على الخادم فقط — التحقق يعيد استخدام
    /// دخول SaylPOS نفسه (clsUsersService.LoginAsync: BCrypt + قفل المحاولات +
    /// فحص التفعيل) ثم فحص هوية الدور AppPermissions.IsAdmin — فالكاشير
    /// والمحاسب وأي دور مخصص مرفوضون. لا هوية تُصدَّق من الواجهة، ولا كلمة
    /// مرور تُخزَّن أو تُسجَّل أبدًا (التدقيق يحمل اسم المدير وسببه ومبلغه فقط).
    /// الاعتماد سجل دوري يستهلك مرة واحدة داخل معاملة البيع — لا يعدّل
    /// CreditLimit دائمًا أبدًا (تفويض لعملية واحدة بمبلغ محدد).
    /// ملاحظة قرار: المدير المسجَّل دخوله حاليًا يعيد المصادقة أيضًا عند
    /// الاعتماد (LoginAsync + IsAdmin هنا) — الواجهة ليست حد ثقة، وتطبيق
    /// «إثبات الحضور» عند كل تجاوز متسق مع قفل الخصم/الضريبة القائم.</summary>
    public class clsCreditApprovalService
    {
        private readonly SaylDbContext _dbContext;
        private readonly clsUsersService _usersService;

        /// <summary>حد طول سبب الاعتماد — اتفاقية clsValidation.ValidateString (الحد الأدنى 2).</summary>
        public const int ReasonMaxLength = 500;

        public clsCreditApprovalService(SaylDbContext dbContext, clsUsersService usersService)
        {
            _dbContext = dbContext;
            _usersService = usersService;
        }

        // ═══════════════════════════════════════════════════════════════
        // منح الاعتماد: مصادقة المدير + التحقق من دوره + لقطة دائمة
        // ═══════════════════════════════════════════════════════════════
        /// <summary>يتحقق من مدير النظام (اسم + كلمة مرور حقيقية على الخادم) ثم
        /// يسجّل اعتمادًا granted مرتبطًا بالعميل والمبلغ ولقطة الموقف الائتماني.
        /// يُنادى من الواجهة بعد ظهور نتيجة CreditApprovalRequiredResult — قبل
        /// إعادة محاولة البيع بمعرف الاعتماد (يُستهلك داخل معاملة البيع).
        /// الفشل (اعتماد/كلمة/دور/سبب) نتيجة منظمة + تدقيق رفض على اتصال مستقل.</summary>
        public async Task<Result<int>> ApproveCreditSaleAsync(
            CreditApprovalRequiredInfo info, string managerUsername, string managerPassword, string reason)
        {
            if (info == null)
                return Result<int>.Fail("لا توجد عملية تجاوز حد ائتمان للاعتماد.");

            // سبب إلزامي ذو معنى: غير فارغ، 2..500 حرفًا بعد التشذيب (اتفاقية المشروع)
            reason = reason?.Trim() ?? "";
            if (reason.Length < 2)
                return Result<int>.Fail("سبب الاعتماد مطلوب — اكتب سببًا واضحًا لتجاوز الحد.");
            if (reason.Length > ReasonMaxLength)
                return Result<int>.Fail($"سبب الاعتماد طويل جدًا — الحد الأقصى {ReasonMaxLength} حرفًا.");

            if (info.NewCreditAmount <= 0)
                return Result<int>.Fail("مبلغ ائتمان غير صالح للاعتماد.");
            if (info.CustomerId <= 0)
                return Result<int>.Fail("العميل غير محدد للاعتماد.");

            // ══ مصادقة المدير: دخول SaylPOS نفسه (BCrypt على الخادم) ══
            // قفل المحاولات المتكررة وفحص التفعيل ورسالة موحدة — كلها قائمة.
            var login = await _usersService.LoginAsync(managerUsername?.Trim() ?? "", managerPassword ?? "");
            if (!login.IsSuccess || login.Data == null)
            {
                await clsAudit.LogRejectedAsync(_dbContext, null, "CreditApprovalDenied",
                    $"اعتماد تجاوز حد ائتمان مرفوض: فشل التحقق من هوية المدير " +
                    $"(العميل #{info.CustomerId} — جديد {info.NewCreditAmount:N2} — الحد {info.CreditLimit:N2}).");
                return Result<int>.Fail("فشل التحقق من هوية المدير — راجع اسم المستخدم وكلمة المرور.");
            }

            // ══ فحص هوية الدور: مدير النظام (4095) وحده يعتمد ══
            // IsAdmin بالهوية لا بالبت — المحاسب (508) وأي دور مخصص (سقف 3071)
            // والكاشير (0) مرفوضون بنيويًا مهما كانت شاشاتهم.
            if (!AppPermissions.IsAdmin(login.Data.RoleValue))
            {
                await clsAudit.LogRejectedAsync(_dbContext, login.Data.UserId, "CreditApprovalDenied",
                    $"اعتماد تجاوز حد ائتمان مرفوض: الحساب '{login.Data.UserName}' ليس مدير نظام " +
                    $"(العميل #{info.CustomerId} — جديد {info.NewCreditAmount:N2}).");
                return Result<int>.Fail("هذا الحساب غير مصرَّح له باعتماد تجاوز حد الائتمان — يلزم مدير نظام.");
            }

            // العميل موجود؟ (الاعتماد بلا عميل حقيقي لا معنى له)
            bool customerExists = await _dbContext.Customers.AnyAsync(c => c.PartyId == info.CustomerId);
            if (!customerExists)
                return Result<int>.Fail("العميل غير موجود.");

            var approval = new CreditApproval
            {
                CustomerId = info.CustomerId,
                ManagerUserId = login.Data.UserId,
                CurrentOutstanding = info.CurrentOutstanding,
                CreditLimit = info.CreditLimit,
                NewCreditAmount = info.NewCreditAmount,
                ProjectedOutstanding = info.ProjectedOutstanding,
                ExcessAmount = info.ExcessAmount,
                Reason = reason,
                Status = "granted",
                ApprovedAtUtc = DateTime.UtcNow
            };

            _dbContext.CreditApprovals.Add(approval);
            await _dbContext.SaveChangesAsync();

            // تدقيق المنح: هوية المدير + السبب + الأرقام — لا أسرار أبدًا
            clsAudit.Log(_dbContext, login.Data.UserId, "CreditApprovalGranted",
                $"اعتماد تجاوز حد ائتمان #{approval.ApprovalId} — العميل #{info.CustomerId}: " +
                $"الحالي {info.CurrentOutstanding:N2} + جديد {info.NewCreditAmount:N2} = {info.ProjectedOutstanding:N2} " +
                $"(الحد {info.CreditLimit:N2} — الفائض {info.ExcessAmount:N2}) — السبب: {reason}");
            await _dbContext.SaveChangesAsync();

            return Result<int>.Ok(approval.ApprovalId, "تم اعتماد تجاوز حد الائتمان — أعد إتمام البيع.");
        }

        // ═══════════════════════════════════════════════════════════════
        // إحراق الاعتماد: بيع فشل بعد المنح — سجل يبقى، صلاحية لا تبقى
        // ═══════════════════════════════════════════════════════════════
        /// <summary>انتقال CAS granted → burned خارج معاملة البيع الفاشلة (كتابة
        /// مستقلة تنجو من تراجعها): الاعتماد الذي فشل بيعه يُحرق فلا يستفاد منه
        /// لعملية أخرى، ويبقى سجل تدقيق «مُنح ولم يُستهلك» بحالة صريحة.
        /// استهلاك ناجح داخل معاملة البيع (granted → consumed) لا يمر من هنا.
        /// تُنادى من clsCheckoutService عند فشل بيع حمل معرف اعتماد.</summary>
        public async Task BurnApprovalAsync(int approvalId, string reason)
        {
            try
            {
                int burned = await _dbContext.CreditApprovals
                    .Where(a => a.ApprovalId == approvalId && a.Status == "granted")
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(a => a.Status, "burned"));
                if (burned > 0)
                    await clsAudit.LogRejectedAsync(_dbContext, null, "CreditApprovalBurned",
                        $"اعتماد ائتمان #{approvalId} أُحرق بلا استهلاك: فشلت عملية البيع بعد المنح. السبب: {reason}");
            }
            catch
            {
                // الحرق تحصين — فشله لا يكسر معالجة فشل البيع الأصلية
            }
        }
    }
}
