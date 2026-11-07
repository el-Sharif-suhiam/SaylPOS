/* ============================================================
   Upgrade_15 - افتراضيات الطوابع الزمنية التقنية -> UTC
   ------------------------------------------------------------
   السياسة الموحدة للوقت (VULN-45 / ProgramClock):
   • التخزين: كل الطوابع الزمنية التقنية (Created/Updated/Started)
     بتوقيت UTC — الكود يكتب DateTime.UtcNow / GETUTCDATE() صراحة.
   • لكن افتراضيات الأعمدة في SaylDB.sql كانت (GETDATE()) — توقيت
     الخادم المحلي (UTC+3 السعودية): فخ كامن لأي مسار INSERT يُغفل
     العمود — انحراف صامت بثلاث ساعات حول منتصف الليل يفسد تقارير
     اليوم (Z) وحدود FEFO والمقارنات التاريخية.

   هذا السكربت: يستبدل تعريف الافتراضية فقط — DROP CONSTRAINT ثم
   ADD CONSTRAINT DEFAULT (SYSUTCDATETIME()) بنفس الاسم المتعارف
   (DF_الجدول_العمود). لا يُزحزح أي قيمة تاريخية مخزنة:
   Upgrade_08 (VULN-45) حوّل البيانات التاريخية إلى UTC مسبقًا،
   والكاتبون الحاليون يكتبون UTC صراحة — فالأفتراضيات فقط كانت
   متأخرة. أعمدة تواريخ الأعمال (PaymentDate، ExpiryDate، الفترات
   المالية، الرواتب) أعمدة DATE بلا افتراضيات زمنية — لا تُمس.

   Idempotent: الافتراضية الموجودة بتعريف UTC تُتخطى؛ إعادة
   التشغيل بلا أثر.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_15_UtcDefaults.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

SET NOCOUNT ON;

-----------------------------------------------------------
-- (الجدول، العمود) للطوابع الزمنية التقنية ذات الافتراضية.
-- أعمدة تواريخ الأعمال (DATE) عمدًا غير مدرجة هنا.
-- ملاحظة: الجداول/الأعمدة الغائبة (قاعدة أقدم) تُتخطى بأمان.
-----------------------------------------------------------
DECLARE @targets TABLE (TableName SYSNAME NOT NULL, ColumnName SYSNAME NOT NULL);
INSERT INTO @targets (TableName, ColumnName) VALUES
    (N'Parties',          N'CreatedAt'),
    (N'Employees',        N'CreatedAt'),
    (N'Users',            N'CreatedAt'),
    (N'Products',         N'CreatedAt'),
    (N'Batches',          N'CreatedAt'),
    (N'Inventory',        N'UpdatedAt'),
    (N'Invoices',         N'CreatedAt'),
    (N'StockReservations', N'CreatedAt'),
    (N'Payments',         N'CreatedAt'),
    (N'StockMovements',   N'CreatedAt'),
    (N'Expenses',         N'CreatedAt'),
    (N'SystemLogs',       N'CreatedAt'),
    (N'POSSessions',      N'StartedAt'),
    (N'Settings',         N'UpdatedAt'),
    (N'CashMovements',    N'CreatedAt'),
    (N'PayrollRecords',   N'CreatedAt'),
    (N'StockCounts',      N'CreatedAt');

DECLARE @table SYSNAME, @column SYSNAME;
DECLARE @constraint SYSNAME, @definition NVARCHAR(4000), @sql NVARCHAR(MAX);

DECLARE target_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName, ColumnName FROM @targets;
OPEN target_cursor;
FETCH NEXT FROM target_cursor INTO @table, @column;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.' + QUOTENAME(@table), @column) IS NOT NULL
    BEGIN
        -- الافتراضية الحالية على العمود (لو وجدت)
        SELECT @constraint = dc.name, @definition = dc.definition
        FROM sys.default_constraints dc
        WHERE dc.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@table), N'U')
          AND dc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME(@table)), @column, 'ColumnId');

        -- افتراضية توقيت محلي (GETDATE) → تُسقط وتُعاد UTC. أسماء القيود
        -- المعروفة (DF_SystemLogs_CreatedAt، DF_Settings_UpdatedAt،
        -- DF_CashMovements_CreatedAt، DF_StockReservations_CreatedAt)
        -- تُستعاد بنفسها لأن التسمية العامة هي DF_<الجدول>_<العمود>.
        IF @constraint IS NOT NULL AND LOWER(@definition) LIKE '%getdate%'
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table)
                     + N' DROP CONSTRAINT ' + QUOTENAME(@constraint) + N';';
            EXEC(@sql);
            SET @constraint = NULL;
        END;

        IF @constraint IS NULL
        BEGIN
            -- لا افتراضية (أو أُسقطت للتو) → أضف الافتراضية UTC بالاسم المعياري
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table)
                     + N' ADD CONSTRAINT DF_' + @table + N'_' + @column
                     + N' DEFAULT (SYSUTCDATETIME()) FOR ' + QUOTENAME(@column) + N';';
            EXEC(@sql);
            PRINT N'Upgrade_15: ' + @table + N'.' + @column + N' default -> SYSUTCDATETIME()';
        END
        ELSE
            PRINT N'Upgrade_15: ' + @table + N'.' + @column + N' already UTC-based — no-op';
    END
    ELSE
        PRINT N'Upgrade_15: ' + @table + N'.' + @column + N' not present — skipped (older database)';

    FETCH NEXT FROM target_cursor INTO @table, @column;
END
CLOSE target_cursor;
DEALLOCATE target_cursor;
GO

PRINT 'Upgrade_15: technical timestamp defaults are now UTC (defaults-only — no stored values touched)';
