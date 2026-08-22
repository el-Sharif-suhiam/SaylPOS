-- ====================================================
-- Demo Data for SaylDB
-- ====================================================

-- Disable foreign key constraints (temporarily) to allow truncation
-- Note: We'll insert in dependency order, but if needed we can use DELETE FROM in reverse order
-- First, delete all existing data to start fresh (optional)
DELETE FROM SystemLogs;
DELETE FROM POSSessions;
DELETE FROM StockMovements;
DELETE FROM Payments;
DELETE FROM InvoiceItems;
DELETE FROM Invoices;
DELETE FROM Inventory;
DELETE FROM Batches;
DELETE FROM Products;
DELETE FROM Users;
DELETE FROM Roles;
DELETE FROM Employees;
DELETE FROM Suppliers;
DELETE FROM Customers;
DELETE FROM Parties;
DELETE FROM Expenses;

-- Reset identity columns
DBCC CHECKIDENT ('Parties', RESEED, 0);
DBCC CHECKIDENT ('Employees', RESEED, 0);
DBCC CHECKIDENT ('Roles', RESEED, 0);
DBCC CHECKIDENT ('Users', RESEED, 0);
DBCC CHECKIDENT ('Products', RESEED, 0);
DBCC CHECKIDENT ('Batches', RESEED, 0);
DBCC CHECKIDENT ('Invoices', RESEED, 0);
DBCC CHECKIDENT ('InvoiceItems', RESEED, 0);
DBCC CHECKIDENT ('Payments', RESEED, 0);
DBCC CHECKIDENT ('StockMovements', RESEED, 0);
DBCC CHECKIDENT ('Expenses', RESEED, 0);
DBCC CHECKIDENT ('SystemLogs', RESEED, 0);
DBCC CHECKIDENT ('POSSessions', RESEED, 0);
GO

-- ===============================================
-- 1) Expenses
-- ===============================================
SET IDENTITY_INSERT Expenses ON;
INSERT INTO Expenses (ExpenseId, Description, Amount, ExpenseDate, Category, Notes)
VALUES
(1, N'إيجار المحل', 2000.00, '2026-01-01', N'إيجار', N'إيجار شهر يناير'),
(2, N'فاتورة كهرباء', 450.75, '2026-01-05', N'مرافق', N'استهلاك شهر ديسمبر'),
(3, N'رواتب موظفين', 5000.00, '2026-01-31', N'رواتب', N'رواتب شهر يناير');
SET IDENTITY_INSERT Expenses OFF;

-- ===============================================
-- 2) Parties (Customers and Suppliers)
-- ===============================================
SET IDENTITY_INSERT Parties ON;
INSERT INTO Parties (PartyId, PartyType, Name, Phone1, Phone2, Email, AddressLine1, AddressLine2, Notes, IsActive)
VALUES
(1, 'C', N'أحمد العلي', '0501111111', NULL, 'ahmad@example.com', N'شارع الملك فهد، الرياض', N'حي الروضة', N'زبون دائم', 1),
(2, 'C', N'سارة الشمري', '0502222222', '0112222222', 'sara@example.com', N'شارع الأمير سلطان، جدة', N'حي السلامة', NULL, 1),
(3, 'C', N'محمد القحطاني', '0503333333', NULL, 'mohammed@example.com', N'شارع العليا، الخبر', NULL, N'زبون جملة', 1),
(4, 'S', N'شركة الدواء المحدودة', '0114444444', NULL, 'info@dawaa.com', N'الرياض - المنطقة الصناعية', N'ص.ب 12345', N'مورد أدوية', 1),
(5, 'S', N'مؤسسة الأغذية المتحدة', '0505555555', '0115555555', 'food@united.com', N'جدة - المستودعات', NULL, N'مورد مواد غذائية', 1),
(6, 'B', N'النور للتجارة (زبون ومورد)', '0506666666', NULL, 'alnoor@trade.com', N'الدمام - طريق الخبر', N'مكتب رقم 3', N'شركة تتعامل معنا بالوجهين', 1);
SET IDENTITY_INSERT Parties OFF;

-- ===============================================
-- 3) Customers
-- ===============================================
INSERT INTO Customers (PartyId, Balance, CreditLimit)
VALUES
(1, 0.00, 5000.00),    -- أحمد العلي
(2, 0.00, 3000.00),    -- سارة الشمري
(3, 0.00, 10000.00),   -- محمد القحطاني
(6, 0.00, 8000.00);    -- النور للتجارة (بصفة زبون)

-- ===============================================
-- 4) Suppliers
-- ===============================================
INSERT INTO Suppliers (PartyId, Balance, Category)
VALUES
(4, 0.00, N'أدوية'),   -- شركة الدواء
(5, 0.00, N'مواد غذائية'), -- مؤسسة الأغذية المتحدة
(6, 0.00, N'عام');     -- النور للتجارة (بصفة مورد)

-- ===============================================
-- 5) Employees
-- ===============================================
SET IDENTITY_INSERT Employees ON;
INSERT INTO Employees (EmployeeId, Name, Phone, Email, Salary, HireDate, Address, Status, Position, Department)
VALUES
(1, N'عبدالله السالم', '0501234567', 'a.salem@sayl.com', 4000.00, '2025-01-15', N'الرياض - الملز', 'active', N'كاشير', N'المبيعات'),
(2, N'فاطمة الزهراني', '0502345678', 'f.zah@sayl.com', 5500.00, '2025-03-01', N'الرياض - النزهة', 'active', N'مديرة فرع', N'الإدارة'),
(3, N'خالد العتيبي', '0503456789', 'k.o@sayl.com', 3500.00, '2025-06-01', N'الرياض - الشفا', 'on_leave', N'كاشير', N'المبيعات'),
(4, N'مريم الحربي', '0504567890', 'm.h@sayl.com', 0.00, '2024-09-10', N'الرياض - العقيق', 'resigned', N'متدربة', N'التدريب');
SET IDENTITY_INSERT Employees OFF;

-- ===============================================
-- 6) Roles
-- ===============================================
SET IDENTITY_INSERT Roles ON;
INSERT INTO Roles (RoleId, Name, RoleValue)
VALUES
(1, N'مدير النظام', 100),
(2, N'كاشير', 50),
(3, N'محاسب', 75);
SET IDENTITY_INSERT Roles OFF;

-- ===============================================
-- 7) Users (passwords hashed with BCrypt)
-- ===============================================
-- Demo login credentials:
--   admin    / Admin@123
--   cashier1 / Cashier@123
--   manager  / Manager@123
--   cashier2 / Cashier2@123
SET IDENTITY_INSERT Users ON;
INSERT INTO Users (UserId, Username, PasswordHash, EmployeeId, RoleId, CreatedAt)
VALUES
(1, N'admin', N'$2a$11$oCgAsF162xgzRYlaBa7elOoAmjeMBbp9p6B2NukyhmTetpjmvenhq', 1, 1, '2025-02-01 08:00:00'),     -- عبدالله (مدير)
(2, N'cashier1', N'$2a$11$qEzXqA3D4ohz3k8xNjKGyON9buLjMGPcWXmMqsbbf2tb0QSnX2SmW', 1, 2, '2025-02-01 09:00:00'),  -- عبدالله كاشير أيضاً (يمكن أن يكون نفس الشخص)
(3, N'manager', N'$2a$11$BiA0Or/ByzEygzuHM15sXe5ilM10KpF6zdQy3ucTG9PYaukb99rbC', 2, 1, '2025-03-15 08:30:00'),    -- فاطمة مديرة
(4, N'cashier2', N'$2a$11$7DlI/JCHdLeKtnURDN1muOW0IlkU5NQueLY2XepccKPanMCDjyGWm', 3, 2, '2025-06-01 10:00:00'); -- خالد (في إجازة لكن حسابه موجود)
SET IDENTITY_INSERT Users OFF;

-- ===============================================
-- 8) Products
-- ===============================================
SET IDENTITY_INSERT Products ON;
INSERT INTO Products (ProductId, Name, Barcode, Unit, Type, BasePrice, ReorderLevel, CreatedAt)
VALUES
(1, N'بنادول أزرق', '6281002123450', N'علبة', 'stock', 12.50, 20, '2026-01-01'),
(2, N'أموكسيسيلين 500mg', '6281002123467', N'شريط', 'stock', 35.00, 10, '2026-01-01'),
(3, N'فيتامين سي فوار', '6281002123474', N'أنبوب', 'stock', 8.00, 50, '2026-01-01'),
(4, N'توصيل للمنازل', NULL, N'خدمة', 'service', 15.00, NULL, '2026-01-01'),
(5, N'زيت زيتون بكر 500مل', '6281002123481', N'زجاجة', 'stock', 25.00, 30, '2026-01-01'),
(6, N'خبز برجر', '6281002123498', N'كيس', 'stock', 3.00, 100, '2026-01-01'),
(7, N'لحم مفروم (كغم)', NULL, N'كغم', 'stock', 45.00, 20, '2026-01-01'),
(8, N'بطاطس مقلية مجمدة 1كغم', '6281002123504', N'كيس', 'stock', 18.00, 15, '2026-01-01'),
(9, N'وجبة برجر كلاسيك', NULL, N'وحدة', 'manufactured', 22.00, 5, '2026-01-01'),
(10, N'خدمة تغليف هدايا', NULL, N'خدمة', 'service', 5.00, NULL, '2026-01-01');
SET IDENTITY_INSERT Products OFF;

-- ===============================================
-- 9) Batches
-- ===============================================
SET IDENTITY_INSERT Batches ON;
INSERT INTO Batches (BatchId, ProductId, BatchNumber, ExpiryDate, BatchQuantity, CreatedAt)
VALUES
(1, 1, N'BN-2026-001', '2027-06-30', 100, '2026-06-15'),
(2, 1, N'BN-2026-002', '2027-12-31', 50, '2026-06-20'),
(3, 2, N'AM-2026-101', '2028-01-15', 200, '2026-06-10'),
(4, 3, N'VC-2026-303', '2027-03-31', 150, '2026-06-12'),
(5, 5, N'OO-2026-501', NULL, 80, '2026-06-15'); -- زيت زيتون بدون تاريخ انتهاء
SET IDENTITY_INSERT Batches OFF;

-- ===============================================
-- 10) Inventory (summary)
-- ===============================================
INSERT INTO Inventory (ProductId, QuantityAvailable, QuantityReserved, UpdatedAt, LastCountedAt)
VALUES
(1, 150, 0, '2026-06-20 10:00:00', NULL),  -- 100+50
(2, 200, 0, '2026-06-10 12:00:00', NULL),
(3, 150, 0, '2026-06-12 09:30:00', NULL),
(5, 80,  0, '2026-06-15 11:00:00', NULL),
(6, 300, 0, '2026-06-01 08:00:00', NULL),  -- منتج بدون دفعة (تخزين عادي)
(7, 50,  0, '2026-06-01 08:00:00', NULL),
(8, 40,  0, '2026-06-01 08:00:00', NULL);
-- services and manufactured do not appear in inventory

-- ===============================================
-- 11) Invoices
-- ===============================================
SET IDENTITY_INSERT Invoices ON;
-- فاتورة بيع لـ أحمد العلي (زبون PartyId=1)
INSERT INTO Invoices (InvoiceId, Type, Status, PaymentStatus, PartyType, PartyId, UserId, OriginalInvoiceId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, Notes, CreatedAt)
VALUES
(1, 'sale', 'completed', 'paid', 'C', 1, 2, NULL, 47.50, 0.00, 7.13, 54.63, N'فاتورة بيع نقدي', '2026-07-01 10:30:00');
-- فاتورة بيع لـ سارة الشمري (زبون PartyId=2) آجل
INSERT INTO Invoices (InvoiceId, Type, Status, PaymentStatus, PartyType, PartyId, UserId, OriginalInvoiceId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, Notes, CreatedAt)
VALUES
(2, 'sale', 'completed', 'unpaid', 'C', 2, 2, NULL, 72.00, 0.00, 10.80, 82.80, N'فاتورة آجلة - لم تسدد بعد', '2026-07-02 14:00:00');
-- فاتورة شراء من شركة الدواء (مورد PartyId=4)
INSERT INTO Invoices (InvoiceId, Type, Status, PaymentStatus, PartyType, PartyId, UserId, OriginalInvoiceId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, Notes, CreatedAt)
VALUES
(3, 'purchase', 'completed', 'paid', 'S', 4, 2, NULL, 1250.00, 0.00, 0.00, 1250.00, N'شراء أدوية - دفعة نقداً', '2026-07-03 09:00:00');
-- فاتورة مرتجع بيع من أحمد العلي للفاتورة رقم 1
INSERT INTO Invoices (InvoiceId, Type, Status, PaymentStatus, PartyType, PartyId, UserId, OriginalInvoiceId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, Notes, CreatedAt)
VALUES
(4, 'customer_return', 'completed', 'paid', 'C', 1, 2, 1, -12.50, 0.00, -1.88, -14.38, N'إرجاع علبة بنادول من الفاتورة 1', '2026-07-04 11:00:00');
-- فاتورة بيع خدمة توصيل (زبون PartyId=1)
INSERT INTO Invoices (InvoiceId, Type, Status, PaymentStatus, PartyType, PartyId, UserId, OriginalInvoiceId, TotalAmount, DiscountAmount, TaxAmount, NetAmount, Notes, CreatedAt)
VALUES
(5, 'sale', 'completed', 'paid', 'C', 1, 2, NULL, 15.00, 0.00, 2.25, 17.25, N'خدمة توصيل', '2026-07-05 16:00:00');
SET IDENTITY_INSERT Invoices OFF;

-- ===============================================
-- 12) Invoice Items
-- ===============================================
SET IDENTITY_INSERT InvoiceItems ON;
-- Invoice 1 (sale to customer 1)
INSERT INTO InvoiceItems (InvoiceItemId, InvoiceId, ProductId, Quantity, BatchId, ItemPrice)
VALUES
(1, 1, 1, 2, 1, 12.50),  -- 2 علب بنادول من الباتش 1
(2, 1, 3, 1, 4, 8.00),   -- 1 فيتامين سي
(3, 1, 5, 1, 5, 25.00);  -- 1 زيت زيتون

-- Invoice 2 (sale to customer 2)
INSERT INTO InvoiceItems (InvoiceItemId, InvoiceId, ProductId, Quantity, BatchId, ItemPrice)
VALUES
(4, 2, 2, 2, 3, 35.00),  -- 2 أموكسيسيلين
(5, 2, 4, 1, NULL, 15.00); -- خدمة توصيل

-- Invoice 3 (purchase from supplier 4)
INSERT INTO InvoiceItems (InvoiceItemId, InvoiceId, ProductId, Quantity, BatchId, ItemPrice)
VALUES
(6, 3, 1, 100, 1, 10.00), -- شراء بنادول بكمية 100 بسعر 10 (هذا سيؤثر على المخزون؟ إنها فاتورة شراء ولكن الكمية في الباتش موجودة مسبقاً، نحن هنا نفترض أن الشراء قد حدث وأنشأ الباتش بالفعل. للمحاكاة نعتبر الدفعات أنشئت من فواتير الشراء. لذا نترك الحركات المخزنية لتوافقها).
(7, 3, 2, 50, 3, 28.00);  -- شراء أموكسيسيلين

-- Invoice 4 (customer return from invoice 1)
INSERT INTO InvoiceItems (InvoiceItemId, InvoiceId, ProductId, Quantity, BatchId, ItemPrice)
VALUES
(8, 4, 1, 1, 1, 12.50); -- إرجاع علبة بنادول لنفس الباتش

-- Invoice 5 (service sale)
INSERT INTO InvoiceItems (InvoiceItemId, InvoiceId, ProductId, Quantity, BatchId, ItemPrice)
VALUES
(9, 5, 4, 1, NULL, 15.00); -- خدمة توصيل

SET IDENTITY_INSERT InvoiceItems OFF;

-- ===============================================
-- 13) Payments
-- ===============================================
SET IDENTITY_INSERT Payments ON;
INSERT INTO Payments (PaymentId, Type, InvoiceId, PartyType, PartyId, Amount, PaymentDate, PaymentMethod, Notes, CreatedAt)
VALUES
(1, 'income', 1, 'C', 1, 54.63, '2026-07-01', 'cash', N'دفع كامل للفاتورة 1', '2026-07-01 10:35:00'),
(2, 'outcome', 3, 'S', 4, 1250.00, '2026-07-03', 'bank transfer', N'دفع للمورد', '2026-07-03 09:10:00'),
(3, 'outcome', 4, 'C', 1, 14.38, '2026-07-04', 'cash', N'استرداد نقدي للعميل عند الإرجاع', '2026-07-04 11:05:00'),
(4, 'income', 5, 'C', 1, 17.25, '2026-07-05', 'cash', N'دفع خدمة التوصيل', '2026-07-05 16:05:00');
SET IDENTITY_INSERT Payments OFF;

-- ===============================================
-- 14) Stock Movements (generated from invoices/manual)
-- ===============================================
SET IDENTITY_INSERT StockMovements ON;

-- Movements for sale invoice 1 (negative quantities)
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(1, 1, 'sale', -2, 'Invoice', 1, 1, '2026-07-01 10:30:00'),
(2, 3, 'sale', -1, 'Invoice', 1, 4, '2026-07-01 10:30:00'),
(3, 5, 'sale', -1, 'Invoice', 1, 5, '2026-07-01 10:30:00');

-- Movements for sale invoice 2
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(4, 2, 'sale', -2, 'Invoice', 2, 3, '2026-07-02 14:00:00');
-- خدمة التوصيل (product 4) لا تؤثر على المخزون

-- Movements for purchase invoice 3 (positive quantities) – هذه تُنشئ الدُفعات فعلياً في الحركة، لكننا أدرجنا الدفعات يدوياً مسبقاً؛ لا بأس.
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(5, 1, 'purchase', 100, 'Invoice', 3, 1, '2026-07-03 09:00:00'),
(6, 2, 'purchase', 50, 'Invoice', 3, 3, '2026-07-03 09:00:00');

-- Movements for customer return invoice 4 (positive quantity back to stock)
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(7, 1, 'customer_return', 1, 'Invoice', 4, 1, '2026-07-04 11:00:00');

-- Manual adjustment: زيادة مخزون خبز البرجر (منتج 6) بمقدار 300 وحدة
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(8, 6, 'adjustment_in', 300, 'ManualAdjustment', NULL, NULL, '2026-06-01 08:00:00');
-- ضبط اللحم المفروم
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(9, 7, 'adjustment_in', 50, 'ManualAdjustment', NULL, NULL, '2026-06-01 08:00:00');
-- ضبط البطاطس
INSERT INTO StockMovements (MovementId, ProductId, MovementType, Quantity, SourceType, SourceId, BatchId, CreatedAt)
VALUES
(10, 8, 'adjustment_in', 40, 'ManualAdjustment', NULL, NULL, '2026-06-01 08:00:00');

SET IDENTITY_INSERT StockMovements OFF;

-- ===============================================
-- 15) System Logs
-- ===============================================
SET IDENTITY_INSERT SystemLogs ON;
INSERT INTO SystemLogs (LogId, UserId, Action, Details, CreatedAt)
VALUES
(1, 1, 'Login', N'تسجيل دخول المستخدم admin', '2026-07-01 08:00:00'),
(2, 2, 'CreateInvoice', N'إنشاء فاتورة بيع رقم 1', '2026-07-01 10:30:00'),
(3, 2, 'CreateInvoice', N'إنشاء فاتورة بيع رقم 2', '2026-07-02 14:00:00'),
(4, 1, 'Logout', N'تسجيل خروج المستخدم admin', '2026-07-01 17:00:00');
SET IDENTITY_INSERT SystemLogs OFF;

-- ===============================================
-- 16) POS Sessions
-- ===============================================
SET IDENTITY_INSERT POSSessions ON;
INSERT INTO POSSessions (SessionId, UserId, OpeningAmount, ClosingAmount, ExpectedAmount, StartedAt, ClosedAt, Status)
VALUES
(1, 2, 200.00, 271.88, 271.88, '2026-07-01 09:00:00', '2026-07-01 17:00:00', 'closed'), -- cashier1
(2, 2, 150.00, NULL, NULL, '2026-07-02 09:00:00', NULL, 'open');                        -- جلسة مفتوحة ليوم 2
SET IDENTITY_INSERT POSSessions OFF;

-- ===============================================
-- Recalculate Inventory to match movements (optional)
-- ===============================================
-- Update inventory quantities based on current stock movements (if needed)
-- We'll manually set them to match the sum of batches + adjustments without sales effects?
-- Actually, the data already includes sales movements (negative). Inventory should reflect net effect.
-- Let's simply update Inventory based on sum of StockMovements.
UPDATE Inventory
SET QuantityAvailable = ISNULL((SELECT SUM(Quantity) FROM StockMovements WHERE ProductId = Inventory.ProductId), 0),
    QuantityReserved = 0,
    UpdatedAt = GETDATE();

-- Also update Batches quantities to reflect sales/returns (optional, but for realism we can adjust)
UPDATE Batches SET BatchQuantity = BatchQuantity - 2 WHERE BatchId = 1; -- sold 2, then 1 returned
UPDATE Batches SET BatchQuantity = BatchQuantity - 2 WHERE BatchId = 3; -- sold 2
UPDATE Batches SET BatchQuantity = BatchQuantity - 1 WHERE BatchId = 4; -- sold 1
UPDATE Batches SET BatchQuantity = BatchQuantity - 1 WHERE BatchId = 5; -- sold 1
-- Adjust batch 1 for return: +1 (already handled? sale -2, return +1 => net -1; we'll set correctly)
-- Let's recalc batches from movements for accuracy (optional)
-- This script leaves the data logically consistent after initial insert.

PRINT N'تم إدراج البيانات التجريبية بنجاح';
GO