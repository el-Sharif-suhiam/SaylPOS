# UI Implementation Prompt for Sayl WinForms Application

## Project Context
Read AGENTS.md and TASKS.md first to understand the project architecture, services, DTOs, and patterns.

Now implement the complete WinForms UI layer for the Sayl system, following all existing patterns and integrating with the already-built backend services.

## General UI Architecture
The application consists of three main forms:

1. **LoginForm**  
2. **PosForm** (Cashier workspace)  
3. **MainForm** (Admin/Accountant dashboard)

After successful login, redirect:
- Cashier → `PosForm`
- Admin or Accountant → `MainForm`

Do NOT expose EF Core `DbContext` directly to any UI. Use only the services from `Sayl.Core.Services` via constructor injection or a central service provider.

## LoginForm Requirements
- Centered on screen, **borderless with rounded corners**.
- Contains a **ComboBox** listing active users (from `clsAuthService`). Display user name or employee name.
- A password `TextBox` (with `UseSystemPasswordChar = true`).
- A Login button.
- A small **About/Help (?) button** in the corner (opens About form).
- Should be visually modern and clean. Creative design is allowed.
- On login success, open the appropriate form and close the login form.

## PosForm (Cashier Screen) Requirements
This is a **touch-friendly single window** designed for fast sales.

- **Top area**: Search box for product (barcode or name). A search button or real-time search (Enter key).
- **Invoice items grid** (`DataGridView`): columns like product name, quantity, unit price, total. Allow remove line or change quantity.
- **Customer selection**: optional; button "Select Customer" opens a picker (or quick add). Display customer name.
- **Total / Discount / Tax / Net** display.
- **Payment button**: opens `PaymentForm` to choose payment method, enter received amount, shows change.
- **Suspend button**: suspends current invoice (calls `CheckoutService.ProcessCheckoutAsync` with `suspend=true`).
- **Resume suspended invoice**: button to list suspended invoices and resume.
- **Receive Stock button**: visible only if current user role has permission `can_receive_stock`. Opens `ReceiveStockForm`.
- Confirmation popup before finalizing any sale or suspending.

## MainForm (Admin/Accountant Dashboard) Requirements
- **Sidebar navigation** with buttons for:
  - Dashboard
  - Products
  - Inventory
  - Customers
  - Suppliers
  - Sales Invoices
  - Purchase Invoices
  - Expenses
  - Reports
  - Users (Admin only)
  - Roles & Permissions (Admin only)
- **Main content panel** that hosts `UserControl` for each section. When a sidebar button is clicked, swap the current `UserControl` inside the panel (do not open new windows for these sections).
- Admin and Accountant see Dashboard and all operational sections, but only Admin sees Users and Roles.

## Dashboard UserControl
- Display graphical statistics using a charting library (e.g., LiveCharts2, ScottPlot, or OxyPlot).
- Show sales, incoming/outgoing amounts, with filters by week, month, quarter, year, and previous year.
- Show top selling products.
- Data comes from `clsReportService`.

## All UserControls (Products, Inventory, Customers, Suppliers, Invoices, Expenses, Reports, Users, Roles)
- Follow standard CRUD pattern.
- Use `DataGridView` for listing with search, add, edit, delete buttons.
- Editing/adding opens a **modal popup form** specific to that entity (e.g., `ProductEditForm`, `CustomerEditForm`).
- Delete operations MUST show a confirmation popup.
- All forms must be touch-friendly with adequate button sizes and spacing.

## Popup Forms Needed
- `PaymentForm` – for processing payments (cash, card, bank transfer). Shows total, received amount, change. For credit sales, allows partial payment.
- `CustomerPickerForm` – search and select customer, or add new quick customer.
- `SuspendedInvoicesForm` – list suspended invoices with resume/delete options.
- `ReceiveStockForm` – enter new stock (product, quantity, purchase price, batch number, expiry if applicable). Uses `clsStockService.ReceiveStockAsync`.
- `AdjustStockForm` – manual stock adjustment (increase/decrease) with reason.
- `ConfirmationDialog` – reusable popup for confirming sensitive actions (delete, finalize sale, adjust stock, role changes). Can use `MessageBox.Show` with YesNo, but ensure consistent styling.
- `AboutForm` – displays project name, version, description, maybe logo.

## Responsive and Touch-Friendly Requirements
- All forms should use proper anchoring and docking so they adapt to different screen sizes (from small tablets to large monitors).
- Use `AutoScaleMode.Dpi` to support high DPI screens.
- For PosForm, buttons should be large enough for touch (minimum 48x48 px).
- Use `TableLayoutPanel` or `FlowLayoutPanel` where appropriate to create responsive layouts.
- Ensure no controls overlap or cut off when window is resized.

## About/Help Integration
- Add a (?) button or menu item in the corner of `LoginForm` and possibly in `MainForm` sidebar (bottom) to open the About form.
- The About form should include project details (you can use placeholder text, but mention Sayl system).

## Permissions
- UI should respect role-based permissions.
- Define roles: Admin, Accountant, Cashier.
- Admin has full access including Users and Roles management.
- Accountant has all except Users and Roles management.
- Cashier only sees PosForm; no access to MainForm.
- `ReceiveStock` permission is controlled by a permission code `can_receive_stock` assigned to Cashier role. When checked, show Receive Stock button in PosForm; otherwise hide.

## Integration with Backend Services
- Use existing services from `Sayl.Core.Services`.
- Services are already implemented (or will be completed according to TASKS.md) and should be injected via constructor or resolved from a central `IServiceProvider` configured in `Program.cs`.
- Register all forms and services in `Program.cs` using `Microsoft.Extensions.DependencyInjection`.
- Do not modify service logic; only call their methods and handle `Result`/`Result<T>` returns.

## Output
Generate complete code files for all forms, user controls, popups, and any helper classes needed for the UI.
Include proper namespaces (e.g., `Sayl.UI.Forms`, `Sayl.UI.UserControls`).
Use clear naming conventions.
After implementation, provide a summary of files created and any assumptions made.