# SaylPOS — Fiscal Close Readiness & Financial Calculations

**Branch:** `bug-fixes`
**Date:** 2026-11-05
**Task:** Prompt 6 of 10 — Audit and correct fiscal close readiness and financial calculations
**Status:** Confirmed defects fixed; new tests added; full suite executed.

---

## Executive Summary

| # | Concern | Verdict | Action |
|---|---------|---------|--------|
| 1 | Payment allocations missing from readiness debt math | **CONFIRMED** | **Fixed** — readiness now routes through `clsPaymentAllocation` |
| 2 | Returns ignored in readiness debt math | **CONFIRMED** | **Fixed** — same formula as payment service |
| 3 | Refunds / refund allocations in readiness math | **CONFIRMED** (same root cause) | **Fixed** — refund effects (negative return NetAmount) now reflected |
| 4 | Double-counting of payments in readiness | **NOT PRESENT** | No change needed — evidence below |
| 5 | Returns from previous fiscal periods (fiscal guard) | **ALREADY CORRECT** | No change needed — evidence below |
| 6 | Test fixture: `Roles.RoleId TINYINT IDENTITY` overflow | **CONFIRMED** (suite-wide flake) | **Fixed** in `TestSeed` (role reuse pattern already established) |

---

## The Accounting Rules Actually Used by SaylPOS (verified, not invented)

The authoritative per-invoice debt equation lives in `clsPaymentAllocation.GetPartyOutstandingAsync`
(`Sayl_DataAccess\Services\clsPaymentAllocation.cs`) and is the formula the **payment service itself
enforces** (`clsPaymentService.PayInvoiceAsync` refuses payment beyond `row.Outstanding`):

```
EffectiveNet = Invoice.NetAmount + Returns          (returns are negative NetAmount rows
                                                     linked via OriginalInvoiceId — VULN-07)
Outstanding  = EffectiveNet − LinkedPaid − AllocatedGeneral
```

- **LinkedPaid** = sum of payments with `InvoiceId = this invoice` (any type).
- **AllocatedGeneral** = deterministic oldest-first allocation of general payments
  (`InvoiceId = null`, collected from a customer / paid to a supplier) across the party's
  completed sale/purchase invoices, ordered by `CreatedAt` then `InvoiceId`.
- **Surplus** beyond total outstanding remains *unapplied credit* on the party — it is
  **not** attached to any invoice.
- **Refunds** (`BuildRefundPlanAsync`): debt first (reduces party balance), then cash back to
  the original payment methods, capped at what was actually paid (linked + allocated share).
  Cash refund rows are posted **on the return invoice** (`InvoiceId = returnInvoiceId`), not
  the original — so they never enter `LinkedPaid` of the original.

The same year-close semantics: `CloseYearAsync` only flips two `Settings` keys inside one
transaction (CAS-guarded); readiness (`GetCloseReadinessAsync`) is computed inside it for the
actual closed period `[current, newYearStart)` **before** any write. No posted transaction is
modified at close.

---

## Findings and Evidence

### 1. CONFIRMED — Readiness debt math ignored allocations, returns, and refunds (fixed)

**File:** `Sayl_DataAccess\Services\clsFiscalYearService.cs` — `GetCloseReadinessAsync`, checks 5 & 6.

**Before:** paid amounts were computed as `SUM(Payments WHERE InvoiceId = invoice)` only.

Consequences (both were **warning-level**, so they never blocked a close — they *misinformed*
the accountant at the moment of closing):

- An invoice settled via a **general payment** (customer collection with no invoice attached)
  was still reported as unpaid — inflated `OldDebtCarried` / false `UnpaidThisYear`.
- An invoice **fully covered by a return** (negative `NetAmount` via `OriginalInvoiceId`) was
  still reported as unpaid.
- An invoice whose remainder was covered by an **allocated general payment** was misreported.

**Fix:** checks 5 & 6 now compute per-invoice outstanding via
`clsPaymentAllocation.GetOutstandingForInvoicesAsync` — the exact formula `PayInvoiceAsync`
uses to decide "already paid". Readiness math and payment enforcement now cannot disagree.

### 2. NOT PRESENT — Double-counting of payments

Evidence: in `clsPaymentAllocation`, the general-payment pool and linked payments are consumed
**disjointly** (`AllocatedGeneral = Min(remaining, generalCredit)`; pool decremented). Refund
cash rows are posted on return invoices, which are excluded from the invoice list
(`Type == sale || purchase` only). Readiness inherits this disjointness by delegating. Verified
by tests `CloseReadiness_GeneralPaymentAcrossInvoices_OldestFirstAllocation` and
`CloseReadiness_GeneralPaymentSurplus_UnallocatedCreditNotCountedAsDebt`.

### 3. ALREADY CORRECT — Returns from previous fiscal periods

- **Creating** a return on a closed-year invoice is rejected by the fiscal guard
  (`clsReturnService.CreateCustomerReturnAsync` / `CreateSupplierReturnAsync` →
  `IsBeforeCurrentFiscalYearAsync`), tested in `CustomerReturn_OnClosedYearInvoice_IsRejected`.
- **Historically recorded** returns (made while the period was open) reduce the original
  invoice's outstanding — before the fix readiness ignored even these; now covered by
  `CloseReadiness_PreviousEraReturn_ReducesOldDebt`.
- No historical transaction was modified.

### 4. NOT FIXED HERE (noted) — Refund `PaymentDate` uses UTC date

`clsReturnService.cs` refund payments use `DateOnly.FromDateTime(DateTime.UtcNow)` (audit Issue
7a). It does not affect fiscal-close calculations (readiness is date-driven by invoice
`CreatedAt` / payroll periods, not payment dates), so per instructions it is noted, not fixed.

### 5. CONFIRMED (test infra) — `Roles.RoleId TINYINT IDENTITY` overflow

Full-suite runs failed 14 unrelated tests (`Arithmetic overflow error converting IDENTITY to
data type tinyint`) because `TestSeed` inserted one `Role` row per test; the suite consumes the
255-row identity range. The codebase already documents and applies two mitigations
(`ReceiveStockCostingTests` shared seed; `StockCountVarianceValuationTests` role reuse).
`TestSeed.SeedAsync` now **reuses an existing active admin role** (same `RoleValue`, no
semantic difference) and inserts one only on first seed. Roles are never mutated in tests
(verified by search).

---

## Blocking vs Non-blocking Classification (preserved, unchanged policy)

- **Errors (block):** open POS sessions, suspended invoices inside the closed period
  `[FiscalYearStart, closingTo)`, pending payroll records intersecting the period, unposted
  stock count sessions.
- **Warnings (allow):** old carried debt (`OldDebtCarried`), unpaid/partial invoices of the
  closing period (`UnpaidThisYear`). Debts legitimately carry into the new year — the close is
  "new beginning" semantics (nothing is deleted or rewritten).
- Classification was verified and left intact; only the **amounts/counters** were corrected.

---

## Changed Files

| File | Change |
|------|--------|
| `Sayl_DataAccess\Services\clsFiscalYearService.cs` | Checks 5 & 6 route debt math through `clsPaymentAllocation.GetOutstandingForInvoicesAsync` (allocation-, return-, refund-aware). No other logic touched. |
| `Sayl.Tests\FiscalYearCloseTests.cs` | +15 integration tests (below) and seed helpers for linked/general payments and return invoices. |
| `Sayl.Tests\TestSeed.cs` | Reuse existing active admin role (identity-range fix, established pattern). |

---

## New Tests (independently computed expected values)

All expected values derived by hand from the documented equation
`Outstanding = Net + Returns − LinkedPaid − AllocatedGeneral`; shared-database noise isolated
by baseline-delta measurement (read before seeding, assert the delta).

| Test | Scenario | Expected |
|------|----------|----------|
| `CloseReadiness_FullyPaidLinkedInvoice_NotCountedAsUnpaid` | Fully paid invoice (100/100 linked) | remainder 0 → not counted |
| `CloseReadiness_PartiallyPaidInvoice_CountedWithExactRemainder` | Partial payment (100/40) | old debt +60.00 exactly |
| `CloseReadiness_GeneralPaymentCoveringInvoice_NotCountedAsDebt` | Unallocated payment covers invoice (100 general) | delta 0 |
| `CloseReadiness_GeneralPaymentAcrossInvoices_OldestFirstAllocation` | 120 general over invoices 100+50, oldest first | +30.00 exactly (100→old, 20→next) |
| `CloseReadiness_GeneralPaymentSurplus_UnallocatedCreditNotCountedAsDebt` | 200 general over 100+50 | delta 0; surplus 50 not invoice debt |
| `CloseReadiness_SamePeriodReturnCoveringInvoice_NotCountedAsUnpaid` | Full return −100 in same period | not counted |
| `CloseReadiness_PartialReturnInPeriod_CountedWithRemainder` | Partial return −40, no payment | counted (remainder 60) |
| `CloseReadiness_PreviousEraReturn_ReducesOldDebt` | Historical return −100 on old invoice | delta 0 |
| `CloseReadiness_RefundOnFullyPaidInvoice_NoPhantomDebt` | 100/100 paid, return −40, cash refund on return invoice | delta 0 (−40 outstanding, refunds not double-counted) |
| `CloseReadiness_RefundAllocation_DebtFirstThenCash` | 100/50 paid, return −80 → debt-first plan | delta 0 (outstanding −30) |
| `CloseReadiness_RepeatedChecks_Idempotent` | Mixed data, 3 consecutive readiness calls | identical results + exact expected values (old debt +60, unpaid +1) |
| `CloseReadiness_CleanPeriod_NoErrors_CanClose` | Clean period, fully paid invoice | `CanClose`, no errors |
| `CloseReadiness_DebtsOnly_ProducesWarningsNotErrors` | Old debt 100 + partial invoice | warnings only, `CanClose`, +100.00 / +1 exact |
| `CloseReadiness_BlockedPeriod_ErrorsPreventCloseDespiteWarnings` | Suspended invoice + old debt | `CanClose = false`, error present |
| `ReadinessAgreesWithPaymentService_GeneralPaymentCoveredInvoice_CannotBePaidTwice` | Invoice covered by general payment | readiness: not unpaid **and** direct payment rejected ("مسددة") — readiness and payment enforcement use the same equation |

---

## Test Results (ACTUAL — executed on this machine)

```
dotnet test Sayl.Tests\Sayl.Tests.csproj

Fiscal close class only:  Passed! - Failed: 0, Passed: 36, Total: 36
FULL suite (run 1):       Passed! - Failed: 0, Passed: 254, Total: 254 (1 m 44 s)
FULL suite (run 2):       Passed! - Failed: 0, Passed: 254, Total: 254 (1 m 45 s)
```

Baseline comparison (for attribution): full suite on the clean tree passed 239/239; with the
fiscal changes but before the `TestSeed` role fix, 14 unrelated tests failed with
`IDENTITY → tinyint` overflow (reproduced twice, root-caused, fixed in `TestSeed`).

Build: `dotnet build` — 0 errors (warnings pre-existing, unchanged).

---

## Deliberately Not Changed

- Stored procedures / stored business logic: no defects found requiring modification — the
  readiness correction reuses the existing `clsPaymentAllocation` service, the single source
  of the allocation policy.
- Historical posted transactions: untouched.
- The blocking/warning classification, the CAS close ordering, and the first-close semantics.
- Closed-year return dead end ("approved accounting adjustment" message): known design gap,
  out of scope (stakeholder policy question from the audit report).
- Refund `PaymentDate` UTC issue: noted (Issue 7a), does not affect fiscal calculations.
