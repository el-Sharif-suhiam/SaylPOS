/* ============================================================
   Upgrade_21 - لقطات وحدة الإدخال على بنود الفواتير (InvoiceItems)
   ------------------------------------------------------------
   لماذا: البند التاريخي يحفظ Quantity بوحدة الأساس فقط + السعر — ولا
   يذكر الوحدة التي كتبها المستخدم فعليًا («2 كرتون»). Product.Unit
   قابل للتعديل، فالاعتماد عليه في العرض يغيّر معنى الفواتير القديمة
   صامتًا. الحماية من اللقطات (نفس سياسة ItemPrice/ItemCost —
   VULN-14، و StockCounts.ProductName): كل بند يجمّد وحدة إدخاله.

   الأعمدة الأربعة (كلها NULL):
   - UnitName         : الوحدة كما أُدخلت ('كرتون'). NULL = صف إرثي/وحدة أساس
   - UnitFactorToBase : عامل التحويل لحظة الحركة
   - EnteredQuantity  : الكمية كما أُدخلت في وحدة الإدخال
   - ProductUnitId    : مؤشر اختياري للتقارير على الصفوف الجديدة —
                        ليس حمولة حرجة؛ العرض يقرأ اللقطة النصية أولًا

   الترحيل: لا يوجد — الصفوف القائمة تبقى NULL ولا تُختلق لها
   وحدة ولا عامل (تُعرض بوحدة Product.Unit الحالية بعامل 1 ضمني).

   حذف الوحدة عبر المفتاح الأجنبي: NO ACTION (Restrict).
   Idempotent - إعادة تشغيله بلا ضرر.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_21_TransactionUnitSnapshots.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.InvoiceItems', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'UnitName'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD UnitName NVARCHAR(20) NULL;
        PRINT 'Upgrade_21: added InvoiceItems.UnitName';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'UnitFactorToBase'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD UnitFactorToBase DECIMAL(18,6) NULL;
        PRINT 'Upgrade_21: added InvoiceItems.UnitFactorToBase';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'EnteredQuantity'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD EnteredQuantity DECIMAL(10,3) NULL;
        PRINT 'Upgrade_21: added InvoiceItems.EnteredQuantity';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems') AND name = N'ProductUnitId'
    )
    BEGIN
        ALTER TABLE dbo.InvoiceItems ADD ProductUnitId INT NULL
            CONSTRAINT FK_ProductUnits_ProductUnitId
            REFERENCES dbo.ProductUnits(ProductUnitId);
        PRINT 'Upgrade_21: added InvoiceItems.ProductUnitId';
    END;

    -- فهرس غير فريد مُرشَّح: الانضمام الاختياري للصفوف الجديدة فقط
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.InvoiceItems')
          AND name = N'IX_InvoiceItems_ProductUnitId'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX IX_InvoiceItems_ProductUnitId
            ON dbo.InvoiceItems(ProductUnitId)
            WHERE ProductUnitId IS NOT NULL;
        PRINT 'Upgrade_21: added IX_InvoiceItems_ProductUnitId';
    END;
END;
GO

PRINT 'Upgrade_21: InvoiceItems unit snapshots migration done';
