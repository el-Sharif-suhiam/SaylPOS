/* ============================================================
   Upgrade_14 - SystemLogs.UserId قابل للـ NULL (تدقيق الرفض)
   ------------------------------------------------------------
   تدقيق المحاولات المرفوضة (Audit Log — denied attempts):
   كان SystemLogs.UserId INT NOT NULL مع FK على Users(UserId)،
   فمحاولة دخول فاشلة باسم مستخدم غير معروف (تصيّد بيانات الدخول)
   لا يمكن تسجيلها إطلاقًا — لا هوية FK صالحة لها.

   الآن: UserId NULL مسموح — سجل الرفض يُكتب بلا فاعل، وبالفاعل
   (UserId صحيح) إن عُرف. قيد FK يبقى قائمًا على القيمة إن وُجدت.

   تعديل nullability عمود مرتبط بـ FK يتطلب إسقاط القيد ثم
   إعادته — يُعاد بنفس الاسم فلا يتغير شيء آخر.

   Idempotent — لا يفعل شيئًا إن كان العمود NULL مسموحًا.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_14_SystemLogsUserIdNullable.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.SystemLogs', N'U') IS NOT NULL
BEGIN
    IF COLUMNPROPERTY(OBJECT_ID(N'dbo.SystemLogs'), N'UserId', 'AllowsNull') = 0
    BEGIN
        ALTER TABLE dbo.SystemLogs DROP CONSTRAINT FK_SystemLogs_UserId;
        ALTER TABLE dbo.SystemLogs ALTER COLUMN UserId INT NULL;
        ALTER TABLE dbo.SystemLogs ADD CONSTRAINT FK_SystemLogs_UserId
            FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId);
        PRINT 'Upgrade_14: SystemLogs.UserId is now nullable (FK re-added)';
    END;
    ELSE
        PRINT 'Upgrade_14: SystemLogs.UserId already nullable — no-op';
END;
GO

PRINT 'Upgrade_14: SystemLogs.UserId migration done';
