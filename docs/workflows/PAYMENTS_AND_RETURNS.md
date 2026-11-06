# Payments and Returns — SaylPOS

Covers the money flows around invoices: invoice payments, general collections and
supplier payments, the deterministic allocation engine, drawer sessions and the Z
reconciliation, customer/supplier management touchpoints, and both return types.

## 1. Participants

| Role | Component |
|---|---|
| UI | `PaymentForm`, `SupplierPaymentForm`, `TodayInvoicesForm`, `InvoiceDetailsForm`, `ReturnForm`, `SupplierReturnForm`, `PartyDetailsForm`, `PartyEditForm` |
| Services | `clsPaymentService`, `clsPaymentAllocation`, `clsReturnService`, `clsPartyService`, `clsPosSessionService`, `clsCashService` |
| Data | `Payments`, `Invoices`, `Customers`, `Suppliers`, `StockMovements`, `Batches`, `SystemLogs` |

## 2. The Allocation Engine (`clsPaymentAllocation`)

The single source of truth for "what is still owed on an invoice":

```
EffectiveNet      = NetAmount + Returns          (returns are negative invoices)
Outstanding       = EffectiveNet − InvoiceLinkedPaid − AllocatedGeneral
AllocatedGeneral  = deterministic oldest-first share of general payments
                    (InvoiceId = NULL), ordered by CreatedAt then InvoiceId
```

- Deterministic: raw events (invoices, returns, payments) are re-derived from the
  database on every call — no allocation table, no call-order sensitivity.
- General payments (a collection from a customer, a payment to a supplier, not tied
  to an invoice) reduce the party balance **and** are allocated to the party's
  outstanding invoices oldest-first, updating each invoice's `PaymentStatus`
  (`ApplyGeneralPaymentToInvoiceStatusesAsync`).
- A surplus beyond all outstanding invoices stays in the party's balance as
  unapplied credit.
- This same equation governs invoice payment limits, refund planning, debt-ageing
  reports and fiscal-close readiness (`Outstanding = Net + Returns − LinkedPaid −
  AllocatedGeneral`, commit 0209373).

## 3. Paying an Invoice — `PayInvoiceAsync(DtoPaymentRequest)`

1. Validation: amount > 0, invoice exists, status `completed`, not already paid.
2. Permission at the service boundary: paying a purchase or supplier-return invoice
   requires the **Purchase Invoices** bit; a sale/customer-return invoice requires the
   **Sales Invoices** bit.
3. Serializable transaction:
   - Drawer-session validation (`clsPermissions.ValidateDrawerSessionAsync`): session
     exists, is open, and is owned by the actor (or the actor is admin).
   - Outstanding is recomputed **inside** the transaction through the allocation
     engine, so general payments and returns already covering the debt are seen —
     the same debt cannot be paid twice.
   - The payment amount may not exceed the outstanding.
   - Insert `Payments` (`income` for sales, `outcome` for purchases;
     `SessionId` = the drawer that physically received the cash).
   - Update `PaymentStatus` from total effective paid (linked + allocated + this
     payment) versus effective net.
   - Decrease the customer/supplier balance atomically (floored at zero).
4. Commit; rollback on any failure.

## 4. General Payments

- **Collect from customer** — `ReceiveCustomerPaymentAsync` (Customers bit):
  `UPDLOCK` read of the balance inside the transaction (parallel collectors wait,
  no stale reads), payment must not exceed the balance, insert `Payments`
  (`income`, `InvoiceId = NULL`), apply the deterministic allocation to update
  invoice statuses, decrease the balance atomically.
- **Pay supplier** — `PaySupplierAsync` (Suppliers bit): the mirror image with
  `Type = 'outcome'`.
- Both validate the drawer session identically and record `SessionId` for the Z
  report.

## 5. POS Sessions and the Drawer

- `OpenSessionAsync` — one open session per user, enforced by the application check
  and by `UX_POSSessions_OneOpenPerUser` (filtered unique index) under parallelism.
- `CloseSessionAsync` — requires the cashier's identity (own session) or the Sales
  Invoices bit; a cashier can never close another user's drawer (admin only).
  Atomic claim `Status = 'open' → 'closed'` inside a Serializable transaction.
- Expected cash (the Z reconciliation):

```
expected = OpeningAmount
         + cash sales            (Payments income,  method = cash, SessionId = session)
         + cash collections      (income, no invoice, same session)
         − cash refunds          (outcome, method = cash, same session)
         − cash supplier payments
         + CashMovements paid-in − paid-out
```

- Cash attribution rule (dual-source): new rows carry `Payments.SessionId`;
  legacy rows with `SessionId = NULL` fall back to the invoice's session so old
  Z reports never shift. Card/transfer payments never enter the drawer.
- `clsCashService.AddMovementAsync` records paid-in/paid-out movements against an
  open, owned session.

## 6. Customer Returns — `CreateCustomerReturnAsync(DtoReturnRequest)`

1. Validation: items present, quantities > 0.
2. Permission: cashier identity **or** the Sales Invoices bit.
3. The original invoice must be a completed `sale`; a return against an invoice from
   a closed fiscal year is refused (settlement goes through an authorized accounting
   adjustment).
4. Eligibility: per-product returned quantity may not exceed
   `sold − alreadyReturned` (request items grouped first; re-checked inside the
   transaction against parallel returns — Serializable).
5. Refund computation (exact proportional method): `netRatio = Net / Total` of the
   original invoice; the refund is the returned gross × netRatio, with the audit
   breakdown showing the item's share of the original discount and tax. One rounding
   at the end.
6. Insert the return invoice: `Type = 'customer_return'`, amounts **negative**,
   `OriginalInvoiceId` set, `SessionId` = the actor's drawer (the refund cash leaves
   today's drawer).
7. Stock restoration: returned quantities go back to the **original batches**;
   expired or missing-source batches are quarantined rather than returned to
   sellable stock.
8. Refund distribution (`BuildRefundPlanAsync`-consistent): cash refund from the
   drawer for the cash the customer actually paid, balance reduction for the credit
   portion — never more than the outstanding computed by the allocation engine.
9. Commit.

## 7. Supplier Returns — `CreateSupplierReturnAsync(DtoReturnRequest)`

1. Permission: Purchase Invoices bit.
2. Same eligibility mechanics against the original completed `purchase` invoice and
   the same closed-fiscal-year guard.
3. Refund from the supplier uses the identical proportional method (net ratio over
   the original invoice, including discount and tax shares) — the merchant recovers
   exactly what was paid, not the raw cost.
4. The return invoice (`supplier_return`, negative amounts) deducts the quantities
   from the **original batches** and reduces `Inventory.QuantityAvailable`.
5. The supplier's balance decreases by the refund amount (a payable is reversed);
   cash payout is attributed to the acting drawer.

## 8. Cross-Workflow Relationships

- Credit sales create customer balances → settled by invoice payments or general
  collections (§3–4).
- Returns reverse revenue and COGS symmetrically and restore/quarantine stock
  ([INVENTORY_WORKFLOW.md](INVENTORY_WORKFLOW.md)).
- Outstanding balances and returns feed debt ageing, the dashboard and fiscal-close
  readiness ([FISCAL_CLOSING.md](FISCAL_CLOSING.md)).
- Drawer sessions bound the cash flows; every payment/return validates its session
  (§5).

## 9. Known Limitation

Authorization denials are returned to the caller as failed results but are **not**
written to `SystemLogs` — denied attempts leave no audit trace. This is a documented
open item.


