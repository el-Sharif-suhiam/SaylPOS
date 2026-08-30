# AGENTS.md — Project Context for Sayl

## 1. Overview
Sayl is a sales and inventory management system built on C# / .NET 10 with SQL Server and EF Core.
It serves retail stores, pharmacies, restaurants, and small businesses.
The UI is WinForms (built): LoginForm, PosForm (cashier), MainForm dashboard with 13 sections.
All backend services are implemented. Remaining work is listed in TASK.md.

## 2. Architecture
- Single-layer services: services combine business logic and data access.
- `SaylDbContext` is injected into services via constructor.
- Do NOT pass `DbContext` as a parameter to methods.
- All services return `Result` or `Result<T>`.
- Do not throw exceptions to the end user except in internal technical cases.

## 3. Projects
- `Common`: contains DTOs, Enums, Results, Validation.
- `Sayl.Core` (folder `Sayl_DataAccess`): contains Models, DbContext, Services.
- `DependencyInjection`: DI registration extensions.
- `Sayl.UI`: WinForms UI (Forms, UserControls, Infrastructure/AppTheme).
- `PosLicensing`: offline RSA-2048 licensing library (HWID-bound licenses).
- `KeygenTool`: vendor-only WinForms tool that generates key pairs and issues licenses (never ship it).
- `installer/`: Inno Setup script (English wizard) + build-installer.ps1; auto-installs SQL Express, creates SaylDB, supports MAIN/TERMINAL roles.
- Licensing is enforced at startup in Sayl.UI Program.cs (`ActivationManager.IsActivated()` before login).

## 4. Database (main tables)
- `Parties`: parties (customers/suppliers). Columns: PartyId, PartyType, Name, Phone...
- `Customers`: linked to Parties via PartyId (PK,FK). Extra columns: Balance, CreditLimit.
- `Suppliers`: linked to Parties. Extra columns: Balance, Category.
- `Employees`: employee data.
- `Roles` and `Users`: users and roles.
- `Products`: products. `Type` can be stock, service, manufactured.
- `Batches`: batches. Columns: BatchId, ProductId, BatchNumber, ExpiryDate, BatchQuantity.
- `Inventory`: stock summary. Columns: ProductId, QuantityAvailable, QuantityReserved.
- `StockMovements`: stock movement log.
- `Invoices` and `InvoiceItems`: invoices and line items.
- `Payments`: payments.
- `Expenses`: expenses.
- `POSSessions`: POS sessions.
- `SystemLogs`: activity logs.

## 5. Coding patterns
- **DTOs**: transfer objects between layers. EF entities are not exposed to UI.
- **Result**: `Result.Ok(msg)`, `Result.Fail(msg)`, `Result<T>.Ok(data, msg)`, `Result<T>.Fail(msg)`.
- **Partial update**: when updating an entity, only fields that are sent (non-empty/non-null) are updated.
- **FEFO**: in sales, batches are selected automatically by expiry date (oldest first).
- **Suspended invoice**: when suspending, stock is reserved (increase `QuantityReserved`) without deducting quantity. When completing, actual deduction happens and reservation is released.
- **Transactions**: complex operations (Checkout) use `BeginTransactionAsync` with full commit/rollback.

## 6. Implemented services (all of them)
- `clsPartyService`, `clsEmployeeService`, `clsCustomerService`, `clsSupplierService`
- `clsUsersService`: login/logout/change password/user management (+`clsPermissions`)
- `clsProductService`: CRUD, barcode lookup, batches list
- `clsRoleService`, `clsExpenseService`, `clsSettingsService`, `clsBackupService`
- `clsStockService`: receive/sell (FEFO) + manual adjustment + batch transfer
- `clsCheckoutService`: full checkout with suspended invoices
- `clsInvoiceService`: lists, details, print model; `clsPurchaseService`: purchase invoices (used by PurchaseForm)
- `clsPaymentService`, `clsReturnService` (customer/supplier), `clsPosSessionService`, `clsCashService`
- `clsReportService`: daily sales, inventory status, profit (COGS), top selling, dashboard summary, sales trend, expiring batches

## 7. Remaining services
None. Remaining work is outside services — see TASK.md (DB restore, tests, license hardening).

## 8. Agent instructions
- Do not change current design or names.
- Do not make large architectural changes unless asked.
- Follow existing patterns.
- Write code compatible with EF Core 10 and C#.
- When building WinForms later, use `UserControl` inside a main panel.
- Review code carefully before delivering.
- If a database change is needed, tell me first.

## 9. Suggested work plan
1. Implement remaining services.
2. Build WinForms UI (login, main dashboard, POS).
3. Link UI to services.
4. Test scenarios.