# Data Flow — SaylPOS

Sequence diagrams for the main operation flows. Each diagram reflects the current
implementation on the `bug-fixes` branch.

## 1. Typical Operation Flow (UI → service → DAL → SQL)

```mermaid
sequenceDiagram
    participant F as Form / UserControl
    participant S as Service (Sayl.Core.Services)
    participant C as SaylDbContext (EF Core 10)
    participant DB as SQL Server SaylDB

    F->>S: async call with request DTO + actor userId
    S->>S: permission check (clsPermissions.GetUserRoleValueAsync)
    S->>C: LINQ validation reads
    C->>DB: SELECT
    DB-->>C: rows
    S->>S: business rules and guards
    S->>C: BeginTransaction (Serializable where needed)
    C->>DB: INSERT / UPDATE / raw atomic SQL
    S->>C: SaveChangesAsync
    C->>DB: commit
    S-->>F: Result / Result-of-T (success or Arabic failure message)
    F->>F: bind Data or show Message
```

## 2. Sales and Payment Workflow

```mermaid
sequenceDiagram
    participant POS as PosForm
    participant CK as clsCheckoutService
    participant DB as SaylDB

    POS->>CK: ProcessCheckoutAsync(DtoCheckoutRequest, suspend=false)
    CK->>CK: discount/implicit-price permission checks
    CK->>DB: BEGIN TRANSACTION (Serializable)
    CK->>DB: resolve open POS session
    CK->>DB: INSERT Invoices header (totals rounded once)
    CK->>DB: INSERT InvoiceItems rows
    CK->>DB: FEFO batch selection (expiry asc, skip expired/quarantined)
    CK->>DB: UPDATE Batches.BatchQuantity, INSERT StockMovements (sale, per batch)
    CK->>DB: UPDATE Inventory.QuantityAvailable
    CK->>DB: INSERT Payments (cash-attributed to session), SET PaymentStatus
    CK->>DB: UPDATE Customers.Balance for credit sale (credit-limit guard)
    CK->>DB: INSERT SystemLogs audit
    CK->>DB: COMMIT
    CK-->>POS: Result of int (invoiceId)
```

Failure paths: any guard throw rolls back the whole transaction; SQL deadlock (1205)
is retried up to 3 times; parallel sell against the same stock is blocked by the
Serializable re-check of availability.

## 3. Inventory Receipt and Stock Movement

```mermaid
flowchart TB
    subgraph Inputs
        PI["Purchase invoice item<br/>(clsPurchaseService)"]
        MR["Manual receive<br/>(clsStockService.ReceiveStockAsync)"]
        ADJ["Manual adjustment / stock-count variance<br/>(clsStockService.AdjustStockAsync)"]
        RET["Customer return<br/>(clsReturnService)"]
        TRF["Batch transfer<br/>(clsStockService.TransferBatchAsync)"]
    end
    subgraph Batch["Batches row"]
        QTY["BatchQuantity += delta"]
        COST["UnitCost weighted-average merge<br/>(nullable: no cost invented,<br/>falls back to Product.CostPrice)"]
    end
    INV["Inventory.QuantityAvailable<br/>atomic UPDATE"]
    MOV["StockMovements row<br/>type + batch + SourceType/SourceId"]
    SNAP["StockCount.BookQuantityAtCount snapshot<br/>(when counted during a count session)"]

    PI --> QTY
    MR --> QTY
    ADJ --> QTY
    RET --> QTY
    TRF --> QTY
    QTY --> COST
    QTY --> INV
    QTY --> MOV
    ADJ --> SNAP
```

Cost rules (see [COSTING.md](../COSTING.md)):

- COGS is read from the consumed batch (`Batches.UnitCost`), never from
  `Product.CostPrice` directly.
- Merging quantity into an existing batch (purchase, manual receive, transfer)
  applies a quantity-weighted average of the batch cost.
- A batch with no stored cost falls back to `Product.CostPrice` at sale time —
  the documented reserve; no alternative cost is invented.
- A sale line that consumed several batches is split into one `InvoiceItem` row per
  batch, each with its own `ItemCost`.

## 4. Stock Count and Posting (with idempotency)

```mermaid
sequenceDiagram
    participant U as InventoryControl / StockCountForm
    participant SC as clsStockCountService
    participant ST as clsStockService
    participant DB as SaylDB

    U->>SC: StartSessionAsync(userId)
    SC->>DB: permission gate (Inventory bit)
    SC->>DB: BEGIN Serializable + sp_getapplock "StockCountSession"
    SC->>DB: snapshot every active stock product (BookQuantity, UnitCost)
    SC->>DB: COMMIT (CountSessionId = max + 1)

    loop per counted line
        U->>SC: SetCountedAsync(countId, counted, actor)
        SC->>DB: permission gate
        SC->>DB: sp_getapplock "StockCountPost" (session-owned)
        SC->>DB: snapshot BookQuantityAtCount + CountedAt
        SC->>DB: release lock
    end

    U->>SC: PostSessionAsync(userId)
    SC->>DB: permission gate + "StockCountPost" lock
    loop per unposted line
        SC->>DB: any StockMovement with SourceType=ManualAdjustment AND SourceId=CountId?
        alt movement exists (previous partial failure)
            SC->>SC: skip line, restore PostedMovementId
        else
            SC->>ST: AdjustStockAsync(diff, SourceId=CountId)
            ST->>DB: per-line Serializable transaction (FEFO deduction or auto batch)
            SC->>DB: set PostedMovementId + IsPosted in memory
        end
    end
    SC->>DB: one SaveChangesAsync (all flags atomic)
    SC->>DB: audit log
```

Idempotency contract:

- Every posting adjustment writes `StockMovements.SourceType = 'ManualAdjustment'`
  with `SourceId = CountId` — a permanent idempotency key (multiple movements per
  line are legitimate for FEFO shortages, hence a filtered non-unique index,
  `IX_StockMovements_ManualAdjustmentSource`, not a unique constraint).
- Before posting a line, the service checks the database (not memory) for an
  existing movement with that key; found movements are linked into
  `StockCounts.PostedMovementId` and the line is skipped.
- All `IsPosted` / `PostedMovementId` flags are saved in one `SaveChangesAsync`,
  so a mid-run failure leaves recovered lines marked and retriable without any
  double adjustment.
- A filtered unique index (`UQ_StockCounts_OpenProduct`, `WHERE IsPosted = 0`)
  prevents two open count sessions for the same product.

## 5. Backup and Restore

```mermaid
sequenceDiagram
    actor Admin
    participant UI as SettingsControl
    participant BK as clsBackupService
    participant DB as SQL Server

    Admin->>UI: request backup
    UI->>BK: BackupAsync(folder, keepLast=30)
    BK->>DB: grant write ACL to SQL service account (icacls, best effort)
    BK->>DB: BACKUP DATABASE ... TO DISK
    BK->>BK: cleanup old .bak files (intends to keep pre-restore copies; see BACKUP_AND_RESTORE.md)

    Admin->>UI: request restore (Settings permission required)
    UI->>BK: RestoreAsync(path, actorUserId)
    BK->>DB: authorization gate: active user + AppPermissions.Settings
    BK->>DB: refuse if any POS session is open
    BK->>DB: audit "RestoreDatabase" start record
    BK->>DB: mandatory pre-restore BACKUP (suffix pre-restore)
    BK->>DB: ALTER DATABASE SET SINGLE_USER WITH ROLLBACK IMMEDIATE
    BK->>DB: RESTORE DATABASE ... WITH REPLACE, RECOVERY (MOVE to current file paths)
    BK->>DB: ALTER DATABASE SET MULTI_USER
    BK->>DB: audit "RestoreDatabaseCompleted" (best effort)
    BK-->>UI: Result of string
```

Notes:

- Restore is deliberately destructive; automated tests cover the authorization gate
  and pre-checks only — no successful RESTORE is exercised in tests.

## 6. Authentication / Authorization Flow

See [AUTHENTICATION_AND_AUTHORIZATION.md](../security/AUTHENTICATION_AND_AUTHORIZATION.md)
for the full policy; the condensed flow:

```mermaid
sequenceDiagram
    actor U as User
    participant LF as LoginForm
    participant US as clsUsersService
    participant SS as SessionState (UI)
    participant Svc as Target service

    U->>LF: username + password
    LF->>US: LoginAsync(username, password)
    US->>US: lockout check (5 failures -> 15 min, in-memory)
    US->>US: BCrypt verify (dummy-hash timing equalizer for unknown users)
    US->>US: fail closed if user or role inactive
    US->>US: SystemLogs audit "Login"
    US-->>LF: Result of UserVM (RoleValue mask)
    LF->>SS: SetUser(vm)
    SS-->>U: cashier -> PosForm, otherwise MainForm (sections filtered by mask)

    U->>Svc: any state-changing operation
    Svc->>Svc: GetUserRoleValueAsync(actorUserId) + AppPermissions.Has(mask, bit)
    Svc-->>U: Result.Fail when denied (not audited — known gap)
```


