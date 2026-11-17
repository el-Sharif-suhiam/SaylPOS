/* ============================================================
   Upgrade_17 - اعتمادات المدير لتجاوز حد الائتمان (جدول جديد)
   ------------------------------------------------------------
   لماذا: التنفيذ المركزي لحد الائتمان (Task 2) يرفض البيع الآجل
   المتجاوز للحد بنتيجة منظمة «يتطلب موافقة المدير» دون أي أثر.
   هذه الترقية تضيف السجل الدوري الذي يُنفَّذ عنده التفويض: صف
   CreditApprovals لكل اعتماد، يستهلك مرة واحدة بالضبط داخل
   معاملة البيع (انتقال CAS granted -> consumed) — فلا يمكن
   إعادة استخدام اعتماد لفاتورة ثانية ولا تزوير اعتماد من الواجهة.

   الجدول:
   • لقطة موقف العميل لحظة الاعتماد: CurrentOutstanding / CreditLimit /
     NewCreditAmount / ProjectedOutstanding / ExcessAmount (ريال وفّات
     لا كسور — DECIMAL(10,2) مطابق لدفتر Customers.Balance).
   • ManagerUserId: المدير المعتمِد (حساب تحقق على الخادم بكلمة
     المرور BCrypt) — لا هوية تُصدَّق من الواجهة.
   • Reason: سبب إلزامي (2..500 حرفًا بعد التشذيب — اتفاقية
     clsValidation.ValidateString).
   • Status: granted / consumed / burned — قيد CHECK.
     burned = مُنح ثم فشلت عملية البيع بعده؛ يبقى سجل تدقيق
     (اعتماد بلا بيع) ولا يصلح لعملية مستقبلية.
   • ConsumedByInvoiceId: فاتورة الاستهلاك — فهرس فريد مُرشَّح
     يمنع ربط صفين بنفس الفاتورة (حاجز محرك إضافي فوق CAS).
   • ApprovedAtUtc/ConsumedAtUtc: UTC دائمًا (اتفاقية Upgrade_15).

   لا بيانات قائمة تُرحَّل ولا يُمس أي صف موجود — إنشاء جدول
   وفهارس وقيود فقط. CreditLimit دائمًا لم يتغير هنا أصلًا:
   الاعتماد تفويض لعملية واحدة لا تعديل حد.

   الحارس: OBJECT_ID — الجدول يُنشأ إن غاب فقط، وإعادة التشغيل
   بلا أثر. التطبيق نفسه (EnsureSchemaUpgradesAsync عند الإقلاع)
   ينفذ نفس المنطق — فتشغيل السكربت على قاعدة عُقّلت من التطبيق
   يمر بلا أثر، والعكس صحيح. SaylDB.sql (التركيب الجديد) ينشئ
   الجدول مع المخطط أيضًا — فالثلاثة مسارات متطابقة النتيجة.

   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_17_CreditApprovals.sql

   التراجع (Rollback): DROP TABLE dbo.CreditApprovals — الجدول
   سجل تدقيق صرف لا يشير إليه أي مسار بيع إلا استهلاكًا؛ إسقاطه
   يعيد النظام إلى سلوك ما قبل الترقية (رفض البيع المتجاوز بلا
   مسار اعتماد). لا بيانات عملاء أو فواتير تُمس.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

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
    /* فهرس فريد مُرشَّح: فاتورة واحدة لا يمكن أن تُنسب لاعتمادين —
       حاجز محرك إضافي فوق انتقال CAS داخل معاملة البيع */
    CREATE UNIQUE INDEX UQ_CreditApprovals_ConsumedByInvoice
        ON dbo.CreditApprovals(ConsumedByInvoiceId) WHERE ConsumedByInvoiceId IS NOT NULL;

    PRINT 'Upgrade_17: CreditApprovals table created';
END
ELSE IF OBJECT_ID(N'dbo.CreditApprovals', N'U') IS NOT NULL
BEGIN
    /* قاعدة عُقّلت من التطبيق (أو تركيب جديد بجدول موجود) —
       الفهارس المُرشَّحة تُضمن هنا أيضًا إن غابت عن قواعد أقدم.
       Idempotent: تُنشأ إن غابت فقط. */
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.CreditApprovals')
                     AND name = N'UQ_CreditApprovals_ConsumedByInvoice')
        EXEC(N'CREATE UNIQUE INDEX UQ_CreditApprovals_ConsumedByInvoice
               ON dbo.CreditApprovals(ConsumedByInvoiceId) WHERE ConsumedByInvoiceId IS NOT NULL');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.CreditApprovals')
                     AND name = N'IX_CreditApprovals_Status')
        EXEC(N'CREATE INDEX IX_CreditApprovals_Status ON dbo.CreditApprovals(Status)');

    PRINT 'Upgrade_17: CreditApprovals already present — indexes ensured';
END
ELSE
    PRINT 'Upgrade_17: required tables not present — skipped (unexpected for a Sayl database)';
GO

PRINT 'Upgrade_17: credit approvals schema done';
