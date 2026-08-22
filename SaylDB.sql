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
===========================================================
*/

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
-- 2) Drop existing objects if they already exist
--    (Useful while rebuilding during development)
-----------------------------------------------------------

-- Drop foreign keys first by dropping tables in dependency order
IF OBJECT_ID('dbo.SystemLogs', 'U') IS NOT NULL DROP TABLE dbo.SystemLogs;
IF OBJECT_ID('dbo.POSSessions', 'U') IS NOT NULL DROP TABLE dbo.POSSessions;
IF OBJECT_ID('dbo.StockMovements', 'U') IS NOT NULL DROP TABLE dbo.StockMovements;
IF OBJECT_ID('dbo.Payments', 'U') IS NOT NULL DROP TABLE dbo.Payments;
IF OBJECT_ID('dbo.InvoiceItems', 'U') IS NOT NULL DROP TABLE dbo.InvoiceItems;
IF OBJECT_ID('dbo.Invoices', 'U') IS NOT NULL DROP TABLE dbo.Invoices;
IF OBJECT_ID('dbo.Inventory', 'U') IS NOT NULL DROP TABLE dbo.Inventory;
IF OBJECT_ID('dbo.Batches', 'U') IS NOT NULL DROP TABLE dbo.Batches;
IF OBJECT_ID('dbo.Products', 'U') IS NOT NULL DROP TABLE dbo.Products;
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL DROP TABLE dbo.Users;
IF OBJECT_ID('dbo.Roles', 'U') IS NOT NULL DROP TABLE dbo.Roles;
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
IF OBJECT_ID('dbo.Suppliers', 'U') IS NOT NULL DROP TABLE dbo.Suppliers;
IF OBJECT_ID('dbo.Customers', 'U') IS NOT NULL DROP TABLE dbo.Customers;
IF OBJECT_ID('dbo.Parties', 'U') IS NOT NULL DROP TABLE dbo.Parties;
IF OBJECT_ID('dbo.Expenses', 'U') IS NOT NULL DROP TABLE dbo.Expenses;
GO

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
    HireDate DATE NULL,
    Address NVARCHAR(400) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT ('active'),
    Position NVARCHAR(50) NOT NULL,
    Department NVARCHAR(50) NULL,
    CreatedAt DATETIME2 DEFAULT (GETDATE()),

    CONSTRAINT CK_Employees_Status CHECK (Status IN ('active','on_leave','resigned'))
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
    CONSTRAINT FK_Users_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES Employees(EmployeeId),
    CONSTRAINT FK_Users_RoleId FOREIGN KEY (RoleId) REFERENCES Roles(RoleId),

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

-- Users
CREATE INDEX IX_Users_RoleId ON Users(RoleId);

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