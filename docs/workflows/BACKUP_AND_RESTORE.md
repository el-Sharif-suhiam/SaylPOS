# Backup and Restore — SaylPOS

Database backup and restore are the backbone of an offline deployment: the data lives
on a single machine, so backups are the only recovery path.

## 1. Participants

| Role | Component |
|---|---|
| UI | `SettingsControl` (backup folder, keep-last setting, manual backup, restore) |
| Services | `clsBackupService` |
| Data | SQL Server `BACKUP`/`RESTORE` commands; `SystemLogs` audit |
| Installer | `installer/setup.iss` backs up an existing `SaylDB` during upgrades |

## 2. Backup — `BackupAsync(backupFolder, keepLast = 30)`

```mermaid
sequenceDiagram
    actor U as User
    participant UI as SettingsControl
    participant BK as clsBackupService
    participant FS as File system
    participant DB as SQL Server

    U->>UI: request backup
    UI->>BK: BackupAsync(folder, keepLast)
    BK->>FS: create folder
    BK->>DB: read SQL service account (sys.dm_server_services)
    BK->>FS: icacls grant Modify to the SQL service account only
    BK->>DB: BACKUP DATABASE [db] TO DISK=@path WITH INIT
    BK->>FS: cleanup old .bak files (intends to keep pre-restore copies; see below)
    BK-->>UI: Result of string (full path)
```

Details:

- File name: `<db>_<UTC yyyyMMdd_HHmmss>.bak` in the configured folder (default
  `%ProgramData%\SaylBackups`).
- SQL Server service accounts usually cannot write into user folders, so the service
  grants **the SQL service account only** Modify access via `icacls`. Broad grants
  (e.g., Authenticated Users) are deliberately not used — on a shared shop PC they
  would let any Windows account delete or replace every backup.
- If the service account cannot be read, nothing is granted (fail-closed): the
  resulting BACKUP failure is clearer than silently writable backups.
- Retention cleanup deletes the oldest `.bak` files beyond `keepLast`. Pre-restore
  copies (names ending `_pre-restore.bak`) are exempt: cleanup never deletes them,
  regardless of age, and they do not count toward `keepLast` (which applies to
  ordinary backups only).

## 3. Restore — `RestoreAsync(backupFilePath, actorUserId)`

Restore is deliberately destructive: it replaces the entire live database. The flow is
designed so a failed restore can never leave the shop without a database.

```mermaid
sequenceDiagram
    actor Admin as User (Settings permission)
    participant UI as SettingsControl
    participant BK as clsBackupService
    participant DB as SQL Server

    Admin->>UI: choose .bak file, confirm
    UI->>BK: RestoreAsync(path, actorUserId)

    BK->>DB: 1) authorization gate: active user + AppPermissions.Settings
    BK->>DB: 2) refuse if any POS session is open
    BK->>DB: 3) audit "RestoreDatabase" start (written before the safety copy)
    BK->>DB: 4) mandatory pre-restore BACKUP (suffix pre-restore; fails -> abort)
    BK->>DB: 5) resolve current data/log file paths (sys.master_files)
    BK->>DB: 6) ALTER DATABASE SET SINGLE_USER WITH ROLLBACK IMMEDIATE
    BK->>DB: 7) RESTORE DATABASE WITH REPLACE, RECOVERY (MOVE to current paths)
    BK->>DB: 8) ALTER DATABASE SET MULTI_USER
    BK->>DB: 9) audit "RestoreDatabaseCompleted" (best effort)
    BK-->>UI: Result of string
```

Safety rules, in order:

1. **Authorization gate (commit f6a57b4):** the acting user must exist and be active
   and hold the **Settings** screen bit (`AppPermissions.Settings`) — checked inside
   the service, so hiding the button is not the gate. Accountant and cashier roles are
   structurally refused.
2. **Open sessions block restore:** all POS sessions must be closed first.
3. **Mandatory pre-restore backup (VULN-23):** a safety copy with a `pre-restore`
   suffix is created and must succeed before anything touches the database; it is the
   permanent rollback point and is exempt from retention cleanup (section 2).
4. **Single-user window:** the database is switched to `SINGLE_USER WITH ROLLBACK
   IMMEDIATE` for the restore and always returned to `MULTI_USER` (including on
   failure paths).
5. **Path resilience:** the restore uses `MOVE` with the database's current
   data/log paths, so a backup from another machine restores correctly.
6. **Audit records (VULN-27):** the start record is written *before* the safety copy
   (so it survives inside it); the completion record is written into the restored
   database when the same user exists there. Restores themselves are destructive, so
   denied attempts are not audited (known gap, as elsewhere).

## 4. Restore Authorization and Testing

- The authorization gate and pre-checks are covered by
  `Sayl.Tests/BackupRestoreAuthorizationTests.cs`.
- **No successful RESTORE is exercised in the automated tests** — it is destructive by
  design (it would replace the test fixture's database). The full restore path is
  verified manually.
- `DatabaseFixtureSafetyTests` cover the test infrastructure's destructive-operation
  gates (only databases with the test prefix may be dropped).

## 5. Installer Backups

`installer/setup.iss` backs up an existing `SaylDB` to the SQL instance's default
backup folder before an upgrade installation, so reinstalling over a live database
never loses data.

## 6. Operator Guidance

- Take a manual backup before any risky operation (restore, fiscal close — the close
  wizard includes a backup step, see [FISCAL_CLOSING.md](FISCAL_CLOSING.md)).
- Verify the backup folder is on a different physical disk than the database files
  when possible; the service cannot verify this.
- Copy `pre-restore` backups somewhere safe manually — they are preserved by
  retention cleanup, but the cleanup itself is not an off-machine copy.

## 7. Known Gaps

- **No successful RESTORE in automated tests** (see section 4).
  (The retention-cleanup pattern mismatch that left pre-restore copies exposed
  to cleanup has been fixed — see section 2 and `BackupRetentionTests`.)


