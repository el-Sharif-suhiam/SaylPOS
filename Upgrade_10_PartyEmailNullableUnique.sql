/* ============================================================
   Upgrade_10 - بريد الطرف: فهرس فريد لا يبتلع العملاء بلا بريد
   ------------------------------------------------------------
   لماذا: parties_email_unique كان فهرسًا فريدًا عاديًا بلا فلتر،
   وSQL Server يسمح في الفهرس الفريد بـNULL واحد فقط — أي بعد
   أن يُنشأ أول عميل بترك البريد فارغًا ترتد كل إضافة ثانية
   (المسار الأهم: إضافة الزبون السريع من شاشة الكاشير التي
   لا تملك حقل بريد) بخطأ 2601 «مفتاح مكرر» مُسوّى برسالة
   «فشلت عملية الإضافة».
   الإصلاح: فهرس فريد **مُرشّح** WHERE Email IS NOT NULL —
   القاعدة الصحيحة: «البريد فريد إن وُجد» وNULL كثر مسموحة.
   Idempotent - إعادة تشغيله بلا ضرر.
   ============================================================ */

/* الفوهرس الفريد/المُرشّح يتطلب إعدادات SET صحيحة في أدوات سطر الأوامر
   (sqlcmd/osql افتراضاتها OFF للـQUOTED_IDENTIFIER) — صريح هنا ليصير
   السكربت فريد النتيجة مهما كان المشغّل. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_ID(N'SaylDB') IS NOT NULL
BEGIN
    /* 1) حارس بيانات: بريدات فعلية مكررة تحجب إنشاء الفهرس الفريد —
          لا نحذف بيانات أحد أبدًا؛ نُبلغ وننشئه بعد تنظيفها يدويًا. */
    IF EXISTS (
        SELECT 1
        FROM dbo.Parties
        WHERE Email IS NOT NULL
        GROUP BY Email
        HAVING COUNT(*) > 1
    )
    BEGIN
        PRINT 'Upgrade_10: duplicate non-null emails exist in dbo.Parties - fix them manually, index NOT recreated.';
        RETURN;
    END;

    /* 2) فهرس موجود لكن غير مُرشّح (الحال القديم) -> استبدله */
    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Parties')
          AND name = N'parties_email_unique'
          AND is_unique = 1
          AND has_filter = 0
    )
    BEGIN
        DROP INDEX parties_email_unique ON dbo.Parties;
        PRINT 'Upgrade_10: dropped plain unique index parties_email_unique';
    END;

    /* 3) إنشاؤه المُرشّح إن غاب (بعد الاستبدال أو ضمن قاعدة لم تكسبه) */
    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Parties')
          AND name = N'parties_email_unique'
          AND is_unique = 1
          AND has_filter = 1
    )
    BEGIN
        CREATE UNIQUE INDEX parties_email_unique ON dbo.Parties(Email)
            WHERE Email IS NOT NULL;
        PRINT 'Upgrade_10: created filtered unique index parties_email_unique';
    END;
    ELSE
        PRINT 'Upgrade_10: filtered unique index already in place - nothing to do.';
END;
ELSE
    PRINT 'Upgrade_10: SaylDB not found - nothing to do.';
