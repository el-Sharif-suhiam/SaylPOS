# Stored Procedures — SaylDB

## Status: There Are None

**SaylDB contains no stored procedures, no user-defined functions, no triggers, and
no views.** A search across `SaylDB.sql`, every `Upgrade_*.sql` script, `DataSeed.sql`
and the application code confirms that no `CREATE PROCEDURE` (or `CREATE VIEW`,
`CREATE FUNCTION`, `CREATE TRIGGER`) exists anywhere in the repository. All data
logic lives in the C# service layer.

## Where Data-Level Logic Actually Lives

Although the database exposes only tables, indexes and CHECK constraints, some
operations execute raw SQL from the services. This is the complete inventory of
data-level logic outside EF Core LINQ:

### 1. Atomic in-place updates (`ExecuteSqlInterpolatedAsync`)

| Operation | Statement pattern | Service |
|---|---|---|
| Batch quantity increase | `UPDATE Batches SET BatchQuantity = BatchQuantity + @q WHERE BatchId = @id` | `clsStockService`, `clsPurchaseService` |
| Batch quantity decrease (guarded) | `UPDATE Batches SET BatchQuantity = BatchQuantity - @q WHERE BatchId = @id AND BatchQuantity >= @q` | `clsStockService.TransferBatchAsync` |
| Weighted-average cost merge | `UPDATE Batches SET UnitCost = CASE ... END` (quantity-weighted average, null-preserving) | `clsStockService`, `clsPurchaseService` |
| Inventory increase / decrease | `UPDATE Inventory SET QuantityAvailable = QuantityAvailable + @q, UpdatedAt = GETUTCDATE() ...` | `clsStockService`, `clsPurchaseService` |
| Supplier balance increase | `UPDATE Suppliers SET Balance = Balance + @delta` | `clsPurchaseService` |
| Customer/supplier balance decrease | `ExecuteUpdateAsync` with a floor at zero | `clsPaymentService` |
| State-transition claims (CAS) | `UPDATE Invoices SET Status = ... WHERE ... AND Status = 'suspended'`; `UPDATE POSSessions SET Status = 'closed' WHERE ... AND Status = 'open'`; fiscal-year `UPDATE Settings ... WHERE SettingValue = @expected` | `clsCheckoutService`, `clsPosSessionService`, `clsFiscalYearService` |

### 2. Application locks (`sp_getapplock`)

- `StockCountSession` — Exclusive, transaction owner: serializes count-session
  opening (`clsStockCountService.StartSessionAsync`).
- `StockCountPost` — Exclusive, session owner: serializes count posting and prevents
  editing count lines during a post (`TryAcquirePostLockAsync`).

### 3. Idempotent schema upgrades (executed at startup)

| Method | SQL script | Purpose |
|---|---|---|
| `clsUsersService.EnsureSchemaUpgradesAsync` | `EnsureSchemaSql` | Brings older databases up to current columns/constraints |
| `clsCheckoutService.EnsureReservationSchemaAsync` | `EnsureReservationSchemaSql` | Creates `StockReservations` and migrates existing suspended-invoice reservations |
| `clsStockCountService.EnsureCountSchemaAsync` | `EnsureCountSchemaSql` | Adds `CountedAt`, `BookQuantityAtCount`, `PostedMovementId`, and the open-count unique index |

These are `IF NOT EXISTS`-guarded DDL batches — running them repeatedly is safe.

### 4. Server administration (raw ADO.NET / `ExecuteSqlRawAsync`)

- `BACKUP DATABASE [db] TO DISK = @path WITH INIT, NAME = @name`
  (`clsBackupService.BackupAsync` and the pre-restore copy).
- `RESTORE DATABASE [db] FROM DISK = @path WITH REPLACE, RECOVERY, MOVE ...`
  after `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE`, always
  followed by `SET MULTI_USER`.
- `SELECT service_account FROM sys.dm_server_services ...` to grant the SQL service
  account write access to the backup folder via `icacls`.
- Test-only helpers in `Sayl.Tests` (database creation/drop for isolated fixtures).

### 5. Lock hints

`WITH (UPDLOCK)` on customer/supplier balance reads inside payment-collection
transactions (`clsPaymentService`), so parallel collectors wait instead of reading a
stale balance.

## Why No Stored Procedures

The application follows a deliberate single-layer service design (see
[SYSTEM_ARCHITECTURE.md](../architecture/SYSTEM_ARCHITECTURE.md)): business rules,
validation and SQL all live in `Sayl.Core.Services`, keeping one language and one
deployable artifact (`Sayl.UI.exe`) responsible for behavior. The schema script
remains a pure table/index/constraint definition, which simplifies fresh installs and
the idempotent upgrade path. There is no documented plan to introduce stored
procedures; the concurrency-critical paths rely on transaction isolation, app locks,
CAS updates and filtered unique indexes instead.


