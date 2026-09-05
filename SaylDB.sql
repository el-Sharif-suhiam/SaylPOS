/*
===========================================================
SaylDB
Full SQL Server Script
Purpose:
- Creates the database
- Creates all tables
- Creates keys and constraints
- Creates useful indexes
Designed for:
- Sales and inventory management system
- Supports customers, suppliers, employees, users and roles
- Tracks stock movements with batch/lot control
NOTE: - Fresh installs ONLY: a safety gate right after USE aborts
        the whole script if any table already exists.
      - No DROP statements exist in this file.
      - Session SET options come first because filtered unique indexes
        (concurrency guards) require them.
===========================================================
*/

-----------------------------------------------------------
-- 0) Session options (required for filtered indexes)
-----------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-----------------------------------------------------------
-- 1) Create Database
-----------------------------------------------------------
IF DB_ID('SaylDB') IS NULL
BEGIN
    CREATE DATABASE SaylDB;
END
GO

USE SaylDB;
GO

-----------------------------------------------------------
-- SAFETY GATE: fresh installs only. If ANY table already
-- exists the script STOPS here - it never drops anything.
-- (An earlier DROP-block version destroyed a live DB when
--  run against it; this gate is absolute and runs FIRST.)
-----------------------------------------------------------
IF OBJECT_ID('dbo.Parties', 'U') IS NOT NULL
BEGIN
    RAISERROR('SaylDB already contains schema objects. Fresh installs only - DROP the database manually if you really want to rebuild.', 16, 1);
    SET NOEXEC ON;   -- disables execution for ALL remaining batches on this connection
    RETURN;           -- stops this batch; NOEXEC keeps every later batch inert
END
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL
BEGIN
    RAISERROR('SaylDB already contains schema objects (Users). Fresh installs only.', 16, 1);
    SET NOEXEC ON;
    RETURN;
END
GO

-----------------------------------------------------------
-- 2) Schema tables (no DROP statements anywhere in this
--    file - the safety gate above guarantees fresh-install)
-----------------------------------------------------------

-----------------------------------------------------------
-- 3) Create Parties
--    Base entity for all external parties (customers & suppliers)
-----------------------------------------------------------
CREATE TABLE Parties
(
    PartyId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    PartyType CHAR(1) NOT NULL,               -- C=Customer, S=Supplier, B=Both
    Name NVARCHAR(150) NOT NULL,
    Phone1 NVARCHAR(20) NULL,
    Phone2 NVARCHAR(20) NULL,
    Email NVARCHAR(150) NULL,
    AddressLine1 NVARCHAR(400) NULL,
    AddressLine2 NVARCHAR(400) NULL,
    Notes NVARCHAR(600) NULL,
    IsActive BIT NOT NULL DEFAULT (1),
    CreatedAt DATETIME2 NOT NULL DEFAULT (GETDATE()),
    UpdatedAt DATETIME2 NULL,

    CONSTRAINT CK_Parties_PartyType CHECK (PartyType IN ('C','S','B'))
);

CREATE UNIQUE INDEX parties_email_unique ON Parties(email);

-----------------------------------------------------------
-- 4) Create Customers
--    Extends Parties with customer-specific fields
-----------------------------------------------------------
CREATE TABLE Customers
(
    PartyId INT NOT NULL PRIMARY KEY,
    Balance DECIMAL(10,2) NULL DEFAULT (0),
    CreditLimit DECIMAL(10,2) NULL DEFAULT (0),
    CONSTRAINT FK_Customers_PartyId FOREIGN KEY (PartyId) REFERENCES Parties(PartyId)

);

-----------------------------------------------------------
-- 5) Create Suppliers
--    Extends Parties with supplier-specific fields
-----------------------------------------------------------
CREATE TABLE Suppliers
(
    PartyId INT NOT NULL PRIMARY KEY,
    Balance DECIMAL(10,2) NULL DEFAULT (0),
    Category NVARCHAR(100) NULL,
    CONSTRAINT FK_Suppliers_PartyId FOREIGN KEY (PartyId) REFERENCES Parties(PartyId)


);


-----------------------------------------------------------
-- 6) Create Employees
-----------------------------------------------------------
CREATE TABLE Employees
(
    EmployeeId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Phone NVARCHAR(20) NULL,
    Email NVARCHAR(150) NULL,
    Salary DECIMAL(10,2) NULL,
    SalaryType NVARCHAR(10) NOT NULL DEFAULT ('monthly'),   -- hourly / daily / monthly
    HireDate DATE NULL,
    ResignDate DATE NULL,                                   -- resignation cutoff for monthly proration
    Address NVARCHAR(400) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT ('active'),
    Position NVARCHAR(50) NOT NULL,
    Department NVARCHAR(50) NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT CK_Employees_Status CHECK (Status IN ('active','on_leave','resigned')),
    CONSTRAINT CK_Employees_SalaryType CHECK (SalaryType IN ('hourly','daily','monthly'))
);
GO
-----------------------------------------------------------
-- 7) Create Roles
--    Each user has a single role; roles define permissions
-----------------------------------------------------------
CREATE TABLE Roles
(
    RoleId TINYINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    RoleValue INT NOT NULL,

    CONSTRAINT UQ_Roles_Name UNIQUE (Name)
);
GO

-----------------------------------------------------------
-- 8) Create Users
--    Login accounts; optional link to Employees
-----------------------------------------------------------
CREATE TABLE Users
(
    UserId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Username NVARCHAR(50) NOT NULL,
    PasswordHash NVARCHAR(255) NOT NULL,
    EmployeeId INT NULL,
    RoleId TINYINT NOT NULL,
    IsActive bit NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT UQ_Users_Username UNIQUE (Username),
    -- one account per employee (filtered: NULL = standalone account, unlimited)
    CONSTRAINT FK_Users_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES Employees(EmployeeId),
    CONSTRAINT FK_Users_RoleId FOREIGN KEY (RoleId) REFERENCES Roles(RoleId)

);
GO
-----------------------------------------------------------
-- 9) Create Products
--    Stock items, services, and manufactured products
-----------------------------------------------------------
CREATE TABLE Products
(
    ProductId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Barcode NVARCHAR(50) NULL,
    Unit NVARCHAR(20) NULL,
    Type NVARCHAR(20) NOT NULL,
    BasePrice DECIMAL(10,2) NOT NULL DEFAULT (0),
    CostPrice DECIMAL(10,2) NOT NULL DEFAULT (0),
    ReorderLevel INT DEFAULT (0),
    IsActive BIT NOT NULL DEFAULT (1),
    CreatedAt DATETIME2 DEFAULT (GETDATE()),
    
    CONSTRAINT CK_Products_Type CHECK (Type IN ('stock','service','manufactured'))
);
GO
CREATE UNIQUE INDEX UQ_Products_Barcode
ON dbo.Products(Barcode)
WHERE Barcode IS NOT NULL;
GO
-----------------------------------------------------------
-- 10) Create Batches
--     Tracks inventory batches/lots (expiry, batch numbers)
-----------------------------------------------------------
CREATE TABLE Batches
(
    BatchId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ProductId INT NOT NULL,
    BatchNumber NVARCHAR(100) NOT NULL,
    ExpiryDate DATE NULL,
    BatchQuantity INT NOT NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT FK_Batches_ProductId FOREIGN KEY (ProductId) REFERENCES Products(ProductId),
    CONSTRAINT UQ_Batches_Product_Batch UNIQUE (ProductId, BatchNumber)

);
GO

-----------------------------------------------------------
-- 11) Create Inventory
--     Current stock summary per product (fast read)
-----------------------------------------------------------
CREATE TABLE Inventory
(
    ProductId INT NOT NULL PRIMARY KEY,
    QuantityAvailable INT DEFAULT (0),
    QuantityReserved INT DEFAULT (0),
    UpdatedAt DATETIME2 DEFAULT (GETDATE()),
    LastCountedAt DATETIME2 NULL,
    CONSTRAINT FK_Inventory_ProductId FOREIGN KEY (ProductId) REFERENCES Products(ProductId)

);
GO

-----------------------------------------------------------
-- 12) Create Invoices
--     Unified table for sales, purchases and returns
-----------------------------------------------------------
CREATE TABLE Invoices
(
    InvoiceId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Type NVARCHAR(20) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT ('completed'),
    PaymentStatus NVARCHAR(20) NOT NULL DEFAULT ('unpaid'),
    PartyType CHAR(1) NULL,
    PartyId INT NULL,
    UserId INT NOT NULL,
    SessionId INT NULL,
    OriginalInvoiceId INT NULL,
    TotalAmount DECIMAL(10,2) NOT NULL DEFAULT (0),
    DiscountAmount DECIMAL(10,2) NOT NULL DEFAULT (0),
    TaxAmount DECIMAL(10,2) NOT NULL DEFAULT (0),
    NetAmount DECIMAL(10,2) NOT NULL DEFAULT (0),
    Notes NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT CK_Invoices_Type CHECK (Type IN ('sale','purchase','customer_return','supplier_return')),
    CONSTRAINT CK_Invoices_Status CHECK (Status IN ('completed','suspended','cancelled')),
    CONSTRAINT CK_Invoices_PaymentStatus CHECK (PaymentStatus IN ('unpaid','partial','paid')),
    CONSTRAINT CK_Invoices_PartyType CHECK (PartyType IS NULL OR PartyType IN ('C','S')),

    CONSTRAINT FK_Invoices_PartyId FOREIGN KEY (PartyId) REFERENCES Parties(PartyId),
    CONSTRAINT FK_Invoices_UserId FOREIGN KEY (UserId) REFERENCES Users(UserId),
    CONSTRAINT FK_Invoices_OriginalInvoiceId FOREIGN KEY (OriginalInvoiceId) REFERENCES Invoices(InvoiceId)

);
GO

-----------------------------------------------------------
-- 13) Create InvoiceItems
--     Line items of an invoice; batch linkage optional
-----------------------------------------------------------
CREATE TABLE InvoiceItems
(
    InvoiceItemId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    InvoiceId INT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL DEFAULT (1),
    BatchId INT NULL,
    ItemPrice DECIMAL(10,2) NOT NULL,
    ItemCost DECIMAL(10,2) NULL,

    CONSTRAINT FK_InvoiceItems_InvoiceId FOREIGN KEY (InvoiceId) REFERENCES Invoices(InvoiceId),
    CONSTRAINT FK_InvoicesItems_ProductId FOREIGN KEY (ProductId) REFERENCES Products(ProductId),
    CONSTRAINT FK_InvoicesItems_BatchId FOREIGN KEY (BatchId) REFERENCES Batches(BatchId)
);
GO

-----------------------------------------------------------
-- 14) Create Payments
--     Records incoming/outgoing payments; links to invoice or party
-----------------------------------------------------------
CREATE TABLE Payments
(
    PaymentId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Type NVARCHAR(10) NOT NULL,              -- 'income' or 'outcome'
    InvoiceId INT NULL,
    PartyType CHAR(1) NOT NULL,                  -- 'C' or 'S' when no invoice
    PartyId INT NULL,                        -- refers to Parties.PartyId
    Amount DECIMAL(10,2) NOT NULL,
    PaymentDate DATE NOT NULL,
    PaymentMethod NVARCHAR(50) NOT NULL,
    Notes NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    -- Cash-drawer attribution (VULN-05): the POS session that physically
    -- received/paid this payment. May differ from the invoice's session
    -- (suspended resume in a later shift, return processed today for an
    -- old sale). NULL = no session / pre-migration rows (session reports
    -- fall back to the invoice's session for those).
    SessionId INT NULL,

    CONSTRAINT CK_Payments_Type CHECK (Type IN ('income','outcome')),
    CONSTRAINT FK_Payments_InvoiceId FOREIGN KEY (InvoiceId) REFERENCES Invoices(InvoiceId),
    CONSTRAINT FK_Payments_PartyId FOREIGN KEY (PartyId) REFERENCES Parties(PartyId),
    CONSTRAINT CK_Payments_PartyType CHECK (PartyType IN ('C','S')),
    CONSTRAINT CK_Payments_Amount CHECK (Amount > 0)

);
GO

-----------------------------------------------------------
-- 15) Create StockMovements
--     Audit log of every stock change
-----------------------------------------------------------
CREATE TABLE StockMovements
(
    MovementId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ProductId INT NOT NULL,
    MovementType NVARCHAR(30) NOT NULL,
    Quantity INT NOT NULL,                   -- positive in, negative out
    SourceType VARCHAR(50) NOT NULL,         -- 'Invoice', 'ManualAdjustment', etc.
    SourceId INT NULL,
    BatchId INT NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT CK_StockMovements_MovementType CHECK (MovementType IN (
        'purchase','sale','customer_return','supplier_return','adjustment_in','adjustment_out'
    )),
    CONSTRAINT FK_StockMovements_ProductId FOREIGN KEY (ProductId) REFERENCES Products(ProductId),
    CONSTRAINT FK_StockMovements_BatchId FOREIGN KEY (BatchId) REFERENCES Batches(BatchId)

);
GO

-----------------------------------------------------------
-- 16) Create Expenses
--     Operational costs (rent, utilities, etc.)
-----------------------------------------------------------
CREATE TABLE Expenses
(
    ExpenseId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Description NVARCHAR(255) NOT NULL,
    Amount DECIMAL(10,2) NOT NULL,
    ExpenseDate DATE NOT NULL,
    Category NVARCHAR(50) NULL,
    Notes NVARCHAR(MAX) NULL,
    UserId INT NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),
    CONSTRAINT CK_Expenses_Amount CHECK (Amount > 0),
    CONSTRAINT FK_Expenses_UserId FOREIGN KEY (UserId) REFERENCES Users(UserId)
);
GO

-----------------------------------------------------------
-- 17) Create SystemLogs
--     User activity tracking
-----------------------------------------------------------
CREATE TABLE SystemLogs
(
    LogId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    UserId INT NOT NULL,
    Action NVARCHAR(100) NOT NULL,
    Details NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NULL CONSTRAINT DF_SystemLogs_CreatedAt DEFAULT (GETDATE()),

    CONSTRAINT FK_SystemLogs_UserId FOREIGN KEY (UserId) REFERENCES Users(UserId)
);
GO

-----------------------------------------------------------
-- 18) Create POSSessions
--     Cash drawer sessions for POS operations
-----------------------------------------------------------
CREATE TABLE POSSessions
(
    SessionId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    UserId INT NOT NULL,
    OpeningAmount DECIMAL(10,2) NOT NULL,
    ClosingAmount DECIMAL(10,2) NULL,
    ExpectedAmount DECIMAL(10,2) NULL,
    StartedAt DATETIME2  DEFAULT (GETDATE()),
    ClosedAt DATETIME2 NULL,
    Status NVARCHAR(10) DEFAULT ('open'),

    CONSTRAINT CK_POSSessions_Status CHECK (Status IN ('open','closed')),
    CONSTRAINT FK_POSSessions_UserId FOREIGN KEY (UserId) REFERENCES Users(UserId)
    
);
GO
-----------------------------------------------------------
-- 19) Link Invoices to POS Sessions
--     Added after POSSessions creation (forward reference)
-----------------------------------------------------------
ALTER TABLE Invoices
    ADD CONSTRAINT FK_Invoices_SessionId FOREIGN KEY (SessionId) REFERENCES POSSessions(SessionId);
GO
ALTER TABLE Payments
    ADD CONSTRAINT FK_Payments_SessionId FOREIGN KEY (SessionId) REFERENCES POSSessions(SessionId);
GO
-----------------------------------------------------------
-- 19a) Create Settings
--      App configuration (store info, VAT, receipt, backup)
-----------------------------------------------------------
CREATE TABLE Settings
(
    SettingKey   NVARCHAR(100)  NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
    SettingValue NVARCHAR(MAX)  NULL,
    UpdatedAt    DATETIME       NULL CONSTRAINT DF_Settings_UpdatedAt DEFAULT (GETDATE())
);
GO
-----------------------------------------------------------
-- 19b) Create CashMovements
--      Cash in/out during POS sessions
-----------------------------------------------------------
CREATE TABLE CashMovements
(
    MovementId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashMovements PRIMARY KEY,
    SessionId    INT           NULL,
    MovementType CHAR(1)       NOT NULL CONSTRAINT CK_CashMovements_Type CHECK (MovementType IN ('I','O')),
    Amount       DECIMAL(10,2) NOT NULL CONSTRAINT DF_CashMovements_Amount DEFAULT (0),
    Reason       NVARCHAR(255) NULL,
    UserId       INT           NOT NULL,
    CreatedAt    DATETIME      NULL CONSTRAINT DF_CashMovements_CreatedAt DEFAULT (GETDATE()),

    CONSTRAINT FK_CashMovements_SessionId FOREIGN KEY (SessionId) REFERENCES POSSessions(SessionId),
    CONSTRAINT FK_CashMovements_UserId    FOREIGN KEY (UserId)    REFERENCES Users(UserId)
);
GO
-----------------------------------------------------------
-- 20) Create Shifts
--     Shift DEFINITIONS ONLY (expected schedule, not attendance)
-----------------------------------------------------------
CREATE TABLE Shifts
(
    ShiftId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL,
    StartTime TIME NOT NULL,
    EndTime TIME NOT NULL,          -- End < Start = crosses midnight (night shift)
    Notes NVARCHAR(255) NULL,

    CONSTRAINT UQ_Shifts_Name UNIQUE (Name),
    CONSTRAINT CK_Shifts_Times CHECK (StartTime <> EndTime)
);
GO
-----------------------------------------------------------
-- 21) Create EmployeeShifts
--     Shift assignment per employee per weekday (bitmask: Sun=1..Sat=64)
-----------------------------------------------------------
CREATE TABLE EmployeeShifts
(
    EmployeeShiftId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EmployeeId INT NOT NULL,
    ShiftId INT NOT NULL,
    DaysOfWeek TINYINT NOT NULL,               -- bitmask 1..127 (bit0=Sunday .. bit6=Saturday)
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,                      -- NULL = open-ended

    CONSTRAINT FK_EmployeeShifts_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES Employees(EmployeeId),
    CONSTRAINT FK_EmployeeShifts_ShiftId FOREIGN KEY (ShiftId) REFERENCES Shifts(ShiftId),
    CONSTRAINT CK_EmployeeShifts_Days CHECK (DaysOfWeek BETWEEN 1 AND 127),
    CONSTRAINT UQ_EmployeeShifts_Active UNIQUE (EmployeeId, ShiftId, EffectiveFrom)
);
GO
-----------------------------------------------------------
-- 22) Create OfficialHolidays
--     Paid official holidays (affect daily payroll; annual = recurring)
-----------------------------------------------------------
CREATE TABLE OfficialHolidays
(
    HolidayId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    HolidayDate DATE NOT NULL,
    DurationDays INT NOT NULL DEFAULT (1),
    IsAnnual BIT NOT NULL DEFAULT (0),

    CONSTRAINT CK_OfficialHolidays_Duration CHECK (DurationDays >= 1)
);
GO
-----------------------------------------------------------
-- 23) Create PayrollRecords
--     Payroll per employee per period; paying creates a linked Expense
-----------------------------------------------------------
CREATE TABLE PayrollRecords(
    PayrollId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EmployeeId INT NOT NULL,
    PeriodFrom DATE NOT NULL,
    PeriodTo DATE NOT NULL,
    SalaryType NVARCHAR(10) NOT NULL,           -- snapshot of employee type at generation
    WorkedUnits DECIMAL(10,2) NOT NULL DEFAULT (0),   -- hours (hourly) or days (daily)
    HolidayUnits DECIMAL(10,2) NOT NULL DEFAULT (0), -- paid holiday days (daily)
    Rate DECIMAL(10,2) NOT NULL DEFAULT (0),
    Gross DECIMAL(10,2) NOT NULL DEFAULT (0),
    Deductions DECIMAL(10,2) NOT NULL DEFAULT (0),
    Additions DECIMAL(10,2) NOT NULL DEFAULT (0),
    Net DECIMAL(10,2) NOT NULL DEFAULT (0),
    Status NVARCHAR(10) NOT NULL DEFAULT ('pending'),  -- pending / paid
    PaidAt DATETIME2 NULL,
    ExpenseId INT NULL,                         -- linked expense created on payment
    Notes NVARCHAR(255) NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT FK_PayrollRecords_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES Employees(EmployeeId),
    CONSTRAINT FK_PayrollRecords_ExpenseId FOREIGN KEY (ExpenseId) REFERENCES Expenses(ExpenseId),
    CONSTRAINT CK_PayrollRecords_Period CHECK (PeriodTo >= PeriodFrom),
    CONSTRAINT CK_PayrollRecords_Status CHECK (Status IN ('pending','paid')),
    CONSTRAINT CK_PayrollRecords_Net CHECK (Net >= 0),
    CONSTRAINT UQ_PayrollRecords_Employee_Period UNIQUE (EmployeeId, PeriodFrom, PeriodTo) -- idempotent generation
);
GO
-----------------------------------------------------------
-- 24) Create StockCounts
--     Physical inventory count lines: book snapshot vs counted, posted via stock adjustment
-----------------------------------------------------------
CREATE TABLE StockCounts
(
    CountId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CountSessionId INT NOT NULL,              -- one physical count session (same value for all lines)
    ProductId INT NOT NULL,
    ProductName NVARCHAR(100) NOT NULL,      -- snapshot of product name at count time
    BookQuantity INT NOT NULL,               -- system quantity snapshot at session start
    CountedQuantity INT NULL,                -- actual counted (null = not counted yet)
    UnitCost DECIMAL(10,2) NOT NULL DEFAULT (0),
    IsPosted BIT NOT NULL DEFAULT (0),       -- difference applied to inventory?
    CountedBy INT NOT NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT FK_StockCounts_ProductId FOREIGN KEY (ProductId) REFERENCES Products(ProductId)
);
GO
-----------------------------------------------------------
-- 20) Create Indexes for Performance
-----------------------------------------------------------

-- Parties
CREATE INDEX IX_Parties_PartyType ON Parties(PartyType);
CREATE INDEX IX_Parties_Name ON Parties(Name);
CREATE INDEX IX_Parties_Email ON Parties(Email);

-- Customers
CREATE INDEX IX_Customers_Balance ON Customers(Balance);

-- Suppliers
CREATE INDEX IX_Suppliers_Balance ON Suppliers(Balance);
CREATE INDEX IX_Suppliers_Category ON Suppliers(Category);

-- Employees
CREATE INDEX IX_Employees_Status ON Employees(Status);
CREATE INDEX IX_Employees_Department ON Employees(Department);

-- EmployeeShifts
CREATE INDEX IX_EmployeeShifts_EmployeeId ON EmployeeShifts(EmployeeId);

-- OfficialHolidays
CREATE INDEX IX_OfficialHolidays_Date ON OfficialHolidays(HolidayDate);

-- PayrollRecords
CREATE INDEX IX_PayrollRecords_EmployeeId ON PayrollRecords(EmployeeId);
CREATE INDEX IX_PayrollRecords_Status ON PayrollRecords(Status);
CREATE INDEX IX_PayrollRecords_Period ON PayrollRecords(PeriodFrom, PeriodTo);

-- StockCounts
CREATE INDEX IX_StockCounts_SessionId ON StockCounts(CountSessionId);
CREATE INDEX IX_StockCounts_ProductId ON StockCounts(ProductId);

-- Users
CREATE INDEX IX_Users_RoleId ON Users(RoleId);
-- one account per linked employee (standalone NULL accounts excluded)
CREATE UNIQUE INDEX UX_Users_OnePerEmployee ON Users(EmployeeId) WHERE EmployeeId IS NOT NULL;

-- Products
CREATE INDEX IX_Products_Type ON Products(Type);
CREATE INDEX IX_Products_Barcode ON Products(Barcode);


-- Batches
CREATE INDEX IX_Batches_ProductId ON Batches(ProductId);
CREATE INDEX IX_Batches_ExpiryDate ON Batches(ExpiryDate);
CREATE INDEX IX_Batches_BatchNumber ON Batches(BatchNumber);

-- Inventory
CREATE INDEX IX_Inventory_QuantityAvailable ON Inventory(QuantityAvailable);

-- Invoices 
CREATE INDEX IX_Invoices_Type_Status ON Invoices(Type, Status);
CREATE INDEX IX_Invoices_PartyType_PartyId ON Invoices(PartyType, PartyId);
CREATE INDEX IX_Invoices_UserId ON Invoices(UserId);
CREATE INDEX IX_Invoices_SessionId ON Invoices(SessionId);
CREATE INDEX IX_Invoices_OriginalInvoiceId ON Invoices(OriginalInvoiceId);
CREATE INDEX IX_Invoices_CreatedAt ON Invoices(CreatedAt);

-- InvoiceItems
CREATE INDEX IX_InvoiceItems_InvoiceId ON InvoiceItems(InvoiceId);
CREATE INDEX IX_InvoiceItems_ProductId ON InvoiceItems(ProductId);
CREATE INDEX IX_InvoiceItems_BatchId ON InvoiceItems(BatchId);

-- Payments
CREATE INDEX IX_Payments_InvoiceId ON Payments(InvoiceId);
CREATE INDEX IX_Payments_PartyType_PartyId ON Payments(PartyType, PartyId);
CREATE INDEX IX_Payments_PaymentDate ON Payments(PaymentDate);
CREATE INDEX IX_Payments_SessionId ON Payments(SessionId);

-- StockMovements
CREATE INDEX IX_StockMovements_ProductId ON StockMovements(ProductId);
CREATE INDEX IX_StockMovements_MovementType ON StockMovements(MovementType);
CREATE INDEX IX_StockMovements_BatchId ON StockMovements(BatchId);

-- SystemLogs
CREATE INDEX IX_SystemLogs_UserId ON SystemLogs(UserId);

-- Expenses
CREATE INDEX IX_Expenses_ExpenseDate ON Expenses(ExpenseDate);
CREATE INDEX IX_Expenses_UserId ON Expenses(UserId);

-- POSSessions
CREATE INDEX IX_POSSessions_UserId ON POSSessions(UserId);
CREATE INDEX IX_POSSessions_Status ON POSSessions(Status);

-- CashMovements
CREATE INDEX IX_CashMovements_SessionId ON CashMovements(SessionId);
CREATE INDEX IX_CashMovements_UserId ON CashMovements(UserId);
CREATE INDEX IX_CashMovements_CreatedAt ON CashMovements(CreatedAt);

-----------------------------------------------------------
-- Concurrency guards: one open POS session per user.
-- Filtered unique index — enforced by the database engine,
-- protects against double-click / parallel open requests
-- even when the application check (AnyAsync) is bypassed.
-- (session SET options were applied at the top of this script)
-----------------------------------------------------------
CREATE UNIQUE INDEX UX_POSSessions_OneOpenPerUser
    ON POSSessions(UserId)
    WHERE Status = 'open';
GO

-----------------------------------------------------------
-- Financial amount guards (VULN-02/06, defense-in-depth
-- under the service-level validation):
--   1) sale/purchase invoices can never have a negative net
--      (return invoices are negative BY DESIGN and excluded);
--   2) party balances can never go negative.
-----------------------------------------------------------
ALTER TABLE Invoices
    ADD CONSTRAINT CK_Invoices_NonNegativeNet
    CHECK ([Type] IN ('customer_return', 'supplier_return') OR NetAmount >= 0);
GO

ALTER TABLE Suppliers
    ADD CONSTRAINT CK_Suppliers_NonNegativeBalance CHECK (Balance >= 0);
GO

ALTER TABLE Customers
    ADD CONSTRAINT CK_Customers_NonNegativeBalance CHECK (Balance >= 0);
GO
-----------------------------------------------------------
-- 25) Seed data (minimum for a fresh install to boot)
--     Roles + starter accounts + one demo stock product
--     (login: admin / cashier1 - passwords were set by the
--      owner during installation; change them immediately)
-----------------------------------------------------------

-- Roles (permission values: 100 admin, 75 accountant, 50 cashier)
INSERT INTO Roles (Name, RoleValue) VALUES
(N'مدير النظام', 100),
(N'كاشير', 50),
(N'محاسب', 75);

-- Starter accounts (BCrypt hashes; reset from inside the app)
INSERT INTO Users (Username, PasswordHash, RoleId, IsActive, CreatedAt) VALUES
(N'admin',   N'$2a$11$oCgAsF162xgzRYlaBa7elOoAmjeMBbp9p6B2NukyhmTetpjmvenhq', 1, 1, GETUTCDATE()),
(N'cashier1',N'$2a$11$WNoLkPsWn5BGq3pwwbZsy.BHCIlgru8PHsj1JNyqopT12/RQI9LIi', 2, 1, GETUTCDATE());

-- Demo stock product (delete after real catalog import)
INSERT INTO Products (Name, Barcode, Unit, Type, BasePrice, CostPrice, ReorderLevel, IsActive, CreatedAt)
VALUES (N'منتج تجريبي', N'6281002123450', N'قطعة', 'stock', 12.50, 6.00, 5, 1, GETUTCDATE());

-- Opening inventory for the demo product
INSERT INTO Inventory (ProductId, QuantityAvailable, QuantityReserved, UpdatedAt)

VALUES (SCOPE_IDENTITY(), 500, 0, GETUTCDATE());
GO

-----------------------------------------------------------
-- Demo customer (harness settles test debt against party id 1)
-----------------------------------------------------------
INSERT INTO Parties (PartyType, Name, IsActive, CreatedAt)
VALUES ('C', N'Demo Customer', 1, GETUTCDATE());

INSERT INTO Customers (PartyId, Balance, CreditLimit)
VALUES (SCOPE_IDENTITY(), 0, 0);
GO