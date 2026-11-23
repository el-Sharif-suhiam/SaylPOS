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
        /// القيم المُقدَّمة (الرصيد/الحد) تُعاد مطابقتها مع دفتر Customers.Balance
        /// قبل المنح: أي تزوير أو قِدم (تغيّر الرصيد/الحد) = رفض صريح — ويُخزَّن
        /// الموقف المعاد حسابه من القاعدة وحده. الحد الصفري/السالب مرفوض دائمًا.
        /// الفشل (اعتماد/كلمة/دور/سبب/بيانات) نتيجة منظمة + تدقيق رفض على اتصال مستقل.</summary>
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

            // ══ المعاملة الصريحة الواحدة: لقطة UPDLOCK + كل التحقق + الإدراج + تدقيق المنح ══
            // (كانت اللقطة تُقرأ بأمر autocommit منفصل — قفل UPDLOCK يسقط بنهاية
            // الأمر فيبقى فجوة غير محمية بين القراءة والإدراج، وكان الاعتماد يُحفظ
            // autocommit أولًا ثم تدقيق المنح autocommit ثانيًا — فشل الثاني يترك
            // اعتمادًا محفوظًا بلا سجل تدقيق. الآن: Serializable + معاملة واحدة —
            // قفل صف العميل يبقى حتى الالتزام، وكل شيء يُالتزام معًا أو يتراجع كل
            // شيء: لا اعتماد بلا تدقيق ولا تدقيق بلا اعتماد أبدًا.
            // تدقيق الرفض أعلاه (CreditApprovalDenied) بقي خارج المعاملة عمدًا —
            // كتابة مستقلة على اتصال مستقل تنجو من تراجع المعاملة.)
            using var transaction =
                await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // العميل موجود؟ + القيم الفعلية — تُقرأ معًا من دفتر Customers.Balance أدناه
                // (الاعتماد بلا عميل حقيقي لا معنى له: رفض عند غياب صف العميل).

                // ══ إعادة التحقق الخدمي الكامل: لا ثقة أبدًا ببيانات الواجهة/الطلب ══
                // تُقرأ القيم من دفتر Customers.Balance (المصدر المعتمد نفسه الذي يقرأه
                // إنفاذ البيع UPDLOCK) — أي فجوة بين المُقدَّم والفعلي (تزوير/قِدم:
                // تغيّر الرصيد أو الحد بين رفض البيع والاعتماد) = رفض صريح يقتضي
                // تقييم بيع جديد. يُخزَّن الخادم القيم المعاد حسابها وحدها لا المُقدَّمة.
                // مع UPDLOCK على صف العميل داخل المعاملة: لا سباق مع سداد/تعديل حد متوازٍ.
                CustomerCreditSnapshot? snap = await _dbContext.Database.SqlQuery<CustomerCreditSnapshot>(@$"
                        SELECT ISNULL(Balance, 0) AS [Balance], ISNULL(CreditLimit, 0) AS [CreditLimit]
                        FROM Customers WITH (UPDLOCK)
                        WHERE PartyId = {info.CustomerId}")
                    .FirstOrDefaultAsync();

                if (snap == null)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("العميل غير موجود.");
                }

                decimal dbBalance = snap.Balance;
                decimal dbLimit = snap.CreditLimit;

                // الحد الصفري: منع مطلق لا يعتمده مدير إطلاقًا (قيد أعمال صارم)
                if (dbLimit == 0)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail(
                        "الشراء بالدين ممنوع لهذا العميل: حد الائتمان صفر — لا يمكن اعتماد هذا المنع. " +
                        "لتتمكين الائتمان، عدّل حد الائتمان الدائم للعميل إلى قيمة موجبة أولًا.");
                }
                // الحد السالب: بيانات فاسدة — رفض تحقق صريح لا يُعامل كصفر
                if (dbLimit < 0)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("حد ائتمان العميل قيمة غير صالحة (سالب) — صحّح بيانات العميل أولًا.");
                }

                // مطابقة لقطة الموقف المُقدَّمة مع الوضع الفعلي: أي انحراف = معلومات
                // مزوّرة أو قديمة (تغيّر الرصيد/الحد بين رفض البيع والاعتماد) — الاعتماد
                // مرفوض ويُعاد تقييم البيع من جديد (نتيجة بيع جديدة ثم اعتماد جديد).
                if (info.CurrentOutstanding != dbBalance)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail(
                        $"معلومات الاعتماد لم تعد مطابقة للوضع الفعلي: الرصيد الحالي بالدفتر {dbBalance:N2} " +
                        $"لا {info.CurrentOutstanding:N2} — أعد تقييم البيع ثم اطلب اعتمادًا جديدًا.");
                }
                if (info.CreditLimit != dbLimit)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail(
                        $"معلومات الاعتماد لم تعد مطابقة للوضع الفعلي: حد العميل بالدفتر {dbLimit:N2} " +
                        $"لا {info.CreditLimit:N2} — أعد تقييم البيع ثم اطلب اعتمادًا جديدًا.");
                }

                // مبلغ جديد موجب + تجاوز فعلي للحد الموجب: لا بيع ضمن الحد من مسار الاعتماد
                if (info.NewCreditAmount <= 0)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail("مبلغ ائتمان غير صالح للاعتماد.");
                }
                decimal projected = dbBalance + info.NewCreditAmount;
                if (projected <= dbLimit)
                {
                    await transaction.RollbackAsync();
                    return Result<int>.Fail(
                        $"هذه العملية لا تتجاوز حد الائتمان (المشروع {projected:N2} ≤ الحد {dbLimit:N2}) — " +
                        "لا اعتماد مطلوب: أعد تقييم البيع.");
                }

                var approval = new CreditApproval
                {
                    CustomerId = info.CustomerId,
                    ManagerUserId = login.Data.UserId,
                    CurrentOutstanding = dbBalance,
                    CreditLimit = dbLimit,
                    NewCreditAmount = info.NewCreditAmount,
                    ProjectedOutstanding = projected,
                    ExcessAmount = projected - dbLimit,
                    Reason = reason,
                    Status = "granted",
                    ApprovedAtUtc = Common.Utils.ProgramClock.UtcNow
                };

                _dbContext.CreditApprovals.Add(approval);
                await _dbContext.SaveChangesAsync();

                // تدقيق المنح: هوية المدير + السبب + الأرقام المعاد حسابها — لا أسرار أبدًا
                // (بنفس المعاملة: الاعتماد وسجل تدقيقه يُالتزمان معًا أو يتراجعان معًا)
                clsAudit.Log(_dbContext, login.Data.UserId, "CreditApprovalGranted",
                    $"اعتماد تجاوز حد ائتمان #{approval.ApprovalId} — العميل #{info.CustomerId}: " +
                    $"الحالي {dbBalance:N2} + جديد {info.NewCreditAmount:N2} = {projected:N2} " +
                    $"(الحد {dbLimit:N2} — الفائض {approval.ExcessAmount:N2}) — السبب: {reason}");
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();

                return Result<int>.Ok(approval.ApprovalId, "تم اعتماد تجاوز حد الائتمان — أعد إتمام البيع.");
            }
            catch
            {
                // فشل تقني (قيد/اتصال/حقن اختبار): تراجع كامل — لا اعتماد بلا تدقيق
                // ولا بقايا متتبعة على السياق. الرفض التقني ليس رفضًا أعمالًا فيُعاد
                // إلقاؤه كما هو (نمط مسار البيع).
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                throw;
            }
        }

        /// <summary>لقطة رصيد/حد ائتمان العميل للقراءة الحيّة بلا تتبع (نفس نمط
        /// clsCheckoutService — UPDLOCK على صف العميل).</summary>
        private sealed class CustomerCreditSnapshot
        {
            public decimal Balance { get; set; }
            public decimal CreditLimit { get; set; }
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
