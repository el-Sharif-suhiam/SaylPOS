# Entity Relationships — SaylDB

This page expands the relationships behind the ER diagram in
[DATABASE_OVERVIEW.md](DATABASE_OVERVIEW.md), with key columns and integrity rules.

## 1. Party Model (Customers and Suppliers)

```
Parties (1) ──(0..1)── Customers
Parties (1) ──(0..1)── Suppliers
```

- `Parties.PartyId` — INT IDENTITY, PK.
- `Customers.PartyId` / `Suppliers.PartyId` — INT, PK **and** FK to `Parties`.
  A party of type `C` has a `Customers` row, type `S` a `Suppliers` row, type `B`
  both.
- `PartyType` CHECK: `('C','S','B')`.
- `parties_email_unique` — filtered unique index on `Email` (`WHERE Email IS NOT
  NULL`), so multiple parties without an email are allowed.

Invoices and payments reference `Parties` (not `Customers`/`Suppliers`), so a
type-`B` party can appear on both sides with one identity.

## 2. Identity Model

```
Roles (1) ──(∞)── Users (∞) ──(0..1)── Employees (1) ──(∞)── EmployeeShifts (∞) ──(1)── Shifts
Employees (1) ──(∞)── PayrollRecords (∞) ──(0..1)── Expenses
```

- `Users.RoleId` — TINYINT FK to `Roles` (identity, max 255 — known follow-up).
- `Users.EmployeeId` — nullable FK; filtered unique index `UX_Users_OnePerEmployee`
  allows unlimited standalone accounts but at most one account per employee.
- `EmployeeShifts.DaysOfWeek` — TINYINT bitmask (bit 0 = Sunday … bit 6 = Saturday),
  validated 1–127; `UQ_EmployeeShifts_Active` on (employee, shift, effective-from).
- `PayrollRecords` — `UQ_PayrollRecords_Employee_Period` makes payroll generation
  idempotent; paying a record creates one linked `Expenses` row (`ExpenseId` FK).

## 3. Inventory Model

```
Products (1) ──(∞)── Batches (∞) ──(∞)── InvoiceItems (∞)──(1)── Invoices
Products (1) ──(1)── Inventory
Products (1) ──(∞)── StockMovements
Products (1) ──(∞)── StockReservations (∞)──(1)── Invoices (suspended)
```

- `Batches` — `UQ_Batches_Product_Batch` (ProductId, BatchNumber). `UnitCost`
  (nullable) is the per-batch acquisition cost and the source of COGS.
  `IsQuarantined` marks stock that exists but is not sellable.
- `Inventory` — one summary row per product (PK = FK `ProductId`):
  `QuantityAvailable`, `QuantityReserved`, `UpdatedAt`, `LastCountedAt`.
  The sellable quantity rule is `available − reserved`, with reserved kept equal to
  the sum of `StockReservations.Quantity` per product.
- `InvoiceItems.BatchId` — nullable: rows are written per consumed batch at sale
  time; a multi-batch line becomes multiple `InvoiceItem` rows, one per batch.
- `StockReservations` — one row per (suspended invoice, product)
  (`UQ_StockReservations_Invoice_Product`); `Inventory.QuantityReserved` stays the
  aggregate. Completing or cancelling an invoice releases only its own rows.

## 4. Invoice and Payment Model

```
Invoices (1) ──(∞)── InvoiceItems
Invoices (1) ──(∞)── Payments
Invoices (1) ──(∞)── Invoices (returns via OriginalInvoiceId)
Invoices (∞) ──(0..1)── POSSessions
Payments (∞) ──(0..1)── POSSessions
```

- `Invoices.Type`: `sale`, `purchase`, `customer_return`, `supplier_return`.
  `Status`: `completed`, `suspended`, `cancelled`. `PaymentStatus`: `unpaid`,
  `partial`, `paid`.
- `CK_Invoices_NonNegativeNet`: `NetAmount >= 0` for `sale`/`purchase`; returns are
  stored **negative** by design (`TotalAmount`, `DiscountAmount`, `TaxAmount`,
  `NetAmount` all negated), which is how "returns reduce the original invoice" is
  represented.
- `Payments` links to an invoice (`InvoiceId`) or to a party balance
  (`InvoiceId = NULL`, `PartyType` + `PartyId`). `SessionId` attributes the cash to
  the drawer that physically received/paid it (may differ from the invoice's
  session). `CK_Payments_Amount` requires `Amount > 0`.
- Payment status of an invoice is derived, not stored separately: services compute
  outstanding = effective net (net + negative returns) − invoice-linked payments −
  allocated share of general payments, oldest first
  (`clsPaymentAllocation`).

## 5. Stock Audit Model

```
StockMovements (∞)──(1)── Products
StockMovements (∞)──(0..1)── Batches
StockCounts (∞)──(1)── Products
```

- `StockMovements` is an append-only audit log: `MovementType` (six values),
  signed `Quantity`, `SourceType`/`SourceId` provenance (`Invoice`,
  `ManualAdjustment`, `StockReceive`, `StockSale`, `BatchTransfer`), optional
  `BatchId`.
- `IX_StockMovements_ManualAdjustmentSource` (filtered on
  `SourceType = 'ManualAdjustment' AND SourceId IS NOT NULL`) serves the stock-count
  idempotency check.
- `StockCounts` — per-line physical count for a `CountSessionId`; the difference is
  posted through `clsStockService.AdjustStockAsync`, which writes the resulting
  movements with `SourceId = CountId`. `PostedMovementId` records the movement
  created for the line (no FK by design).

## 6. Financial Operations Model

```
POSSessions (1) ──(∞)── CashMovements
POSSessions (1) ──(∞)── Invoices / Payments
Expenses (∞)──(0..1)── Users
Expenses (∞)──(0..1)── PayrollRecords
SystemLogs (∞)──(1)── Users
```

- `CK_CashMovements_Type`: `I` (paid in) / `O` (paid out).
- Expected drawer cash at close = opening amount + cash sales + cash collections −
  cash refunds − cash supplier payments + paid-in − paid-out (`PaymentMethod =
  'cash'` only; card/transfers never enter the drawer).
- `SystemLogs` records logins, stock adjustments, batch transfers, count sessions,
  fiscal closes and restore operations.

## 7. Integrity Summary

| Mechanism | Where enforced |
|---|---|
| Referential integrity | FKs on every relationship above; the schema uses SQL Server's default `NO ACTION` (no cascading deletes), and EF maps optional relationships as `ClientSetNull` |
| Value domains | CHECK constraints (invoice type/status/payment status, movement type, employee status, cash type, positive amounts) |
| Non-negative money | `CK_Invoices_NonNegativeNet`, `CK_Customers_NonNegativeBalance`, `CK_Suppliers_NonNegativeBalance` |
| One-instance rules | Filtered unique indexes (open session per user, open count per product, reservation per invoice+product, account per employee, unique email/barcode) |
| Idempotency | `UQ_PayrollRecords_Employee_Period`, `StockCounts.PostedMovementId` + `StockMovements.SourceId` |
| History protection | Fiscal-year guard in services rejects edits/returns/completions against closed periods (application-level, not DB) |


