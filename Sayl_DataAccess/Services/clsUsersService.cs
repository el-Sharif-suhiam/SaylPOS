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

        // قفل الحساب بعد 5 محاولات فاشلة لمدة 15 دقيقة (في الذاكرة — كافٍ لتشغيل أوفلاين على جهاز واحد)
        private static readonly Dictionary<string, (int Count, DateTime LockUntil)> _failedLogins = new();
        private const int MaxLoginAttempts = 5;
        private const int LockMinutes = 15;

        public clsUsersService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>سياسة كلمة المرور: 8 أحرف على الأقل وتحتوي حروفًا وأرقامًا.</summary>
        public static string? ValidatePasswordPolicy(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
                return "كلمة المرور يجب أن تكون 8 أحرف على الأقل.";
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
                return "كلمة المرور يجب أن تحتوي على حروف وأرقام.";
            return null;
        }

        public async Task<List<UserVM>> GetUsersAsync(bool includeInactive = false)
        {
            var query = _dbContext.Users.AsQueryable();

            // الافتراضي: الحسابات النشطة فقط — soft-delete لديهم هو IsActive
            if (!includeInactive)
                query = query.Where(u => u.IsActive);

            return await query
                .OrderBy(u => u.Username)
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

                string? policyError = ValidatePasswordPolicy(req.password);
                if (policyError != null)
                    return Result.Fail(policyError);

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

        // VULN-43: كان بلا سياسة كلمة مرور إطلاقًا (يقبل "a") — كود ميت لكنه
        // لغم مفخخ لأول من يوصله (مسار "فرض تغيير" مستقبلي مثلًا). الآن
        // يخضع لنفس سياسة الإضافة/الإعادة/التغيير — يظل بلا مستدعٍ لكنه آمن.
        public async Task<Result> UpdatePasswordAsync(int userId, string newPassword)
            {
                if (string.IsNullOrWhiteSpace(newPassword))
                    return Result.Fail("كلمة المرور الجديدة غير صالحة.");

                string? policyError = ValidatePasswordPolicy(newPassword);
                if (policyError != null)
                    return Result.Fail(policyError);

                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تغيير كلمة المرور بنجاح.");
            }

        // VULN-26: تغيير الأدوار كان بلا حارس خدمي — الشاشة الأدمنية وحدها تحجب.
        // إسناد صلاحية 100 لمستخدم = ترقية ذاتية محتملة من أي عميل خدمة.
        public async Task<Result> ChangeRoleAsync(int userId, byte newRoleId, int? actorUserId = null)
            {
                // بوابة الأدمن: من يغيّر الأدوار يجب أن يكون أدمن فأعلى
                if (actorUserId.HasValue)
                {
                    var actor = await _dbContext.Users.Include(u => u.Role)
                        .FirstOrDefaultAsync(u => u.UserId == actorUserId.Value);
                    if (actor == null || !clsPermissions.HasAtLeast(actor.Role.RoleValue, clsPermissions.Admin))
                        return Result.Fail("تغيير أدوار المستخدمين يتطلب صلاحية مدير.");
                }
                else
                {
                    // بلا فاعل = استدعاء قديم غير موثوق — نرفض (الواجهة تمرره دائمًا الآن)
                    return Result.Fail("تغيير الدور يتطلب تحديد المستخدم الفاعل.");
                }

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

            // قفل الحساب بعد المحاولات الفاشلة المتكررة
            lock (_failedLogins)
            {
                if (_failedLogins.TryGetValue(username, out var state) && state.LockUntil > DateTime.Now)
                {
                    int remaining = (int)Math.Ceiling((state.LockUntil - DateTime.Now).TotalMinutes);
                    return Result<UserVM>.Fail($"الحساب مقفل مؤقتًا بسبب المحاولات الفاشلة. حاول بعد {remaining} دقيقة.");
                }
            }

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .Include(u => u.Employee)
                .FirstOrDefaultAsync(u => u.Username == username);

            // VULN-25: رسالة موحدة + dummy-hash — ثلاث رسائل مميزة كانت تكشف
            // وجود الاسم/تعطيله، والمستخدم الغائب كان يرجع بلا أي عمل BCrypt
            // = قناة توقيت تؤكد وجود الأسماء. الآن: مسار متطابق زمنيًا ورسالة واحدة.
            const string invalidCredentials = "اسم المستخدم أو كلمة المرور غير صحيحة.";
            const string dummyHash = "$2a$11$abcdefghijklmnopqrstuvABCDEFGHIJKLMNOPQRSTUVWXYZ012345678901234567890123456789012"; // تجزئة عديمة الفائدة — تعويض التوقيت فقط

            if (user == null)
            {
                _ = BCrypt.Net.BCrypt.Verify(password, dummyHash); // يعمل بنفس زمن الحقيقي
                return Result<UserVM>.Fail(invalidCredentials);
            }

            if (!user.IsActive)
            {
                _ = BCrypt.Net.BCrypt.Verify(password, dummyHash);
                return Result<UserVM>.Fail(invalidCredentials);
            }

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                RegisterFailedLogin(username);
                return Result<UserVM>.Fail(invalidCredentials);
            }

            lock (_failedLogins)
            {
                _failedLogins.Remove(username);
            }

            // VULN-29: سجل الدخول best-effort — كان فشل كتابة السجل يرفض
            // دخولًا صحيح البصمات (امتلاء سجل = كل الموظفين خارج الخدمة).
            try
            {
                _dbContext.SystemLogs.Add(new SystemLog
                {
                    UserId = user.UserId,
                    Action = "Login",
                    Details = $"تسجيل دخول المستخدم '{user.Username}'",
                    CreatedAt = DateTime.Now
                });
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                // التدقيق تحسين — لا يمنع الدخول الصحيح
            }

            var userVm = new UserVM
            {
                UserId = user.UserId,
                UserName = user.Username,
                RoleName = user.Role.Name,
                Name = user.Employee?.Name ?? "No Employee"
            };

            return Result<UserVM>.Ok(userVm, "تم تسجيل الدخول بنجاح.");
        }

        private static void RegisterFailedLogin(string username)
        {
            lock (_failedLogins)
            {
                var (count, _) = _failedLogins.TryGetValue(username, out var s) ? s : (0, DateTime.MinValue);
                count++;
                var lockUntil = count >= MaxLoginAttempts ? DateTime.Now.AddMinutes(LockMinutes) : DateTime.MinValue;
                _failedLogins[username] = (count >= MaxLoginAttempts ? 0 : count, lockUntil);
            }
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
                CreatedAt = DateTime.Now
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

            string? policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null)
                return Result.Fail(policyError);

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "ChangePassword",
                Details = $"المستخدم '{user.Username}' غيّر كلمة مروره.",
                CreatedAt = DateTime.Now
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تغيير كلمة المرور بنجاح.");
        }

        // ──────────────────────────────────────────────────────
        // 4. إعادة تعيين كلمة المرور (مدير / صلاحية عليا)
        // ──────────────────────────────────────────────────────
        public async Task<Result> ResetPasswordAsync(int userId, string newPassword)
        {
            string? policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null)
                return Result.Fail(policyError);

            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "ResetPassword",
                Details = $"تم إعادة تعيين كلمة مرور المستخدم '{user.Username}' بواسطة مسؤول.",
                CreatedAt = DateTime.Now
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
