/*
===========================================================
SaylDB Upgrade 06 — Cash-drawer session attribution (VULN-05)
===========================================================
Adds Payments.SessionId: the POS-session (cash drawer) that
physically received/paid each payment, independent of the
invoice's session.

Why: a suspended invoice completed in a later session (or a
return processed today for last week's sale) booked its cash
into the ORIGINAL session's drawer — which may already be
closed with a frozen Z-report. The money showed up in NO
live session's expected-cash => permanent unexplainable
drawer overages/shortages (perfect cover for real theft).

With Payment.SessionId:
  - Checkout payments  -> the acting session (request.SessionId)
  - Suspended resume   -> the resuming cashier's session
  - Return refunds     -> the session processing the return
  - Session Z-reports (clsCashService + CloseSessionAsync):
    sum by p.SessionId with a LEGACY fallback — payments with
    NULL SessionId keep their old attribution (invoice's
    session) so historical Z-reports stay identical.

⚠ THIS SCRIPT MUST SHIP WITH THE MODEL CHANGE (database-first).
   Running the app without it = runtime "Invalid column" crash.
   Verified live before commit: column + index + backfill + FK.

Safe to re-run (existence checks on every step).
===========================================================
*/

USE SaylDB;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-----------------------------------------------------------
-- 1) Payments.SessionId (nullable — legacy rows stay NULL
--    and keep their old invoice-session attribution)
-----------------------------------------------------------
IF COL_LENGTH('dbo.Payments', 'SessionId') IS NULL
BEGIN
    ALTER TABLE dbo.Payments ADD SessionId INT NULL;
    PRINT 'Column Payments.SessionId added.';
END
ELSE
    PRINT 'Column Payments.SessionId already exists.';
GO

-----------------------------------------------------------
-- 2) Index for the session Z-report sums
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Payments_SessionId'
      AND object_id = OBJECT_ID('dbo.Payments')
)
BEGIN
    CREATE INDEX IX_Payments_SessionId ON dbo.Payments(SessionId);
    PRINT 'Index IX_Payments_SessionId created.';
END
ELSE
    PRINT 'Index IX_Payments_SessionId already exists.';
GO

-----------------------------------------------------------
-- 3) FK (soft — NULL allowed). Created AFTER backfill so
--    existing rows are consistent first.
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_Payments_SessionId'
      AND parent_object_id = OBJECT_ID('dbo.Payments')
)
BEGIN
    ALTER TABLE dbo.Payments
        ADD CONSTRAINT FK_Payments_SessionId
        FOREIGN KEY (SessionId) REFERENCES dbo.POSSessions(SessionId);
    PRINT 'FK FK_Payments_SessionId created.';
END
ELSE
    PRINT 'FK FK_Payments_SessionId already exists.';
GO

PRINT 'Upgrade 06 applied successfully.';
GO
