/* ============================================================
   Upgrade_18 - سياسة المنح الصارمة لاعتمادات تجاوز حد الائتمان
   ------------------------------------------------------------
   لماذا: قيد CK_CreditApprovals_Amounts (Upgrade_17) كان يفرض
   NewCreditAmount > 0 AND ExcessAmount > 0 AND
   ProjectedOutstanding = CurrentOutstanding + NewCreditAmount فقط —
   ولم يمنع صف اعتماد granted يمثل حدًّا صفريًّا (منعًا مطلقًا لا
   يُعتمد)، ولا حدًّا سالبًا (بيانات فاسدة)، ولا مشروعًا لا يتجاوز
   الحد أصلًا (لا تجاوز فيُعتمد)، ولا فائضًا محسوبًا خطأ.

   السياسة النهائية (Task 4): أي اعتماد granted جديد يجب أن يمثل
   تجاوزًا فعليًا لحدٍّ موجب:
   • CreditLimit > 0                    (الصفر منع مطلق لا اعتماد له؛
                                         السالب بيانات فاسدة مرفوضة)
   • NewCreditAmount > 0                (لا اعتماد لمبلغ غير موجب)
   • ProjectedOutstanding = CurrentOutstanding + NewCreditAmount
   • ProjectedOutstanding > CreditLimit (لا تجاوز = لا اعتماد)
   • ExcessAmount = ProjectedOutstanding - CreditLimit

   القيد الجديد CK_CreditApprovals_GrantPolicy مشروط بالحالة
   (Status = N'granted') عمدًا: صفوف consumed/burned التاريخية —
   ومنها صفوف حقيقية مُنحت تحت السياسة الأقدم الأضعف (مثل حد صفري
   استُهلك فعليًا قبل هذا الإصلاح) — سجل تدقيق لا يُمس ولا يُعدَّل
   ولا يُحذف؛ إضافة القيد بلا شرط كان سيمنعها أو يفرض تعديلها.
   صفوف المنح الجديدة تُدرج granted دائمًا فتخضع للسياسة لحظة
   الإدراج، والاستهلاك/الحرق لا يغيّر المبالغ — فكل صف مُنح بعد
   هذه الترقية صالح السياسة في كل حالاته اللاحقة.

   ترحيل السجلات المخالفة (بيانات قائمة):
   • كل صف granted مخالف للسياسة ينتقل granted -> burned — انتقال
     نفسه المستخدم لحرق اعتماد بيع فشل: «مُنح ولم يُستهلك» يبقى
     سجل تدقيق دائمًا ولا صلاحية له مستقبلًا. لا صف يُحذف أبدًا،
     ولا صف consumed يُمس (تاريخ استهلاك حقيقي).
   • لكل صف أُحرق سجل SystemLogs (Action = CreditApprovalBurned)
     يذكر معرف الاعتماد وسبب الترحيل — بنمط حرق الخدمة الحي.
   • التحديث + سجلات التدقيق + إنشاء القيد داخل معاملة واحدة واحدة —
     لا نافذة نصفة-ترحيل عند انقطاع التشغيل، وفشل أي خطوة يتراجع كل شيء.
   • تسلسل بين تطبيقات متوازية: قفل تطبيقي مسمّى (sp_getapplock Exclusive
     بمالك المعاملة) يُقسط عليه المنفّذ وحده قبل إعادة فحص القيد — من تنتهي
     مهلته يتخطى تخطيًّا غير فادح (الإقلاع/التشغيل اللاحق يعيد التطبيق).

   Idempotent: بالقيد الموجود لا يفعل شيئًا — إعادة التشغيل آمنة.
   التطبيق نفسه (EnsureSchemaUpgradesAsync عند الإقلاع) ينفذ نفس
   المنطق — فتشغيل السكربت على قاعدة عُقّلت من التطبيق يمر بلا
   أثر، والعكس صحيح. SaylDB.sql (التركيب الجديد) ينشئ الجدول بالقيد
   نفسه أيضًا — فالثلاثة مسارات متطابقة النتيجة.

   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_18_CreditApprovalGrantPolicy.sql

   التراجع (Rollback):
   ALTER TABLE dbo.CreditApprovals DROP CONSTRAINT CK_CreditApprovals_GrantPolicy;
   يعيد الإنفاذ إلى قيد Upgrade_17 الأضعف فقط. صفوف burned المُرحَّلة
   تبقى burned (سجل تدقيق صادق عن قرار السياسة وقتها) — استعادتها
   granted يدويًا مسؤولية المالك بعد مراجعة، لا خطوة سكربت آلية.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CreditApprovals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.SystemLogs', N'U') IS NOT NULL
BEGIN
    /* ── 0) قفل تطبيقي مسمّى: تسلسل الترقية بين تطبيقات متوازية ──
       الترقية نفسها تعمل عند إقلاع كل طرفية (EnsureSchemaUpgradesAsync) —
       قفل sp_getapplock Exclusive بمالك المعاملة (نمط clsPayrollService)
       يُسلسِل المنفّذين. من تنتهي مهلته يتخطى تخطيًّا غير فادح (المحاولة
       القادمة تعيد التطبيق — الترقية idempotent). */
    DECLARE @u18Lock int;
    BEGIN TRAN;
    EXEC @u18Lock = sys.sp_getapplock
         @Resource = N'Upgrade18GrantPolicy',
         @LockMode = 'Exclusive',
         @LockOwner = 'Transaction',
         @LockTimeout = 10000;
    IF @u18Lock >= 0
    BEGIN
        BEGIN TRY
            /* ── إعادة فحص حالة الترقية بعد القسط على القفل — لا قبله ── */
            IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                           WHERE name = N'CK_CreditApprovals_GrantPolicy'
                             AND parent_object_id = OBJECT_ID(N'dbo.CreditApprovals'))
            BEGIN
                /* ── 1) ترحيل صفوف granted المخالفة إلى burned (مع تدقيق) ── */
                DECLARE @violating TABLE (ApprovalId INT NOT NULL);

                INSERT INTO @violating (ApprovalId)
                SELECT ApprovalId
                FROM dbo.CreditApprovals WITH (UPDLOCK)
                WHERE Status = N'granted'
                  AND NOT (CreditLimit > 0
                           AND NewCreditAmount > 0
                           AND ProjectedOutstanding = CurrentOutstanding + NewCreditAmount
                           AND ProjectedOutstanding > CreditLimit
                           AND ExcessAmount = ProjectedOutstanding - CreditLimit);

                UPDATE a
                SET a.Status = N'burned'
                FROM dbo.CreditApprovals a
                JOIN @violating v ON v.ApprovalId = a.ApprovalId;

                INSERT INTO dbo.SystemLogs (UserId, Action, Details, CreatedAt)
                SELECT NULL, N'CreditApprovalBurned',
                       N'اعتماد ائتمان #' + CONVERT(NVARCHAR(20), v.ApprovalId)
                         + N' أُحرق بترحيل Upgrade_18: صف granted مخالف لسياسة المنح الصارمة'
                         + N' (حد ائتماني غير موجب أو مشروع لا يتجاوز الحد أو فائض محسوب خطأ)'
                         + N' — سجل تدقيق يبقى محفوظًا ولا صلاحية للاعتماد مستقبلًا.',
                       GETUTCDATE()
                FROM @violating v;

                /* ── 2) قيد سياسة المنح — مشروط بالحالة granted (انظر الرأس) ──
                   داخل المعاملة نفسها: الحرق والتدقيق والقيد يُالتزمان معًا —
                   فشل أي خطوة يتراجع الترقية كلها (لا حالة نصف مُرحّلة). */
                ALTER TABLE dbo.CreditApprovals ADD CONSTRAINT CK_CreditApprovals_GrantPolicy CHECK (
                    Status <> N'granted'
                    OR (CreditLimit > 0
                        AND NewCreditAmount > 0
                        AND ProjectedOutstanding = CurrentOutstanding + NewCreditAmount
                        AND ProjectedOutstanding > CreditLimit
                        AND ExcessAmount = ProjectedOutstanding - CreditLimit));

                DECLARE @burnedCount INT = (SELECT COUNT(*) FROM @violating);

                PRINT 'Upgrade_18: grant-policy constraint added'
                    + ' (legacy violating granted rows transitioned to burned: '
                    + CONVERT(NVARCHAR(20), @burnedCount) + ')';
            END
            ELSE
                PRINT 'Upgrade_18: CK_CreditApprovals_GrantPolicy already present — skipped';

            COMMIT TRAN;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRAN;
            THROW;
        END CATCH;
    END
    ELSE
    BEGIN
        /* مهلة القفل/ازدحام: تخطٍّ غير فادح — إقلاع/تشغيل لاحق يعيد التطبيق */
        ROLLBACK TRAN;
        PRINT 'Upgrade_18: another instance holds the upgrade lock — skipped (will be applied by the next run)';
    END
END
ELSE
    PRINT 'Upgrade_18: required tables not present — skipped (unexpected for a Sayl database)';
GO

PRINT 'Upgrade_18: credit approval grant-policy schema done';
