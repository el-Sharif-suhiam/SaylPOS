/*
===========================================================
SaylDB Upgrade 01 — Production readiness
- Settings        : app configuration (store info, VAT, receipt, backup)
- CashMovements   : cash in/out during POS sessions
Run once against SaylDB. Safe to re-run (existence checks).
===========================================================
*/

USE SaylDB;
GO

-----------------------------------------------------------
-- 1) Settings (Key/Value)
-----------------------------------------------------------
IF OBJECT_ID('dbo.Settings', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Settings (
        SettingKey   NVARCHAR(100)  NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
        SettingValue NVARCHAR(MAX)  NULL,
        UpdatedAt    DATETIME       NULL CONSTRAINT DF_Settings_UpdatedAt DEFAULT (getdate())
    );
END
GO

-----------------------------------------------------------
-- 2) CashMovements (cash in/out per POS session)
-----------------------------------------------------------
IF OBJECT_ID('dbo.CashMovements', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashMovements (
        MovementId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashMovements PRIMARY KEY,
        SessionId    INT               NULL,
        MovementType CHAR(1)           NOT NULL CONSTRAINT CK_CashMovements_Type CHECK (MovementType IN ('I','O')),
        Amount       DECIMAL(10, 2)    NOT NULL CONSTRAINT DF_CashMovements_Amount DEFAULT (0),
        Reason       NVARCHAR(255)     NULL,
        UserId       INT               NOT NULL,
        CreatedAt    DATETIME          NULL CONSTRAINT DF_CashMovements_CreatedAt DEFAULT (getdate()),

        CONSTRAINT FK_CashMovements_SessionId FOREIGN KEY (SessionId) REFERENCES dbo.POSSessions (SessionId),
        CONSTRAINT FK_CashMovements_UserId    FOREIGN KEY (UserId)    REFERENCES dbo.Users (UserId)
    );

    CREATE INDEX IX_CashMovements_SessionId ON dbo.CashMovements (SessionId);
    CREATE INDEX IX_CashMovements_UserId    ON dbo.CashMovements (UserId);
    CREATE INDEX IX_CashMovements_CreatedAt ON dbo.CashMovements (CreatedAt);
END
GO

PRINT 'Upgrade 01 applied successfully.';
GO
