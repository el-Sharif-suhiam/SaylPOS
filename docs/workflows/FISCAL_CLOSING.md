# Fiscal Closing — SaylPOS

The annual fiscal-year close: readiness checks, the close itself, and the
history-protection rules that the close installs for the rest of the application.

## 1. Participants

| Role | Component |
|---|---|
| UI | `SettingsControl`, `FiscalYearCloseForm` (wizard: readiness → backup → closing reports → close) |
| Services | `clsFiscalYearService`, plus fiscal guards consumed by `clsCheckoutService`, `clsReturnService`, `clsExpenseService` |
| Data | `Settings` (`FiscalYearStart`, `FiscalYearClosedOn`), `POSSessions`, `Invoices`, `PayrollRecords`, `StockCounts`, `SystemLogs` |

## 2. Model

The fiscal year is **two settings keys**, not a table:

| Key | Meaning |
|---|---|
| `FiscalYearStart` | Start date of the current fiscal year; default = January 1 of the current program year on first run |
| `FiscalYearClosedOn` | Date of the most recent close (empty if never closed) |

Closing is a "new beginning" simulation: **no record is deleted or modified**. The
close only advances `FiscalYearStart`; all history remains readable and comparable,
while edits touching archived periods are refused by the service-level guards.

The period considered "closed" when closing to `newYearStart` is
`[FiscalYearStart, newYearStart)`.

## 3. Close Readiness — `GetCloseReadinessAsync(closingTo)`

Errors block the close; warnings allow it.

| # | Check | Severity |
|---|---|---|
| 1 | Open POS sessions | **Error** — close them first |
| 2 | Suspended invoices created inside the closing period | **Error** — complete or cancel them (a suspended invoice from an earlier closed year does not block) |
| 3 | Pending payroll sheets intersecting the closing period | **Error** — pay or reverse them |
| 4 | Open (unposted) stock-count session | **Error** — complete and post it |
| 5 | Old debts (completed sales before `FiscalYearStart` with outstanding > 0.01) | **Warning** — the amount carried forward |
| 6 | Current-year invoices with outstanding > 0.01 | **Warning** — they remain open receivables |

Checks 5 and 6 compute outstanding with the allocation engine
(`clsPaymentAllocation.GetOutstandingForInvoicesAsync`): for each party, general
payments are allocated oldest-first, so an invoice settled by a general collection or
covered by a return is no longer falsely reported as debt
(`Outstanding = Net + Returns − LinkedPaid − AllocatedGeneral`, commit 0209373).

## 4. The Close — `CloseYearAsync(userId, newYearStart)`

1. **Permission gate (service boundary):** requires the **Inventory** screen bit —
   the stock count is a pillar of the close, matching the UI button's gate in
   `SettingsControl`.
2. Guard rails: `newYearStart` must be after the current start and not in the future
   (program day, UTC+3).
3. Serializable transaction:
   - Readiness checks run **before** any write, against the *previous* period
     `[current, newYearStart)`. (On a first close the stored start row may not exist;
     the default start is used, and the raw insert happens only after the checks.)
   - **CAS update:** `UPDATE Settings SET SettingValue = @new WHERE SettingKey =
     'FiscalYearStart' AND SettingValue = @current` — a concurrent second close sees
     zero affected rows and fails safely (no double close).
   - First close with no row → explicit insert.
   - `ChangeTracker.Clear()` so tracked (pre-close) settings values cannot overwrite
     the raw updates.
   - `FiscalYearClosedOn` upserted (program-day date).
   - Audit row `CloseFiscalYear` with the old/new start and carried debt.
4. Commit; on any failure the transaction rolls back — there is never a half-closed
   state (new start without a close date or vice versa).

## 5. History Protection (installed by the close)

Once `FiscalYearStart` advances, services refuse operations that would change
archived periods:

| Operation | Guard |
|---|---|
| Complete a suspended invoice dated before the current fiscal year | Refused — settle through an authorized accounting adjustment |
| Customer return against a sale invoice from a closed year | Refused — record an adjustment in the current period |
| Supplier return against a purchase invoice from a closed year | Refused — same policy |
| Expenses (add/update/delete) touching closed-year dates | Refused (`clsExpenseService` fiscal guards) |

`IsBeforeCurrentFiscalYearAsync(date)` is the shared helper; the readiness checks in
section 3 ensure nothing pending is left behind that would need these guards after
the close.

## 6. Recommended Close Sequence

`FiscalYearCloseForm` guides the operator through:

1. Run the readiness check (fix all errors; review warnings).
2. Take a backup ([BACKUP_AND_RESTORE.md](BACKUP_AND_RESTORE.md)).
3. Print/save the closing reports (periodic report, profit, debt ageing).
4. Execute the close (this sets the two settings keys atomically).

## 7. Relationship to Other Workflows

- Stock count: an open count session blocks the close; count postings are part of
  year-end inventory truth ([STOCK_COUNT_WORKFLOW.md](STOCK_COUNT_WORKFLOW.md)).
- Payments: outstanding-balance math shared with the allocation engine
  ([PAYMENTS_AND_RETURNS.md](PAYMENTS_AND_RETURNS.md)).
- Payroll: pending sheets must be paid or reversed before closing.
- POS sessions: all drawers closed before the close.


