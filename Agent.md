# AGENTS.md — Project Context for Sayl

## 1. Overview
Sayl is a sales and inventory management system built on C# / .NET 10 with SQL Server and EF Core.
It serves retail stores, pharmacies, and small businesses.
The UI is WinForms: `LoginForm`, `PosForm` (cashier), and `MainForm` with 13 sections.
All backend services are implemented. Remaining work is listed in TASK.md.

## 2. Architecture (read before writing code)
- **Single-layer services:** business logic, validation, and data access live together
  in the service classes. Despite any "Business layer" naming, those services live in
  `Sayl_DataAccess/Services` (project `Sayl.Core`). There is no separate repository or
  business-manager layer.
- `SaylDbContext` is injected into services via constructor. Do NOT pass `DbContext`
  as a method parameter.
- All services return `Result` or `Result<T>` (`Common.Results`). Do not throw
  exceptions to the end user except in internal technical cases (guard failures inside
  a transaction are caught and converted to failed results at the operation boundary).
- DTOs cross the UI/service boundary; EF entities are never exposed to the UI.
- **Authorization lives at the service boundary.** The UI hides unauthorized screens
  (cosmetic only); every state-changing service operation checks the actor's role
  bitmask via `clsPermissions.GetUserRoleValueAsync` + `AppPermissions.Has`.
- **Time convention:** all transactional timestamps are written in UTC
  (`DateTime.UtcNow` in EF, `GETUTCDATE()` in raw SQL). The "shop day" is
  `Common.Utils.ProgramClock` = UTC + 3 (fixed offset) — shared by FEFO expiry, daily
  reports, payment dates, and fiscal boundaries. Never use `ToLocalTime()` or
  `GETDATE()` in new code.
- **Stored procedures: none exist.** The schema contains tables, indexes, and CHECK
  constraints only. Data-level logic (raw atomic SQL, `sp_getapplock`, CAS
  transitions) stays as-is unless a demonstrated defect requires change.
- **Transactions:** multi-step operations use explicit EF Core transactions
  (Serializable where a read-check-write race matters), `sp_getapplock` named locks
  (stock-count open/post), and CAS-style conditional UPDATEs. Follow the existing
  envelope: permission check first, transaction, re-checks inside the transaction,
  commit, rollback on failure.

## 3. Projects
- `Common`: DTOs, enums, `Result`, `AppPermissions`, `ProgramClock`, validation helpers.
- `Sayl.Core` (folder `Sayl_DataAccess`): models, `SaylDbContext`, services.
- `DependencyInjection`: `AddSaylServices` DI registration.
- `Sayl.UI`: WinForms UI (Forms, UserControls, Infrastructure) + printing + licensing gate.
- `PosLicensing`: offline RSA-2048 licensing library (HWID-bound licenses).
- `KeygenTool`: vendor-only tool that issues licenses (never ship it).
- `installer/`: Inno Setup script + build script; MAIN role installs app + SQL Express +
  SaylDB; TERMINAL role points an extra PC at the host's SQL instance over LAN.
- `Sayl.Tests`: xUnit integration tests against real LocalDB with isolated per-run
  databases; `consol app` is a console E2E QA harness (scratch DB by default).
- The top-level `Services/` folder is legacy leftover code, not part of the solution.

## 4. Database (main tables)
- `Parties`: base entity for customers/suppliers (`PartyType` C/S/B).
- `Customers` / `Suppliers`: extend `Parties` (PK = FK `PartyId`); balances, credit limit.
- `Employees`, `Roles` (`RoleValue` bitmask; `RoleId` is TINYINT), `Users`
  (BCrypt hashes, optional employee link, one account per employee).
- `Products` (`Type` = stock/service/manufactured), `Batches` (lot control, `UnitCost`
  = acquisition cost = COGS source, `IsQuarantined`), `Inventory` (available/reserved
  summary), `StockReservations` (per-suspended-invoice ownership).
- `Invoices` (unified: sale/purchase/customer_return/supplier_return; returns negative
  by design, `OriginalInvoiceId`), `InvoiceItems` (one row per consumed batch),
  `Payments` (single cash ledger, drawer `SessionId` attribution).
- `StockMovements` (append-only audit; `SourceType`/`SourceId` provenance) and
  `StockCounts` (physical counts). The idempotency pair: the `StockCount` class's
  `PostedMovementId` links to the movement written with `StockMovements.SourceId =
  CountId`.
- `Expenses`, `POSSessions`, `CashMovements`, `Settings` (key/value, incl.
  `FiscalYearStart`/`FiscalYearClosedOn`), `SystemLogs` (audit).
- Schema source of truth: `SaylDB.sql` (fresh installs only — safety gate aborts if any
  table exists; no DROP statements). Idempotent `Upgrade_09..13` scripts migrate older
  databases; the app also runs three idempotent schema upgrades at startup.
- Documentation: `docs/database/` (overview, ER model, stored-procedure status).

## 5. Coding patterns
- **DTOs** cross layer boundaries; **partial update**: only sent (non-empty/non-null)
  fields are modified.
- **FEFO:** consumption order is first-expiry-first-out (null expiry last, skip expired
  and quarantined) using `ProgramClock.Today`. **Costing:** COGS comes from the
  consumed batch (`Batches.UnitCost ?? Product.CostPrice` documented reserve); a line
  consuming several batches becomes several `InvoiceItem` rows. Merging into an
  existing batch uses quantity-weighted average cost.
- **Suspended invoices:** suspending reserves stock via `StockReservations` (one row per
  invoice+product) and raises `Inventory.QuantityReserved` only. Completing consumes
  the invoice's own reservation; cancelling releases it; no operation touches another
  invoice's reservations.
- **Idempotency contract:** stock-count posting writes adjustments with
  `SourceType='ManualAdjustment'`, `SourceId=CountId`; before posting a line, check the
  database for an existing movement with that key and skip if found
  (`StockCounts.PostedMovementId` records the link). Never break this contract.
- **Payments:** outstanding per invoice is always computed through
  `clsPaymentAllocation` (`Net + Returns − LinkedPaid − AllocatedGeneral`, oldest-first
  general-payment allocation). Do not invent alternate balance math.

## 6. Implemented services (all of them)
- `clsPartyService`, `clsEmployeeService`, `clsCustomerService`, `clsSupplierService`
- `clsUsersService`: login/logout/password policy/user management (+`clsPermissions`,
  `clsAudit`)
- `clsProductService`: CRUD, barcode lookup, batches, net-available quantity
- `clsRoleService`, `clsExpenseService`, `clsSettingsService`, `clsBackupService`
- `clsStockService`: receive / FEFO sell / manual adjustment / batch transfer
- `clsCheckoutService`: full checkout with suspended invoices
- `clsStockCountService`: count sessions, variance valuation, idempotent posting
- `clsPurchaseService`: purchase invoices (used by `PurchaseForm`)
- `clsPaymentService` (+`clsPaymentAllocation`), `clsReturnService` (customer/supplier),
  `clsPosSessionService`, `clsCashService`
- `clsInvoiceService`: lists, details, print model
- `clsReportService`: daily sales, inventory status, profit (COGS), top selling,
  dashboard, sales trend, expiring batches, profitability, debt ageing, cash flow
- `clsPayrollService`, `clsShiftService`, `clsFiscalYearService`

## 7. Rules for agents and contributors
1. **Business logic and validation belong in the service layer** (`Sayl.Core.Services`).
2. **The DAL (`SaylDbContext` + models) handles persistence and data access only.**
3. **The DAL must never call the API or the UI.** There is no separate API layer
   today; if one is ever introduced, it must not contain business logic, and the DAL
   must never call it.
4. **The UI layer must not contain business logic** — forms validate input format for
   convenience, but the authoritative rules live in services.
5. **Layer dependencies must follow the existing architecture:**
   `Sayl.UI → DependencyInjection → Sayl.Core → Common`. Do not add references that
   skip or invert these edges.
6. **Do not introduce unnecessary layers** (repositories, managers, mediators, CQRS).
   The single-layer service design is deliberate.
7. **Avoid unrelated refactoring.** Change only what the task requires; do not rename
   existing classes or redesign working code.
8. **Stored procedures: none exist.** Keep data-level business logic (raw SQL, app
   locks, CAS transitions) as-is unless a demonstrated defect requires change; any
   such change must state the defect and its test.
9. **Never weaken or remove tests to make builds pass.** Fix the code, or justify the
   test change explicitly.
10. **Update documentation whenever architectural or behavioral changes are made**
    (`README.md`, `Agent.md`, and the relevant `docs/` file).
11. If a database change is needed, tell the owner first and provide an idempotent
    `Upgrade_*.sql` script; never drop data.
12. Write code compatible with EF Core 10 / .NET 10; build WinForms with `UserControl`
    inside the main panel; follow existing naming and Arabic-message conventions.

## 8. Remaining work
See TASK.md and docs/MIGRATION_PLAN.md (DB restore verification, license hardening,
schema follow-ups).


