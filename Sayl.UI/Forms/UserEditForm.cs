using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Results;
using Common.Utils;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة مستخدم جديد: اسم مستخدم + كلمة مرور + دور + ربط اختياري بموظف.</summary>
    public class UserEditForm : Form
    {
        private readonly List<DtoRole> _roles;

        private TextBox _txtUserName = null!;
        private TextBox _txtPassword = null!;
        private ComboBox _cmbRole = null!;
        private ComboBox _cmbEmployee = null!;
        private List<Common.DTOs.DtoEmployee> _employees = new();

        public UserEditForm(List<DtoRole> roles)
        {
            _roles = roles;
            BuildUi();
            Load += async (_, _) => await LoadEmployeesAsync();
        }

        private async Task LoadEmployeesAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var employeeService = scope.ServiceProvider.GetRequiredService<clsEmployeeService>();
                // موظفون نشطون فقط (لا يُربط مستقيل بحساب جديد)
                _employees = await employeeService.GetEmployeesListAsync(includeInactive: false);

                _cmbEmployee.Items.Clear();
                _cmbEmployee.Items.Add(AppLang.T("UserEdit.NoEmployee"));
                foreach (var e in _employees)
                    _cmbEmployee.Items.Add(e.Name);
                _cmbEmployee.SelectedIndex = 0;
            }
            catch
            {
                _cmbEmployee.Items.Clear();
                _cmbEmployee.Items.Add(AppLang.T("UserEdit.NoEmployee"));
                _cmbEmployee.SelectedIndex = 0;
            }
        }

        // 📖 «الإحداثيات اليدوية» — راجع EmployeeEditForm للأساسيات. نقاطها:
        //  • الأدوار تُمرَّر مسبقًا للمنشئ (لا استعلام داخل البناء) — نفس
        //    فكرة «البناء بلا بيانات، التحميل لاحقًا» لكنها هنا معامل.
        //  • قائمة الموظفين عنصرها الأول «بدون ربط بموظف» — فهرس 0 يعني
        //    لا شيء، والموظف الحقيقي فهرسًا 1..N (احسبه -1 عند الحفظ).
        //  • سياسة كلمة المرور موثقة داخل تسمية الحقل نفسها (8 أحرف+).
        //  • ClientSize يُضبط مرتين (330 ثم 410) — لأن الحقل الأخير أُضيف
        //    بعد التقدير الأول؛ السطر الثاني هو الفاصل. عدّل الأخير فقط.
        private void BuildUi()
        {
            Text = AppLang.T("UserEdit.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 330);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = AppLang.T("UserEdit.Heading"), Bounds = new Rectangle(0, 16, 420, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblUser = new Label { Text = AppLang.T("UserEdit.Label.Username"), Bounds = new Rectangle(30, 58, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtUserName = new TextBox { Bounds = new Rectangle(30, 82, 360, 36), Font = AppTheme.FontBody };

            var lblPass = new Label { Text = AppLang.T("UserEdit.Label.Password"), Bounds = new Rectangle(30, 128, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtPassword = new TextBox { Bounds = new Rectangle(30, 152, 360, 36), Font = AppTheme.FontBody, UseSystemPasswordChar = true };

            var lblRole = new Label { Text = AppLang.T("UserEdit.Label.Role"), Bounds = new Rectangle(30, 198, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbRole = new ComboBox { Bounds = new Rectangle(30, 222, 360, 36), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            // وصف الشاشات بدل الرقم الصم — قائمة الأدوار تصل مفلترة هرميًا
            // من UsersControl (أدنى من المشغّل + الكاشير)
            foreach (var role in _roles)
                _cmbRole.Items.Add($"{role.Name} — {AppLang.PermDescribe(role.RoleValue)}");
            _cmbRole.SelectedIndex = _roles.Count > 0 ? 0 : -1;

            var lblEmployee = new Label { Text = AppLang.T("UserEdit.Label.Employee"), Bounds = new Rectangle(30, 268, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbEmployee = new ComboBox { Bounds = new Rectangle(30, 292, 360, 36), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Add"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 346, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(270, 346, 120, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            ClientSize = new Size(420, 410);
            Controls.AddRange(new Control[] { lblTitle, lblUser, _txtUserName, lblPass, _txtPassword, lblRole, _cmbRole, lblEmployee, _cmbEmployee, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtUserName.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("UserEdit.Msg.UsernameRequired"));
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtPassword.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("UserEdit.Msg.PasswordRequired"));
                return;
            }

            string? policyError = clsUsersService.ValidatePasswordPolicy(_txtPassword.Text);
            if (policyError != null)
            {
                UiHelpers.ShowError(this, policyError);
                return;
            }

            if (_cmbRole.SelectedIndex < 0 || _cmbRole.SelectedIndex >= _roles.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("UserEdit.Msg.PickRole"));
                return;
            }

            // ربط الموظف: الفهرس 0 = بدون ربط
            int? employeeId = null;
            if (_cmbEmployee.SelectedIndex > 0 && _cmbEmployee.SelectedIndex - 1 < _employees.Count)
                employeeId = _employees[_cmbEmployee.SelectedIndex - 1].EmployeeId;

            var request = new NewUserRequest(
                _txtUserName.Text.Trim(),
                _txtPassword.Text,
                _roles[_cmbRole.SelectedIndex].RoleId,
                EmployeeId: employeeId);

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                result = await usersService.AddNewUserAsync(request, actorUserId: SessionState.CurrentUser?.UserId);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("UserEdit.Msg.AddFailed") : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, AppLang.T("UserEdit.Msg.Added"));
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>نافذة إدخال كلمة مرور بسيطة.</summary>
    public class PasswordInputForm : Form
    {
        private readonly TextBox _txt = new();

        // VULN-31: بلا Trim — كلمة المرور قيمة حرفية؛ المسافات الجانبية
        // المقصودة كانت تُقلَم بصمت فتُحفظ غير المكتوبة (وتختلف عن المدخلة).
        public string Value => _txt.Text ?? "";

        public PasswordInputForm(string caption)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 190);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            Text = AppLang.T("PwdInput.Title");

            var lbl = new Label { Text = caption, Bounds = new Rectangle(24, 14, 352, 40), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextPrimary };
            _txt.Bounds = new Rectangle(24, 58, 352, 36);
            _txt.UseSystemPasswordChar = true;

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Confirm"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(24, 110, 200, 48);
            btnOk.Click += (_, _) =>
            {
                string? policyError = clsUsersService.ValidatePasswordPolicy(_txt.Text);
                if (policyError != null)
                {
                    UiHelpers.ShowError(this, policyError);
                    return;
                }
                DialogResult = DialogResult.OK;
            };

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(240, 110, 136, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lbl, _txt, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ActiveControl = _txt;
        }
    }
}
