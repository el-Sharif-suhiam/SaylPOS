using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// مساعدات الصلاحيات الخدمية — نظام البتات (عقد موثق في AppPermissions).
    /// فحص الشاشة: Has(roleValue, AppPermissions.Xxx) — المسؤولية على الخدمة،
    /// والواجهة تخفي فقط (إخفاء الزر تجميل؛ البوابة هنا).
    /// </summary>
    public static class clsPermissions
    {
        /// <summary>أقصى نسبة خصم يحق للكاشير دون صلاحية محاسب.</summary>
        public const decimal MaxCashierDiscountPercent = 10m;

        /// <summary>
        /// قيمة دور المستخدم للفحص الخدمي: مستخدم نشط بدور نشط وإلا -1.
        /// (-1 لا يملك أي بت ولا هوية كاشير → يُرفض في كل فحص صلاحيات.)
        /// </summary>
        public static async Task<int> GetUserRoleValueAsync(SaylDbContext db, int userId)
        {
            if (userId <= 0)
                return -1;

            return await db.Users
                .Where(u => u.UserId == userId && u.IsActive && u.Role.IsActive)
                .Select(u => (int?)u.Role.RoleValue)
                .FirstOrDefaultAsync() ?? -1;
        }

        // ─────────────────────────────────────────────────
        // VULN-05 + التحقق الجلسي الموحد (مدفوعات ومرتجعات): عملية تُوجَّه
        // لدرج جلسة يجب أن تُقيَّم وجودًا وفتحًا وملكية قبل قيدها:
        //   • الجلسة غير موجودة → رفض.
        //   • الجلسة مغلقة → رفض (لا نقد يدخل أو يخرج من درج مغلق).
        //   • جلسة مستخدم آخر → رفض إلا لمدير النظام (صلاحية إدارية صريحة).
        // null = عملية بلا توجيه درج (توافق/خلفية) — تمر بلا قيد جلسي،
        //        والصلاحيات تُفحص دائمًا بشكل مستقل عن الجلسة (لا تجاوز بـ null).
        // تُستدعى داخل معاملة Serializable لمنع سباق مع إغلاق الجلسة.
        // ─────────────────────────────────────────────────
        public static async Task<Common.Results.Result> ValidateDrawerSessionAsync(
            SaylDbContext db, int? sessionId, int userId)
        {
            if (!sessionId.HasValue)
                return Common.Results.Result.Ok();

            var session = await db.PosSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == sessionId.Value);

            if (session == null)
            {
                await clsAudit.LogRejectedAsync(db, userId, "DrawerSessionDenied",
                    $"عملية مرفوضة: جلسة الدرج #{sessionId} غير موجودة.");
                return Common.Results.Result.Fail("جلسة الدرج غير موجودة.");
            }

            if (session.Status != enPosSessionStatusType.open)
            {
                await clsAudit.LogRejectedAsync(db, userId, "DrawerSessionDenied",
                    $"عملية مرفوضة: جلسة الدرج #{sessionId} مغلقة (الحالة {session.Status}).");
                return Common.Results.Result.Fail("جلسة الدرج مغلقة — افتح جلسة جديدة قبل تسجيل العملية.");
            }

            if (session.UserId != userId)
            {
                int actorRole = await GetUserRoleValueAsync(db, userId);
                if (!AppPermissions.IsAdmin(actorRole))
                {
                    await clsAudit.LogRejectedAsync(db, userId, "DrawerSessionDenied",
                        $"عملية مرفوضة: توجيه عملية لجلسة درج #{sessionId} لمستخدم آخر (مالكها #{session.UserId}) — تتطلب صلاحية مدير النظام.");
                    return Common.Results.Result.Fail("لا يمكنك تسجيل عملية في جلسة درج مستخدم آخر — يتطلب ذلك صلاحية مدير النظام.");
                }
            }

            return Common.Results.Result.Ok();
        }
    }

    /// <summary>مساعد تدقيق: يكتب في SystemLogs سجل كل عملية حساسة.</summary>
    public static class clsAudit
    {
        public static void Log(SaylDbContext db, int userId, string action, string details)
        {
            db.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = action,
                Details = details,
                CreatedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// تدقيق المحاولات المرفوضة (Audit Log — denied attempts): محاولة دخول
        /// فاشلة، رفض صلاحية، فشل تحقق أعمال (دفع زائد/مخزون غير كافٍ/سنة مغلقة)…
        /// تُكتب على اتصال مستقل (سياق جديد + SaveChanges فوري) حتى ينجو السجل
        /// من تراجع معاملة العملية المرفوضة — الكتابة عبر سياق العملية نفسها
        /// تُمسح بالـrollback معها فيضل الرفض بلا أثر (الثغرة المرصودة).
        ///
        /// • userId قابل للـ null: محاولة دخول باسم مستخدم غير معروف ليس لها
        ///   فاعل (Upgrade_14 جعل SystemLogs.UserId NULL مسموحًا).
        /// • أفضل جهد: فشل كتابة التدقيق لا يُلقي ولا يُحوّل الرفض إلى نجاح —
        ///   الرفض يبقى رفضًا بلا سجل على أسوأ حال.
        /// • لا تُكتب كلمات مرور أو أسرار — المتصل يمرر سبب الرفض وحده.
        /// </summary>
        public static async Task LogRejectedAsync(SaylDbContext db, int? userId, string action, string details)
        {
            if (db == null)
                return;

            try
            {
                string? connectionString = db.Database.GetConnectionString();
                if (string.IsNullOrEmpty(connectionString))
                    return; // سياق بلا اتصال معروف — لا شيء يُكتب

                var options = new DbContextOptionsBuilder<SaylDbContext>()
                    .UseSqlServer(connectionString)
                    .Options;

                await using var auditDb = new SaylDbContext(options);
                auditDb.SystemLogs.Add(new SystemLog
                {
                    UserId = userId,
                    Action = action,
                    Details = details,
                    CreatedAt = DateTime.UtcNow
                });
                // حفظ فوري على الاتصال المستقل — لا ينتظر التزام معاملة العملية
                await auditDb.SaveChangesAsync();
            }
            catch
            {
                // أفضل جهد — التدقيق لا يكسر رفض العملية أبدًا
            }
        }
    }
}
