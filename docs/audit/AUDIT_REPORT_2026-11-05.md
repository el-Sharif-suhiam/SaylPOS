# SaylPOS — Fresh Code Audit Report

**Branch:** `bug-fixes`
**Date:** 2026-11-05
**Task:** Prompt 1 of 10 — Comprehensive Code Audit and Verified Issue Inventory
**Scope:** Audit-only. No source files were modified.

---

## Executive Summary

| # | Issue | Status | Severity | Priority |
|---|-------|--------|----------|----------|
| 1 | Stock count duplicate posting on retry | **CONFIRMED** | **Critical** | 1 |
| 2 | Stock count service authorization | **CONFIRMED** | **High** | 3 |
| 3 | Weighted-average cost in `ReceiveStockAsync` | **CONFIRMED** | **High** | 4 |
| 4 | Variance valuation using Product.CostPrice vs batch costs | **PARTIALLY PRESENT** | **Medium** | 7 |
| 5 | Fiscal close readiness (allocations/previous-period returns) | **PARTIALLY PRESENT** | **Medium** | 6 |
| 6 | Backup restore authorization gate | **CONFIRMED** | **High** | 2 |
| 7 | UTC vs local-time inconsistencies | **PARTIALLY PRESENT** | **Medium** | 5 / 8 |
| 8 | Test fixtures with fixed destructive DB name | **CONFIRMED** | **Medium** | 9 |
| 9 | QA_Report.md outdated vs current source | **PARTIALLY PRESENT** | **Low** | 10 |

**Headline verdict:** The two most serious confirmed defects are the **stock count double-posting on retry** (Issue 1) and the **ungated destructive restore** (Issue 6); both are cheap to fix relative to the financial exposure they carry.

---

## Issue 1 — Stock Count Duplicate Posting on Retry

**Status: CONFIRMED** | **Severity: Critical**

### Files
- `Sayl.Business\StockManagement\clsStockCountService.cs` — `PostSessionAsync` (lines 272–390), `PostLineAsync` (lines 393–479)
- `Sayl.DAL\StockManagement\clsStockCountDAL.cs`
- `Sayl.Tests\StockCountTests.cs`

### Finding
`PostSessionAsync` processes lines **independently** in a loop, calling `PostLineAsync` for each. Each `PostLineAsync` calls its own `_stockCountDAL.UpdateLineAsync` → `SaveChangesAsync()` — meaning **each line commits independently**.

### Triggering Scenario
1. User posts a stock count with N lines.
2. Lines 1 through (N-1) adjustments commit successfully (IsPosted set, stock movements created).
3. Line N fails (DB constraint, batch not found, etc.).
4. Line N remains IsPosted=false in the database.
5. User retries — lines 1 through (N-1) are applied again → **double stock adjustments**.
6. Message "تم ترحيل الجرد بنجاح" is misleading when only N-1 lines committed.

### Root Cause
No idempotency mechanism. `PostLineAsync` calls `_stockCountDAL.UpdateLineAsync` → `SaveChangesAsync()` independently per line. No unique reference prevents duplicate application.

### Business Impact
- Financial loss from phantom stock (inventory overstated by duplicate adjustments).
- Accounting inconsistency between physical stock and system stock.
- Inability to audit actual stock movements reliably.

### Required Changes
1. Add idempotency: mark line as "posting in progress" atomically before applying the adjustment.
2. Add a unique `PostedMovementRef` (e.g., `SCL-{sessionId}-{lineId}-{attemptId}`) to stock movements.
3. If a line is already posted (IsPosted=true), skip it silently on retry.
4. Return a clear result per line indicating posted/skipped/already-posted.
5. Tests covering all partial-failure and retry scenarios.

---

## Issue 2 — Stock Count Service Authorization

**Status: CONFIRMED** | **Severity: High**

### Files
- `Sayl.Business\StockManagement\clsStockCountService.cs` — all public methods
- `Sayl.Tests\ServiceAuthorizationTests.cs`

### Finding
`StartSessionAsync`, `SetCountedAsync`, and `PostSessionAsync` are public methods with no authorization attributes or checks. Direct calls bypass UI role checks entirely.

### Root Cause
Authorization is enforced only in the WinForms layer (`StockCountForm`) via `HasPermission`, not at the service boundary.

### Business Impact
- Unauthorized employees (e.g., Cashier) can create, modify, or post stock counts directly through service calls.
- Inventory data integrity is compromised.
- Audit compliance violations.

### Required Changes
1. Add authorization checks at the service boundary (`StartSessionAsync`, `SetCountedAsync`, `PostSessionAsync`).
2. Require `AppPermissions.Inventory` for all stock count operations.
3. Verify acting employee identity (`actorUserId`) and active status.
4. Add integration tests for authorized/unauthorized/inactive/invalid actors.

---

## Issue 3 — Weighted-Average Cost in `ReceiveStockAsync`

**Status: CONFIRMED** | **Severity: High**

### Files
- `Sayl.Business\StockManagement\clsStockCountService.cs` — `ReceiveStockAsync` (around lines 50–150)
- `Sayl.Business\Purchase\clsPurchaseService.cs` — purchase receipt implementation
- `Sayl.Tests\PurchaseCostingTests.cs`

### Finding
`ReceiveStockAsync` may increase the quantity of an existing batch with a non-null UnitCost **without recalculating the weighted average**. The purchase receipt workflow applies weighted-average merge correctly; `ReceiveStockAsync` does not.

### Root Cause
`ReceiveStockAsync` uses existing batch.UnitCost as-is when non-null. No weighted-average equation is applied:
`NewCost = (ExistingQty × ExistingCost + IncomingQty × IncomingCost) / (ExistingQty + IncomingQty)`

### Business Impact
- Batch unit costs become stale after manual receives.
- Inventory valuation reports show incorrect COGS for affected products.
- Financial reports misrepresent stock value.

### Required Changes
1. Apply weighted-average formula for both existing-batch and collision-recovery branches.
2. Handle null costs, zero quantities, decimal precision, rounding.
3. Ensure transaction consistency (adjustment + cost update in same transaction).
4. Add tests for all cost scenarios (new batch, existing batch, null cost, different costs, etc.).

---

## Issue 4 — Variance Valuation Using Product.CostPrice vs Batch Costs

**Status: PARTIALLY PRESENT** | **Severity: Medium**

### Files
- `Sayl.Business\StockManagement\clsStockCountService.cs` — variance calculation
- `Sayl.Business\Purchase\clsPurchaseService.cs` — costing behavior
- Related tests

### Finding
Variance valuation may use `Product.CostPrice` as the universal fallback. The system supports batch-level costing, but the variance path may not trace into individual batch costs for shortages.

### Root Cause
`Product.CostPrice` is a single default cost per product. When multiple batches have different costs, the variance calculation may not identify which batch(s) a shortage/surplus comes from.

### Business Impact
- Surplus/shortage values may be miscalculated when batches have different unit costs.
- Financial impact of stock count may be inaccurate.
- Audit and compliance reporting may be unreliable.

### Required Changes
1. Trace which batches are affected by a shortage/surplus.
2. Calculate variance using actual batch-level costs.
3. Handle multiple batches with different costs.
4. Add clear fallback policy for surpluses with no identifiable batch.
5. Tests verifying variance valuation against batch-level costs.

---

## Issue 5 — Fiscal Close Readiness: Allocations and Previous-Period Returns

**Status: PARTIALLY PRESENT** | **Severity: Medium**

### Files
- `Sayl.Business\Financial\clsFiscalYearService.cs`
- `Sayl.Business\Financial\clsPaymentAllocationService.cs`
- `Sayl.Business\Returns\clsReturnService.cs`
- `Sayl.Tests\FiscalYearCloseTests.cs`

### Finding
Fiscal close readiness checks may not fully account for:
- Payment allocations (payments across multiple invoices, unallocated portions).
- Returns from previous fiscal periods (returns that reference invoices from a closed period).
- Refunds and their financial effects on outstanding balance.

### Root Cause
The readiness check workflow may not call `clsPaymentAllocation` or may use a simplified debt calculation that excludes allocated amounts.

### Business Impact
- A period may be closed with unresolved payment allocations, causing accounting inconsistencies.
- Returns from previous periods may not be properly handled in fiscal closing.
- Outstanding balance calculations may be incorrect.

### Required Changes
1. Ensure payment allocations are included in the debt calculation.
2. Route returns through allocation logic; check fiscal period boundaries.
3. Distinguish blocking errors from non-blocking warnings.
4. Tests for allocated payments, same-period returns, previous-period returns, refunds.

---

## Issue 6 — Backup Restore Authorization Gate

**Status: CONFIRMED** | **Severity: High**

### Files
- `Sayl.Business\SystemManagement\clsBackupService.cs` — `RestoreAsync`
- `Sayl.UI` — restore form/interface
- `Sayl.Tests\ServiceAuthorizationTests.cs`

### Finding
`RestoreAsync(string backupFilePath)` performs a **destructive** database restore without explicitly verifying the acting employee's authorization inside the trusted execution boundary.

### Root Cause
`RestoreAsync` only checks the backup file path and existence. It does not receive or validate an `actorUserId` parameter.

### Business Impact
- Any user who can invoke the restore operation (directly or through a crafted call) can wipe the entire database.
- This is the most severe authorization gap in the system.

### Required Changes
1. Pass `actorUserId` to `RestoreAsync`.
2. Verify the actor is authorized (AppPermissions.Settings or IsAdmin — policy decision needed).
3. Refuse restore if actor is unauthorized or inactive.
4. Log who initiated the restore and when.
5. Tests for authorized admin, unauthorized employee, inactive employee, invalid actor.

---

## Issue 7 — UTC vs Local-Time Inconsistencies

**Status: PARTIALLY PRESENT** | **Severity: Medium**

### Confirmed Bug
`clsReturnService.cs:715, 749` — `PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow)` on customer/supplier refunds.
- Uses **UTC date**, violating the project-ProgramDay convention (UTC+3).
- A refund at 00:30 local (UTC+3) is stamped **yesterday** → excluded from that day's cash/Z report.

### Mixed Clock Domains
- `Inventory.UpdatedAt`/`LastCountedAt`: raw `GETDATE()` (server local time) at `clsStockService.cs:137, 378` vs `DateTime.UtcNow` at other locations.
- `Settings.UpdatedAt`: mixed `GETDATE()` vs `DateTime.UtcNow` in different services.

### Hardcoded UTC+3 Offset
`AppClock.cs:31`, `clsFiscalYearService.cs:38`, `clsReportService.cs:607`, etc. — consistent across readers but fragile if SQL Server host timezone changes.

### Business Impact
- Cash/Z reports may exclude refund transactions that occurred in the early morning hours (00:00–03:00).
- FEFO expiry enforcement may be unreliable around midnight.
- Historical data comparisons using `UpdatedAt`/`LastCountedAt` may be unreliable.

### Required Changes
(a) Fix refund `PaymentDate` to use program day (UTC+3) — smallest, highest-value fix.
(b) Replace raw `GETDATE()` with `GETUTCDATE()`/`SYSUTCDATETIME()` in all raw UPDATEs.
(c) Route FEFO "today" through one shared helper.
(d) Tests for UTC/UTC+3-straddling timestamps.

---

## Issue 8 — Test Fixtures: Fixed Destructive Database Name

**Status: CONFIRMED** | **Severity: Medium**

### Files
- `Sayl.Tests\DatabaseFixture.cs:20` — `public const string DbName = "SaylCogsTest"`
- `consol app\Program.cs:31` — connects to **real** `Server=.\SAYL;Database=SaylDB`

### Finding
1. `DatabaseFixture.cs` uses a **fixed** DB name `SaylCogsTest` and runs `SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE` in both `InitializeAsync` and `DisposeAsync`. Two concurrent test runs will drop each other's database.

2. The QA console harness connects to the **production** `SaylDB` and performs writes.

### Business Impact
- Developer data loss risk during concurrent test runs.
- Production data corruption risk if the console harness is accidentally run against the production database.

### Required Changes
1. Randomize the integration DB name per run (e.g., `SaylCogsTest_{Guid:N12}`).
2. Add drop-by-prefix cleanup for orphaned test databases.
3. Sandbox the console harness by default with an explicit opt-in flag for live-DB QA.

---

## Issue 9 — QA_Report.md Outdated vs Current Source

**Status: PARTIALLY PRESENT** | **Severity: Low**

### Finding
`QA_Report.md` (dated 2026-09-16, branch `security-fixes`) is behind the current `bug-fixes` branch by many commits.

### Status of Previously Reported Issues

| QA item | Description | Current status |
|---|---|---|
| A2 | Backup restore gate | **Still open** — Issue 6 above |
| A3 | Actor identity from client-supplied UserId | Unchanged design concern |
| A10 | Pre-restore cleanup | Fixed |
| B1 | Direct sale releasing reservation | Fixed |
| B2/A11 | First-line returns | Fixed |
| B4 | Refund by original payment methods | Fixed |
| B5 | Expired-batch restock | Fixed but **different than documented** |
| B9 | Program day UTC+3 | Unified in report service — **regressed in refund PaymentDate** |
| B8 | Fiscal guards on expenses/payroll/returns | Present |

### Required Changes
Refresh the report, correct B5 remediation text, and re-open A2 in any tracking list.

---

# Prioritized Implementation Plan

| # | Issue | Status | Severity | Action |
|---|-------|--------|----------|--------|
| 1 | Stock count post idempotency | CONFIRMED | Critical | Persist IsPosted per line atomically; unique movement refs; partial-failure retry tests |
| 2 | Restore authorization | CONFIRMED | High | actorUserId + permission gate + audit before any destructive step |
| 3 | Stock count service authorization | CONFIRMED | High | Gates on StartSessionAsync/PostSessionAsync/SetCountedAsync; identity+gate tests |
| 4 | Weighted-average cost in ReceiveStockAsync | CONFIRMED | High | Apply purchase-path merge equation; costing tests |
| 5 | Refund PaymentDate program day | PARTIALLY PRESENT | Medium | Fix DateOnly.FromDateTime(DateTime.UtcNow) to program day; boundary test |
| 6 | Fiscal readiness debt math | PARTIALLY PRESENT | Medium | Route through clsPaymentAllocation; allocation-aware and return-aware debt tests |
| 7 | Variance valuation | PARTIALLY PRESENT | Medium | Batch-level cost on count lines; costed surplus batches; summary tests |
| 8 | Time-domain cleanup | PARTIALLY PRESENT | Medium | GETDATE to GETUTCDATE in raw SQL; shared program-day helper for FEFO |
| 9 | Test fixture hygiene | CONFIRMED | Medium | Randomized DB name; sandbox console harness |
| 10 | Documentation refresh | PARTIALLY PRESENT | Low | Refresh QA_Report.md; correct B5 text; document UTC+3 policy |

---

# Business Policy Questions (Require Stakeholder Clarification)

1. **Stock count retry semantics:** Should retry re-derive diffs from *current* inventory (reconciliation), or should posting be strictly atomic (all-or-nothing)?
2. **Restore gate bit:** `AppPermissions.Settings` (2048) as the UI currently implies, or admin-only (`IsAdmin`)?
3. **Manual receive into a costed batch:** Should differing `UnitCost` merge weighted-average, or be rejected with "use a new batch number"?
4. **Surplus cost basis:** For count surpluses with no identifiable batch, is `Product.CostPrice` an acceptable documented fallback?
5. **Closed-year return dead end:** Is the "approved accounting adjustment" workflow intentionally manual, or should a current-period adjustment mechanism be built?
6. **Timezone contract:** Confirm business operates permanently at UTC+3. If deployments could run elsewhere, program-day helper should read a configurable offset.
7. **Live-DB QA harness:** Is running the console harness against production `SaylDB` an accepted, supervised practice?

---

*No application source, database, or documentation files were modified during this audit.*
