/*
===========================================================
SaylDB Upgrade 05 — Financial amount guards (VULN-02)
===========================================================
Adds CHECK constraints enforcing non-negative money at the
database level (defense-in-depth under the service guards):

  1) CK_Invoices_NonNegativeNet   : NetAmount   >= 0
  2) CK_Suppliers_NonNegativeBalance (only if no
     negative balances exist — otherwise reported)
  3) CK_Customers_NonNegativeBalance (same)

A negative net created a fully "paid" invoice from nothing
(proven live during the attack audit: a -999,900 invoice was
inserted with no constraint blocking it — finding VULN-02).

Run once against SaylDB. Safe to re-run (existence checks).
===========================================================
*/

USE SaylDB;
GO

-----------------------------------------------------------
-- 1) فواتير البيع/الشراء: صافٍ غير سالب
-- (فواتير المرتجعات customer_return/supplier_return سالبة
--  بالتصميم — الاسترداد — فتُستثنى من القيد عمداً)
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_Invoices_NonNegativeNet'
)
BEGIN
    -- فواتير بيع/شراء سالبة فقط هي الخطأ (المكتشف الحي: فاتورة -999,900)
    IF EXISTS (SELECT 1 FROM dbo.Invoices
               WHERE NetAmount < 0
                 AND [Type] IN ('sale', 'purchase'))
    BEGIN
        RAISERROR('CK_Invoices_NonNegativeNet: توجد فواتير بيع/شراء بصافٍ سالب — عالجها قبل تطبيق القيد.', 16, 1);
        RETURN;
    END
    ALTER TABLE dbo.Invoices
        ADD CONSTRAINT CK_Invoices_NonNegativeNet
        CHECK ([Type] IN ('customer_return', 'supplier_return') OR NetAmount >= 0);
    PRINT 'CK_Invoices_NonNegativeNet created (returns excluded by design).';
END
ELSE
    PRINT 'CK_Invoices_NonNegativeNet already exists.';
GO

-----------------------------------------------------------
-- 2) Suppliers.Balance >= 0  (رصيد مورد سالب = مورد يستحيل سداده)
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_Suppliers_NonNegativeBalance'
)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Suppliers WHERE Balance < 0)
    BEGIN
        PRINT 'WARNING: يوجد مورودون بأرصدة سالبة — القيد لم يُطبق. راجعهم يدويًا ثم أعد التشغيل.';
    END
    ELSE
    BEGIN
        ALTER TABLE dbo.Suppliers
            ADD CONSTRAINT CK_Suppliers_NonNegativeBalance CHECK (Balance >= 0);
        PRINT 'CK_Suppliers_NonNegativeBalance created.';
    END
END
ELSE
    PRINT 'CK_Suppliers_NonNegativeBalance already exists.';
GO

-----------------------------------------------------------
-- 3) Customers.Balance >= 0
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_Customers_NonNegativeBalance'
)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Customers WHERE Balance < 0)
    BEGIN
        PRINT 'WARNING: يوجد عملاء بأرصدة سالبة — القيد لم يُطبق. راجعهم يدويًا ثم أعد التشغيل.';
    END
    ELSE
    BEGIN
        ALTER TABLE dbo.Customers
            ADD CONSTRAINT CK_Customers_NonNegativeBalance CHECK (Balance >= 0);
        PRINT 'CK_Customers_NonNegativeBalance created.';
    END
END
ELSE
    PRINT 'CK_Customers_NonNegativeBalance already exists.';
GO

PRINT 'Upgrade 05 applied successfully.';
GO
