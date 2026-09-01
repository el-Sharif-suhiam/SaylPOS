/*
===========================================================
SaylDB Upgrade 04 — Physical stock counting (annual close support)
- StockCounts: count session lines (book snapshot vs counted,
  posted via existing stock adjustment service)
Run once against SaylDB. Safe to re-run (existence checks).
===========================================================
*/

USE SaylDB;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.StockCounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockCounts (
        CountId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockCounts PRIMARY KEY,
        CountSessionId   INT          NOT NULL,
        ProductId        INT          NOT NULL,
        ProductName      NVARCHAR(100) NOT NULL,
        BookQuantity     INT          NOT NULL,
        CountedQuantity  INT          NULL,
        UnitCost         DECIMAL(10,2) NOT NULL DEFAULT (0),
        IsPosted         BIT          NOT NULL DEFAULT (0),
        CountedBy        INT          NOT NULL,
        CreatedAt        DATETIME2    DEFAULT (GETDATE()),

        CONSTRAINT FK_StockCounts_ProductId FOREIGN KEY (ProductId) REFERENCES dbo.Products(ProductId)
    );

    CREATE INDEX IX_StockCounts_SessionId ON dbo.StockCounts(CountSessionId);
    CREATE INDEX IX_StockCounts_ProductId ON dbo.StockCounts(ProductId);
END
GO

PRINT 'Upgrade 04 applied successfully.';
GO
