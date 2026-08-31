/*
===========================================================
SaylDB Upgrade 02 — Concurrency guards
Adds a filtered unique index enforcing one open POS session
per user. Protects against parallel session-open requests
(double-click / multi-terminal) at the database level.

Run once against SaylDB. Safe to re-run (existence check).
NOTE: if a user already has 2+ open sessions the index
creation will fail — close the duplicates first:
    SELECT UserId, COUNT(*) FROM POSSessions
    WHERE Status = 'open' GROUP BY UserId HAVING COUNT(*) > 1;
===========================================================
*/

USE SaylDB;
GO

-----------------------------------------------------------
-- 1) One open POS session per user (filtered unique index)
-----------------------------------------------------------
-- Filtered indexes require these SET options (sqlcmd defaults to OFF)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UX_POSSessions_OneOpenPerUser'
      AND object_id = OBJECT_ID('dbo.POSSessions')
)
BEGIN
    CREATE UNIQUE INDEX UX_POSSessions_OneOpenPerUser
        ON dbo.POSSessions(UserId)
        WHERE Status = 'open';
END
GO

PRINT 'Upgrade 02 applied successfully.';
GO
