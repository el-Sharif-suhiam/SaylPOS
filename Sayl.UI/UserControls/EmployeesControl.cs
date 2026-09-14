using Common.DTOs;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// قسم الموظفين: عرض/بحث/إضافة/تعديل + تعيين شفت + ربط حساب + كشوف رواتب.
    /// حماية الارتباط: موظف مربوط بحساب يوازي المشغّل أو يعلوه لا يُعدَّل
    /// (إلا إن كان حسابه هو) — الطريق الثاني لبيانات الحسابات الحساسة.
    /// </summary>
    internal class EmployeesControl : SectionListControl
    {
        private List<DtoEmployee> _employees = new();
        private Dictionary<int, (string username, int roleValue)> _linkedUsers = new();

        protected override string Title => AppLang.T("Employees.Section.Title");

        // مفتاح «عرض المعطّلين» — الافتراضي: النشطون وفي إجازة (بلا مستقيلين)
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<clsEmployeeService>();
                _employees = await service.GetEmployeesListAsync(TxtSearch.Text?.Trim() ?? "", includeInactive: ShowInactive);

                // خريطة الارتباط: (اسم الحساب + قيمة دوره) لكل موظف — تُستخدم
                // للعرض ولحماية التحرير على موظفي الحسابات العليا
                _linkedUsers = new Dictionary<int, (string, int)>();
                using (var scope2 = Program.Services.CreateScope())
                {
                    var db = scope2.ServiceProvider.GetRequiredService<Sayl.Core.Data.SaylDbContext>();
                    var links = await db.Users
                        .Where(u => u.EmployeeId != null)
                        .Select(u => new { u.EmployeeId, u.Username, u.Role.RoleValue })
                        .ToListAsync();
                    foreach (var l in links)
                        _linkedUsers[l.EmployeeId!.Value] = (l.Username, l.RoleValue);
                }

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", AppLang.T("Common.Label.Name"));
                Grid.Columns.Add("colPosition", AppLang.T("Employees.Label.Position"));
                Grid.Columns.Add("colDept", AppLang.T("Employees.Label.Department"));
                Grid.Columns.Add("colType", AppLang.T("Employees.Label.SalaryType"));
                Grid.Columns.Add("colSalary", AppLang.T("Employees.Label.Rate"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns.Add("colAccount", AppLang.T("Employees.Label.LinkedAccount"));
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
                        _linkedUsers.TryGetValue(e.EmployeeId, out var u) ? u.username : "-");
                    if (e.Status == "resigned")
                        Grid.Rows[row].Cells["colStatus"].Style.ForeColor = AppTheme.Danger;
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.LoadFailed"));
            }
        }

        // ── حارس القسم: هل يحق للمشغّل تعديل موظف؟ ──
        // موظف بلا حساب = كيان بلا سلطة — تعديل حر.
        // مربوط بحساب يوازيني أو يعلوني = ممنوع (راجع مدير النظام).
        // حسابه هو نفسه = مسموح (استثناءه الشخصي).
        private bool CanEditEmployee(DtoEmployee emp)
        {
            if (!_linkedUsers.TryGetValue(emp.EmployeeId, out var link))
                return true;

            var me = SessionState.CurrentUser;
            if (me != null && link.username == me.UserName)
                return true;

            return AppPermissions.Outranks(SessionState.CurrentRoleValue, link.roleValue);
        }

        private string? EmployeeGuardMessage(DtoEmployee emp)
        {
            if (!_linkedUsers.TryGetValue(emp.EmployeeId, out var link))
                return null;

            var me = SessionState.CurrentUser;
            if (me != null && link.username == me.UserName)
                return null;

            if (!AppPermissions.Outranks(SessionState.CurrentRoleValue, link.roleValue))
                return AppLang.T("Employees.Msg.LinkGuard", emp.Name, link.username);

            return null;
        }

        internal static string TranslateSalaryType(Common.Enums.enSalaryType type) => type switch
        {
            Common.Enums.enSalaryType.daily => AppLang.T("Employees.Salary.Daily"),
            Common.Enums.enSalaryType.hourly => AppLang.T("Employees.Salary.Hourly"),
            _ => AppLang.T("Employees.Salary.Monthly")
        };

        internal static string TranslateStatus(string status) => status switch
        {
            "active" => AppLang.T("Employees.Status.Active"),
            "on_leave" => AppLang.T("Employees.Status.OnLeave"),
            "resigned" => AppLang.T("Employees.Status.Resigned"),
            _ => status
        };

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // أوسع شريط أدوات بالنظام: إضافة/تعديل/شفت/رواتب/ربط حساب
        // + «الشفتات والإجازات» للأدمن فقط (نمط الزر الشرطي).
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Employees.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditEmployee(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 100);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Employees.Button.AssignShift"), AppTheme.Accent, AppTheme.AccentDark, (_, _) => AssignShift(), 120);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Employees.Button.Payrolls"), AppTheme.Success, Color.FromArgb(21, 128, 61),
                (_, _) => OpenPayroll(), 130);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Employees.Button.LinkAccount"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => LinkAccount(), 150);

            if (SessionState.Can(Common.Utils.AppPermissions.Users))
            {
                AddToolbarGap();
                AddToolbarButton(AppLang.T("Employees.Button.Shifts"), AppTheme.SidebarBg, AppTheme.SidebarHover,
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
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.PickEmployee"));
                return;
            }

            // حارس الارتباط: موظف الحساب الأعلى لا يُعدَّل من هنا
            string? guard = EmployeeGuardMessage(emp);
            if (guard != null)
            {
                UiHelpers.ShowError(this, guard);
                return;
            }

            EditEmployee(emp);
        }

        private void AssignShift()
        {
            var emp = Selected();
            if (emp == null)
            {
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.PickEmployee"));
                return;
            }

            string? guard = EmployeeGuardMessage(emp);
            if (guard != null)
            {
                UiHelpers.ShowError(this, guard);
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

        /// <summary>ربط الموظف بحساب مستخدم (للموظفين بلا حساب) — LinkEmployeeAsync.
        /// الحارس: مربوط بحساب أعلى/موازٍ لا يُلمس — والقائمة تقترح الحرة فقط.</summary>
        private async void LinkAccount()
        {
            var emp = Selected();
            if (emp == null)
            {
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.PickEmployee"));
                return;
            }

            if (emp.Status == "resigned")
            {
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.CantLinkResigned"));
                return;
            }

            string? guard = EmployeeGuardMessage(emp);
            if (guard != null)
            {
                UiHelpers.ShowError(this, guard);
                return;
            }

            try
            {
                // اختيار المستخدم غير المرتبط بموظف آخر — وبأدوار يمكن للمشغّل إدارتها
                List<Common.DTOs.ViewModel.UserVM> freeUsers;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    var all = await usersService.GetUsersAsync();
                    int myValue = SessionState.CurrentRoleValue;
                    freeUsers = all.Where(u => (u.Name == "No Employee" || string.IsNullOrWhiteSpace(u.Name))
                        && AppPermissions.Outranks(myValue, u.RoleValue)).ToList();
                }

                if (freeUsers.Count == 0)
                {
                    UiHelpers.ShowInfo(this, AppLang.T("Employees.Msg.NoFreeAccounts"));
                    return;
                }

                using var form = new LinkAccountForm(emp.Name, freeUsers);
                if (form.ShowDialog(FindForm()) != DialogResult.OK || form.SelectedUserId == null)
                    return;

                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    result = await usersService.LinkEmployeeAsync(form.SelectedUserId.Value, emp.EmployeeId,
                        actorUserId: SessionState.CurrentUser?.UserId);
                }

                UiHelpers.ShowResult(this, result);
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Employees.Msg.LinkFailed"));
            }
        }
    }
}
