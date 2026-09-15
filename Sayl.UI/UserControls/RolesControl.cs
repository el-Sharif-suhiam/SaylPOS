using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// قسم الأدوار والصلاحيات (المدير وحده — بت 1024):
    /// إضافة/تعديل أدوار مخصصة بفورم الشبكة، وتعطيل/تفعيل (حذف ناعم).
    /// الأدوار الأساسية الثلاثة تظهر للعلم ولا تُعدَّل ولا تُعطَّل (خدميًا).
    /// </summary>
    internal class RolesControl : SectionListControl
    {
        private List<DtoRole> _roles = new();

        protected override string Title => AppLang.T("Roles.Section.Title");

        // مفتاح «عرض المعطلين» — الأدوار المعطلة تظهر ويمكن استعادتها
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                var paged = await roleService.GetRolesPagedAsync(
                    TxtSearch.Text?.Trim() ?? "",
                    includeInactive: ShowInactive,
                    page: _currentPage, pageSize: _pageSize);
                if (!ApplyPaged(paged))
                    return;
                _roles = paged.Items;

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", AppLang.T("Invoices.Label.Id"));
                Grid.Columns.Add("colName", AppLang.T("Roles.Label.RoleName"));
                Grid.Columns.Add("colDesc", AppLang.T("Roles.Label.Perms"));
                Grid.Columns.Add("colValue", AppLang.T("Roles.Label.Value"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns["colId"].FillWeight = 8;
                Grid.Columns["colName"].FillWeight = 24;
                Grid.Columns["colDesc"].FillWeight = 42;
                Grid.Columns["colValue"].FillWeight = 12;
                Grid.Columns["colStatus"].FillWeight = 14;

                Grid.Rows.Clear();
                foreach (var r in _roles)
                {
                    bool fixedRole = r.RoleValue == AppPermissions.AdminRoleValue
                        || r.RoleValue == AppPermissions.AccountantRoleValue
                        || r.RoleValue == AppPermissions.CashierRoleValue;

                    string desc = fixedRole
                        ? AppLang.PermDescribe(r.RoleValue)
                        : AppLang.T("Roles.Custom", AppLang.PermDescribe(r.RoleValue));

                    string status = fixedRole
                        ? AppLang.T("Roles.Fixed")
                        : r.IsActive ? AppLang.T("Common.Status.Active") : AppLang.T("Common.Status.Inactive");

                    Grid.Rows.Add(r.RoleId, r.Name, desc, r.RoleValue, status);
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Roles.Msg.LoadFailed"));
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // «تعطيل/تفعيل» (لا حذف) — سياسة النظام: الحذف الناعم للأدوار.
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Roles.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditRole(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Roles.Button.Toggle"), AppTheme.Warning, Color.FromArgb(180, 83, 9), async (_, _) => await ToggleActiveAsync(), 140);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _roles.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Roles.Msg.PickRole"));
                return;
            }
            EditRole(_roles[idx]);
        }

        private void EditRole(DtoRole? existing)
        {
            // الأدوار الأساسية محمية من التعديل — لا نفتح المحرر لها أصلًا
            if (existing != null &&
                (existing.RoleValue == AppPermissions.AdminRoleValue ||
                 existing.RoleValue == AppPermissions.AccountantRoleValue ||
                 existing.RoleValue == AppPermissions.CashierRoleValue))
            {
                UiHelpers.ShowInfo(this, AppLang.T("Roles.Msg.FixedNoEdit", existing.Name));
                return;
            }

            using var form = new RoleEditForm(existing);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task ToggleActiveAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _roles.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Roles.Msg.PickRole"));
                return;
            }

            var role = _roles[idx];

            // الحماية هنا إرشادية — الخدمة ترفض أيضًا (دفاع مزدوج)
            if (role.RoleValue == AppPermissions.AdminRoleValue ||
                role.RoleValue == AppPermissions.AccountantRoleValue ||
                role.RoleValue == AppPermissions.CashierRoleValue)
            {
                UiHelpers.ShowError(this, AppLang.T("Roles.Msg.FixedNoToggle", role.Name));
                return;
            }

            bool deactivating = role.IsActive;
            if (!ConfirmationDialog.Show(this,
                    deactivating ? AppLang.T("Roles.Title.Deactivate") : AppLang.T("Roles.Title.Activate"),
                    deactivating
                        ? AppLang.T("Roles.Msg.DeactivateConfirm", role.Name)
                        : AppLang.T("Roles.Msg.ActivateConfirm", role.Name)))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                result = deactivating
                    ? await roleService.DeactivateRoleAsync(role.RoleId, SessionState.CurrentUser?.UserId ?? 0)
                    : await roleService.ActivateRoleAsync(role.RoleId, SessionState.CurrentUser?.UserId ?? 0);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
