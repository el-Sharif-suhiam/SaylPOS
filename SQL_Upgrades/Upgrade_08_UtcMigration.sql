/*
===========================================================
SaylDB Upgrade 08 — Historical timestamp migration to UTC (VULN-45)
===========================================================
The app recorded every CreatedAt/UpdatedAt/StartedAt/ClosedAt/
PaidAt/LastCountedAt with the machine's LOCAL time (UTC+3 in KSA).
The code now stamps DateTime.UtcNow - existing local-time rows
must be shifted to UTC so old data lines up with new rows.

Shift: local (UTC+3) -> UTC = subtract 3 hours.
If your machines run a different offset, change @offset below.

Safe to re-run: it stamps a marker in Settings; if present, exits
immediately (prevents double-subtracting on re-run).
===========================================================
*/

USE SaylDB;
GO

-- الفهارس المفلترة (UX_POSSessions_OneOpenPerUser) تتطلب هذه SETs قبل أي UPDATE
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

SET NOCOUNT ON;

-----------------------------------------------------------
-- Guard: one-time marker (re-run safety)
-----------------------------------------------------------
IF EXISTS (SELECT 1 FROM Settings WHERE SettingKey = 'UTC_Migration_v1')
BEGIN
    PRINT 'UTC migration already applied - skipping (marker present).';
    RETURN;
END
GO

DECLARE @offsetHours INT = 3;  -- KSA local = UTC+3; adjust if machines differ
DECLARE @shift DATETIME2 = DATEADD(HOUR, -@offsetHours, 0);

PRINT 'Migrating historical local-time stamps to UTC (minus ' + CAST(@offsetHours AS VARCHAR(2)) + 'h)...';
GO

-----------------------------------------------------------
-- Shift all timestamp columns (local -> UTC)
-----------------------------------------------------------
UPDATE Batches       SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE CashMovements SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE Employees     SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
UPDATE Expenses      SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE Inventory     SET LastCountedAt = DATEADD(HOUR, -3, LastCountedAt) WHERE LastCountedAt IS NOT NULL;
UPDATE Inventory     SET UpdatedAt = DATEADD(HOUR, -3, UpdatedAt)  WHERE UpdatedAt IS NOT NULL;
UPDATE Invoices      SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE Parties       SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE Parties       SET UpdatedAt = DATEADD(HOUR, -3, UpdatedAt)   WHERE UpdatedAt IS NOT NULL;
UPDATE Payments      SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)   WHERE CreatedAt IS NOT NULL;
UPDATE PayrollRecords SET CreatedAt = DATEADD(HOUR, -3, CreatedAt) WHERE CreatedAt IS NOT NULL;
UPDATE PayrollRecords SET PaidAt = DATEADD(HOUR, -3, PaidAt)       WHERE PaidAt IS NOT NULL;
UPDATE POSSessions   SET StartedAt = DATEADD(HOUR, -3, StartedAt)  WHERE StartedAt IS NOT NULL;
UPDATE POSSessions   SET ClosedAt = DATEADD(HOUR, -3, ClosedAt)    WHERE ClosedAt IS NOT NULL;
UPDATE Products      SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
UPDATE Settings      SET UpdatedAt = DATEADD(HOUR, -3, UpdatedAt)  WHERE UpdatedAt IS NOT NULL;
UPDATE StockCounts   SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
UPDATE StockMovements SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
UPDATE SystemLogs     SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
UPDATE Users          SET CreatedAt = DATEADD(HOUR, -3, CreatedAt)  WHERE CreatedAt IS NOT NULL;
PRINT 'All timestamp columns shifted to UTC.';
GO

-----------------------------------------------------------
-- Marker: never shift twice
-----------------------------------------------------------
INSERT INTO Settings (SettingKey, SettingValue, UpdatedAt)
VALUES ('UTC_Migration_v1', CONVERT(VARCHAR(30), SYSUTCDATETIME()), SYSUTCDATETIME());
GO

PRINT 'Upgrade 08 applied successfully - historical stamps are now UTC (VULN-45 solution B).';
GO
