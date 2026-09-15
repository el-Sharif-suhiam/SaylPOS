using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// خدمة الأدوار — نظام البتات الجديد (انظر AppPermissions):
    /// • الأدوار الثابتة (مدير/كاشير/محاسب) محمية من التعديل والتعطيل نهائيًا.
    /// • أي دور مخصص سقفه 3071 — قيمة بت الأدوار (1024) تُرفض هنا أيضًا
    ///   لا في الواجهة فقط: علوّ المدير مضمون بالبناء والخدمة معًا.
    /// • التغييرات كلها تُوثَّق في SystemLogs (مَن غيّر أي دور ومتى).
    /// </summary>
    public class clsRoleService
    {
        private readonly SaylDbContext _dbContext;

        public clsRoleService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>قائمة الأدوار — الافتراضي: النشطة فقط (IsActive = الحذف الناعم للأدوار).</summary>
        public async Task<List<DtoRole>> GetRolesListAsync(string search = "", bool includeInactive = false)
        {
            var query = _dbContext.Roles.AsQueryable();

            if (!includeInactive)
                query = query.Where(r => r.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(r => r.Name.Contains(search));

            return await query
                .OrderBy(r => r.RoleId)
                .Select(r => new DtoRole
                {
                    RoleId = r.RoleId,
                    Name = r.Name,
                    RoleValue = r.RoleValue,
                    IsActive = r.IsActive
                })
                .ToListAsync();
        }

        /// <summary>صفحة أدوار (ترقيم SQL): نفس فلاتر القائمة + عدّ كامل + Skip/Take.</summary>
        public async Task<PagedResult<DtoRole>> GetRolesPagedAsync(
            string search = "", bool includeInactive = false, int page = 1, int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

            var query = _dbContext.Roles.AsQueryable();

            if (!includeInactive)
                query = query.Where(r => r.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(r => r.Name.Contains(search));

            int total = await query.CountAsync();
            var items = await query
                .OrderBy(r => r.RoleId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new DtoRole
                {
                    RoleId = r.RoleId,
                    Name = r.Name,
                    RoleValue = r.RoleValue,
                    IsActive = r.IsActive
                })
                .ToListAsync();

            return new PagedResult<DtoRole> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
        }

        public async Task<Result<DtoRole>> GetRoleByIdAsync(byte roleId)
        {
            var role = await _dbContext.Roles.FindAsync(roleId);
            if (role == null)
                return Result<DtoRole>.Fail("الدور غير موجود.");

            return Result<DtoRole>.Ok(new DtoRole
            {
                RoleId = role.RoleId,
                Name = role.Name,
                RoleValue = role.RoleValue,
                IsActive = role.IsActive
            });
        }

        // ── الحارس الهرمي: يحلّ فاعل العملية وقيمته ──
        private async Task<Role?> GetActorRoleAsync(int actorUserId)
        {
            var actor = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == actorUserId);
            return actor?.Role;
        }

        /// <summary>إضافة دور جديد — المدير وحده (بت الأدوار)، والقيمة لا تتجاوز السقف 3071 ولا تكون فارغة.</summary>
        public async Task<Result<byte>> AddNewRoleAsync(DtoRole dtoRole, int actorUserId)
        {
            // بوابة بت الأدوار: من يضيف أدوارًا يجب أن يملك شاشة الأدوار (المدير)
            var actorRole = await GetActorRoleAsync(actorUserId);
            if (actorRole == null || !AppPermissions.Has(actorRole.RoleValue, AppPermissions.Roles))
                return Result<byte>.Fail("إدارة الأدوار تتطلب صلاحية مدير النظام.");

            if (string.IsNullOrWhiteSpace(dtoRole.Name))
                return Result<byte>.Fail("اسم الدور مطلوب.");

            // منع الدور الفارغ: قناع بلا أي شاشة = دخول لا معنى له (الكاشير 0 حالة هوية خاصة لا دور مخصص)
            if (dtoRole.RoleValue == 0)
                return Result<byte>.Fail("يجب اختيار شاشة واحدة على الأقل لهذا الدور.");
            if (dtoRole.RoleValue < 0 || dtoRole.RoleValue > AppPermissions.CustomRoleCeiling)
                return Result<byte>.Fail($"قيمة صلاحيات الدور يجب أن تكون بين 1 و{AppPermissions.CustomRoleCeiling}.");

            // رفض بت الأدوار (1024) في أي دور مخصص — علوّ المدير بنيوي
            if (AppPermissions.Has(dtoRole.RoleValue, AppPermissions.Roles))
                return Result<byte>.Fail("شاشة الأدوار والصلاحيات لمدير النظام وحده — لا تُضمَّ لأي دور آخر.");

            bool nameExists = await _dbContext.Roles.AnyAsync(r => r.Name == dtoRole.Name);
            if (nameExists)
                return Result<byte>.Fail("اسم الدور موجود مسبقا.");

            var role = new Role
            {
                Name = dtoRole.Name.Trim(),
                RoleValue = dtoRole.RoleValue,
                IsActive = true
            };

            _dbContext.Roles.Add(role);
            clsAudit.Log(_dbContext, actorUserId, "AddRole",
                $"إضافة الدور '{role.Name}' (صلاحيات {role.RoleValue}: {AppPermissions.Describe(role.RoleValue)})");
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved
                ? Result<byte>.Ok(role.RoleId, "تمت إضافة الدور بنجاح.")
                : Result<byte>.Fail("فشلت إضافة الدور.");
        }

        /// <summary>تعديل دور مخصص — الأدوار الثابتة محمية، والسقف 3071 مفروض، والتدقيق يسجل القيمة قبل/بعد.</summary>
        public async Task<Result> UpdateRoleAsync(DtoRole dtoRole, int actorUserId)
        {
            var actorRole = await GetActorRoleAsync(actorUserId);
            if (actorRole == null || !AppPermissions.Has(actorRole.RoleValue, AppPermissions.Roles))
                return Result.Fail("إدارة الأدوار تتطلب صلاحية مدير النظام.");

            var role = await _dbContext.Roles.FindAsync(dtoRole.RoleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

            // الدوران الأساسيان محميان نهائيًا — تعطيل دور الكاشير = نقطة البيع ميتة
            if (role.RoleValue == AppPermissions.AdminRoleValue || role.RoleValue == AppPermissions.CashierRoleValue)
                return Result.Fail("الأدوار الأساسية (مدير النظام، كاشير) لا تُعدَّل نهائيًا.");
            if (dtoRole.RoleValue == AppPermissions.AdminRoleValue || dtoRole.RoleValue == AppPermissions.CashierRoleValue)
                return Result.Fail("لا يمكن تحويل دور مخصص إلى دور أساسي.");

            if (dtoRole.RoleValue == 0)
                return Result.Fail("يجب اختيار شاشة واحدة على الأقل لهذا الدور.");
            if (dtoRole.RoleValue < 0 || dtoRole.RoleValue > AppPermissions.CustomRoleCeiling)
                return Result.Fail($"قيمة صلاحيات الدور يجب أن تكون بين 1 و{AppPermissions.CustomRoleCeiling}.");
            if (AppPermissions.Has(dtoRole.RoleValue, AppPermissions.Roles))
                return Result.Fail("شاشة الأدوار والصلاحيات لمدير النظام وحده — لا تُضمَّ لأي دور آخر.");

            if (!string.IsNullOrWhiteSpace(dtoRole.Name) && dtoRole.Name.Trim() != role.Name)
            {
                bool nameExists = await _dbContext.Roles
                    .AnyAsync(r => r.Name == dtoRole.Name && r.RoleId != dtoRole.RoleId);
                if (nameExists)
                    return Result.Fail("اسم الدور موجود مسبقا.");
                role.Name = dtoRole.Name.Trim();
            }

            int oldValue = role.RoleValue;
            role.RoleValue = dtoRole.RoleValue;

            clsAudit.Log(_dbContext, actorUserId, "UpdateRole",
                $"تعديل الدور '{role.Name}': الصلاحيات من {oldValue} ({AppPermissions.Describe(oldValue)}) إلى {role.RoleValue} ({AppPermissions.Describe(role.RoleValue)})");
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تحديث الدور بنجاح. تسري التعديلات على حامليه بعد إعادة دخولهم.");
        }

        /// <summary>تعطيل دور (حذف ناعم) — الأدوار الأساسية محمية، والدور المستخدم يحتاج تأكيد إدارة الحسابات أولاً.</summary>
        public async Task<Result> DeactivateRoleAsync(byte roleId, int actorUserId)
        {
            var actorRole = await GetActorRoleAsync(actorUserId);
            if (actorRole == null || !AppPermissions.Has(actorRole.RoleValue, AppPermissions.Roles))
                return Result.Fail("إدارة الأدوار تتطلب صلاحية مدير النظام.");

            var role = await _dbContext.Roles.FindAsync(roleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

            if (role.RoleValue == AppPermissions.AdminRoleValue ||
                role.RoleValue == AppPermissions.CashierRoleValue ||
                role.RoleValue == AppPermissions.AccountantRoleValue)
                return Result.Fail("الأدوار الأساسية (مدير النظام، كاشير، محاسب) لا تُعطَّل نهائيًا.");

            if (!role.IsActive)
                return Result.Fail("الدور معطل بالفعل.");

            int holders = await _dbContext.Users.CountAsync(u => u.RoleId == roleId && u.IsActive);
            if (holders > 0)
                return Result.Fail($"هذا الدور يستخدمه {holders} حسابًا نشطًا — أعد تعيينهم لدور آخر أولًا (شاشة المستخدمين).");

            role.IsActive = false;
            clsAudit.Log(_dbContext, actorUserId, "DeactivateRole", $"تعطيل الدور '{role.Name}' (كانت صلاحياته {role.RoleValue})");
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تعطيل الدور. يظهر عند تفعيل «عرض المعطلين» ويمكن استعادته.");
        }

        /// <summary>استعادة دور معطل.</summary>
        public async Task<Result> ActivateRoleAsync(byte roleId, int actorUserId)
        {
            var actorRole = await GetActorRoleAsync(actorUserId);
            if (actorRole == null || !AppPermissions.Has(actorRole.RoleValue, AppPermissions.Roles))
                return Result.Fail("إدارة الأدوار تتطلب صلاحية مدير النظام.");

            var role = await _dbContext.Roles.FindAsync(roleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

            if (role.IsActive)
                return Result.Fail("الدور نشط بالفعل.");

            role.IsActive = true;
            clsAudit.Log(_dbContext, actorUserId, "ActivateRole", $"تفعيل الدور '{role.Name}'");
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تفعيل الدور.");
        }

        /// <summary>
        /// حذف فعلي — أُبقي للتوافق مع أدوات الصيانة فقط: مرفوض للاستخدام العام.
        /// سياسة النظام: التعطيل الناعم (DeactivateRoleAsync) هو المسار المعتمد.
        /// </summary>
        public async Task<Result> DeleteRoleAsync(byte roleId, int actorUserId)
        {
            var actorRole = await GetActorRoleAsync(actorUserId);
            if (actorRole == null || !AppPermissions.Has(actorRole.RoleValue, AppPermissions.Roles))
                return Result.Fail("إدارة الأدوار تتطلب صلاحية مدير النظام.");

            var role = await _dbContext.Roles.FindAsync(roleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

            if (role.RoleValue == AppPermissions.AdminRoleValue ||
                role.RoleValue == AppPermissions.CashierRoleValue ||
                role.RoleValue == AppPermissions.AccountantRoleValue)
                return Result.Fail("الأدوار الأساسية لا تُحذف. استخدم التعطيل للأدوار المخصصة.");

            bool inUse = await _dbContext.Users.AnyAsync(u => u.RoleId == roleId);
            if (inUse)
                return Result.Fail("لا يمكن حذف الدور لأنه مستخدم من قبل مستخدمين.");

            _dbContext.Roles.Remove(role);
            clsAudit.Log(_dbContext, actorUserId, "DeleteRole", $"حذف الدور '{role.Name}' (قيمة {role.RoleValue})");
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved ? Result.Ok("تم حذف الدور بنجاح.") : Result.Fail("فشل حذف الدور.");
        }
    }
}
