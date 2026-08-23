using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم المستخدمون (للمدير فقط): إضافة، تعطيل/تفعيل، تغيير كلمة المرور.</summary>
    internal class UsersControl : SectionListControl
    {
        private List<UserVM> _users = new();
        private List<DtoRole> _roles = new();

        protected override string Title => "المستخدمون";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();

                _users = await usersService.GetUsersAsync();
                _roles = await roleService.GetRolesListAsync();

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", "الرقم");
                Grid.Columns.Add("colUser", "اسم المستخدم");
                Grid.Columns.Add("colName", "الاسم");
                Grid.Columns.Add("colRole", "الدور");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns["colId"].FillWeight = 10;
                Grid.Columns["colUser"].FillWeight = 22;
                Grid.Columns["colName"].FillWeight = 30;
                Grid.Columns["colRole"].FillWeight = 20;
                Grid.Columns["colStatus"].FillWeight = 18;

                Grid.Rows.Clear();
                foreach (var u in _users)
                {
                    string name = string.IsNullOrWhiteSpace(u.Name) || u.Name == "No Employee" ? "-" : u.Name;
                    Grid.Rows.Add(u.UserId, u.UserName, name, u.RoleName, u.IsActive ? "نشط" : "معطل");
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل المستخدمين.");
            }
        }

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة مستخدم", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => AddUser(), 150);
            AddToolbarGap();
            AddToolbarButton("تفعيل/تعطيل", AppTheme.Warning, Color.FromArgb(180, 83, 9), async (_, _) => await ToggleActiveAsync(), 140);
            AddToolbarGap();
            AddToolbarButton("تغيير كلمة المرور", AppTheme.SidebarBg, AppTheme.SidebarHover, async (_, _) => await ResetPasswordAsync(), 180);
        }

        private void AddUser()
        {
            if (_roles.Count == 0)
            {
                UiHelpers.ShowError(this, "لا توجد أدوار متاحة.");
                return;
            }

            using var form = new UserEditForm(_roles);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task ToggleActiveAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _users.Count)
            {
                UiHelpers.ShowError(this, "اختر مستخدمًا من القائمة.");
                return;
            }

            var user = _users[idx];
            if (user.UserId == SessionState.CurrentUser?.UserId)
            {
                UiHelpers.ShowError(this, "لا يمكنك تعطيل حسابك الحالي.");
                return;
            }

            if (!ConfirmationDialog.Show(this, user.IsActive ? "تعطيل مستخدم" : "تفعيل مستخدم",
                    user.IsActive ? $"سيتم تعطيل المستخدم '{user.UserName}'. هل أنت متأكد؟" : $"سيتم تفعيل المستخدم '{user.UserName}'. متابعة؟"))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                result = user.IsActive
                    ? await usersService.DeactivateUserAsync(user.UserId)
                    : await usersService.ActivateUserAsync(user.UserId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        private async Task ResetPasswordAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _users.Count)
            {
                UiHelpers.ShowError(this, "اختر مستخدمًا من القائمة.");
                return;
            }

            var user = _users[idx];
            using var input = new PasswordInputForm($"كلمة مرور جديدة للمستخدم '{user.UserName}'");
            if (input.ShowDialog(FindForm()) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Value))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                result = await usersService.ResetPasswordAsync(user.UserId, input.Value);
            }

            UiHelpers.ShowResult(this, result, "تم تغيير كلمة المرور.");
        }
    }
}
