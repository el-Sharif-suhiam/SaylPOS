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

        protected override string Title => "الأدوار والصلاحيات";

        // مفتاح «عرض المعطلين» — الأدوار المعطلة تظهر ويمكن استعادتها
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                _roles = await roleService.GetRolesListAsync(TxtSearch.Text?.Trim() ?? "",
                    includeInactive: ShowInactive);

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", "الرقم");
                Grid.Columns.Add("colName", "اسم الدور");
                Grid.Columns.Add("colDesc", "الصلاحيات");
                Grid.Columns.Add("colValue", "القيمة");
                Grid.Columns.Add("colStatus", "الحالة");
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
                        ? AppPermissions.Describe(r.RoleValue)
                        : $"مخصص — {AppPermissions.Describe(r.RoleValue)}";

                    string status = fixedRole
                        ? "أساسي (محمي)"
                        : r.IsActive ? "نشط" : "معطل";

                    Grid.Rows.Add(r.RoleId, r.Name, desc, r.RoleValue, status);
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الأدوار.");
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // «تعطيل/تفعيل» (لا حذف) — سياسة النظام: الحذف الناعم للأدوار.
        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة دور", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditRole(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("تعطيل/تفعيل", AppTheme.Warning, Color.FromArgb(180, 83, 9), async (_, _) => await ToggleActiveAsync(), 140);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _roles.Count)
            {
                UiHelpers.ShowError(this, "اختر دورًا من القائمة.");
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
                UiHelpers.ShowInfo(this, $"الدور '{existing.Name}' أساسي ومحمي — لا يُعدَّل. أنشئ دورًا مخصصًا جديدًا.");
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
                UiHelpers.ShowError(this, "اختر دورًا من القائمة.");
                return;
            }

            var role = _roles[idx];

            // الحماية هنا إرشادية — الخدمة ترفض أيضًا (دفاع مزدوج)
            if (role.RoleValue == AppPermissions.AdminRoleValue ||
                role.RoleValue == AppPermissions.AccountantRoleValue ||
                role.RoleValue == AppPermissions.CashierRoleValue)
            {
                UiHelpers.ShowError(this, $"الدور '{role.Name}' أساسي ومحمي — لا يُعطَّل ولا يُفعَّل.");
                return;
            }

            bool deactivating = role.IsActive;
            if (!ConfirmationDialog.Show(this,
                    deactivating ? "تعطيل دور" : "تفعيل دور",
                    deactivating
                        ? $"سيتم تعطيل الدور '{role.Name}'. حاملوه الحاليون (إن وُجدوا) يُمنعون من الدخول حتى إعادة تعيينهم. متابعة؟"
                        : $"سيتم تفعيل الدور '{role.Name}'. متابعة؟"))
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
