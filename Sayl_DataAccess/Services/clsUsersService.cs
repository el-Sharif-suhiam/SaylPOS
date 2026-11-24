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
        private readonly clsStoreClockService _storeClock;

        // قفل الحساب بعد 5 محاولات فاشلة لمدة 15 دقيقة (في الذاكرة — كافٍ لتشغيل أوفلاين على جهاز واحد)
        private static readonly Dictionary<string, (int Count, DateTime LockUntil)> _failedLogins = new();
        private const int MaxLoginAttempts = 5;
        private const int LockMinutes = 15;

        public clsUsersService(SaylDbContext dbContext, clsStoreClockService? storeClock = null)
        {
            _dbContext = dbContext;
            _storeClock = storeClock ?? new clsStoreClockService(dbContext);
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
                    Name = u.Employee == null ? "No Employee" : u.Employee.Name ?? "No Employee",
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
                    Name = u.Employee == null ? "No Employee" : u.Employee.Name ?? "No Employee",
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
                        Name = u.Employee == null ? "No Employee" : u.Employee.Name ?? "No Employee",
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
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "UserManageDenied",
                    $"عملية إدارية على '{target.Username}' مرفوضة: تعذر التحقق من صلاحيات الفاعل.");
                return Result.Fail("تعذر التحقق من صلاحياتك — أعد تسجيل الدخول.");
            }

            if (!AppPermissions.Outranks(actorValue, target.Role.RoleValue))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "UserManageDenied",
                    $"عملية إدارية على '{target.Username}' (دوره {target.Role.RoleValue}) مرفوضة: دور الفاعل ({actorValue}) لا يعلوه.");
                return Result.Fail($"لا يمكنك إدارة حساب '{target.Username}' — دوره يوازي دورك أو أعلى منه. راجع مدير النظام.");
            }

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
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                        $"إنشاء حساب '{req.userName}' مرفوض: يتطلب صلاحية شاشة المستخدمين.");
                    return Result.Fail("إنشاء المستخدمين يتطلب صلاحية شاشة المستخدمين.");
                }
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

            // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            // 5. إنشاء المستخدم
            var user = new User
            {
                Username = req.userName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.password),
                RoleId = req.RoleId,
                EmployeeId = req.EmployeeId ?? null,
                IsActive = true
            };

            _dbContext.Users.Add(user);
            clsAudit.Log(_dbContext, actorUserId.Value, "AddUser",
                $"إضافة المستخدم '{user.Username}' بدور '{role.Name}'",
                nowUtc);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;

            return saved ? Result.Ok("تم إضافة المستخدم بنجاح.") : Result.Fail("فشل حفظ المستخدم.");
        }

        /// <summary>هل يوجد أي مستخدم نشط على الإطلاق؟ — بوابة إنشاء حساب المدير الأول.</summary>
        public async Task<bool> HasActiveUsersAsync()
            => await _dbContext.Users.AnyAsync(u => u.IsActive);

        /// <summary>الحساب الافتراضي: يُنشأ صامتًا عند صفر مستخدمين نشطين، ويُجبر على
        /// تغيير كلمة المرور في أول دخول (MustChangePassword=1).</summary>
        public const string DefaultAdminUsername = "admin";
        public const string DefaultAdminPassword = "Admin@123";

        /// <summary>ترقية المخطط idempotent قبل أول استعلام مستخدمين: عمود
        /// MustChangePassword (جديد) وجدول Settings (في القواعد الأقدم الذي أُضيف
        /// بعدها) — أي غياب يُسقط استعلامات EF بـInvalid object/column name خارج الـ catchات.</summary>
        public const string EnsureSchemaSql = @"
IF COL_LENGTH(N'dbo.Users', N'MustChangePassword') IS NULL
    ALTER TABLE dbo.Users ADD MustChangePassword BIT NOT NULL
        CONSTRAINT DF_Users_MustChangePassword DEFAULT (0);
/* حجب/حجر الدفعات: مخزون موجود فعليًا لكنه غير صالح للبيع (مرتجع منتهي،
   مرتجع بلا دفعة مصدر) — عمود على القواعد القائمة idempotent، والقواعد
   الجديدة تنشئه مع الجدول (SaylDB.sql). */
IF OBJECT_ID(N'dbo.Batches', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Batches', N'IsQuarantined') IS NULL
    ALTER TABLE dbo.Batches ADD IsQuarantined BIT NOT NULL
        CONSTRAINT DF_Batches_IsQuarantined DEFAULT (0);
IF OBJECT_ID(N'dbo.Settings', N'U') IS NULL
    CREATE TABLE dbo.Settings
    (
        SettingKey   NVARCHAR(100)  NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
        SettingValue NVARCHAR(MAX)  NULL,
        /* Upgrade_15: افتراضية UTC لا GETDATE — منطقة زمنية واحدة مع الكُتّاب (DateTime.UtcNow / GETUTCDATE) */
        UpdatedAt    DATETIME       NULL CONSTRAINT DF_Settings_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
/* Upgrade_10: parties_email_unique -> فهرس فريد مُرشّح. الفهرس الفريد
   العادي يقبض بعد NULL واحد فقط فيرتد أي عميل ثانٍ بلا بريد (منها
   الإضافة السريعة من POS) بـ2601 على القواعد القديمة. لا بيانات تُمس:
   البريدات الفعلية المكررة تحجب الاستبدال حتي يُنظفها المالك يدويًا.
   (Microsoft.Data.SqlClient يقف QUOTED_IDENTIFIER ON — شرط الفوهرس المرشح.) */
IF OBJECT_ID(N'dbo.Parties', N'U') IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Parties')
          AND name = N'parties_email_unique'
          AND is_unique = 1 AND has_filter = 1
    )
    AND NOT EXISTS (
        SELECT 1 FROM dbo.Parties WHERE Email IS NOT NULL
        GROUP BY Email HAVING COUNT(*) > 1
    )
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.Parties')
                 AND name = N'parties_email_unique')
        DROP INDEX parties_email_unique ON dbo.Parties;
    EXEC(N'CREATE UNIQUE INDEX parties_email_unique ON dbo.Parties(Email) WHERE Email IS NOT NULL');
END;
/* Upgrade_11: Batches.UnitCost — تكلفة الاقتناء التاريخية للوحدة (بعد تخصيص
   خصم الشراء) وهي مصدر COGS عند البيع/المرتجع. القواعد الجديدة تنشئه مع
   الجدول (SaylDB.sql)؛ القواعد القديمة تأخذ العمود هنا ثم تُرحَّل دفعاتها
   بلا تكلفة إلى تكلفة أحدث بند شراء مكتمل (ItemCost كما سُجل وقت الشراء) —
   لا قيم مُخترَعة ولا مساس بأي بيانات أخرى. EXEC ديناميكي لأن ALTER في نفس
   الدفعة لا يُرى وقت تجميعها (deferred name resolution لا يشمل الأعمدة).
   Idempotent: العمود يُنشأ إن غاب فقط، والترحيل يملأ الدفعات بلا تكلفة فقط. */
IF OBJECT_ID(N'dbo.Batches', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Batches', N'UnitCost') IS NULL
        ALTER TABLE dbo.Batches ADD UnitCost DECIMAL(10,2) NULL;
    EXEC(N'
    UPDATE b SET b.UnitCost = pi.UnitCost
    FROM dbo.Batches b
    CROSS APPLY (
        SELECT TOP (1) ii.ItemCost AS UnitCost
        FROM dbo.InvoiceItems ii
        JOIN dbo.Invoices i ON i.InvoiceId = ii.InvoiceId
        WHERE ii.BatchId = b.BatchId
          AND ii.ItemCost IS NOT NULL
          AND i.Type = N''purchase''
          AND i.Status = N''completed''
        ORDER BY i.CreatedAt DESC, ii.InvoiceItemId DESC
    ) pi
    WHERE b.UnitCost IS NULL AND pi.UnitCost IS NOT NULL;');
END;
/* Upgrade_14: SystemLogs.UserId قابل للـ NULL — تدقيق المحاولات المرفوضة.
   كان NOT NULL مع FK يمنع تسجيل محاولات الدخول الفاشلة باسم مستخدم غير
   معروف (تصيّد بيانات الدخول) إطلاقًا. تعديل nullability عمود مرتبط بـ FK
   يتطلب إسقاط القيد وإعادته — EXEC ديناميكي لأن ALTER COLUMN في نفس الدفعة
   لا يُرى وقت تجميعها. Idempotent: يعمل فقط إن كان العمود NOT NULL. */
IF OBJECT_ID(N'dbo.SystemLogs', N'U') IS NOT NULL
   AND COLUMNPROPERTY(OBJECT_ID(N'dbo.SystemLogs'), N'UserId', 'AllowsNull') = 0
BEGIN
    ALTER TABLE dbo.SystemLogs DROP CONSTRAINT FK_SystemLogs_UserId;
    EXEC(N'ALTER TABLE dbo.SystemLogs ALTER COLUMN UserId INT NULL;
           ALTER TABLE dbo.SystemLogs ADD CONSTRAINT FK_SystemLogs_UserId
               FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId);');
END;
/* VULN-21: فهرس فريد مُرشّح يمنع جلستين مفتوحتين لمستخدم واحد — الحماية على
   مستوى محرك القاعدة (سد TOCTOU في فحص-ثم-إدراج بطلبات متوازية)، وهو نفس
   تعريف SaylDB.sql تمامًا. قواعد التثبيت الجديد تنشئه مع الجدول؛ القواعد
   القديمة تأخذه هنا عند الإقلاع. Idempotent: يُنشأ إن غاب فقط وإعادة
   التشغيل لا تفعل شيئًا. إن وُجدت جلسات مفتوحة مكررة (بيانات تالفة قائمة
   قبل الترقية) فلا تُمسّ ولا تُحذف تلقائيًا — THROW يُفشل الإقلاع برسالة
   تحمل معرّفات المتعارضين ويعالجها المالك يدويًا. EXEC ديناميكي لأن CREATE
   INDEX في نفس الدفعة لا يُرى وقت تجميعها؛ جلسة SqlClient تقف
   QUOTED_IDENTIFIER ON فتقبل الفهارس المُرشّحة (نفس قاعدة parties_email_unique). */
IF OBJECT_ID(N'dbo.POSSessions', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes
       WHERE object_id = OBJECT_ID(N'dbo.POSSessions')
         AND name = N'UX_POSSessions_OneOpenPerUser'
         AND is_unique = 1 AND has_filter = 1
   )
BEGIN
    DECLARE @dupes NVARCHAR(MAX);
    SELECT @dupes = ISNULL(@dupes + N', ', N'') + CONVERT(NVARCHAR(20), UserId)
    FROM dbo.POSSessions
    WHERE Status = N'open'
    GROUP BY UserId HAVING COUNT(*) > 1;

    IF @dupes IS NOT NULL
    BEGIN
        DECLARE @msg NVARCHAR(2048) =
            N'فشل ترقية المخطط: جلسات درج مفتوحة مكررة للمستخدمين (' + @dupes
            + N') — القيد الفريد UX_POSSessions_OneOpenPerUser يمنع وجودها. لم تُمسّ أي جلسة: عالِج التكرار يدويًا (أغلق الجلسة الزائدة أو صحّح بياناتها) ثم أعد التشغيل.';
        THROW 50001, @msg, 1;
    END;

    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.POSSessions')
                 AND name = N'UX_POSSessions_OneOpenPerUser')
        DROP INDEX UX_POSSessions_OneOpenPerUser ON dbo.POSSessions;
    EXEC(N'CREATE UNIQUE INDEX UX_POSSessions_OneOpenPerUser ON dbo.POSSessions(UserId) WHERE Status = N''open''');
END;
/* Task-1: الترحيل لمرة واحدة لحد الائتمان الافتراضي (Upgrade_16).
   كان إنشاء العميل يكتب CreditLimit = 0 صراحةً (النمط القديم) وافتراضية
   العمود 0 — الصفر إذًا يعني «لم يُضبط أبدًا» لا «قرارًا صريحًا بالمنع».
   الترحيل: 0 أو NULL -> 1000 (القيمة الافتراضية المعتمدة للعملاء الجدد)؛
   الحدود غير الصفرية قيم فعلية يقررها المالك فلا تُمسّ. صف هويّة العملاء
   وفواتيرهم ومدفوعاتهم لا يتأثر — عمود واحد فقط.
   الحارس: صف Settings مفتاح CreditLimitMigrationV1 — بعد نجاح الدفعة
   لا يعاد التنفيذ أبدًا، فعميل ضبطه المالك على 0 لاحقًا (منع الآجل عليه)
   يبقى 0 في كل إقلاعات لاحقة. UPDATE + INSERT العلامة داخل معاملة واحدة
   فلا نافذة نصفة-ترحيل عند انقطاع الإقلاع. Idempotent: بالعلامة موجودة
   لا يفعل شيئًا. نفس المنطق في Upgrade_16_DefaultCreditLimit.sql. */
IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Settings', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Settings WHERE SettingKey = N'CreditLimitMigrationV1')
BEGIN
    BEGIN TRAN;
    UPDATE dbo.Customers
    SET CreditLimit = 1000
    WHERE CreditLimit IS NULL OR CreditLimit = 0;
    INSERT INTO dbo.Settings (SettingKey, SettingValue, UpdatedAt)
    VALUES (N'CreditLimitMigrationV1', N'1', SYSUTCDATETIME());
    COMMIT TRAN;
END;
/* Task-3: جدول اعتمادات المدير لتجاوز حد الائتمان (Upgrade_17).
   مسار اعتماد البيع المتجاوز للحد يحتاج سجلًا دوريًا يُستهلك مرة
   واحدة بالضبط داخل معاملة البيع (انتقال CAS granted -> consumed
   مع مطابقة الربط: نفس العميل + نفس المبلغ + نفس لقطة الرصيد/الحد).
   نفس تعريف Upgrade_17_CreditApprovals.sql وSaylDB.sql تمامًا:
   إنشاء إن غاب فقط (idempotent) + الفهرس الفريد المُرشَّح الذي يمنع
   ربط صفين بنفس الفاتورة. لا بيانات قائمة تُمسّ أبدًا. */
IF OBJECT_ID(N'dbo.CreditApprovals', N'U') IS NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Invoices', N'U') IS NOT NULL
BEGIN
    CREATE TABLE dbo.CreditApprovals
    (
        ApprovalId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_CreditApprovals PRIMARY KEY,
        CustomerId INT NOT NULL,
        ManagerUserId INT NOT NULL,
        ConsumedByInvoiceId INT NULL,
        CurrentOutstanding DECIMAL(10,2) NOT NULL,
        CreditLimit DECIMAL(10,2) NOT NULL,
        NewCreditAmount DECIMAL(10,2) NOT NULL,
        ProjectedOutstanding DECIMAL(10,2) NOT NULL,
        ExcessAmount DECIMAL(10,2) NOT NULL,
        Reason NVARCHAR(500) NOT NULL,
        Status NVARCHAR(10) NOT NULL CONSTRAINT DF_CreditApprovals_Status DEFAULT (N'granted'),
        ApprovedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_CreditApprovals_ApprovedAtUtc DEFAULT (SYSUTCDATETIME()),
        ConsumedAtUtc DATETIME2 NULL,
        CONSTRAINT CK_CreditApprovals_Status CHECK (Status IN (N'granted', N'consumed', N'burned')),
        CONSTRAINT CK_CreditApprovals_Reason CHECK (LEN(Reason) >= 2),
        CONSTRAINT CK_CreditApprovals_Amounts CHECK (
            NewCreditAmount > 0 AND ExcessAmount > 0
            AND ProjectedOutstanding = CurrentOutstanding + NewCreditAmount),
        CONSTRAINT FK_CreditApprovals_CustomerId FOREIGN KEY (CustomerId)
            REFERENCES dbo.Customers(PartyId),
        CONSTRAINT FK_CreditApprovals_ManagerUserId FOREIGN KEY (ManagerUserId)
            REFERENCES dbo.Users(UserId),
        CONSTRAINT FK_CreditApprovals_ConsumedByInvoiceId FOREIGN KEY (ConsumedByInvoiceId)
            REFERENCES dbo.Invoices(InvoiceId)
    );
    CREATE INDEX IX_CreditApprovals_CustomerId ON dbo.CreditApprovals(CustomerId);
    CREATE INDEX IX_CreditApprovals_Status ON dbo.CreditApprovals(Status);
    CREATE INDEX IX_CreditApprovals_ManagerUserId ON dbo.CreditApprovals(ManagerUserId);
    EXEC(N'CREATE UNIQUE INDEX UQ_CreditApprovals_ConsumedByInvoice
           ON dbo.CreditApprovals(ConsumedByInvoiceId) WHERE ConsumedByInvoiceId IS NOT NULL');
END;
/* Task-4: سياسة المنح الصارمة لاعتمادات تجاوز حد الائتمان (Upgrade_18).
   أي اعتماد granted جديد يجب أن يمثل تجاوزًا فعليًا لحدٍّ موجب (الصفر
   منع مطلق لا يعتمده مدير، السالب بيانات فاسدة). صفوف granted التاريخية
   المخالفة تنتقل burned (انتقال الحرق نفسه — سجل تدقيق يبقى، لا حذف)،
   ولكل صف سجل SystemLogs. القيد مشروط بالحالة granted عمدًا: صفوف
   consumed/burned القديمة سجل تدقيق لا يُمس. نفس منطق
   Upgrade_18_CreditApprovalGrantPolicy.sql وSaylDB.sql تمامًا —
   والمسارات الثلاثة متطابقة النتيجة. Idempotent: بالقيد الموجود لا يفعل شيئًا.
   التزامن: هذه الكتلة تعمل عند إقلاع كل طرفية على الشبكة — قفل تطبيقي مسمّى
   (sp_getapplock Exclusive بمالك المعاملة، نمط clsPayrollService) يُسلسِل
   الترقية بين الطرفيات: من يفوز بالقفل يُله المعاملة الواحدة (حرق + تدقيق +
   إنشاء القيد معًا)، ومن تنتهي مهلته يتخطى الترقية تخطيًّا غير فادح —
   الإقلاع يكمل والإقلاع اللاحق يعيد المحاولة (idempotent). فحص وجود القيد
   يُعاد بعد القسط على القفل لا قبله — لا سباق قيد مزدوج. فشل ترقية حقيقي
   يتراجع كليًا (لا حرق نصف مُرتحل ولا قيد بلا حرق) ثم يُرمى ليفشل الإقلاع
   بصوت عالٍ. */
IF OBJECT_ID(N'dbo.CreditApprovals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.SystemLogs', N'U') IS NOT NULL
BEGIN
    DECLARE @u18Lock int;
    BEGIN TRAN;
    EXEC @u18Lock = sys.sp_getapplock
         @Resource = N'Upgrade18GrantPolicy',
         @LockMode = 'Exclusive',
         @LockOwner = 'Transaction',
         @LockTimeout = 10000;
    IF @u18Lock >= 0
    BEGIN
        BEGIN TRY
            /* إعادة فحص حالة الترقية بعد القسط على القفل — لا قبله */
            IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                           WHERE name = N'CK_CreditApprovals_GrantPolicy'
                             AND parent_object_id = OBJECT_ID(N'dbo.CreditApprovals'))
            BEGIN
                DECLARE @u18Violating TABLE (ApprovalId INT NOT NULL);
                INSERT INTO @u18Violating (ApprovalId)
                SELECT ApprovalId
                FROM dbo.CreditApprovals WITH (UPDLOCK)
                WHERE Status = N'granted'
                  AND NOT (CreditLimit > 0
                           AND NewCreditAmount > 0
                           AND ProjectedOutstanding = CurrentOutstanding + NewCreditAmount
                           AND ProjectedOutstanding > CreditLimit
                           AND ExcessAmount = ProjectedOutstanding - CreditLimit);
                UPDATE a SET a.Status = N'burned'
                FROM dbo.CreditApprovals a
                JOIN @u18Violating v ON v.ApprovalId = a.ApprovalId;
                INSERT INTO dbo.SystemLogs (UserId, Action, Details, CreatedAt)
                SELECT NULL, N'CreditApprovalBurned',
                       N'اعتماد ائتمان #' + CONVERT(NVARCHAR(20), v.ApprovalId)
                         + N' أُحرق بترحيل Upgrade_18: صف granted مخالف لسياسة المنح الصارمة'
                         + N' (حد ائتماني غير موجب أو مشروع لا يتجاوز الحد أو فائض محسوب خطأ)'
                         + N' — سجل تدقيق يبقى محفوظًا ولا صلاحية للاعتماد مستقبلًا.',
                       GETUTCDATE()
                FROM @u18Violating v;
                ALTER TABLE dbo.CreditApprovals ADD CONSTRAINT CK_CreditApprovals_GrantPolicy CHECK (
                    Status <> N'granted'
                    OR (CreditLimit > 0
                        AND NewCreditAmount > 0
                        AND ProjectedOutstanding = CurrentOutstanding + NewCreditAmount
                        AND ProjectedOutstanding > CreditLimit
                        AND ExcessAmount = ProjectedOutstanding - CreditLimit));
            END;
            COMMIT TRAN;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRAN;
            THROW;
        END CATCH;
    END
    ELSE
    BEGIN
        /* مهلة القفل/ازدحام: تخطٍّ غير فادح للإقلاع — إقلاع لاحق يعيد التطبيق */
        ROLLBACK TRAN;
    END
END;
/* Task-2/3: الترقيم التجاري السنوي للفواتير INV-YYYY-NNNNNN (Upgrade_19).
   الفواتير تُعرَّف بـInvoiceId داخليًا فقط — المطلوب رقم تجاري سنوي واحد
   مشترك عبر الأنواع الأربعة كلها (sale/purchase/customer_return/
   supplier_return) فريد داخل سنتها البرمجية (UTC+3 — اتفاقية
   ProgramClock). العمود NVARCHAR(32): يُضاف nullable أولًا كي يُرحِّل
   القديم (لا يمكن شدّ NOT NULL قبل وجود رقم لكل صف)، ثم بعد اكتمال
   الترحيل يُشدَّد NOT NULL — لا مسار إنتاج يُدرج فاتورة بلا رقم:
   المولّد (Task 3) يسنده داخل معاملة كل عملية، وNOT NULL حاجز هيكلي
   يمنع أي إدراج بلا رقم. فهرس فريد — الصيغة النهائية عادية بلا
   مرشِّح: يمنع تكرار الرقم عبر كل صفوف الجدول (حاجز محرك فوق أي
   فحص-ثم-إسناد لاحق)؛ أثناء الترحيل فقط يُبنى مُرشَّحًا (NULL حرّ
   كي تُرقَّم الصفوف) ثم يُهدم ويُعاد عاديًّا بعد شدّ NOT NULL. جدول
   عدّاد لكل سنة يقرؤه المولّد ويجعله فوق أعلى لاحقة صادرة. الترحيل:
   صف بلا CreatedAt (بيانات فاسدة قائمة لكنها حقيقية) يُرقَّم في السنة
   البرمجية الحالية — مُوثَّق عمدًا. الترتيب داخل السنة حتمي: بلا
   CreatedAt أخيرًا ثم CreatedAt ثم InvoiceId. القاعدة العدّادية:
   التسلسل = base + rn حيث base لكل سنة = MAX(عدّاد إن وُجد، أعلى
   لاحقة مُحلَّلة من المحفوظ INV-<digits>-<digits>، 0) — ROW_NUMBER من 1
   الصرف كان يصطدم بالأرقام المحفوظة في إعادة تشغيل بعد ترقية جزئية.
   FORMAT(seq,'000000') يزيد الحشو ولا يقص أبدًا (RIGHT كان يقص —
   مرفوض)؛ أقصى طول 'INV-YYYY-' + 19 = 28 ≤ 32. upsert العدّاد لا
   يُخفَّضه أبدًا. رفض صريح: إن بقي بعد الترحيل صف بلا رقم (NULL أو
   فارغ — الفارغ يسمم الفهرس الفريد العادي) فTHROW يتراجع عن الترقية
   كلها: لمسُ بيانات تافهة قرار مالك لا سكربت تلقائي. كل شيء (عمود +
   فهرس + جدول + ترحيل + عدّادات + تعميد NOT NULL) في
   معاملة واحدة تحت قفل تطبيقي مسمّى (sp_getapplock Exclusive بمالك
   المعاملة — نمط Upgrade_18) يُسلسِل الطرفيات المتوازية، وفحص الحالة
   يُعاد بعد القسط على القفل — من تنتهي مهلته يتخطى تخطيًّا غير فادح
   والإقلاع اللاحق يعيد التطبيق (idempotent: المُرقَّم لا يُعاد إسناده
   فإعادة التشغيل تزايدية بلا تصادم، والحالة النهائية على تركيب جديد
   من SaylDB.sql النهائية تُكتشف فيتخطى كل شيء). فشل حقيقي يتراجع
   كليًا ثم يُرمى.
   نفس منطق Upgrade_19_AnnualInvoiceNumbering.sql وSaylDB.sql تمامًا —
   والمسارات الثلاثة متطابقة النتيجة. */
IF OBJECT_ID(N'dbo.Invoices', N'U') IS NOT NULL
BEGIN
    DECLARE @u19Lock int;
    BEGIN TRAN;
    EXEC @u19Lock = sys.sp_getapplock
         @Resource = N'Upgrade19InvoiceNumbering',
         @LockMode = 'Exclusive',
         @LockOwner = 'Transaction',
         @LockTimeout = 10000;
    IF @u19Lock >= 0
    BEGIN
        BEGIN TRY
            /* إعادة فحص حالة الترقية بعد القسط على القفل — لا قبله.
               الحراس على مرحلتين عمدًا: فحص الصفوف NULL يُكتب بعمود
               InvoiceNumber — لو جُمع مع فحص وجود العمود في شرط واحد
               لفشل الترجمة على قاعدة قديمة بلا عمود (Invalid column name
               لحظة ترجمة العبارة قبل تنفيذ ALTER). */
            DECLARE @u19Todo bit = 0;
            IF OBJECT_ID(N'dbo.InvoiceNumberCounters', N'U') IS NULL
               OR NOT EXISTS (SELECT 1 FROM sys.columns
                              WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                                AND name = N'InvoiceNumber')
               OR EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                            AND name = N'InvoiceNumber' AND is_nullable = 1)
               OR NOT EXISTS (SELECT 1 FROM sys.indexes
                              WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                                AND name = N'UQ_Invoices_InvoiceNumber'
                                AND is_unique = 1 AND has_filter = 0)
                SET @u19Todo = 1;
            ELSE
            BEGIN
                /* فحص الصفوف NULL/فارغ عبر sp_executesql وجوبي: مرجعٌ ثابت
                   للعمود في هذا الفرع كان سيقتل ترجمة الدفعة كلها على قاعدة
                   قديمة بلا العمود (Msg 207 قبل تنفيذ شيء — مُثبَت تجريبيًا)،
                   وهو لا يُنفَّذ إلا بعد ثبوت وجود العمود فترجمته اللحظية
                   للدفعة الفرعية آمنة. */
                DECLARE @u19Nulls int = 0;
                EXEC sp_executesql
                     N'SELECT @u19Nulls = COUNT(*) FROM dbo.Invoices
                       WHERE InvoiceNumber IS NULL OR InvoiceNumber = N''''',
                     N'@u19Nulls int OUTPUT', @u19Nulls = @u19Nulls OUTPUT;
                IF @u19Nulls > 0
                    SET @u19Todo = 1;
            END;

            IF @u19Todo = 1
            BEGIN
                /* EXEC وجوبي — مُثبَت تجريبيًا: أي مرجع ثابت للعمود (حتى في
                   فروع IF/ELSE غير منفَّذة) يُترجم مع الدفعة كلها لحظة بدئها —
                   قبل تنفيذ ALTER إضافة العمود على قاعدة قديمة — فيفشل بـ
                   Msg 207 ولا يُنفَّذ شيء. الدفعة الفرعية الأولى تضيف العمود
                   فقط؛ والثانية تُترجم لحظة تنفيذها بعد وجود العمود فمراجعه
                   فيها كلها آمنة. */
                EXEC(N'
                    /* العمود — إن غاب فقط (nullable أولًا: الترحيل يحتاجه كي
                       يملأ كل الصفوف قبل شدّ NOT NULL) */
                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                     AND name = N''InvoiceNumber'')
                        ALTER TABLE dbo.Invoices ADD InvoiceNumber NVARCHAR(32) NULL;
                ');

                EXEC(N'
                /* الفهرس الفريد المُرشَّح — إن غاب فقط (حاجز محرك أثناء
                   الترحيل، يُستبدل بعاديٍّ بعد شدّ NOT NULL). أرقام قديمة
                   مكررة (بيانات فاسدة قائمة) تفشل الإنشاء بـTHROW وتراجع
                   كل شيء: لمسها قرار مالك لا سكربت تلقائي. داخل دفعة
                   EXEC الفرعية: العمود موجود وقت ترجمتها، فالمرجع آمن. */
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                 AND name = N''UQ_Invoices_InvoiceNumber'')
                    CREATE UNIQUE INDEX UQ_Invoices_InvoiceNumber
                        ON dbo.Invoices(InvoiceNumber) WHERE InvoiceNumber IS NOT NULL;

                /* جدول العدّاد — إن غاب فقط */
                IF OBJECT_ID(N''dbo.InvoiceNumberCounters'', N''U'') IS NULL
                    CREATE TABLE dbo.InvoiceNumberCounters
                    (
                        NumberYear INT NOT NULL
                            CONSTRAINT PK_InvoiceNumberCounters PRIMARY KEY,
                        LastIssuedNumber BIGINT NOT NULL
                            CONSTRAINT DF_InvoiceNumberCounters_LastIssuedNumber DEFAULT (0),
                        LastUpdatedAtUtc DATETIME2 NOT NULL
                            CONSTRAINT DF_InvoiceNumberCounters_LastUpdatedAtUtc DEFAULT (SYSUTCDATETIME())
                    );

                /* الترحيل — الصفوف NULL فقط (تزايدية). نفس خوارزمية
                   Upgrade_19_AnnualInvoiceNumbering.sql حرفيًا. */
                CREATE TABLE #Inv19Targets
                (
                    InvoiceId   INT NOT NULL PRIMARY KEY,
                    ProgramYear INT NOT NULL,
                    rn          BIGINT NOT NULL
                );

                INSERT INTO #Inv19Targets (InvoiceId, ProgramYear, rn)
                SELECT InvoiceId,
                       ProgramYear,
                       ROW_NUMBER() OVER (PARTITION BY ProgramYear
                                          ORDER BY CASE WHEN CreatedAt IS NULL THEN 1 ELSE 0 END,
                                                   CreatedAt, InvoiceId)
                FROM (
                    SELECT i.InvoiceId,
                           i.CreatedAt,
                           CASE WHEN i.CreatedAt IS NULL
                                THEN YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME()))
                                ELSE YEAR(DATEADD(HOUR, 3, i.CreatedAt))
                           END AS ProgramYear
                    FROM dbo.Invoices i
                    WHERE i.InvoiceNumber IS NULL
                ) t;

                CREATE TABLE #Inv19Maxes
                (
                    ProgramYear INT NOT NULL PRIMARY KEY,
                    Base        BIGINT NOT NULL,
                    NewMax      BIGINT NOT NULL
                );

                WITH Preserved AS (
                    SELECT CAST(SUBSTRING(n.Num, 5, 4) AS INT)  AS ProgramYear,
                           CAST(SUBSTRING(n.Num, 10, 100) AS BIGINT) AS Suffix
                    FROM (SELECT InvoiceNumber AS Num
                          FROM dbo.Invoices WITH (UPDLOCK)
                          WHERE InvoiceNumber IS NOT NULL) n
                    WHERE LEN(n.Num) >= 10
                      AND SUBSTRING(n.Num, 1, 4) = N''INV-''
                      AND SUBSTRING(n.Num, 5, 4) NOT LIKE N''%[^0-9]%''
                      AND SUBSTRING(n.Num, 9, 1) = N''-''
                      AND SUBSTRING(n.Num, 10, 100) <> N''''
                      AND SUBSTRING(n.Num, 10, 100) NOT LIKE N''%[^0-9]%''
                ),
                Candidates AS (
                    SELECT c.NumberYear AS ProgramYear, c.LastIssuedNumber AS Val
                    FROM dbo.InvoiceNumberCounters c
                    UNION ALL
                    SELECT p.ProgramYear, p.Suffix FROM Preserved p
                ),
                Bases AS (
                    SELECT y.ProgramYear, MAX(COALESCE(cd.Val, 0)) AS Base
                    FROM (
                        SELECT DISTINCT ProgramYear FROM #Inv19Targets
                        UNION SELECT ProgramYear FROM Candidates
                    ) y
                    LEFT JOIN Candidates cd ON cd.ProgramYear = y.ProgramYear
                    GROUP BY y.ProgramYear
                )
                INSERT INTO #Inv19Maxes (ProgramYear, Base, NewMax)
                SELECT b.ProgramYear, b.Base, b.Base + COALESCE(t.mx, 0)
                FROM Bases b
                OUTER APPLY (SELECT MAX(t.rn) AS mx
                             FROM #Inv19Targets t
                             WHERE t.ProgramYear = b.ProgramYear) t;

                UPDATE i
                SET i.InvoiceNumber = N''INV-'' + CONVERT(NVARCHAR(4), m.ProgramYear)
                                      + N''-'' + FORMAT(m.Base + t.rn, N''000000'')
                FROM dbo.Invoices i
                JOIN #Inv19Targets t ON t.InvoiceId = i.InvoiceId
                JOIN #Inv19Maxes m ON m.ProgramYear = t.ProgramYear;

                /* upsert العدّاد لكل سنة — لا يُخفَّض أبدًا */
                UPDATE c
                SET c.LastIssuedNumber = CASE WHEN c.LastIssuedNumber > m.NewMax
                                              THEN c.LastIssuedNumber ELSE m.NewMax END,
                    c.LastUpdatedAtUtc = SYSUTCDATETIME()
                FROM dbo.InvoiceNumberCounters c
                JOIN #Inv19Maxes m ON m.ProgramYear = c.NumberYear;

                INSERT INTO dbo.InvoiceNumberCounters (NumberYear, LastIssuedNumber, LastUpdatedAtUtc)
                SELECT m.ProgramYear, m.NewMax, SYSUTCDATETIME()
                FROM #Inv19Maxes m
                WHERE NOT EXISTS (SELECT 1 FROM dbo.InvoiceNumberCounters c
                                  WHERE c.NumberYear = m.ProgramYear);

                DROP TABLE #Inv19Targets;
                DROP TABLE #Inv19Maxes;

                /* رفض إتمام التعميد: صف بلا رقم (NULL أو فارغ) بعد الترحيل =
                   بيانات لا يمكن شدّ NOT NULL معها (والفارغ يسمم الفهرس
                   الفريد العادي) — THROW يتراجع عن الترقية كلها: لمسُها
                   قرار مالك لا سكربت تلقائي. */
                DECLARE @u19Unnumbered int =
                    (SELECT COUNT(*) FROM dbo.Invoices
                     WHERE InvoiceNumber IS NULL OR InvoiceNumber = N'''');
                IF @u19Unnumbered > 0
                BEGIN
                    DECLARE @u19Msg nvarchar(2048) =
                        N''Upgrade_19: تعذر إتمام الترقية — بقي ''
                        + CONVERT(nvarchar(20), @u19Unnumbered)
                        + N'' صف فاتورة بلا رقم تجاري (NULL أو فارغ) بعد الترحيل. ''
                        + N''أصلح هذه الصفوف يدويًا ثم أعد تشغيل الترقية.'';
                    THROW 50019, @u19Msg, 1;
                END;

                /* التعميد النهائي: هدم الفهرس المُرشَّح (وجوبًا قبل ALTER —
                   SQL Server يرفض ALTER على عمود مُفهرس) ثم شدّ NOT NULL
                   ثم إعادة الفهرس فريدًا عاديًّا بالاسم نفسه (داخل هذه
                   الدفعة الفرعية مباشرة: العمود موجود وقت ترجمتها).
                   الصيغة النهائية: كل صف مميَّز برقم لا NULL ولا فارغ. */
                IF EXISTS (SELECT 1 FROM sys.indexes
                           WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                             AND name = N''UQ_Invoices_InvoiceNumber'')
                    DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;

                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                             AND name = N''InvoiceNumber'' AND is_nullable = 1)
                    ALTER TABLE dbo.Invoices
                        ALTER COLUMN InvoiceNumber NVARCHAR(32) NOT NULL;

                CREATE UNIQUE INDEX UQ_Invoices_InvoiceNumber
                    ON dbo.Invoices(InvoiceNumber);
                ');
            END;
            COMMIT TRAN;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRAN;
            THROW;
        END CATCH;
    END
    ELSE
    BEGIN
        /* مهلة القفل/ازدحام: تخطٍّ غير فادح للإقلاع — إقلاع لاحق يعيد التطبيق */
        ROLLBACK TRAN;
    END
END;";

        /// <summary>يضمن أعمدة/جداول المخطط الجديدة على أي قاعدة (قديمة أو جديدة) — يُنادى مرة عند الإقلاع.</summary>
        public async Task EnsureSchemaUpgradesAsync()
            => await _dbContext.Database.ExecuteSqlRawAsync(EnsureSchemaSql);

        /// <summary>VULN-47: إنشاء «حساب المدير» في أول تركيب — الاستبدال الآمن
        /// للحساب الديمو (admin/Admin@123) الذي كان يُزرَع بكلمة مرور معروفة في كل
        /// تثبيت جديد. القاعدة: أول حساب نشط في النظام هو مالكه — ومنذ وجود أي
        /// مستخدم نشط يُغلق هذا المسار إلى الأبد (لا يعاد تشغيله فوق بيانات حية).
        /// requirePasswordChange: يُعلّم الحساب المُنشأ بأنه يجب تغيير كلمته أول دخول.
        /// reactivateExisting: عند إعادة التركيب فوق قاعدة قديمة قد يوجد صف admin
        /// معطّل (IsActive=0) بلا أي مستخدم نشط. بدل الفشل بـ«اسم المستخدم موجود
        /// مسبقًا» نعيد تفعيله ونصفّر كلمته — هذا هو مسار التعافي الوحيد المسموح،
        /// ويبقى محصورًا في حالة انعدام أي مستخدم نشط.</summary>
        public async Task<Result<UserVM>> CreateOwnerAsync(string username, string password,
            bool requirePasswordChange = false, bool reactivateExisting = false)
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

            using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                // لقطة SQL واحدة لكل عملية إنشاء/تعافي المالك (Task 2) — سجل
                // التدقيق يشترك في هذه اللحظة من ساعة SQL Server
                DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

                // A9: قفل جدول Users نفسه (موجود في كل قاعدة) — القفل السابق على
                // جدول Settings كان يُسقط الإنشاء بـSqlException «Invalid object name»
                // على القواعد الأقدم التي بُنيت قبل إضافة Settings، والخطأ كان يتسرب
                // خارج الـ catchات فيؤدي إلى شاشة «لا يوجد مستخدمين».
                await _dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT COUNT_BIG(*) FROM Users WITH (TABLOCKX, HOLDLOCK)");

                bool anyActive = await _dbContext.Users.AnyAsync(u => u.IsActive);
                if (anyActive)
                    return Result<UserVM>.Fail("إنشاء حساب المدير غير متاح — النظام يحتوي مستخدمين بالفعل.");

                var adminRole = await _dbContext.Roles
                    .FirstOrDefaultAsync(r => r.RoleValue == AppPermissions.AdminRoleValue && r.IsActive);
                if (adminRole == null)
                    return Result<UserVM>.Fail("دور المدير غير متوفر — لا يمكن إنشاء حساب المدير.");

                if (reactivateExisting)
                {
                    var existing = await _dbContext.Users
                        .FirstOrDefaultAsync(u => u.Username == username);
                    if (existing != null)
                    {
                        existing.IsActive = true;
                        existing.RoleId = adminRole.RoleId;
                        existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                        existing.MustChangePassword = requirePasswordChange;

                        await _dbContext.SaveChangesAsync();
                        await transaction.CommitAsync();

                        clsAudit.Log(_dbContext, existing.UserId, "OwnerRecovered",
                            $"إعادة تفعيل حساب المدير القديم '{existing.Username}' وتعيين كلمة مرور افتراضية.",
                            nowUtc);
                        try
                        {
                            await _dbContext.SaveChangesAsync();
                        }
                        catch
                        {
                            // فشل سجل التدقيق لا يمنع استعادة المالك
                        }

                        return Result<UserVM>.Ok(new UserVM
                        {
                            UserId = existing.UserId,
                            UserName = existing.Username,
                            RoleId = adminRole.RoleId,
                            RoleName = adminRole.Name,
                            RoleValue = adminRole.RoleValue,
                            IsActive = true,
                            MustChangePassword = requirePasswordChange
                        });
                    }
                }

                bool exists = await _dbContext.Users.AnyAsync(u => u.Username == username);
                if (exists)
                    return Result<UserVM>.Fail("اسم المستخدم موجود مسبقًا.");

                var owner = new User
                {
                    Username = username,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    RoleId = adminRole.RoleId,
                    EmployeeId = null,
                    IsActive = true,
                    MustChangePassword = requirePasswordChange
                };

                _dbContext.Users.Add(owner);
                await _dbContext.SaveChangesAsync();

                await transaction.CommitAsync();

                clsAudit.Log(_dbContext, owner.UserId, "OwnerSetup",
                    $"إنشاء حساب المدير الأول '{owner.Username}'",
                    nowUtc);
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
                    IsActive = true,
                    MustChangePassword = requirePasswordChange
                });
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "UQ_Users_Username"))
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result<UserVM>.Fail("اسم المستخدم موجود مسبقًا.");
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                return Result<UserVM>.Fail($"فشل إنشاء حساب المدير: {ex.Message}");
            }
            catch (Exception ex)
            {
                // A9: أي فشل آخر (SqlException من القفل أو أي خطأ غير متوقع) يرجع
                // نتيجة Fail واضحة بدل تسرّب الاستثناء إلى بوابة الإقلاع.
                try { await transaction.RollbackAsync(); } catch { /* المعاملة قد تكون منتهية أصلًا */ }
                _dbContext.ChangeTracker.Clear();
                return Result<UserVM>.Fail($"فشل إنشاء حساب المدير: {ex.Message}");
            }
        }

        /// <summary>الحساب الافتراضي: يُنشأ صامتًا عند صفر مستخدمين نشطين مع إجبار
        /// تغيير كلمة المرور أول دخول. يفشل برسالة واضحة إن وُجد مستخدم نشط.
        /// على قاعدة قديمة بقي فيها صف admin معطّل من تركيب سابق، يُعاد تفعيله
        /// بدل الفشل بـ«اسم المستخدم موجود مسبقًا» (تعافٍ آمن بعد إعادة التركيب).</summary>
        public async Task<Result<UserVM>> EnsureDefaultAdminAsync()
        {
            if (await HasActiveUsersAsync())
                return Result<UserVM>.Fail("إنشاء حساب المدير غير متاح — النظام يحتوي مستخدمين بالفعل.");

            return await CreateOwnerAsync(DefaultAdminUsername, DefaultAdminPassword,
                requirePasswordChange: true, reactivateExisting: true);
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex, string constraintName)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is Microsoft.Data.SqlClient.SqlException sql
                    && (sql.Number == 2601 || sql.Number == 2627
                        || sql.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
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
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                        $"تغيير دور المستخدم #{userId} مرفوض: يتطلب صلاحية شاشة المستخدمين.");
                    return Result.Fail("تغيير أدوار المستخدمين يتطلب صلاحية شاشة المستخدمين.");
                }

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

                // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
                DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

                clsAudit.Log(_dbContext, actorUserId.Value, "ChangeRole",
                    $"تغيير دور '{user.Username}' من '{oldRoleName}' إلى '{newRole.Name}'",
                    nowUtc);
                await _dbContext.SaveChangesAsync();
                return Result.Ok("تم تغيير الدور بنجاح.");
            }

        public async Task<Result> LinkEmployeeAsync(int userId, int employeeId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("ربط الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                    $"ربط موظف #{employeeId} بالمستخدم #{userId} مرفوض: يتطلب صلاحية شاشة المستخدمين.");
                return Result.Fail("ربط الحسابات بالمستخدمين يتطلب صلاحية شاشة المستخدمين.");
            }

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

            // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            clsAudit.Log(_dbContext, actorUserId.Value, "LinkEmployee",
                $"ربط الموظف رقم {employeeId} بالحساب '{user.Username}'",
                nowUtc);
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم ربط الموظف بالمستخدم.");
        }

        public async Task<Result> DeactivateUserAsync(int userId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("تعطيل الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                    $"تعطيل المستخدم #{userId} مرفوض: يتطلب صلاحية شاشة المستخدمين.");
                return Result.Fail("تعطيل الحسابات يتطلب صلاحية شاشة المستخدمين.");
            }

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

            // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            clsAudit.Log(_dbContext, actorUserId.Value, "DeactivateUser", $"تعطيل المستخدم '{user.Username}'",
                nowUtc);
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل المستخدم.");
        }

        public async Task<Result> ActivateUserAsync(int userId, int? actorUserId = null)
        {
            if (!actorUserId.HasValue)
                return Result.Fail("تفعيل الحسابات يتطلب تحديد المستخدم الفاعل.");

            int actorValue = await GetActorRoleValueAsync(actorUserId.Value);
            if (actorValue < 0 || !AppPermissions.Has(actorValue, AppPermissions.Users))
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                    $"تفعيل المستخدم #{userId} مرفوض: يتطلب صلاحية شاشة المستخدمين.");
                return Result.Fail("تفعيل الحسابات يتطلب صلاحية شاشة المستخدمين.");
            }

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            var guard = await EnsureCanManageAsync(actorUserId.Value, user);
            if (!guard.IsSuccess)
                return guard;

            user.IsActive = true;

            // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            clsAudit.Log(_dbContext, actorUserId.Value, "ActivateUser", $"تفعيل المستخدم '{user.Username}'",
                nowUtc);
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تفعيل المستخدم.");
        }


        public async Task<Result<UserVM>> LoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return Result<UserVM>.Fail("اسم المستخدم وكلمة المرور مطلوبان.");

            // قفل الحساب بعد المحاولات الفاشلة المتكررة
            // ══ استثناء مقصود (Task 2) ══ قفل المحاولات مؤقّت أمني في الذاكرة
            // (_failedLogins) لا سجل عمل يُخزَّن: يموت بانتهاء العملية، وتوقيت
            // SQL لكل محاولة يضيف زمنًا على مسار الدخول الحساس، وهو ليس طابعًا
            // تجاريًا محفوظًا — لذا يبقى على DateTime.UtcNow. كل الطوابع
            // المحفوظة (سجل الدخول والتدقيق) تبقى من لقطة SQL كما في العهد.
            string lockoutKey = NormalizeLockoutKey(username);
            bool lockedOut = false;
            int lockRemainingMinutes = 0;
            lock (_failedLogins)
            {
                if (_failedLogins.TryGetValue(lockoutKey, out var state) && state.LockUntil > DateTime.UtcNow)
                {
                    lockedOut = true;
                    lockRemainingMinutes = (int)Math.Ceiling((state.LockUntil - DateTime.UtcNow).TotalMinutes);
                }
            }

            // لقطة SQL واحدة لكل عملية دخول (Task 2) — سجل الدخول وسجلات
            // التدقيق (نجاحًا ورفضًا) كلها تشترك في هذه اللحظة
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            if (lockedOut)
            {
                // تدقيق الرفض: محاولة دخول أثناء قفل الحساب (قد لا يكون لها فاعل معروف)
                await clsAudit.LogRejectedAsync(_dbContext, null, "LoginDenied",
                    $"محاولة دخول أثناء قفل مؤقت للحساب '{lockoutKey}' — حاول بعد {lockRemainingMinutes} دقيقة.",
                    nowUtc);
                return Result<UserVM>.Fail($"الحساب مقفل مؤقتًا بسبب المحاولات الفاشلة. حاول بعد {lockRemainingMinutes} دقيقة.");
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
                // تدقيق الرفض: اسم مستخدم غير معروف — بلا فاعل (UserId = null)
                await clsAudit.LogRejectedAsync(_dbContext, null, "LoginFailed",
                    $"محاولة دخول باسم مستخدم غير معروف: '{username}'.",
                    nowUtc);
                return Result<UserVM>.Fail(invalidCredentials);
            }

            if (!user.IsActive)
            {
                _ = BCrypt.Net.BCrypt.Verify(password, dummyHash);
                // تدقيق الرفض: محاولة دخول لحساب معطل
                await clsAudit.LogRejectedAsync(_dbContext, user.UserId, "LoginDenied",
                    $"محاولة دخول لحساب معطل: '{user.Username}'.",
                    nowUtc);
                return Result<UserVM>.Fail(invalidCredentials);
            }

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                RegisterFailedLogin(username);
                // تدقيق الرفض: كلمة مرور غير صحيحة — الفاعل معروف (لا كلمة المرور أبدًا)
                await clsAudit.LogRejectedAsync(_dbContext, user.UserId, "LoginFailed",
                    $"محاولة دخول فاشلة بكلمة مرور غير صحيحة للحساب '{user.Username}'.",
                    nowUtc);
                return Result<UserVM>.Fail(invalidCredentials);
            }

            // بوابة الدور المعطل (Fail-Closed): كلمة صحيحة لكن دورك معطل = رفض
            // واضح لا رسالة دخول عامة — صاحب الحساب يعرف السبب والمدير يملك الحل.
            if (!user.Role.IsActive)
                return Result<UserVM>.Fail($"دورك '{user.Role.Name}' معطل حاليًا. راجع مدير النظام.");

            lock (_failedLogins)
            {
                _failedLogins.Remove(lockoutKey);
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
                    CreatedAt = nowUtc
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
                Name = user.Employee?.Name ?? "No Employee",
                MustChangePassword = user.MustChangePassword
            };

            return Result<UserVM>.Ok(userVm, "تم تسجيل الدخول بنجاح.");
        }

        private static void RegisterFailedLogin(string username)
        {
            string key = NormalizeLockoutKey(username);
            lock (_failedLogins)
            {
                var (count, _) = _failedLogins.TryGetValue(key, out var s) ? s : (0, DateTime.MinValue);
                count++;
                var lockUntil = count >= MaxLoginAttempts ? DateTime.UtcNow.AddMinutes(LockMinutes) : DateTime.MinValue;
                _failedLogins[key] = (count >= MaxLoginAttempts ? 0 : count, lockUntil);
            }
        }

        private static string NormalizeLockoutKey(string username)
            => username?.ToUpperInvariant() ?? string.Empty;


        public async Task<Result> LogoutAsync(int userId)
        {
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            // لقطة SQL واحدة للعملية (Task 2) — سجل الخروج من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = userId,
                Action = "Logout",
                Details = $"تسجيل خروج المستخدم '{user.Username}'",
                CreatedAt = nowUtc
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
                // تدقيق الرفض: فشل إثبات الهوية عند تغيير كلمة المرور (ناقل تصيّد محتمل)
                await clsAudit.LogRejectedAsync(_dbContext, user.UserId, "PasswordChangeDenied",
                    $"فشل تغيير كلمة المرور: كلمة المرور القديمة غير صحيحة للحساب '{user.Username}'.");
                return Result.Fail("كلمة المرور القديمة غير صحيحة.");
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.MustChangePassword = false;

            // لقطة SQL واحدة للعملية (Task 2) — سجل التغيير من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = user.UserId,
                Action = "ChangePassword",
                Details = $"المستخدم '{user.Username}' غيّر كلمة مروره.",
                CreatedAt = nowUtc
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
            {
                await clsAudit.LogRejectedAsync(_dbContext, actorUserId.Value, "UserOpDenied",
                    $"إعادة تعيين كلمة مرور المستخدم #{userId} مرفوضة: لمدير النظام وحده.");
                return Result.Fail("إعادة تعيين كلمات المرور للآخرين لمدير النظام وحده.");
            }

            string? policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null)
                return Result.Fail(policyError);

            var user = await _dbContext.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
                return Result.Fail("المستخدم غير موجود.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            // لقطة SQL واحدة للعملية (Task 2) — سجل التدقيق من ساعة SQL Server
            DateTime nowUtc = await _storeClock.GetUtcNowAsync(_dbContext);

            _dbContext.SystemLogs.Add(new SystemLog
            {
                UserId = actorUserId.Value,
                Action = "ResetPassword",
                Details = $"مدير النظام أعاد تعيين كلمة مرور المستخدم '{user.Username}'.",
                CreatedAt = nowUtc
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
                    Name = u.Employee == null ? "No Employee" : u.Employee.Name ?? "No Employee"
                })
                .SingleOrDefaultAsync();
        }
    }

}
