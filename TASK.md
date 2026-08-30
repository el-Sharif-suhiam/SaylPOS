# Tasks for Agent

Read AGENTS.md first for full project context. All backend services and the main UI are implemented.
The remaining work is hardening and completion items below.

## Backlog
1. **Test suite** — xUnit project covering money paths: checkout FEFO, suspended invoices (reserve/release), returns, session close, purchase invoice totals, backup/restore.
2. **License hardening (spec §11)** — clock-rollback guard (store last-seen UtcTicks in registry), periodic in-runtime license checks before critical operations, obfuscate release builds (ConfuserEx/.NET Reactor).
3. Optional later: barcode label printing, first-run forced admin password change after seed, offline auto-update story.

## Done recently (do not redo)
- Purchase invoice UI (`PurchaseForm` wired into purchases tab)
- Database restore: `clsBackupService.RestoreAsync` (blocks on open sessions, single-user lock with guaranteed MULTI_USER release, MOVE to current file paths) + «استعادة نسخة احتياطية» button in SettingsControl

## Patterns to follow
- Inject SaylDbContext via constructor
- Use DTOs from Common.DTOs (create new if needed)
- Return Result or Result<T>
- Partial update: only update provided fields
- Use transactions where needed
- UI: DialogForm base + AppTheme, services via Program.Services.CreateScope(), results via UiHelpers.ShowResult
- Do not modify existing files unless required; ask first if a schema change is needed

## Output
Provide complete code files with proper namespaces. After completion, provide summary.
