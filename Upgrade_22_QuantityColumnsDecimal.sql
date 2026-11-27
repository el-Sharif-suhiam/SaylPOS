/* ============================================================
   Upgrade_22 - الكميات الكسرية: INT -> DECIMAL(10,3) لكل أعمدة الكميات
   ------------------------------------------------------------
   لماذا: المنتجات بالوزن/الحجم (كغم، لتر) تحتاج كميات كسرية
   (2.5 كغم، 0.125 لتر). INT يجبر البيع والشراء والجرد على أعداد
   صحيحة. DECIMAL(10,3) فقط لا float/double: الثابت المحاسبي
   «مجموع الدفعات = المخزون المتاح» يجب أن يبقى تامًا بلا خطأ
   عائم (نفس فلسفة النقود DECIMAL(10,2) — B16).

   الأعمدة (النوع الجديد DECIMAL(10,3) — دقة 10 = حتى 9,999,999.999):
   - Products.ReorderLevel            NULL DEFAULT 0   (كان INT)
   - Batches.BatchQuantity            NOT NULL         (كان INT)
   - Inventory.QuantityAvailable      NULL DEFAULT 0   (كان INT)
   - Inventory.QuantityReserved       NULL DEFAULT 0   (كان INT)
   - InvoiceItems.Quantity            NOT NULL DEFAULT 1 (كان INT)
   - StockReservations.Quantity       NOT NULL DEFAULT 1 (كان INT) + CHECK > 0
   - StockMovements.Quantity          NOT NULL         (كان INT)
   - StockCounts.BookQuantity         NOT NULL         (كان INT)
   - StockCounts.CountedQuantity      NULL             (كان INT)
   - StockCounts.BookQuantityAtCount  NULL             (كان INT)

   الترحيل: تحويل بلا فقد — كل قيمة INT تُمثَّل حرفيًا في
   DECIMAL(10,3) (حد INT أقل بكثير من 10 أرقام). القيم القائمة
   (5) تبقى (5.000) دلاليًا نفس القيمة. الفهارس على الأعمدة
   المُحوَّلة تُعاد بناؤها تلقائيًا مع ALTER COLUMN.

   StockReservations.Quantity: القيد CK_StockReservations_Quantity
   يُسقَط قبل التحويل ويُعاد إنشاؤه بعده (ALTER COLUMN لا يجري مع
   CHECK مرتبط بالعمود القديم النوع) — الدلالة نفسها (Quantity > 0).

   الترتيب: يجب أن تُنشر هذه الترقية قبل أي إصدار برنامج يكتب
   كميات كسرية (باراميترات decimal على أعمدة INT كانت تُقرَّب
   صامتة) — قاعدة جديدة قبل التنفيذي القديم آمنة (int يقيم في
   decimal)، والعكس غير آمن.
   Idempotent - إعادة تشغيله بلا ضرر.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_22_QuantityColumnsDecimal.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ═══ تحويل عمود واحد يحافظًا على Nullability والافتراضي ═══
-- ALTER COLUMN لا يمس الافتراضي القائم (DEFAULT constraint باقٍ)،
-- والفحص بالحرف: يُحول فقط إن كان العمود INT فعلًا.

IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Products') AND name = N'ReorderLevel'
                 AND system_type_id = TYPE_ID(N'int'))
BEGIN
    ALTER TABLE dbo.Products ALTER COLUMN ReorderLevel DECIMAL(10,3) NULL;
    PRINT 'Upgrade_22: Products.ReorderLevel -> DECIMAL(10,3) NULL';
END;
GO

IF OBJECT_ID(N'dbo.Batches', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'BatchQuantity'
                 AND system_type_id = TYPE_ID(N'int'))
BEGIN
    ALTER TABLE dbo.Batches ALTER COLUMN BatchQuantity DECIMAL(10,3) NOT NULL;
    PRINT 'Upgrade_22: Batches.BatchQuantity -> DECIMAL(10,3) NOT NULL';
END;
GO

IF OBJECT_ID(N'dbo.Inventory', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Inventory') AND name = N'QuantityAvailable'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.Inventory ALTER COLUMN QuantityAvailable DECIMAL(10,3) NULL;
        PRINT 'Upgrade_22: Inventory.QuantityAvailable -> DECIMAL(10,3) NULL';
    END;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Inventory') AND name = N'QuantityReserved'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.Inventory ALTER COLUMN QuantityReserved DECIMAL(10,3) NULL;
        PRINT 'Upgrade_22: Inventory.QuantityReserved -> DECIMAL(10,3) NULL';
    END;
END;
GO

IF OBJECT_ID(N'dbo.InvoiceItems', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'Quantity'
                 AND system_type_id = TYPE_ID(N'int'))
BEGIN
    ALTER TABLE dbo.InvoiceItems ALTER COLUMN Quantity DECIMAL(10,3) NOT NULL;
    PRINT 'Upgrade_22: InvoiceItems.Quantity -> DECIMAL(10,3) NOT NULL';
END;
GO

-- StockReservations: أسيط القيد أولًا — ALTER COLUMN بلا CHECK على العمود
IF OBJECT_ID(N'dbo.StockReservations', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE object_id = OBJECT_ID(N'dbo.CK_StockReservations_Quantity', N'C'))
    BEGIN
        ALTER TABLE dbo.StockReservations DROP CONSTRAINT CK_StockReservations_Quantity;
        PRINT 'Upgrade_22: dropped CK_StockReservations_Quantity (will recreate)';
    END;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.StockReservations') AND name = N'Quantity'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.StockReservations ALTER COLUMN Quantity DECIMAL(10,3) NOT NULL;
        PRINT 'Upgrade_22: StockReservations.Quantity -> DECIMAL(10,3) NOT NULL';
    END;

    -- القيد نفسه: الكمية موجبة حصرًا (الحجز صفر أو سالب = فساد بيانات)
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE object_id = OBJECT_ID(N'dbo.CK_StockReservations_Quantity', N'C'))
    BEGIN
        ALTER TABLE dbo.StockReservations ADD CONSTRAINT CK_StockReservations_Quantity CHECK (Quantity > 0);
        PRINT 'Upgrade_22: recreated CK_StockReservations_Quantity CHECK (Quantity > 0)';
    END;
END;
GO

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.StockMovements') AND name = N'Quantity'
                 AND system_type_id = TYPE_ID(N'int'))
BEGIN
    ALTER TABLE dbo.StockMovements ALTER COLUMN Quantity DECIMAL(10,3) NOT NULL;
    PRINT 'Upgrade_22: StockMovements.Quantity -> DECIMAL(10,3) NOT NULL';
END;
GO

IF OBJECT_ID(N'dbo.StockCounts', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'BookQuantity'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.StockCounts ALTER COLUMN BookQuantity DECIMAL(10,3) NOT NULL;
        PRINT 'Upgrade_22: StockCounts.BookQuantity -> DECIMAL(10,3) NOT NULL';
    END;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'CountedQuantity'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.StockCounts ALTER COLUMN CountedQuantity DECIMAL(10,3) NULL;
        PRINT 'Upgrade_22: StockCounts.CountedQuantity -> DECIMAL(10,3) NULL';
    END;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'BookQuantityAtCount'
                 AND system_type_id = TYPE_ID(N'int'))
    BEGIN
        ALTER TABLE dbo.StockCounts ALTER COLUMN BookQuantityAtCount DECIMAL(10,3) NULL;
        PRINT 'Upgrade_22: StockCounts.BookQuantityAtCount -> DECIMAL(10,3) NULL';
    END;
END;
GO

PRINT 'Upgrade_22: quantity columns INT -> DECIMAL(10,3) migration done';
