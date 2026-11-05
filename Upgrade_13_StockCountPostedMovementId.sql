/* ============================================================
   Upgrade_13 - عمود PostedMovementId في StockCounts (IDEMPOTENCY)
   ------------------------------------------------------------
   StockCount.PostedMovementId يربط كل سطر جرد بحركة التسوية
   (StockMovement) الناتجة عن ترحيله — عبر StockMovements.SourceId
   (SourceType = 'ManualAdjustment'، SourceId = CountId).

   الغاية: منع تطبيق تسوية الجرد نفسها مرتين بعد فشل جزئي:
   - كل حركة ترحيل تحمل SourceId = CountId (مفتاح idempotency دائم).
   - عند إعادة الترحيل بعد فشل جزئي: السطر الذي له حركة مرتبطة
   يُتخطى — مهما تكررت المحاولات أو أُعيد تشغيل التطبيق.
   - ملاحظة: سطر العجز قد يولّد عدة حركات (حركة لكل دفعة FEFO
   خُصمت) كلها بنفس SourceId — لذا لا يُنشأ فهرس فريد على
   SourceId (تعدد الحركات للسطر الواحد سلوك مشروع)، والضمان
   يتم عبر فحص «وجود حركة مرتبطة» + قفل الترحيل التطبيقي.

   Idempotent — لا يُغيّر شيئًا إن كان العمود موجودًا.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_13_StockCountPostedMovementId.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.StockCounts', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'PostedMovementId'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD PostedMovementId INT NULL;
        PRINT 'Upgrade_13: added StockCounts.PostedMovementId';
    END;

    -- فهرس على StockMovements.SourceId لتسريع فحص idempotency
    -- (مرشَّح على ManualAdjustment ذي SourceId — تسويات الجرد فقط)
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.StockMovements')
          AND name = N'IX_StockMovements_ManualAdjustmentSource'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX IX_StockMovements_ManualAdjustmentSource
            ON dbo.StockMovements(SourceId)
            WHERE SourceType = 'ManualAdjustment' AND SourceId IS NOT NULL;
        PRINT 'Upgrade_13: added IX_StockMovements_ManualAdjustmentSource';
    END;
END;
GO

PRINT 'Upgrade_13: StockCounts.PostedMovementId migration done';
