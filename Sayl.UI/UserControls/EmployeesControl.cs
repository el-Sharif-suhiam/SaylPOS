using Common.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم الموظفين: عرض/بحث/إضافة/تعديل + تعيين شفت + ربط حساب + كشوف رواتب.</summary>
    internal class EmployeesControl : SectionListControl
    {
        private List<DtoEmployee> _employees = new();

        protected override string Title => "الموظفون";

        // مفتاح «عرض المعطّلين» — الافتراضي: النشطون وفي إجازة (بلا مستقيلين)
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<clsEmployeeService>();
                _employees = await service.GetEmployeesListAsync(TxtSearch.Text?.Trim() ?? "", includeInactive: ShowInactive);

                // اسم الحساب المرتبط لكل موظف (خريطة واحدة — لا استعلام لكل صف)
                var userByEmp = new Dictionary<int, string>();
                using (var scope2 = Program.Services.CreateScope())
                {
                    var db = scope2.ServiceProvider.GetRequiredService<Sayl.Core.Data.SaylDbContext>();
                    var links = await db.Users
                        .Where(u => u.EmployeeId != null)
                        .Select(u => new { u.EmployeeId, u.Username })
                        .ToListAsync();
                    foreach (var l in links)
                        userByEmp[l.EmployeeId!.Value] = l.Username;
                }

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", "الاسم");
                Grid.Columns.Add("colPosition", "المسمى");
                Grid.Columns.Add("colDept", "القسم");
                Grid.Columns.Add("colType", "نوع الراتب");
                Grid.Columns.Add("colSalary", "المعدل");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns.Add("colAccount", "حساب مرتبط");
                Grid.Columns["colName"].FillWeight = 20;
                Grid.Columns["colPosition"].FillWeight = 14;
                Grid.Columns["colDept"].FillWeight = 12;
                Grid.Columns["colType"].FillWeight = 11;
                Grid.Columns["colSalary"].FillWeight = 11;
                Grid.Columns["colStatus"].FillWeight = 11;
                Grid.Columns["colAccount"].FillWeight = 13;

                Grid.Rows.Clear();
                foreach (var e in _employees)
                {
                    int row = Grid.Rows.Add(e.Name, e.Position, e.Department ?? "-",
                        TranslateSalaryType(e.SalaryType), UiHelpers.Money(e.Salary ?? 0),
                        TranslateStatus(e.Status),
                        userByEmp.TryGetValue(e.EmployeeId, out var uname) ? uname : "-");
                    if (e.Status == "resigned")
                        Grid.Rows[row].Cells["colStatus"].Style.ForeColor = AppTheme.Danger;
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الموظفين.");
            }
        }

        internal static string TranslateSalaryType(Common.Enums.enSalaryType type) => type switch
        {
            Common.Enums.enSalaryType.daily => "يومي",
            Common.Enums.enSalaryType.hourly => "بالساعة",
            _ => "شهري"
        };

        internal static string TranslateStatus(string status) => status switch
        {
            "active" => "نشط",
            "on_leave" => "في إجازة",
            "resigned" => "مستقيل",
            _ => status
        };

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة موظف", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditEmployee(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 100);
            AddToolbarGap();
            AddToolbarButton("تعيين شفت", AppTheme.Accent, AppTheme.AccentDark, (_, _) => AssignShift(), 120);
            AddToolbarGap();
            AddToolbarButton("كشوف الرواتب", AppTheme.Success, Color.FromArgb(21, 128, 61),
                (_, _) => OpenPayroll(), 130);
            AddToolbarGap();
            AddToolbarButton("ربط حساب مستخدم", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => LinkAccount(), 150);

            if (SessionState.IsAdmin)
            {
                AddToolbarGap();
                AddToolbarButton("الشفتات والإجازات", AppTheme.SidebarBg, AppTheme.SidebarHover,
                    (_, _) => new ShiftsForm().ShowDialog(FindForm()), 150);
            }
        }

        private DtoEmployee? Selected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            return idx >= 0 && idx < _employees.Count ? _employees[idx] : null;
        }

        private void EditEmployee(DtoEmployee? existing)
        {
            using var form = new EmployeeEditForm(existing);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private void EditSelected()
        {
            var emp = Selected();
            if (emp == null)
            {
                UiHelpers.ShowError(this, "اختر موظفًا من القائمة.");
                return;
            }
            EditEmployee(emp);
        }

        private void AssignShift()
        {
            var emp = Selected();
            if (emp == null)
            {
                UiHelpers.ShowError(this, "اختر موظفًا من القائمة.");
                return;
            }

            using var form = new EmployeeShiftAssignForm(emp.EmployeeId, emp.Name);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private void OpenPayroll()
        {
            var emp = Selected();
            using var form = new PayrollForm(emp?.EmployeeId, emp?.Name);
            form.ShowDialog(FindForm());
        }

        /// <summary>ربط الموظف بحساب مستخدم (للموظفين بلا حساب) — LinkEmployeeAsync.</summary>
        private async void LinkAccount()
        {
            var emp = Selected();
            if (emp == null)
            {
                UiHelpers.ShowError(this, "اختر موظفًا من القائمة.");
                return;
            }

            if (emp.Status == "resigned")
            {
                UiHelpers.ShowError(this, "لا يمكن ربط موظف مستقيل بحساب.");
                return;
            }

            try
            {
                // اختيار المستخدم غير المرتبط بموظف آخر
                List<Common.DTOs.ViewModel.UserVM> freeUsers;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    var all = await usersService.GetUsersAsync();
                    freeUsers = all.Where(u => u.Name == "No Employee" || string.IsNullOrWhiteSpace(u.Name)).ToList();
                }

                if (freeUsers.Count == 0)
                {
                    UiHelpers.ShowInfo(this, "لا توجد حسابات متاحة (كل الحسابات مرتبطة بموظفين).");
                    return;
                }

                using var form = new LinkAccountForm(emp.Name, freeUsers);
                if (form.ShowDialog(FindForm()) != DialogResult.OK || form.SelectedUserId == null)
                    return;

                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    result = await usersService.LinkEmployeeAsync(form.SelectedUserId.Value, emp.EmployeeId);
                }

                UiHelpers.ShowResult(this, result);
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر ربط الحساب.");
            }
        }
    }
}
