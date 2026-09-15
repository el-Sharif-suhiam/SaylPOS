/* ============================================================
   Upgrade_09 - سحب login 'sayl' من sysadmin إلى الامتياز الأدنى
   ------------------------------------------------------------
   لماذا: sayl (هوية تشغيل التطبيق على كل الأجهزة) كان يُنشأ sysadmin —
   أي من يملك كلمة مروره يملك خادم SQL بالكامل. الإصلاح يمنحه فقط ما
   يحتاجه التطبيق فعلًا:
     • dbcreator           -> RESTORE DATABASE + ALTER DATABASE (single/multi)
     • db_backupoperator   -> BACKUP DATABASE
     • dbo على SaylDB      -> كامل CRUD (ALTER AUTHORIZATION)
   يُشغَّل مرة واحدة على الخادم الرئيسي بجلسة مدير (Windows أو sa)
   من أي سياق قاعدة (master افتراضيًا). Idempotent - إعادة تشغيله بلا ضرر.
   ============================================================ */

IF EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = 'sayl')
BEGIN
    /* 1) سحب sysadmin (قد يكون وُجد من تثبيت قديم) */
    IF EXISTS (
        SELECT 1
        FROM sys.server_role_members m
        JOIN sys.server_principals r ON m.role_principal_id = r.principal_id
        JOIN sys.server_principals p ON m.member_principal_id = p.principal_id
        WHERE r.name = 'sysadmin' AND p.name = 'sayl'
    )
    BEGIN
        ALTER SERVER ROLE sysadmin DROP MEMBER [sayl];
        PRINT 'Upgrade_09: sysadmin removed from sayl';
    END;

    /* 2) أدوار الخادم الأدنى المطلوبة لتشغيل التطبيق */
    IF NOT EXISTS (
        SELECT 1
        FROM sys.server_role_members m
        JOIN sys.server_principals r ON m.role_principal_id = r.principal_id
        JOIN sys.server_principals p ON m.member_principal_id = p.principal_id
        WHERE r.name = 'dbcreator' AND p.name = 'sayl'
    )
    BEGIN
        ALTER SERVER ROLE dbcreator ADD MEMBER [sayl];
        PRINT 'Upgrade_09: dbcreator added to sayl';
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.server_role_members m
        JOIN sys.server_principals r ON m.role_principal_id = r.principal_id
        JOIN sys.server_principals p ON m.member_principal_id = p.principal_id
        WHERE r.name = 'db_backupoperator' AND p.name = 'sayl'
    )
    BEGIN
        ALTER SERVER ROLE db_backupoperator ADD MEMBER [sayl];
        PRINT 'Upgrade_09: db_backupoperator added to sayl';
    END;

    /* 3) ملكية SaylDB -> sayl (كانت ملك جلسة الإنشاء الإدارية تاريخيًا،
          والوصول القديم كان عبر sysadmin نفسه) */
    IF DB_ID(N'SaylDB') IS NOT NULL
    BEGIN
        ALTER AUTHORIZATION ON DATABASE::[SaylDB] TO [sayl];
        PRINT 'Upgrade_09: SaylDB owner set to sayl (dbo)';
    END;
    ELSE
        PRINT 'Upgrade_09: SaylDB not found - owner step skipped (login roles done)';
END;
ELSE
    PRINT 'Upgrade_09: login sayl does not exist - nothing to do.';