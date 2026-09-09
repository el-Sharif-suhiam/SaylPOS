using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
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
    /// قسم المستخدمون (من يملك بت 512 — المدير أو المشرف):
    /// • إنشاء/تعطيل/تغيير أدوار الحسابات الأدنى فقط (حماية الأعلى).
    /// • «تغيير كلمة المرور» يظهر للمدير وحده (4095) — المشرف يدير
    ///   الحسابات بلا أسرار الناس.
    /// • قائمة الأدوار في كل نوافذ القسم مفلترة: دون دور المشغّل أو يوازيه
    ///   (+ الكاشير 0 دائمًا متاح — أدنى الجميع).
    /// </summary>
    internal class UsersControl : SectionListControl
    {
        private List<UserVM> _users = new();
        private List<DtoRole> _roles = new();

        protected override string Title => "المستخدمون";

        // مفتاح «عرض المعطّلين» — الافتراضي: الحسابات النشطة فقط
        protected override bool SupportsShowDeleted => true;

        /// <summary>الأدوار المتاحة لهذا المشغّل: أدنى منه + الكاشير (0).</summary>
        private List<DtoRole> AssignableRoles()
        {
            int myValue = SessionState.CurrentRoleValue;
            return _roles.Where(r => r.IsActive &&
                (r.RoleValue == AppPermissions.CashierRoleValue || AppPermissions.Outranks(myValue, r.RoleValue)))
                .ToList();
        }

        /// <summary>هل المشغّل يملك حق إدارة هذا الصف؟ (واجهةً — الخدمة الحارس النهائي).</summary>
        private bool CanManage(UserVM user)
        {
            if (user.UserId == SessionState.CurrentUser?.UserId)
                return true; // استثناءه الشخصي
            return AppPermissions.Outranks(SessionState.CurrentRoleValue, user.RoleValue);
        }

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();

                _users = await usersService.GetUsersAsync(includeInactive: ShowInactive);
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

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // «تغيير كلمة المرور» للمدير وحده (IsAdmin بالهوية 4095).
        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة مستخدم", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => AddUser(), 150);
            AddToolbarGap();
            AddToolbarButton("تعديل الدور", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => ChangeRole(), 130);
            AddToolbarGap();
            AddToolbarButton("تفعيل/تعطيل", AppTheme.Warning, Color.FromArgb(180, 83, 9), async (_, _) => await ToggleActiveAsync(), 140);

            if (SessionState.IsAdmin)
            {
                AddToolbarGap();
                AddToolbarButton("تغيير كلمة المرور", AppTheme.SidebarBg, AppTheme.SidebarHover, async (_, _) => await ResetPasswordAsync(), 180);
            }
        }

        /// <summary>تغيير دور مستخدم — القائمة مفلترة بالسلم الهرمي.</summary>
        private void ChangeRole()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _users.Count)
            {
                UiHelpers.ShowError(this, "اختر مستخدمًا من القائمة.");
                return;
            }

            var user = _users[idx];
            if (!CanManage(user))
            {
                UiHelpers.ShowError(this, $"لا يمكنك إدارة حساب '{user.UserName}' — دوره يوازي دورك أو أعلى منه.");
                return;
            }

            var assignable = AssignableRoles();
            if (assignable.Count == 0)
            {
                UiHelpers.ShowError(this, "لا توجد أدوار متاحة لك للإسناد.");
                return;
            }

            using var cmb = new Form
            {
                Text = $"تغيير دور المستخدم '{user.UserName}'",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 220),
                MaximizeBox = false,
                MinimizeBox = false,
                RightToLeft = RightToLeft.Yes,
                Font = AppTheme.FontBody,
                BackColor = AppTheme.CardBg
            };

            var lbl = new Label { Text = "الدور الجديد", Bounds = new Rectangle(30, 26, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var cmbRole = new ComboBox { Bounds = new Rectangle(30, 52, 360, 36), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var r in assignable)
                cmbRole.Items.Add($"{r.Name} ({AppPermissions.Describe(r.RoleValue)})");
            int current = assignable.FindIndex(r => r.RoleId == user.RoleId);
            cmbRole.SelectedIndex = current >= 0 ? current : 0;

            var btnOk = AppTheme.MakeButton("تغيير", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 120, 220, 48);
            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(270, 120, 120, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            cmb.Controls.AddRange(new Control[] { lbl, cmbRole, btnOk, btnCancel });

            btnOk.Click += async (_, _) =>
            {
                if (cmbRole.SelectedIndex < 0 || cmbRole.SelectedIndex >= assignable.Count)
                {
                    UiHelpers.ShowError(cmb, "اختر دورًا.");
                    return;
                }

                var newRole = assignable[cmbRole.SelectedIndex];
                if (newRole.RoleId == user.RoleId)
                {
                    UiHelpers.ShowInfo(cmb, "لم يتغير الدور.");
                    cmb.Close();
                    return;
                }

                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    result = await usersService.ChangeRoleAsync(user.UserId, newRole.RoleId,
                        actorUserId: SessionState.CurrentUser?.UserId ?? 0);
                }

                UiHelpers.ShowResult(cmb, result);
                if (result.IsSuccess)
                {
                    cmb.DialogResult = DialogResult.OK;
                    cmb.Close();
                }
            };

            if (cmb.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private void AddUser()
        {
            var assignable = AssignableRoles();
            if (assignable.Count == 0)
            {
                UiHelpers.ShowError(this, "لا توجد أدوار متاحة لك للإسناد.");
                return;
            }

            using var form = new UserEditForm(assignable);
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

            if (!CanManage(user))
            {
                UiHelpers.ShowError(this, $"لا يمكنك إدارة حساب '{user.UserName}' — دوره يوازي دورك أو أعلى منه.");
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
                    ? await usersService.DeactivateUserAsync(user.UserId, SessionState.CurrentUser?.UserId)
                    : await usersService.ActivateUserAsync(user.UserId, SessionState.CurrentUser?.UserId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        /// <summary>إعادة تعيين كلمة مرور — المدير وحده (الزر نفسه يظهر له فقط).</summary>
        private async Task ResetPasswordAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _users.Count)
            {
                UiHelpers.ShowError(this, "اختر مستخدمًا من القائمة.");
                return;
            }

            var user = _users[idx];
            if (!CanManage(user))
            {
                UiHelpers.ShowError(this, $"لا يمكنك إدارة حساب '{user.UserName}' — دوره يوازي دورك أو أعلى منه.");
                return;
            }

            using var input = new PasswordInputForm($"كلمة مرور جديدة للمستخدم '{user.UserName}'");
            if (input.ShowDialog(FindForm()) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Value))
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                result = await usersService.ResetPasswordAsync(user.UserId, input.Value,
                    actorUserId: SessionState.CurrentUser?.UserId);
            }

            UiHelpers.ShowResult(this, result, "تم تغيير كلمة المرور.");
        }
    }
}
