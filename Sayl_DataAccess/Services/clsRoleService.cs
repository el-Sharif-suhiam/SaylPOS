using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    public class clsRoleService
    {
        private readonly SaylDbContext _dbContext;

        public clsRoleService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<DtoRole>> GetRolesListAsync(string search = "")
        {
            var query = _dbContext.Roles.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(r => r.Name.Contains(search));

            return await query
                .OrderBy(r => r.RoleId)
                .Select(r => new DtoRole
                {
                    RoleId = r.RoleId,
                    Name = r.Name,
                    RoleValue = r.RoleValue
                })
                .ToListAsync();
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
                RoleValue = role.RoleValue
            });
        }

        public async Task<Result<byte>> AddNewRoleAsync(DtoRole dtoRole)
        {
            if (string.IsNullOrWhiteSpace(dtoRole.Name))
                return Result<byte>.Fail("اسم الدور مطلوب.");

            if (dtoRole.RoleValue < 0)
                return Result<byte>.Fail("قيمة الدور يجب أن تكون صفرا أو أكثر.");

            bool nameExists = await _dbContext.Roles.AnyAsync(r => r.Name == dtoRole.Name);
            if (nameExists)
                return Result<byte>.Fail("اسم الدور موجود مسبقا.");

            var role = new Role
            {
                Name = clsValidation.ValidateString(dtoRole.Name, "Name", 100),
                RoleValue = dtoRole.RoleValue
            };

            _dbContext.Roles.Add(role);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved
                ? Result<byte>.Ok(role.RoleId, "تمت إضافة الدور بنجاح.")
                : Result<byte>.Fail("فشلت إضافة الدور.");
        }

        public async Task<Result> UpdateRoleAsync(DtoRole dtoRole)
        {
            if (dtoRole.RoleId <= 0)
                return Result.Fail("معرف الدور غير صالح.");

            var role = await _dbContext.Roles.FindAsync(dtoRole.RoleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

            if (!string.IsNullOrWhiteSpace(dtoRole.Name))
            {
                bool nameExists = await _dbContext.Roles
                    .AnyAsync(r => r.Name == dtoRole.Name && r.RoleId != dtoRole.RoleId);
                if (nameExists)
                    return Result.Fail("اسم الدور موجود مسبقا.");
                role.Name = clsValidation.ValidateString(dtoRole.Name, "Name", 100);
            }

            if (dtoRole.RoleValue > 0)
                role.RoleValue = dtoRole.RoleValue;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث الدور بنجاح.");
        }

        public async Task<Result> DeleteRoleAsync(byte roleId, int actorUserId)
        {
            var actor = await _dbContext.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == actorUserId);
            if (actor == null || !clsPermissions.HasAtLeast(actor.Role.RoleValue, clsPermissions.Admin))
                return Result.Fail("حذف الأدوار يتطلب صلاحية مدير النظام.");

            var role = await _dbContext.Roles.FindAsync(roleId);
            if (role == null)
                return Result.Fail("الدور غير موجود.");

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
