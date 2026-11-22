/* ============================================================
   Upgrade_19 - الترقيم التجاري السنوي للفواتير INV-YYYY-NNNNNN
   ------------------------------------------------------------
   لماذا: الفواتير تُعرَّف بـ InvoiceId فقط — رقم متسلسل داخلي
   بلا معنى تجاري ولا يفصح عن السنة، ولا يُشارك بين الأنواع
   الأربعة (sale/purchase/customer_return/supplier_return) ولو
   بني على الهوية. المطلوب رقم تجاري سنوي واحد مشترك: INV-<سنة
   برنامجية UTC+3>-<تسلسل من ست خانات على الأقل> فريد عبر كل
   أنواع الفواتير داخل السنة.

   السياسة:
   • العمود InvoiceNumber NVARCHAR(32) — يُضاف nullable أولًا فقط
     كي يُرحِّل القديمَ (لا يمكن شدّ NOT NULL قبل وجود رقم لكل
     صف)، ثم بعد اكتمال الترحيل يُشدَّد NOT NULL: لا مسار إنتاج
     يُدرج فاتورة بلا رقم — المولّد (Task 2/3) يسنده داخل معاملة
     كل عملية، وNOT NULL حاجزٌ هيكلي يمنع أي إدراج بلا رقم.
   • فهرس فريد UQ_Invoices_InvoiceNumber — الصيغة النهائية عادية
     بلا مرشِّح: يمنع تكرار الرقم عبر كل صفوف الجدول (حاجز محرك
     فوق أي فحص-ثم-إسناد). أثناء الترحيل فقط يُبنى مُرشَّحًا
     (NULL حرّ كي تُرقَّم الصفوف)، ثم يُهدم ويُعاد عاديًّا بعد
     شدّ NOT NULL.
   • جدول InvoiceNumberCounters — عدّاد لكل سنة برنامجية
     (UTC+3، اتفاقية ProgramClock) يقرؤه المولّد ويجعله فوق
     أعلى لاحقة صادرة (لا يُخفَّض أبدًا).
   • تسلسل مشترك لكل السنوات الأربعة من الأنواع بلا استثناء —
     فالرقم فريد عبر الفواتير كلها في سنتها لا عبر نوعٍ واحد.

   الترحيل (بيانات قائمة):
   • الصفوف المُرقَّمة سلفًا (InvoiceNumber غير NULL بأي تنسيق)
     سجل تاريخي لا يُمس ولا يُعاد إسناده أبدًا — إعادة إسناد
     رقم محفوظ فسادٌ وتضليل مستندات.
   • الصفوف NULL فقط تُرقَّم. السنة لكل صف =
     YEAR(DATEADD(HOUR, 3, CreatedAt)) — اتفاقية ProgramClock
     (UtcOffsetHours=3) حيث «سنة البرنامج» لا سنة الخادم. صف
     بلا CreatedAt (بيانات قديمة فاسدة لكنها حقيقية لا يُمس
     غير رقمها) يُرقَّم في السنة البرمجية الحالية
     YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME())) — مُوثَّق هنا
     عمدًا، فإفلاته بلا رقم يهزم غرض الرقم التجاري، وترحيله
     لسنة زمن CreatedAt مستحيل (لا يوجد زمن).
   • الترتيب داخل السنة حتمي: الصفوف بلا CreatedAt أخيرًا ثم
     CreatedAt ثم InvoiceId — ROW_NUMBER يُثبِّت من يأخذ
     التسلسل الأول بغضّ النظر عن خطة التنفيذ، فإعادة التشغيل
     لا تعيد الخلط.
   • القاعدة العدّادية (حاسم ضد التصادم مع الأرقام المحفوظة):
     base لكل سنة = MAX(0,
        counter.LastIssuedNumber إن وُجد صف العدّاد،
        أعلى لاحقة رقمية مُحلَّلة من الأرقام المحفوظة ذات
        التنسيق INV-<digits>-<digits> في تلك السنة)؛
     والتسلسل = base + ROW_NUMBER(). ROW_NUMBER من 1 الصرف
     كان يصطدم بالأرقام المحفوظة في إعادة تشغيلٍ بعد ترقيةٍ
     جزئية — ممنوع. اللواحق بتنسيقات أخرى تُتجاهل في التحليل
     لكن قيمها تبقى محفوظة كما هي.
   • التنسيق N'INV-' + <سنة> + N'-' + FORMAT(seq, '000000') —
     FORMAT يزيد الحشو ولا يقص أبدًا (RIGHT('000000'+.., 6)
     كان يقص التسلسل الأطول من ست خانات — خطأ مرفوض). أقصى
     طول: 'INV-YYYY-' (9) + 19 خانة (BIGINT الأقصى) = 28 ≤ 32.
   • upsert العدّاد لكل سنة: LastIssuedNumber = CASE WHEN
     الموجود > المحسوب THEN الموجود ELSE المحسوب END — لا
     يُخفَّض العدّاد أبدًا، وإعادة التشغيل بعد تراجعٍ أو
     ترقيةٍ جزئية لا تعيد إسناد ما سبق.

   المعاملة والتزامن: كل شيء (عمود + فهرس + جدول عدّاد +
   ترحيل + عدّادات) في معاملة واحدة — لا نافذة نصفة-ترحيل عند
   انقطاع التشغيل، وفشل أي خطوة يتراجع كل شيء. قفل تطبيقي
   مسمّى (sp_getapplock Exclusive بمالك المعاملة — نمط
   Upgrade_18) يُسلسِل المنفّذين المتوازيين (الترقية تعمل عند
   إقلاع كل طرفية) قبل إعادة فحص الحالة — من تنتهي مهلته
   يتخطى تخطيًّا غير فادح والإقلاع اللاحق يعيد التطبيق.

   Idempotent: الصفوف المُرقَّمة والعمود والفهرس والجدول
   الموجودة لا تُلمس — إعادة التشغيل آمنة وتُرقِّم ما زال
   NULL فقط (تزايدية). التطبيق نفسه (EnsureSchemaUpgradesAsync
   عند الإقلاع) ينفذ نفس المنطق — فالمسارَان متطابقا النتيجة،
   وSaylDB.sql (التركيب الجديد) ينشئ العمود والفهرس والجدول
   نفسه — فالمسارات الثلاثة متطابقة النتيجة. الحالة النهائية
   (عمود NOT NULL + فهرس فريد عادي + لا صف NULL/فارغ) تُكتشف
   في الحارس فيتخطى التشغيل كله — تركيبٌ جديد من SaylDB.sql
   النهائية يولد بالحالة النهائية نفسها فلا يفعل الترقية شيئًا.
   رفضٌ صريح: إن بقي بعد الترحيل صف بلا رقم (NULL أو فارغ —
   الفارغ يسمم الفهرس الفريد العادي) فTHROW يتراجع عن الترقية
   كلها: لمسُ بيانات تافهة قرار مالك لا سكربت تلقائي.

   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_19_AnnualInvoiceNumbering.sql

   التراجع (Rollback):
   DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;
   DROP TABLE dbo.InvoiceNumberCounters;
   ALTER TABLE dbo.Invoices DROP COLUMN InvoiceNumber;
   يعيد القاعدة إلى ما قبل الترقية. الأرقام المُرحَّلة تُسقط
   مع العمود — إعادة تشغيل الترقية بعدها تُسندها من جديد
   (وإن خُلّطت عمليات بيعَ حيّةً بينهما فقد تتغير أرقام صفوف
   عادت إلى NULL) — لذا لا يُنصح بالتراجع بعد بدء الترقيم الحي.
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.Invoices', N'U') IS NOT NULL
BEGIN
    /* ── قفل تطبيقي مسمّى: تسلسل الترقية بين تطبيقات متوازية ──
       الترقية نفسها تعمل عند إقلاع كل طرفية — قفل sp_getapplock
       Exclusive بمالك المعاملة (نمط Upgrade_18) يُسلسِل المنفّذين
       قبل إعادة فحص الحالة. من تنتهي مهلته يتخطى تخطيًّا غير
       فادح (المحاولة القادمة تعيد التطبيق — الترقية idempotent). */
    DECLARE @u19Lock int;
    BEGIN TRAN;
    EXEC @u19Lock = sys.sp_getapplock
         @Resource = N'Upgrade19InvoiceNumbering',
         @LockMode = 'Exclusive',
         @LockOwner = 'Transaction',
         @LockTimeout = 10000;
    IF @u19Lock >= 0
    BEGIN
        BEGIN TRY
            /* ── إعادة فحص حالة الترقية بعد القسط على القفل — لا قبله ──
               الحراس على مرحلتين عمدًا: فحص وجود الصفوف NULL يُكتب بعمود
               InvoiceNumber — لو جمعناه مع فحص وجود العمود في شرط واحد
               لفشل الترجمة على قاعدة قديمة بلا عمود (Invalid column name —
               ترجمة العبارة لحظة تنفيذها لا ترحم غياب العمود قبل ALTER)،
               ولذا لا يُتَّهَم فحص الصفوف إلا بعد ثبوت وجود العمود. */
            DECLARE @u19Todo bit = 0;
            IF OBJECT_ID(N'dbo.InvoiceNumberCounters', N'U') IS NULL
               OR NOT EXISTS (SELECT 1 FROM sys.columns
                              WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                                AND name = N'InvoiceNumber')
               OR EXISTS (SELECT 1 FROM sys.columns
                          WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                            AND name = N'InvoiceNumber' AND is_nullable = 1)
               OR NOT EXISTS (SELECT 1 FROM sys.indexes
                              WHERE object_id = OBJECT_ID(N'dbo.Invoices')
                                AND name = N'UQ_Invoices_InvoiceNumber'
                                AND is_unique = 1 AND has_filter = 0)
                SET @u19Todo = 1;
            ELSE
            BEGIN
                /* فحص الصفوف NULL/فارغ عبر sp_executesql وجوبي: مرجعٌ ثابت
                   للعمود في هذا الفرع كان سيقتل ترجمة الدفعة كلها على قاعدة
                   قديمة بلا العمود (Msg 207 قبل تنفيذ شيء — مُثبَت تجريبيًا)،
                   وهو لا يُنفَّذ هنا إلا بعد ثبوت وجود العمود، فالترجمة
                   اللحظية للدفعة الفرعية آمنة. */
                DECLARE @u19Nulls int = 0;
                EXEC sp_executesql
                     N'SELECT @u19Nulls = COUNT(*) FROM dbo.Invoices
                       WHERE InvoiceNumber IS NULL OR InvoiceNumber = N''''',
                     N'@u19Nulls int OUTPUT', @u19Nulls = @u19Nulls OUTPUT;
                IF @u19Nulls > 0
                    SET @u19Todo = 1;
            END;

            IF @u19Todo = 1
            BEGIN
                /* ── EXEC وجوبي: أي مرجع ثابت للعمود InvoiceNumber (حتى في
                   فروع IF/ELSE غير المنفَّذة) يُترجم مع الدفعة كلها لحظة
                   بدئها — قبل تنفيذ ALTER إضافة العمود على قاعدة قديمة —
                   فيفشل بـInvalid column name ولا يُنفَّذ شيء (مُثبَت
                   تجريبيًا). الدفعة الفرعية لـEXEC تُترجم لحظة تنفيذها
                   حين يكون العمود موجودًا أو أُضيف للتو في بدايتها. ── */
                EXEC(N'
                    SET NOCOUNT ON;

                    /* ── 1) العمود — إن غاب فقط (nullable أولًا: الترحيل يحتاجه
                       كي يملأ كل الصفوف قبل شدّ NOT NULL) ── */
                    IF NOT EXISTS (SELECT 1 FROM sys.columns
                                   WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                     AND name = N''InvoiceNumber'')
                        ALTER TABLE dbo.Invoices ADD InvoiceNumber NVARCHAR(32) NULL;
                ');

                /* ── الدفعة الفرعية الثانية: تُترجم لحظة تنفيذها — بعد أن
                   أضاف الأخيرةُ العمودَ (أو وُجد سلفًا) — فمراجع العمود
                   فيها كلها آمنة عند الترجمة. ── */
                EXEC(N'
                    SET NOCOUNT ON;

                    /* ── 2) الفهرس الفريد المُرشَّح — إن غاب فقط (حاجز محرك أثناء
                       الترحيل، يُستبدل بعاديٍّ في الخطوة 7). صفوف القاعدة القديمة
                       قد تحمل أرقامًا مكررة بتنسيق INV-<digits>-<digits> (بيانات
                       فاسدة قائمة) — إن وُجدت يفشل الإنشاء بـTHROW ويتراجع كل
                       شيء: لمسها قرار مالك لا سكربت تلقائي. NULL حرّ أثناء
                       الترحيل فقط. ── */
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                   WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                     AND name = N''UQ_Invoices_InvoiceNumber'')
                        CREATE UNIQUE INDEX UQ_Invoices_InvoiceNumber
                            ON dbo.Invoices(InvoiceNumber) WHERE InvoiceNumber IS NOT NULL;

                    /* ── 3) جدول العدّاد — إن غاب فقط ── */
                    IF OBJECT_ID(N''dbo.InvoiceNumberCounters'', N''U'') IS NULL
                    BEGIN
                        CREATE TABLE dbo.InvoiceNumberCounters
                        (
                            NumberYear INT NOT NULL
                                CONSTRAINT PK_InvoiceNumberCounters PRIMARY KEY,
                            LastIssuedNumber BIGINT NOT NULL
                                CONSTRAINT DF_InvoiceNumberCounters_LastIssuedNumber DEFAULT (0),
                            LastUpdatedAtUtc DATETIME2 NOT NULL
                                CONSTRAINT DF_InvoiceNumberCounters_LastUpdatedAtUtc DEFAULT (SYSUTCDATETIME())
                        );
                    END;

                    /* ── 4) الترحيل — الصفوف NULL فقط (تزايدية: المُرقَّم لا يُعاد إسناده).
                       السنة: YEAR(DATEADD(HOUR,3,CreatedAt)) — اتفاقية ProgramClock
                       (UTC+3)؛ NULL CreatedAt → السنة البرمجية الحالية (مُوثَّق في الرأس).
                       الترتيب الحتمي داخل السنة: بلا CreatedAt أخيرًا ثم CreatedAt ثم InvoiceId.
                       القاعدة (حاسم ضد التصادم): التسلسل = base + rn حيث base لكل سنة =
                       MAX(عدّاد إن وُجد، أعلى لاحقة مُحلَّلة من المحفوظ INV-<digits>-<digits>، 0)
                       — ROW_NUMBER من 1 الصرف كان يصطدم بالأرقام المحفوظة — ممنوع.
                       التحليل: فقط ''INV-<4digits>-<digits>'' (السنة من الرقم نفسه — صف محفوظ
                       بلا CreatedAt لا سنة زمنية له)؛ غير ذلك يُتجاهل وتقيمه محفوظة كما هي.
                       FORMAT(seq,''000000'') يزيد الحشو ولا يقص أبدًا (RIGHT كان يقص — مرفوض). */
                    CREATE TABLE #Inv19Targets
                    (
                        InvoiceId   INT NOT NULL PRIMARY KEY,
                        ProgramYear INT NOT NULL,
                        rn          BIGINT NOT NULL
                    );

                    INSERT INTO #Inv19Targets (InvoiceId, ProgramYear, rn)
                    SELECT InvoiceId,
                           ProgramYear,
                           ROW_NUMBER() OVER (PARTITION BY ProgramYear
                                              ORDER BY CASE WHEN CreatedAt IS NULL THEN 1 ELSE 0 END,
                                                       CreatedAt, InvoiceId)
                    FROM (
                        SELECT i.InvoiceId,
                               i.CreatedAt,
                               CASE WHEN i.CreatedAt IS NULL
                                    THEN YEAR(DATEADD(HOUR, 3, SYSUTCDATETIME()))
                                    ELSE YEAR(DATEADD(HOUR, 3, i.CreatedAt))
                               END AS ProgramYear
                        FROM dbo.Invoices i
                        WHERE i.InvoiceNumber IS NULL
                    ) t;

                    /* أعلى لاحقة محفوظة لكل سنة — من الأرقام ذات تنسيقنا فقط */
                    CREATE TABLE #Inv19Maxes
                    (
                        ProgramYear INT NOT NULL PRIMARY KEY,
                        Base        BIGINT NOT NULL,
                        NewMax      BIGINT NOT NULL
                    );

                    WITH Preserved AS (
                        SELECT CAST(SUBSTRING(n.Num, 5, 4) AS INT)  AS ProgramYear,
                               CAST(SUBSTRING(n.Num, 10, 100) AS BIGINT) AS Suffix
                        FROM (SELECT InvoiceNumber AS Num
                              FROM dbo.Invoices WITH (UPDLOCK)
                              WHERE InvoiceNumber IS NOT NULL) n
                        WHERE LEN(n.Num) >= 10
                          AND SUBSTRING(n.Num, 1, 4) = N''INV-''
                          AND SUBSTRING(n.Num, 5, 4) NOT LIKE N''%[^0-9]%''
                          AND SUBSTRING(n.Num, 9, 1) = N''-''
                          AND SUBSTRING(n.Num, 10, 100) <> N''''
                          AND SUBSTRING(n.Num, 10, 100) NOT LIKE N''%[^0-9]%''
                    ),
                    Candidates AS (
                        SELECT c.NumberYear AS ProgramYear, c.LastIssuedNumber AS Val
                        FROM dbo.InvoiceNumberCounters c
                        UNION ALL
                        SELECT p.ProgramYear, p.Suffix FROM Preserved p
                    ),
                    Bases AS (
                        SELECT y.ProgramYear, MAX(COALESCE(cd.Val, 0)) AS Base
                        FROM (
                            SELECT DISTINCT ProgramYear FROM #Inv19Targets
                            UNION SELECT ProgramYear FROM Candidates
                        ) y
                        LEFT JOIN Candidates cd ON cd.ProgramYear = y.ProgramYear
                        GROUP BY y.ProgramYear
                    )
                    INSERT INTO #Inv19Maxes (ProgramYear, Base, NewMax)
                    SELECT b.ProgramYear, b.Base, b.Base + COALESCE(t.mx, 0)
                    FROM Bases b
                    OUTER APPLY (SELECT MAX(t.rn) AS mx
                                 FROM #Inv19Targets t
                                 WHERE t.ProgramYear = b.ProgramYear) t;

                    /* الإسناد — داخل المعاملة نفسها، والفهرس الفريد المُرشَّح حاجز محرك */
                    UPDATE i
                    SET i.InvoiceNumber = N''INV-'' + CONVERT(NVARCHAR(4), m.ProgramYear)
                                          + N''-'' + FORMAT(m.Base + t.rn, N''000000'')
                    FROM dbo.Invoices i
                    JOIN #Inv19Targets t ON t.InvoiceId = i.InvoiceId
                    JOIN #Inv19Maxes m ON m.ProgramYear = t.ProgramYear;
                    DECLARE @u19Count INT = @@ROWCOUNT;

                    /* ── 5) upsert العدّاد لكل سنة — لا يُخفَّض أبدًا ── */
                    UPDATE c
                    SET c.LastIssuedNumber = CASE WHEN c.LastIssuedNumber > m.NewMax
                                                  THEN c.LastIssuedNumber ELSE m.NewMax END,
                        c.LastUpdatedAtUtc = SYSUTCDATETIME()
                    FROM dbo.InvoiceNumberCounters c
                    JOIN #Inv19Maxes m ON m.ProgramYear = c.NumberYear;

                    INSERT INTO dbo.InvoiceNumberCounters (NumberYear, LastIssuedNumber, LastUpdatedAtUtc)
                    SELECT m.ProgramYear, m.NewMax, SYSUTCDATETIME()
                    FROM #Inv19Maxes m
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.InvoiceNumberCounters c
                                      WHERE c.NumberYear = m.ProgramYear);

                    DROP TABLE #Inv19Targets;
                    DROP TABLE #Inv19Maxes;

                    /* ── 6) رفض إتمام التعميد: صف بلا رقم (NULL أو فارغ) بعد
                       الترحيل = بيانات لا يمكن شدّ NOT NULL معها (والفارغ يسمم
                       الفهرس الفريد العادي) — THROW يتراجع عن الترقية كلها:
                       لمسُها قرار مالك لا سكربت تلقائي. ── */
                    DECLARE @u19Unnumbered int =
                        (SELECT COUNT(*) FROM dbo.Invoices
                         WHERE InvoiceNumber IS NULL OR InvoiceNumber = N'''');
                    IF @u19Unnumbered > 0
                    BEGIN
                        DECLARE @u19Msg nvarchar(2048) =
                            N''Upgrade_19: تعذر إتمام الترقية — بقي ''
                            + CONVERT(nvarchar(20), @u19Unnumbered)
                            + N'' صف فاتورة بلا رقم تجاري (NULL أو فارغ) بعد الترحيل. ''
                            + N''أصلح هذه الصفوف يدويًا ثم أعد تشغيل الترقية.'';
                        THROW 50019, @u19Msg, 1;
                    END;

                    /* ── 7) التعميد النهائي: هدم الفهرس المُرشَّح (وجوبًا قبل
                       ALTER — SQL Server يرفض ALTER على عمود مُفهرس) ثم شدّ
                       NOT NULL ثم إعادة الفهرس فريدًا عاديًّا بالاسم نفسه.
                       الصيغة النهائية: كل صف مميَّز برقم لا NULL ولا فارغ.
                       (داخل دفعة EXEC الفرعية — العبارات مباشرة: العمود
                       موجود وقت ترجمة هذه الدفعة.) ── */
                    IF EXISTS (SELECT 1 FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                 AND name = N''UQ_Invoices_InvoiceNumber'')
                        DROP INDEX UQ_Invoices_InvoiceNumber ON dbo.Invoices;

                    IF EXISTS (SELECT 1 FROM sys.columns
                               WHERE object_id = OBJECT_ID(N''dbo.Invoices'')
                                 AND name = N''InvoiceNumber'' AND is_nullable = 1)
                        ALTER TABLE dbo.Invoices
                            ALTER COLUMN InvoiceNumber NVARCHAR(32) NOT NULL;

                    CREATE UNIQUE INDEX UQ_Invoices_InvoiceNumber
                        ON dbo.Invoices(InvoiceNumber);

                    PRINT ''Upgrade_19: backfilled '' + CONVERT(nvarchar(20), @u19Count)
                        + '' NULL InvoiceNumber rows + counters updated'';
                ');
            END
            ELSE
                PRINT 'Upgrade_19: final invariant already in place (NOT NULL column, normal unique index, no NULL/empty rows) — skipped';
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
        PRINT 'Upgrade_19: another instance holds the upgrade lock — skipped (will be applied by the next run)';
    END
END
ELSE
    PRINT 'Upgrade_19: required tables not present — skipped (unexpected for a Sayl database)';
GO

PRINT 'Upgrade_19: annual invoice numbering schema done';
