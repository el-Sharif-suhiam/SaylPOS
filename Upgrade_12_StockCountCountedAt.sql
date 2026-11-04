/* ============================================================
   Upgrade_12 - عمودا CountedAt / BookQuantityAtCount في StockCounts
   ------------------------------------------------------------
   StockCount: يُحفظ وقت العد الفعلي لكل سطر (CountedAt) والكمية
   الدفترية لحظة ذلك العد (BookQuantityAtCount). يسمح ذلك بترحيل
   الفروق مقابل الكمية الدفترية الحقيقية وقت العد — لا وقت بدء
   الجلسة — فيُحفظ أي حركات (بيع/استلام/تسوية) حصلت بعد العد.

   Idempotent — لا يُغيّر شيئًا إن كانت الأعمدة موجودة.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_12_StockCountCountedAt.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.StockCounts', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'CountedAt'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD CountedAt DATETIME2 NULL;
        PRINT 'Upgrade_12: added StockCounts.CountedAt';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.StockCounts') AND name = N'BookQuantityAtCount'
    )
    BEGIN
        ALTER TABLE dbo.StockCounts ADD BookQuantityAtCount INT NULL;
        PRINT 'Upgrade_12: added StockCounts.BookQuantityAtCount';
    END;
END;
GO

PRINT 'Upgrade_12: StockCounts.CountedAt / BookQuantityAtCount migration done';
