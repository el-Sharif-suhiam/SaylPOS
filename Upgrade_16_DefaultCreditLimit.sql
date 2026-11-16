/* ============================================================
   Upgrade_16 - حد الائتمان الافتراضي للعملاء (ترحيل لمرة واحدة)
   ------------------------------------------------------------
   لماذا: كان كل عميل جديد يُنشأ بحد ائتمان 0 صراحةً (hardcode في
   clsPartyService) وافتراضية العمود 0 — أي عميل تجريبي/حقيقي
   لم يضبط له المالك حدًّا يسقط في «صفر لم يُقرَّر» لا «صفر قرارًا».
   الآن: حد الائتمان الافتراضي إعداد مدير (مفتاح DefaultCreditLimit
   في جدول Settings، افتراضيته 1000 بعملة المتجر)، والعملاء الجدد
   يأخذونه عند الإنشاء.

   الترحيل (بيانات العملاء القائمة):
   • CreditLimit = 0 أو NULL  ->  1000 (القيمة الافتراضية المعتمدة).
     NULL يعامل كصفر عمدًا: كود التطبيق التاريخي كان يكتب 0 صراحةً
     في كل إنشاء عميل وافتراضية العمود (0)، فلا وجود لعملاء جاءت
     قيمتهم NULL من مسار مقصود — NULL هنا «صفر غير مضبوط» يُرحَّل
     مثل الصفر. لا عميل يُتجاهل بصمت.
   • كل حد غير صفري يُحفظ كما هو — حد العميل الفعلي أثمن من أي
     افتراض، والعملاء ذوو حدود مضبوطة ليسوا هدف هذا الترحيل.
   • لا مساس بصفوف Parties أو الفواتير أو المدفوعات أو المرتجعات
     أو أي تاريخ معاملات — عمود واحد في Customers فقط.

   الحارس (Marker): صف في Settings بمفتاح CreditLimitMigrationV1.
   تُدرج العلامة مع التحديث داخل معاملة واحدة؛ بعد نجاحها لا يُعاد
   التنفيذ أبدًا — عميل ضبطه المالك على 0 بعد الترحيل (منع الآجل
   عليه) لن يُرفع إلى 1000 عند إقلاع أو تشغيل لاحق للسكربت.
   تطبيق التطبيق نفسه (EnsureSchemaUpgradesAsync عند الإقلاع)
   ينفذ نفس المنطق المحروس بالعلامة نفسها — فتشغيل هذا السكربت
   على قاعدة عُقّلت من التطبيق يمر بلا أثر، والعكس صحيح.

   Idempotent: بالعلامة الموجودة لا يفعل شيئًا — إعادة التشغيل آمنة.

   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_16_DefaultCreditLimit.sql

   التراجع (Rollback): لا يوجد تراجع آلي عمدًا — القيم المُرحَّلة
   (1000) غير قابلة للتفريق عن حد وضعته يدويًا، وأي سكربت «عكسي»
   سيصفّر حدودًا حقيقية. إن أردت العودة إلى سلوك ما قبل الترحيل
   على قاعدة معروفة: (1) احذف صف العلامة
   DELETE FROM Settings WHERE SettingKey = N'CreditLimitMigrationV1';
   (2) حدّث العملاء كما كانوا بنسخة احتياطية سابقة (مثال أدناه)
   UPDATE c SET c.CreditLimit = b.CreditLimit
   FROM dbo.Customers c
   JOIN <نسختك_الاحتياطية>.dbo.Customers b ON b.PartyId = c.PartyId;
   لا بيانات تُحذف في أي حال — الترحيل يعدل عمودًا واحدًا فقط.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Settings', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Settings WHERE SettingKey = N'CreditLimitMigrationV1')
    BEGIN
        BEGIN TRAN;

        UPDATE dbo.Customers
        SET CreditLimit = 1000
        WHERE CreditLimit IS NULL OR CreditLimit = 0;

        INSERT INTO dbo.Settings (SettingKey, SettingValue, UpdatedAt)
        VALUES (N'CreditLimitMigrationV1', N'1', SYSUTCDATETIME());

        COMMIT TRAN;

        PRINT 'Upgrade_16: backfill applied (Customers.CreditLimit 0/NULL -> 1000; marker CreditLimitMigrationV1 set)';
    END
    ELSE
        PRINT 'Upgrade_16: marker CreditLimitMigrationV1 already present — no-op (deliberate zeros preserved)';
END
ELSE
    PRINT 'Upgrade_16: Customers or Settings table not present — skipped (unexpected for a Sayl database)';
GO

PRINT 'Upgrade_16: default credit limit migration done';
