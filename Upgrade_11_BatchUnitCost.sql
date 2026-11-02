/* ============================================================
   Upgrade_11 - تكلفة الدفعة التاريخية (COGS من الدفعة لا من المنتج)
   ------------------------------------------------------------
   لماذا: كان COGS يُسجل من Product.CostPrice وقت البيع — سعُر آخر
   شراء لا تكلفة الدفعة المستهلكة فعليًا. دفعتان بتكلفة 10 و15 والبيع
   يستهلك الأولى كانت تسجل 15 على الفاتورة (ربح وهمي/خسارة وهمية).

   الإصلاح: عمود Batches.UnitCost — تكلفة الاقتناء للوحدة (بعد تخصيص
   خصم الشراء على البنود). بيع/مرتجع يقرأ التكلفة من الدفعة المستهلكة.
   Product.CostPrice يبقى سعر آخر شراء (عرض/تقدير فقط).

   الترحيل: الدفعات القائمة تُسند إلى تكلفة بند شرائها الأحدث
   (ItemCost على فاتورة شراء مكتملة) — لا تُحذف ولا تُغيَّر أي بيانات.
   Idempotent - إعادة تشغيله بلا ضرر.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

/* يُنفذ على قاعدة البيانات الحالية (أي اسم) — الحارس بنيوي: وجود dbo.Batches.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_11_BatchUnitCost.sql */
IF OBJECT_ID(N'dbo.Batches', N'U') IS NOT NULL
BEGIN
    /* 1) العمود إن غاب */
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.Batches') AND name = N'UnitCost'
    )
    BEGIN
        ALTER TABLE dbo.Batches ADD UnitCost DECIMAL(10,2) NULL;
        PRINT 'Upgrade_11: added Batches.UnitCost';
    END;
END;
GO

/* دفعة منفصلة: ALTER في نفس الدفعة لا يُرى وقت تجميعها — العمود يجب
   أن يكون موجودًا قبل تنفيذ الترحيل. الترحيل نفسه idempotent: يملأ
   الدفعات بلا تكلفة فقط ولا يمس ما اكتمل ترحيله. */
IF OBJECT_ID(N'dbo.Batches', N'U') IS NOT NULL
BEGIN

        /* 2) ترحيل التكلفة التاريخية: الدفعة بلا تكلفة تأخذ تكلفة أحدث بند
              شراء مرتبط بها (ItemCost كما سُجل وقت الشراء). الدفعات ذات
              UnitCost موجودة لا تُمس — بيانات تاريخية أثمن من اجتهاد. */
    UPDATE b
    SET b.UnitCost = pi.UnitCost
    FROM dbo.Batches b
    CROSS APPLY (
        SELECT TOP (1) ii.ItemCost AS UnitCost
        FROM dbo.InvoiceItems ii
        JOIN dbo.Invoices i ON i.InvoiceId = ii.InvoiceId
        WHERE ii.BatchId = b.BatchId
          AND ii.ItemCost IS NOT NULL
          AND i.Type = N'purchase'
          AND i.Status = N'completed'
        ORDER BY i.CreatedAt DESC, ii.InvoiceItemId DESC
    ) pi
    WHERE b.UnitCost IS NULL
      AND pi.UnitCost IS NOT NULL;

    PRINT 'Upgrade_11: Batches.UnitCost migration done';
END;
GO
