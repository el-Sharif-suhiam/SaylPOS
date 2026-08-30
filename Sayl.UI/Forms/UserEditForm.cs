using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة مستخدم جديد: اسم مستخدم + كلمة مرور + دور.</summary>
    public class UserEditForm : Form
    {
        private readonly List<DtoRole> _roles;

        private TextBox _txtUserName = null!;
        private TextBox _txtPassword = null!;
        private ComboBox _cmbRole = null!;

        public UserEditForm(List<DtoRole> roles)
        {
            _roles = roles;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "إضافة مستخدم";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 330);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = "إضافة مستخدم جديد", Bounds = new Rectangle(0, 16, 420, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblUser = new Label { Text = "اسم المستخدم *", Bounds = new Rectangle(30, 58, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtUserName = new TextBox { Bounds = new Rectangle(30, 82, 360, 36), Font = AppTheme.FontBody };

            var lblPass = new Label { Text = "كلمة المرور * (8 أحرف على الأقل، حروف وأرقام)", Bounds = new Rectangle(30, 128, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtPassword = new TextBox { Bounds = new Rectangle(30, 152, 360, 36), Font = AppTheme.FontBody, UseSystemPasswordChar = true };

            var lblRole = new Label { Text = "الدور *", Bounds = new Rectangle(30, 198, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbRole = new ComboBox { Bounds = new Rectangle(30, 222, 360, 36), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var role in _roles)
                _cmbRole.Items.Add($"{role.Name} ({role.RoleValue})");
            _cmbRole.SelectedIndex = _roles.Count > 0 ? 0 : -1;

            var btnOk = AppTheme.MakeButton("إضافة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 268, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(270, 268, 120, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, lblUser, _txtUserName, lblPass, _txtPassword, lblRole, _cmbRole, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtUserName.Text))
            {
                UiHelpers.ShowError(this, "اسم المستخدم مطلوب.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtPassword.Text))
            {
                UiHelpers.ShowError(this, "كلمة المرور مطلوبة.");
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
                UiHelpers.ShowError(this, "اختر دورًا.");
                return;
            }

            var request = new NewUserRequest(
                _txtUserName.Text.Trim(),
                _txtPassword.Text,
                _roles[_cmbRole.SelectedIndex].RoleId,
                EmployeeId: null);

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                result = await usersService.AddNewUserAsync(request);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشلت الإضافة." : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, "تمت إضافة المستخدم بنجاح.");
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>نافذة إدخال كلمة مرور بسيطة.</summary>
    public class PasswordInputForm : Form
    {
        private readonly TextBox _txt = new();

        public string Value => _txt.Text?.Trim() ?? "";

        public PasswordInputForm(string caption)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 190);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            Text = "كلمة المرور";

            var lbl = new Label { Text = caption, Bounds = new Rectangle(24, 14, 352, 40), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextPrimary };
            _txt.Bounds = new Rectangle(24, 58, 352, 36);
            _txt.UseSystemPasswordChar = true;

            var btnOk = AppTheme.MakeButton("تأكيد", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
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

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(240, 110, 136, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lbl, _txt, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ActiveControl = _txt;
        }
    }
}
