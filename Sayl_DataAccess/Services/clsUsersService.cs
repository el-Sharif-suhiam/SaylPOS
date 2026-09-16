using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Common.DTOs;
using Common.DTOs.ViewModel;
using System;
using System.Collections.Generic;
using System.Text;
using Sayl.Core.Models;
using Common.DTOs.RequestModels;
using BCrypt.Net;
using Common.Results;
using Common.Utils;

namespace Sayl.Core.Services
{

    /// <summary>
    /// خدمة المستخدمين — نظام البتات (انظر AppPermissions):
    /// • الدخول يفحص IsActive للدور قبل أي شيء (دور معطل = رفض واضح).
    /// • حماية الأعلى: لا لمس حساب دوره يوازي أو يعلو دور الفاعل
    ///   (إنشاء/تعطيل/تغيير دور/كلمة مرور) — باستثناء نفسه.
    /// • إعادة تعيين كلمات المرور للآخرين: للمدير وحده.
    /// </summary>
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
                    RoleId = u.RoleId,
                    RoleValue = u.Role.RoleValue,
                    Name = u.Employee.Name ?? "No Employee",
                    IsActive = u.IsActive
                })
                .ToListAsync();
        }

        /// <summary>صفحة حسابات (ترقيم SQL): نفس فلاتر القائمة + عدّ كامل + Skip/Take.</summary>
        public async Task<PagedResult<UserVM>> GetUsersPagedAsync(
            bool includeInactive = false, int page = 1, int pageSize = 20)
        {
            page = Math.Max(1, page);
            pageSize = Math.Max(1, pageSize);

            var query = _dbContext.Users.AsQueryable();

            if (!includeInactive)
                query = query.Where(u => u.IsActive);

            int total = await query.CountAsync();
            var items = await query
                .OrderBy(u => u.Username)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new UserVM
                {
                    UserId = u.UserId,
                    UserName = u.Username,
                    RoleName = u.Role.Name,
                    RoleId = u.RoleId,
                    RoleValue = u.Role.RoleValue,
                    Name = u.Employee.Name ?? "No Employee",
                    IsActive = u.IsActive
                })
                .ToListAsync();

            return new PagedResult<UserVM> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
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
                        RoleId = u.RoleId,
                        RoleValue = u.Role.RoleValue,
                        Name = u.Employee.Name ?? "No Employee",
                        IsActive = u.IsActive
                    })
                    .SingleOrDefaultAsync();
            }

        // ── حارسان مشتركان ──
        // 1) حلّ دور الفاعل (مع IsActive — دور معطل = فاعل بلا سلطة إدارية)
        private async Task<int> GetActorRoleValueAsync(int userId)
        {
            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null || !user.Role.IsActive) return -1;
            return user.Role.RoleValue;
        }

        // 2) حماية الأعلى: هل يملك الفاعل سلطة على حساب مستهدف؟
        //    ممنوع إن كان دور الهدف يوازي أو يعلو دور الفاعل — باستثناء نفسه.
        private async Task<Result> EnsureCanManageAsync(int actorUserId, User target)
        {
            if (target.UserId == actorUserId)
                return Result.Ok(); // الاستثناء الشخصي: يدير حسابه (كلمة المرور مثلاً)

            int actorValue = await GetActorRoleValueAsync(actorUserId);
            if (actorValue < 0)
                return Result.Fail("تعذر التحقق من صلاحياتك — أعد تسجيل الدخول.");

            if (!AppPermissions.Outranks(actorValue, target.Role.RoleValue))
                return Result.Fail($"لا يمكنك إدارة حساب '{target.Username}' — دوره يوازي دورك أو أعلى منه. راجع مدير النظام.");

            return Result.Ok();
        }

        public async Task<Result> AddNewUserAsync(NewUserRequest req, int? actorUserId = null)
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

            // بوابة صلاحية: إنشاء الحسابات يتطلب شاشة المستخدمين
            if (actorUserId.HasValue)
            {
                int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
                if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
                    return Result.Fail("إنشاء المستخدمين يتطلب صلاحية شاشة المستخدمين.");
            }
            else
            {
                return Result.Fail("إنشاء المستخدم يتطلب تحديد المستخدم الفاعل.");
            }

            // 2. منع تكرار اسم المستخدم
            bool exists = await _dbContext.Users.AnyAsync(u => u.Username == req.userName);
            if (exists)
                return Result.Fail("اسم المستخدم موجود مسبقاً.");

            // 3. الدور: موجود، نشط، ودون سقف الفاعل الهرمي
            var role = await _dbContext.Roles.FindAsync(req.RoleId);
            if (role == null)
                return Result.Fail("الدور المحدد غير موجود.");
            if (!role.IsActive)
                return Result.Fail("الدور المحدد معطل — فعّله من شاشة الأدوار أولًا.");

            int actorVal = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorVal >= 0 && !AppPermissions.Outranks(actorVal, role.RoleValue) && role.RoleValue != AppPermissions.CashierRoleValue)
                return Result.Fail("لا يمكنك إنشاء حساب بدور يوازي دورك أو أعلى منه.");

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
            clsAudit.Log(_dbContext, actorUserId.Value, "AddUser",
                $"إضافة المستخدم '{user.Username}' بدور '{role.Name}'");
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved ? Result.Ok("تم إضافة المستخدم بنجاح.") : Result.Fail("فشل حفظ المستخدم.");
        }

        /// <summary>هل يوجد أي مستخدم نشط على الإطلاق؟ — بوابة إنشاء حساب المدير الأول.</summary>
        public async Task<bool> HasActiveUsersAsync()
            => await _dbContext.Users.AnyAsync(u => u.IsActive);

        /// <summary>VULN-47: إنشاء «حساب المدير» في أول تركيب — الاستبدال الآمن
        /// للحساب الديمو (admin/Admin@123) الذي كان يُزرَع بكلمة مرور معروفة في كل
        /// تثبيت جديد. القاعدة: أول حساب نشط في النظام هو مالكه — ومنذ وجود أي
        /// مستخدم نشط يُغلق هذا المسار إلى الأبد (لا يعاد تشغيله فوق بيانات حية).</summary>
        public async Task<Result<UserVM>> CreateOwnerAsync(string username, string password)
        {
            if (await HasActiveUsersAsync())
                return Result<UserVM>.Fail("إنشاء حساب المدير غير متاح — النظام يحتوي مستخدمين بالفعل.");

            username = username?.Trim() ?? "";
            if (username.Length < 3 || username.Length > 50)
                return Result<UserVM>.Fail("اسم المستخدم يجب أن يكون بين 3 و50 حرفًا.");

            if (string.IsNullOrWhiteSpace(password))
                return Result<UserVM>.Fail("كلمة المرور مطلوبة.");

            string? policyError = ValidatePasswordPolicy(password);
            if (policyError != null)
                return Result<UserVM>.Fail(policyError);

            if (await _dbContext.Users.AnyAsync(u => u.Username == username))
                return Result<UserVM>.Fail("اسم المستخدم موجود مسبقًا.");

            var adminRole = await _dbContext.Roles
                .FirstOrDefaultAsync(r => r.RoleValue == AppPermissions.AdminRoleValue && r.IsActive);
            if (adminRole == null)
                return Result<UserVM>.Fail("دور المدير غير متوفر — لا يمكن إنشاء حساب المدير.");

            var owner = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleId = adminRole.RoleId,
                EmployeeId = null
            };

            _dbContext.Users.Add(owner);
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Result<UserVM>.Fail("اسم المستخدم موجود مسبقًا.");
            }

            clsAudit.Log(_dbContext, owner.UserId, "OwnerSetup",
                $"إنشاء حساب المدير الأول '{owner.Username}'");
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                // فشل سجل التدقيق لا يمنع إنشاء المالك
            }

            return Result<UserVM>.Ok(new UserVM
            {
                UserId = owner.UserId,
                UserName = owner.Username,
                RoleId = adminRole.RoleId,
                RoleName = adminRole.Name,
                RoleValue = adminRole.RoleValue,
                IsActive = true
            });
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

        // تغيير دور مستخدم — بوابة مزدوجة: شاشة المستخدمين + حماية الأعلى.
        // (تغيير الدور = تسكين سلطة — أخطر عمليات الحسابات.)
        public async Task<Result> ChangeRoleAsync(int userId, byte newRoleId, int? actorUserId = null)
            {
                if (!actorUserId.HasValue)
                    return Result.Fail("تغيير الدور يتطلب تحديد المستخدم الفاعل.");

                int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
                if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
                    return Result.Fail("تغيير أدوار المستخدمين يتطلب صلاحية شاشة المستخدمين.");

                var user = await _dbContext.Users
                    .Include(u => u.Role)
                    .FirstOrDefaultAsync(u => u.UserId == userId);
                if (user == null)
                    return Result.Fail("المستخدم غير موجود.");

                // حماية الأعلى: لا لمس حساب يوازي الفاعل أو يعلوه
                var guard = await EnsureCanManageAsync(actorUserId.Value, user);
                if (!guard.IsSuccess)
                    return guard;

                var newRole = await _dbContext.Roles.FindAsync(newRoleId);
                if (newRole == null)
                    return Result.Fail("الدور الجديد غير موجود.");
                if (!newRole.IsActive)
                    return Result.Fail("الدور الجديد معطل.");

                // ولا رفعًا فوق سقف الفاعل: دور جديد يوازي الفاعل أو أعلى = تسكين سلطة ممنوع
                if (!AppPermissions.Outranks(actorValue, newRole.RoleValue) && newRole.RoleValue != AppPermissions.CashierRoleValue)
                    return Result.Fail("لا يمكن إسناد دور يوازي دورك أو أعلى منه.");

                if (user.RoleId == newRoleId)
                    return Result.Fail("المستخدم يحمل هذا الدور بالفعل.");

                byte oldRoleId = user.RoleId;
                string oldRoleName = user.Role.Name;
                user.RoleId = newRoleId;

                clsAudit.Log(_dbContext, actorUserId.Value, "ChangeRole",
                    $"تغيير دور '{user.Username}' من '{oldRoleName}' إلى '{newRole.Name}'");
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تغيير الدور بنجاح.");
            }

        public async Task<Result> LinkEmployeeAsync(int userId, int employeeId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("ربط الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
                return Result.Fail("ربط الحسابات بالمستخدمين يتطلب صلاحية شاشة المستخدمين.");

            bool empExists = await _dbContext.Employees.AnyAsync(e => e.EmployeeId == employeeId);
            if (!empExists)
                return Result.Fail("الموظف غير موجود.");

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            var guard = await EnsureCanManageAsync(actorUserId.Value, user);
            if (!guard.IsSuccess)
                return guard;

            user.EmployeeId = employeeId;
            clsAudit.Log(_dbContext, actorUserId.Value, "LinkEmployee",
                $"ربط الموظف رقم {employeeId} بالحساب '{user.Username}'");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم ربط الموظف بالمستخدم.");
        }

        public async Task<Result> DeactivateUserAsync(int userId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("تعطيل الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
                return Result.Fail("تعطيل الحسابات يتطلب صلاحية شاشة المستخدمين.");

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            if (user.UserId == actorUserId)
                return Result.Fail("لا يمكنك تعطيل حسابك الحالي.");

            var guard = await EnsureCanManageAsync(actorUserId.Value, user);
            if (!guard.IsSuccess)
                return guard;

            user.IsActive = false;
            clsAudit.Log(_dbContext, actorUserId.Value, "DeactivateUser", $"تعطيل المستخدم '{user.Username}'");
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل المستخدم.");
        }

        public async Task<Result> ActivateUserAsync(int userId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("تفعيل الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
                return Result.Fail("تفعيل الحسابات يتطلب صلاحية شاشة المستخدمين.");

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            var guard = await EnsureCanManageAsync(actorUserId.Value, user);
            if (!guard.IsSuccess)
                return guard;

            user.IsActive = true;
            clsAudit.Log(_dbContext, actorUserId.Value, "ActivateUser", $"تفعيل المستخدم '{user.Username}'");
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
                if (_failedLogins.TryGetValue(username, out var state) && state.LockUntil > DateTime.UtcNow)
                {
                    int remaining = (int)Math.Ceiling((state.LockUntil - DateTime.UtcNow).TotalMinutes);
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

            // بوابة الدور المعطل (Fail-Closed): كلمة صحيحة لكن دورك معطل = رفض
            // واضح لا رسالة دخول عامة — صاحب الحساب يعرف السبب والمدير يملك الحل.
            if (!user.Role.IsActive)
                return Result<UserVM>.Fail($"دورك '{user.Role.Name}' معطل حاليًا. راجع مدير النظام.");

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
                    CreatedAt = DateTime.UtcNow
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
                RoleId = user.RoleId,
                RoleValue = user.Role.RoleValue,
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
                var lockUntil = count >= MaxLoginAttempts ? DateTime.UtcNow.AddMinutes(LockMinutes) : DateTime.MinValue;
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
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تسجيل الخروج بنجاح.");
        }


        // ──────────────────────────────────────────────────────
        // تغيير كلمة المرور الذاتي (من شاشة الدخول أو أي مكان):
        // يتطلب كلمة المرور القديمة — الفرد يدير سرّه بنفسه.
        public async Task<Result> ChangePasswordAsync(string username, string oldPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(oldPassword) || string.IsNullOrWhiteSpace(newPassword))
                return Result.Fail("كلمة المرور القديمة والجديدة مطلوبتان.");

            if (oldPassword == newPassword)
                return Result.Fail("كلمة المرور الجديدة يجب أن تختلف عن القديمة.");

            string? policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null)
                return Result.Fail(policyError);

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Username == username);
            if (user == null || !user.IsActive)
                return Result.Fail("اسم المستخدم أو كلمة المرور غير صحيحة.");

            if (!BCrypt.Net.BCrypt.Verify(oldPassword, user.PasswordHash))
            {
                RegisterFailedLogin(username);
                return Result.Fail("كلمة المرور القديمة غير صحيحة.");
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = user.UserId,
                Action = "ChangePassword",
                Details = $"المستخدم '{user.Username}' غيّر كلمة مروره.",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم تغيير كلمة المرور بنجاح. سجّل الدخول بالكلمة الجديدة.");
        }

        // توافق مع النداءات الداخلية القديمة (بالمعرف)
        public async Task<Result> ChangePasswordAsync(int userId, string oldPassword, string newPassword)
        {
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");
            return await ChangePasswordAsync(user.Username, oldPassword, newPassword);
        }

        // ──────────────────────────────────────────────────────
        // إعادة تعيين كلمة المرور (للآخرين) — المدير وحده
        // (المشرف يملك شاشة المستخدمين لكن بلا أسرار الناس: سياستنا)
        // ──────────────────────────────────────────────────────
        public async Task<Result> ResetPasswordAsync(int userId, string newPassword, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("إعادة تعيين كلمات المرور يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.IsAdmin(actorValue))
                return Result.Fail("إعادة تعيين كلمات المرور للآخرين لمدير النظام وحده.");

            string? policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null)
                return Result.Fail(policyError);

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = actorUserId.Value,
                Action = "ResetPassword",
                Details = $"مدير النظام أعاد تعيين كلمة مرور المستخدم '{user.Username}'.",
                CreatedAt = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            return Result.Ok("تم إعادة تعيين كلمة المرور بنجاح.");
        }

        // ──────────────────────────────────────────────────────
        // الحصول على مستخدم بواسطة المعرف (مساعد)
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
                    RoleId = u.RoleId,
                    RoleValue = u.Role.RoleValue,
                    Name = u.Employee.Name ?? "No Employee"
                })
                .SingleOrDefaultAsync();
        }
    }

}
