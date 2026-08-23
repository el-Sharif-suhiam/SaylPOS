using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم الأدوار والصلاحيات (للمدير فقط): إضافة، تعديل، حذف.</summary>
    internal class RolesControl : SectionListControl
    {
        private List<DtoRole> _roles = new();

        protected override string Title => "الأدوار والصلاحيات";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                _roles = await roleService.GetRolesListAsync(TxtSearch.Text?.Trim() ?? "");

                Grid.Columns.Clear();
                Grid.Columns.Add("colId", "الرقم");
                Grid.Columns.Add("colName", "اسم الدور");
                Grid.Columns.Add("colValue", "قيمة الصلاحية");
                Grid.Columns.Add("colDesc", "الوصف");
                Grid.Columns["colId"].FillWeight = 15;
                Grid.Columns["colName"].FillWeight = 35;
                Grid.Columns["colValue"].FillWeight = 25;
                Grid.Columns["colDesc"].FillWeight = 25;

                Grid.Rows.Clear();
                foreach (var r in _roles)
                {
                    string desc = r.RoleValue >= 100 ? "كامل الصلاحيات (مدير)"
                        : r.RoleValue >= 75 ? "إدارة مالية بدون المستخدمين (محاسب)"
                        : r.RoleValue >= 50 ? "نقطة البيع فقط (كاشير)"
                        : "مخصص";
                    Grid.Rows.Add(r.RoleId, r.Name, r.RoleValue, desc);
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الأدوار.");
            }
        }

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة دور", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditRole(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("حذف", AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeleteAsync(), 110);
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
            using var form = new RoleEditForm(existing);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeleteAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _roles.Count)
            {
                UiHelpers.ShowError(this, "اختر دورًا من القائمة.");
                return;
            }

            var role = _roles[idx];
            if (!ConfirmationDialog.Show(this, "حذف دور",
                $"سيتم حذف الدور '{role.Name}'. لن ينجح الحذف إذا كان الدور مستخدمًا. هل أنت متأكد؟"))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                result = await roleService.DeleteRoleAsync(role.RoleId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
