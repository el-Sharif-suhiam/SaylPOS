/* ============================================================
   Upgrade_20 - جدول وحدات المنتج (ProductUnits) — النموذج الخامل
   ------------------------------------------------------------
   لماذا: Product.Unit كان تسمية حرة بلا أي معنى حسابيًا — لا عامل
   تحويل ولا أساس لوحدات بديلة (كرتون = 12 حبة). المرحلة 3 تضيف
   مخططًا خاملًا فقط: صف أساس واحد لكل منتج (IsBase = 1,
   FactorToBase = 1) هو التكوين القانوني المرآتي لـ Product.Unit،
   والوحدات البديلة تُدار لاحقًا (المرحلة 6).

   الترحيل (CONFIG-ONLY — لا اختراع تاريخ): كل منتج قائم يأخذ صف
   أساس واحد باسم ISNULL(Product.Unit, N'وحدة') بعامل 1. لا تُنشأ
   وحدات بديلة، ولا تُمس أي بيانات حركية (فواتير/حركات/حجوزات).

   حذف المنتج عبر المفتاح الأجنبي: NO ACTION (Restrict) — اللقطات
   التاريخية (UnitName/UnitFactorToBase) قد تشير إلى الوحدة، فلا
   يُحذف صف وحدة ذو مراجع.

   Idempotent - إعادة تشغيله بلا ضرر.
   شغّله: sqlcmd -d <قاعدتك> -i Upgrade_20_ProductUnits.sql
   ============================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.ProductUnits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductUnits
    (
        ProductUnitId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ProductUnits PRIMARY KEY,
        ProductId INT NOT NULL,
        -- اسم الوحدة كما يُدخل ويُعرض (نفس عرض Products.Unit = 20)
        Name NVARCHAR(20) NOT NULL,
        -- عامل التحويل: 1 وحدة بديلة = FactorToBase وحدة أساس؛ صف الأساس = 1
        FactorToBase DECIMAL(18,6) NOT NULL,
        -- صف الأساس الوحيد لكل منتج (المرآة القانونية لـ Product.Unit)
        IsBase BIT NOT NULL CONSTRAINT DF_ProductUnits_IsBase DEFAULT (0),
        -- سعر بيع افتراضي اختياري لهذه الوحدة (NULL = لا يوجد)
        PricePerUnit DECIMAL(10,2) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_ProductUnits_IsActive DEFAULT (1),
        -- Upgrade_15: افتراضية UTC لا GETDATE — منطقة زمنية واحدة مع الكُتّاب
        CreatedAt DATETIME2 NULL CONSTRAINT DF_ProductUnits_CreatedAt DEFAULT (SYSUTCDATETIME()),

        -- NO ACTION (Restrict): اللقطات التاريخية قد تشير إلى الوحدة
        CONSTRAINT FK_ProductUnits_Product FOREIGN KEY (ProductId)
            REFERENCES dbo.Products(ProductId),
        CONSTRAINT CK_ProductUnits_Factor CHECK (FactorToBase > 0),
        -- صف الأساس يجب أن يكون عاملُه 1 (الأساس بتعريفه هو نفسه)
        CONSTRAINT CK_ProductUnits_BaseFactor CHECK (IsBase = 0 OR FactorToBase = 1),
        CONSTRAINT UQ_ProductUnits_Product_Name UNIQUE (ProductId, Name)
    );

    -- أساس واحد بالضبط لكل منتج: فهرس فريد مُرشَّح (نمط UQ_Products_Barcode)
    CREATE UNIQUE INDEX UQ_ProductUnits_Base
        ON dbo.ProductUnits(ProductId)
        WHERE IsBase = 1;
    CREATE INDEX IX_ProductUnits_Product
        ON dbo.ProductUnits(ProductId);

    PRINT 'Upgrade_20: created dbo.ProductUnits';
END;
GO

/* دفعة منفصلة: الجدول يجب أن يكون موجودًا قبل الترحيل. الترحيل نفسه
   idempotent: المنتج ذو صف أساس قائم لا يُمس — لا صفوف بديلة تُخترع. */
IF OBJECT_ID(N'dbo.ProductUnits', N'U') IS NOT NULL
BEGIN
    /* ترحيل التكوين: لكل منتج بلا صف أساس — صف أساس واحد باسم وحدته
       الحالية (أو 'وحدة' إن كانت فارغة). الحارس الثاني يمنع اصطدام
       UQ_ProductUnits_Product_Name إن كان الاسم محجوزًا لصف آخر. */
    INSERT INTO dbo.ProductUnits (ProductId, Name, FactorToBase, IsBase, IsActive)
    SELECT p.ProductId,
           ISNULL(NULLIF(LTRIM(RTRIM(p.Unit)), N''), N'وحدة'),
           1, 1, 1
    FROM dbo.Products p
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.ProductUnits pu
        WHERE pu.ProductId = p.ProductId AND pu.IsBase = 1)
      AND NOT EXISTS (
        SELECT 1 FROM dbo.ProductUnits pu
        WHERE pu.ProductId = p.ProductId
          AND pu.Name = ISNULL(NULLIF(LTRIM(RTRIM(p.Unit)), N''), N'وحدة'));

    PRINT 'Upgrade_20: base-unit backfill done';
END;
GO

PRINT 'Upgrade_20: ProductUnits migration done';
