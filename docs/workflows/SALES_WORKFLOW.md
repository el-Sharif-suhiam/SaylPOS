# Sales Workflow — SaylPOS

Covers the cashier point-of-sale flow: checkout, payment capture, suspended invoices,
and their completion/cancellation. Returns are covered in
[PAYMENTS_AND_RETURNS.md](PAYMENTS_AND_RETURNS.md).

## 1. Participants

| Role | Component |
|---|---|
| UI | `PosForm` (cashier shell), `CustomerPickerForm`, `SuspendedInvoicesForm`, `LastInvoiceForm`, `InvoiceDetailsForm`, `OpenSessionForm`, `CloseSessionForm`, `CustomerDisplayForm` |
| Services | `clsCheckoutService`, `clsPosSessionService`, `clsCashService`, `clsProductService` |
| Data | `Invoices`, `InvoiceItems`, `Batches`, `StockMovements`, `Inventory`, `Payments`, `Customers`, `POSSessions`, `SystemLogs` |

## 2. Preconditions

- A valid license (startup gate).
- A logged-in user. A cashier (`RoleValue == 0`) is routed to `PosForm` by role
  identity; admin/accountant roles may also operate the POS.
- An **open POS session** (cash drawer) is required for payments attributed to a
  drawer; checkout without a session records an invoice but the cash-attribution
  rules treat it as "no session".

## 3. Checkout (Direct Sale)

### User action
Scan or select products, optionally pick a customer, apply a discount, take payment
(mixed cash/card allowed), confirm.

### Service operation — `clsCheckoutService.ProcessCheckoutAsync(DtoCheckoutRequest, suspend: false)`

Validation and authorization:

1. Request must contain items; each quantity must be > 0.
2. **Discount permission:** a percentage discount above
   `clsPermissions.MaxCashierDiscountPercent` (10 %) or any amount discount requires
   the Sales Invoices screen bit (`AppPermissions.SalesInvoices`).
3. **Implicit-price guard:** a client-supplied unit price below the product's current
   `BasePrice` is treated as an implicit discount and is subject to the same
   permission check (the UI is not a trust boundary).
4. Customer existence check when a customer is selected.

Execution (single Serializable transaction, deadlock-retried up to 3 times):

1. Resolve the open POS session (must be open if a session was supplied).
2. Insert the `Invoices` header: `Type = 'sale'`, totals computed from basket-snapshot
   prices, discount and tax guards (0–100 %, amount ≤ total, net ≥ 0), and rounding
   applied exactly once (`Math.Round(net, 2)`).
3. Insert `InvoiceItems` rows (`ItemPrice` = basket snapshot price; `ItemCost` is
   written later from the batches actually consumed).
4. Deduct stock with **FEFO**: batches ordered by earliest expiry (null expiry last),
   excluding expired and quarantined batches, using the program day (UTC+3). Each
   consumed batch gets a `StockMovements` row (`sale`, negative quantity,
   `SourceType = 'Invoice'`, `SourceId = invoiceId`). A line that consumed several
   batches is split into one `InvoiceItem` row per batch with its own `ItemCost`
   (`batch.UnitCost ?? product.CostPrice`).
5. Update `Inventory.QuantityAvailable` (own reservations of the invoice are excluded
   from the availability check; other invoices' reservations are never touched).
6. Process payments: total payments may not exceed the invoice net; each payment is
   inserted with `Type = 'income'`, cash-attributed to the current session.
   `PaymentStatus` becomes `paid` / `partial` / `unpaid`.
7. Credit sale (paid < net) requires a registered customer; the debit is added to
   `Customers.Balance` and guarded by `CreditLimit`.
8. Audit: `SystemLogs` row (`SaleInvoice` / `SuspendInvoice`).
9. Commit.

### Database changes

| Table | Change |
|---|---|
| `Invoices` | +1 row (`sale`) |
| `InvoiceItems` | +1 row per consumed batch |
| `StockMovements` | +1 row per consumed batch (`sale`) |
| `Batches` | `BatchQuantity` decremented per batch |
| `Inventory` | `QuantityAvailable -= quantity` |
| `Payments` | +1 row per payment |
| `Customers` | `Balance += net − paid` for credit sales |
| `SystemLogs` | +1 audit row |

### Failure scenarios

- Insufficient sellable stock → explicit failure, nothing written.
- Payments exceed net → failure (over-payment is not silently absorbed).
- Credit-limit exceeded → failure.
- Closed drawer session → failure.
- Deadlock → automatic retry (3 attempts).
- Any other exception → full rollback with a detailed failure message.

## 4. Suspended Invoices (Hold / Resume)

### Suspend
`ProcessCheckoutAsync(..., suspend: true)` creates the invoice with
`Status = 'suspended'` and **reserves** stock instead of deducting it:

- One `StockReservations` row per (invoice, product) — the reservation's owner.
- `Inventory.QuantityReserved` increased; `QuantityAvailable` untouched.
- Availability check uses `available − reserved`, so the same units cannot be held
  twice. A filtered unique index (`UQ_StockReservations_Invoice_Product`) is the
  database-level guard.

### Complete
`CompleteSuspendedInvoiceAsync(invoiceId, payments, actorUserId, actorSessionId)`:

- Payment session must be open; payments require at least one positive amount.
- Ownership: the invoice's creator may complete it; anyone else needs the Sales
  Invoices bit.
- A suspended invoice from a closed fiscal year is refused (settlement must go
  through an authorized accounting adjustment).
- Atomic claim inside the transaction —
  `UPDATE Invoices SET Status = 'completed' WHERE InvoiceId = @id AND Status = 'suspended'`
  — the first writer wins, preventing double stock deduction.
- Deducts stock (FEFO) while consuming **its own** reservation rows only, processes
  payments attributed to the completer's drawer, and audits
  `CompleteSuspendedInvoice`.

### Cancel
`CancelSuspendedInvoiceAsync(invoiceId, actorUserId)`:

- Same ownership rule as completion (creator, or Sales Invoices bit).
- Atomic claim `Status = 'cancelled'`; releases the invoice's own
  `StockReservations` rows and decrements `Inventory.QuantityReserved` accordingly.
- Never touches another invoice's reservations.

## 5. Related Workflows

- Stock used by sales is supplied by [INVENTORY_WORKFLOW.md](INVENTORY_WORKFLOW.md)
  (receive/purchase) and priced by batch costing (see [COSTING.md](../COSTING.md)).
- Customer credit created by credit sales is settled through
  [PAYMENTS_AND_RETURNS.md](PAYMENTS_AND_RETURNS.md).
- Drawer sessions and the Z-report reconciliation are documented in
  [PAYMENTS_AND_RETURNS.md](PAYMENTS_AND_RETURNS.md) §5.


