# Tasks for Agent

Read AGENTS.md first for full project context. Then implement all remaining backend services.

## Services to implement
1. clsProductService (CRUD, inventory creation for stock type, soft delete)
2. clsRoleService (CRUD, prevent delete if role is used)
3. clsExpenseService (CRUD with filters)
4. clsReportService (daily sales, inventory status, profit summary with COGS placeholder, top selling products)
5. Add to clsStockService: manual stock adjustment, batch transfer

## Patterns to follow
- Inject SaylDbContext via constructor
- Use DTOs from Common.DTOs (create new if needed)
- Return Result or Result<T>
- Partial update: only update provided fields
- Use transactions where needed
- Do not modify existing files, create new ones
- If schema change needed, ask first

## Output
Provide complete code files with proper namespaces. After completion, provide summary.