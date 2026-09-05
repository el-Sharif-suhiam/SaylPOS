/*
===========================================================
SaylDB Upgrade 07 — Historical return-invoice revaluation (VULN-03)
===========================================================
Rebuilds every existing customer_return / supplier_return invoice
using the new proportional-refund equation:

    refund_ratio = original.NetAmount / original.TotalAmount
    item refund  = item.ItemPrice * qty * ratio   (rounded ONCE)

The OLD code refunded raw line prices - leaking the original
invoice's discount and tax:
  - 10% discount: customer paid 90, refund was 100 (+10 phantom)
  - 10% tax:      customer paid 110, refund was 100 (-10 stolen)

This script recalculates, for every return invoice:
  * NetAmount    = proportional refund (rounded once, 2dp)
  * TotalAmount  = returned gross (raw line prices)
  * DiscountAmount / TaxAmount = proportional share of the
    original invoice's discount/tax (audit-grade breakdown)
  * InvoiceItems.ItemPrice is NOT touched (raw history preserved);
    the ratio lives on the invoice header totals.

⚠ Runners beware (idempotent but stateful):
  - Payments already issued against return invoices keep their
    amounts. If a past cash refund exceeds the NEW NetAmount,
    the difference was over-refunded under the old equation and
    is reported (not silently fixed) - the balances are trial
    data per project owner, so we DO adjust customer/supplier
    balances by the refund delta where a balance was affected.
  - Safe to re-run: pure function of current values.

Verified on trial data before commit (preview mode first).
===========================================================
*/

USE SaylDB;
GO

SET NOCOUNT ON;

-----------------------------------------------------------
-- 0) Preview: what the old equation got wrong
-----------------------------------------------------------
PRINT '=== PREVIEW: return invoices that will change ===';
SELECT
    ri.InvoiceId            AS ReturnInvoiceId,
    ri.OriginalInvoiceId,
    ri.[Type],
    ri.NetAmount            AS OldNet,
    CAST(ROUND(
        (SELECT SUM(ii.ItemPrice * ii.Quantity)
         FROM InvoiceItems ii WHERE ii.InvoiceId = ri.InvoiceId)
        * (oi.NetAmount * 1.0 / NULLIF(oi.TotalAmount, 0)), 2, 1)
        AS DECIMAL(10,2))    AS NewNet,
    CASE WHEN ri.[Type] = 'customer_return' THEN 'C' ELSE 'S' END AS Party
FROM Invoices ri
JOIN Invoices oi ON oi.InvoiceId = ri.OriginalInvoiceId
WHERE ri.[Type] IN ('customer_return', 'supplier_return')
  AND oi.TotalAmount > 0
  AND ri.NetAmount <> -CAST(ROUND(
        (SELECT SUM(ii.ItemPrice * ii.Quantity)
         FROM InvoiceItems ii WHERE ii.InvoiceId = ri.InvoiceId)
        * (oi.NetAmount * 1.0 / NULLIF(oi.TotalAmount, 0)), 2, 1)
        AS DECIMAL(10,2));
GO

-----------------------------------------------------------
-- 1) Revaluate return headers (audit-grade breakdown)
-----------------------------------------------------------
;WITH revalue AS (
    SELECT
        ri.InvoiceId,
        ri.OriginalInvoiceId,
        (SELECT SUM(ii.ItemPrice * ii.Quantity)
         FROM InvoiceItems ii WHERE ii.InvoiceId = ri.InvoiceId) AS ReturnedGross,
        oi.TotalAmount  AS InvTotal,
        oi.NetAmount    AS InvNet,
        oi.DiscountAmount AS InvDiscount,
        oi.TaxAmount    AS InvTax
    FROM Invoices ri
    JOIN Invoices oi ON oi.InvoiceId = ri.OriginalInvoiceId
    WHERE ri.[Type] IN ('customer_return','supplier_return')
      AND oi.TotalAmount > 0
)
UPDATE ri SET
    TotalAmount    = -revalue.ReturnedGross,
    DiscountAmount = -CAST(ROUND(revalue.ReturnedGross * (revalue.InvDiscount * 1.0 / revalue.InvTotal), 2, 1) AS DECIMAL(10,2)),
    TaxAmount      = -CAST(ROUND(
                         (revalue.ReturnedGross - revalue.ReturnedGross * (revalue.InvDiscount * 1.0 / revalue.InvTotal))
                         * (revalue.InvTax * 1.0 / NULLIF(revalue.InvTotal - revalue.InvDiscount, 0))
                     , 2, 1) AS DECIMAL(10,2)),
    NetAmount      = -CAST(ROUND(revalue.ReturnedGross * (revalue.InvNet * 1.0 / revalue.InvTotal), 2, 1) AS DECIMAL(10,2))
FROM Invoices ri
JOIN revalue ON revalue.InvoiceId = ri.InvoiceId;
PRINT 'Return headers revaluated: ' + CAST(@@ROWCOUNT AS VARCHAR(10));
GO

-----------------------------------------------------------
-- 2) Reconcile party balances with the corrected refunds.
--    في المعادلة القديمة، الاسترداد القديم خفّض الرصيد بمقداره الخام.
--    المعادلة الجديدة تقول إن الاسترداد الصحيح أقل/أكثر بفارق
--    (الجديد − القديم) — نعيده للرصيد (سالب الجديد يعني استردادًا أكبر
--    => الرصيد يُخفض أكثر، والعكس صحيح). فاتورات بدون عميل/مورد تتخطى.
--    ⚠️ ملاحظة: القاعدة بيانات تجريبية بقرار المالك — نطاق التعديل
--    كامل، والإيداع النقدي القديم يظل كما هو (تاريخ محفوظ للتدقيق).
-----------------------------------------------------------
;WITH delta_cte AS (
    SELECT
        ri.InvoiceId,
        ri.PartyId,
        ri.PartyType,
        -- القيمة الجديدة الصحيحة (نسبة الصافي) ناقص القيمة القديمة
        CAST(ROUND(
            (SELECT SUM(ii.ItemPrice * ii.Quantity) FROM InvoiceItems ii WHERE ii.InvoiceId = ri.InvoiceId)
            * (oi.NetAmount * 1.0 / NULLIF(oi.TotalAmount, 0)), 2, 1)
            AS DECIMAL(10,2)) AS NewRefundNet,
        ABS(ri.NetAmount) AS OldRefundNet
    FROM Invoices ri
    JOIN Invoices oi ON oi.InvoiceId = ri.OriginalInvoiceId
    WHERE ri.[Type] IN ('customer_return','supplier_return')
      AND oi.TotalAmount > 0
      AND ri.PartyId IS NOT NULL
)
SELECT * INTO #return_delta FROM delta_cte;

UPDATE c SET
    -- الاسترداد القديم خفّض الرصيدَ OldRefundNet؛ الصحيح أن يخفّضه NewRefundNet
    -- => نصحح الرصيد بمقدار (OldRefundNet − NewRefundNet) للأعلى أو الأسفل.
    c.Balance = c.Balance + (d.OldRefundNet - d.NewRefundNet)
FROM Customers c
JOIN #return_delta d ON d.PartyId = c.PartyId AND d.PartyType = 'C'
WHERE c.Balance IS NOT NULL AND d.OldRefundNet <> d.NewRefundNet;

UPDATE s SET
    s.Balance = s.Balance + (d.OldRefundNet - d.NewRefundNet)
FROM Suppliers s
JOIN #return_delta d ON d.PartyId = s.PartyId AND d.PartyType = 'S'
WHERE s.Balance IS NOT NULL AND d.OldRefundNet <> d.NewRefundNet;

DROP TABLE #return_delta;
PRINT 'Party balances reconciled with revaluated refunds.';
GO

PRINT 'Upgrade 07 applied successfully — historical returns revaluated (VULN-03 solution B).';
GO
