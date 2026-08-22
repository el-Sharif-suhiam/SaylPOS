using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Common.DTOs.ViewModel;
using System;
using System.Collections.Generic;
using System.Text;
using Sayl.Core.Models;
using Common.DTOs.RequestModels;
using BCrypt.Net;
using Common.Results;
namespace Sayl.Core.Services
{

    public class clsUsersService
    {
        private readonly SaylDbContext _dbContext;

        public clsUsersService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<UserVM>> GetUsersAsync()
        {
            return await _dbContext.Users
                .Select(u => new UserVM
                {
                    UserId = u.UserId,
                    UserName = u.Username,
                    RoleName = u.Role.Name,
                    Name = u.Employee.Name ?? "No Employee",
                    IsActive = u.IsActive
                })
                .ToListAsync();
        }

        public async Task<UserVM?> GetUserAsync(int userId)
            {
                return await _dbContext.Users
                    .Where(u => u.UserId == userId)
                    .Select(u => new UserVM
                    {
                        UserId = u.UserId,
                        UserName = u.Username,
                        RoleName = u.Role.Name,
                        Name = u.Employee.Name ?? "No Employee",
                        IsActive = u.IsActive
                    })
                    .SingleOrDefaultAsync();
            }

        public async Task<Result> AddNewUserAsync(NewUserRequest req)
            {
                // 1. التحقق من المدخلات الأساسية
                if (string.IsNullOrWhiteSpace(req.userName))
                    return Result.Fail("اسم المستخدم مطلوب.");

                if (string.IsNullOrWhiteSpace(req.password))
                    return Result.Fail("كلمة المرور مطلوبة.");

                if (req.RoleId <= 0)
                    return Result.Fail("الدور غير صالح.");

                // 2. منع تكرار اسم المستخدم
                bool exists = await _dbContext.Users.AnyAsync(u => u.Username == req.userName);
                if (exists)
                    return Result.Fail("اسم المستخدم موجود مسبقاً.");

                // 3. التحقق من وجود الدور
                bool roleExists = await _dbContext.Roles.AnyAsync(r => r.RoleId == req.RoleId);
                if (!roleExists)
                    return Result.Fail("الدور المحدد غير موجود.");

                // 4. التحقق من الموظف إن وُجد
                if (req.EmployeeId.HasValue)
                {
                    bool empExists = await _dbContext.Employees.AnyAsync(e => e.EmployeeId == req.EmployeeId.Value);
                    if (!empExists)
                        return Result.Fail("الموظف المرتبط غير موجود.");
                }

                // 5. إنشاء المستخدم
                var user = new User
                {
                    Username = req.userName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.password),
                    RoleId = req.RoleId,
                    EmployeeId = req.EmployeeId ?? null
                };

                _dbContext.Users.Add(user);
                bool saved = (await _dbContext.SaveChangesAsync()) > 0;

                return saved ? Result.Ok("تم إضافة المستخدم بنجاح.") : Result.Fail("فشل حفظ المستخدم.");
            }

        public async Task<Result> UpdatePasswordAsync(int userId, string newPassword)
            {
                if (string.IsNullOrWhiteSpace(newPassword))
                    return Result.Fail("كلمة المرور الجديدة غير صالحة.");

                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تغيير كلمة المرور بنجاح.");
            }

        public async Task<Result> ChangeRoleAsync(int userId, byte newRoleId)
            {
                bool roleExists = await _dbContext.Roles.AnyAsync(r => r.RoleId == newRoleId);
                if (!roleExists)
                    return Result.Fail("الدور الجديد غير موجود.");

                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.RoleId = newRoleId;
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تغيير الدور بنجاح.");
            }

        public async Task<Result> LinkEmployeeAsync(int userId, int employeeId)
            {
                bool empExists = await _dbContext.Employees.AnyAsync(e => e.EmployeeId == employeeId);
                if (!empExists)
                    return Result.Fail("الموظف غير موجود.");

                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.EmployeeId = employeeId;
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم ربط الموظف بالمستخدم.");
            }

        public async Task<Result> DeactivateUserAsync(int userId)
            {
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.IsActive = false;
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تعطيل المستخدم.");
            }

        public async Task<Result> ActivateUserAsync(int userId)
            {
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.IsActive = true;
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تفعيل المستخدم.");
            }


        public async Task<Result<UserVM>> LoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return Result<UserVM>.Fail("اسم المستخدم وكلمة المرور مطلوبان.");

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .Include(u => u.Employee)
                .FirstOrDefaultAsync(u => u.Username == username);

            if (user == null)
                return Result<UserVM>.Fail("اسم المستخدم غير صحيح.");

            if (!user.IsActive)
                return Result<UserVM>.Fail("هذا الحساب معطَّل. راجع المدير.");

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                return Result<UserVM>.Fail("كلمة المرور غير صحيحة.");

            // إنشاء سجل دخول
            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = user.UserId,
                Action = "Login",
                Details = $"تسجيل دخول المستخدم '{user.Username}'",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            var userVm = new UserVM
            {
                UserId = user.UserId,
                UserName = user.Username,
                RoleName = user.Role.Name,
                Name = user.Employee?.Name ?? "No Employee"
            };

            return Result<UserVM>.Ok(userVm, "تم تسجيل الدخول بنجاح.");
        }

      
        public async Task<Result> LogoutAsync(int userId)
        {
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "Logout",
                Details = $"تسجيل خروج المستخدم '{user.Username}'",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تسجيل الخروج بنجاح.");
        }

       
        // ──────────────────────────────────────────────────────
        public async Task<Result> ChangePasswordAsync(int userId, string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(oldPassword) || string.IsNullOrWhiteSpace(newPassword))
                return Result.Fail("كلمة المرور القديمة والجديدة مطلوبتان.");

            if (newPassword.Length < 6)
                return Result.Fail("كلمة المرور الجديدة يجب ألا تقل عن 6 أحرف.");

            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            if (!BCrypt.Net.BCrypt.Verify(oldPassword, user.PasswordHash))
                return Result.Fail("كلمة المرور القديمة غير صحيحة.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "ChangePassword",
                Details = $"المستخدم '{user.Username}' غيّر كلمة مروره.",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تغيير كلمة المرور بنجاح.");
        }

        // ──────────────────────────────────────────────────────
        // 4. إعادة تعيين كلمة المرور (مدير / صلاحية عليا)
        // ──────────────────────────────────────────────────────
        public async Task<Result> ResetPasswordAsync(int userId, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
                return Result.Fail("كلمة المرور الجديدة يجب ألا تقل عن 6 أحرف.");

            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "ResetPassword",
                Details = $"تم إعادة تعيين كلمة مرور المستخدم '{user.Username}' بواسطة مسؤول.",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم إعادة تعيين كلمة المرور بنجاح.");
        }

        // ──────────────────────────────────────────────────────
        // 5. الحصول على مستخدم بواسطة المعرف (مساعد)
        // ──────────────────────────────────────────────────────
        public async Task<UserVM?> GetUserByIdAsync(int userId)
        {
            return await _dbContext.Users
                .Where(u => u.UserId == userId)
                .Select(u => new UserVM
                {
                    UserId = u.UserId,
                    UserName = u.Username,
                    RoleName = u.Role.Name,
                    Name = u.Employee.Name ?? "No Employee"
                })
                .SingleOrDefaultAsync();
        }
    }
    
}
