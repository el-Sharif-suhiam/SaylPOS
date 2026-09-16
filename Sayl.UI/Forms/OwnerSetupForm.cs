using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// VULN-47: شاشة إنشاء «حساب المدير» عند أول تركيب — بديل الحساب الديمو
    /// ذي كلمة المرور المعروفة (admin/Admin@123). تظهر حين يكون النظام بلا
    /// أي مستخدم نشط (التركيب الصامت أو تجاوز ملف التزويد المعلّق). بعد
    /// إنشاء أول حساب نشط يُغلق هذا المسار نهائيًا، ولا يمكن إعادة تشغيله
    /// فوق بيانات حية.
    /// </summary>
    public class OwnerSetupForm : DialogForm
    {
        private readonly string _prefillUsername;

        private TextBox _txtUsername = null!;
        private TextBox _txtPassword = null!;
        private TextBox _txtConfirm = null!;
        private Label _lblError = null!;
        private Button _btnCreate = null!;
        private bool _busy;

        public OwnerSetupForm(string prefillUsername = "")
        {
            _prefillUsername = prefillUsername;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "Sayl";
            CornerRadius = 22;
            BackColor = AppTheme.Bg;
            ForeColor = AppTheme.TextPrimary;
            Font = AppTheme.FontBody;
            ClientSize = new Size(460, 430);
            StartPosition = FormStartPosition.CenterScreen;

            var lblBrand = new Label
            {
                Text = AppLang.T("Owner.Title"),
                AutoSize = false,
                Bounds = new Rectangle(28, 24, 404, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Primary
            };
            Controls.Add(lblBrand);

            var lblInfo = new Label
            {
                Text = AppLang.T("Owner.Info"),
                Bounds = new Rectangle(34, 62, 392, 42),
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.TextMuted,
                Font = AppTheme.FontBody
            };
            Controls.Add(lblInfo);

            var lblUser = new Label
            {
                Text = AppLang.T("Owner.Label.User"),
                Bounds = new Rectangle(34, 118, 392, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            Controls.Add(lblUser);

            _txtUsername = new TextBox
            {
                Bounds = new Rectangle(34, 142, 392, 34),
                BackColor = AppTheme.FieldBg,
                ForeColor = AppTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.FontBody
            };
            Controls.Add(_txtUsername);

            var lblPass = new Label
            {
                Text = AppLang.T("Owner.Label.Password"),
                Bounds = new Rectangle(34, 190, 392, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            Controls.Add(lblPass);

            _txtPassword = new TextBox
            {
                Bounds = new Rectangle(34, 214, 392, 34),
                BackColor = AppTheme.FieldBg,
                ForeColor = AppTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.FontBody,
                PasswordChar = '●'
            };
            Controls.Add(_txtPassword);

            var lblConfirm = new Label
            {
                Text = AppLang.T("Owner.Label.Confirm"),
                Bounds = new Rectangle(34, 262, 392, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            Controls.Add(lblConfirm);

            _txtConfirm = new TextBox
            {
                Bounds = new Rectangle(34, 286, 392, 34),
                BackColor = AppTheme.FieldBg,
                ForeColor = AppTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.FontBody,
                PasswordChar = '●'
            };
            Controls.Add(_txtConfirm);

            var lblPolicy = new Label
            {
                Text = AppLang.T("Owner.Policy"),
                Bounds = new Rectangle(34, 330, 392, 30),
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = AppTheme.AccentDark,
                Font = new Font("Segoe UI", 8.5F)
            };
            Controls.Add(lblPolicy);

            _lblError = new Label
            {
                Text = "",
                Bounds = new Rectangle(34, 362, 392, 18),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Danger,
                Font = new Font("Segoe UI", 8.5F)
            };
            Controls.Add(_lblError);

            _btnCreate = AppTheme.MakeButton(AppLang.T("Owner.Button.Create"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnCreate.Bounds = new Rectangle(129, 394, 202, AppTheme.TouchMin);
            _btnCreate.Click += async (_, _) => await CreateOwnerAsync();
            Controls.Add(_btnCreate);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _txtUsername.Text = _prefillUsername;
            if (!string.IsNullOrEmpty(_prefillUsername))
                _txtPassword.Focus();
            else
                _txtUsername.Focus();
        }

        private async Task CreateOwnerAsync()
        {
            if (_busy) return;
            _lblError.Text = "";

            string username = _txtUsername.Text.Trim();
            string password = _txtPassword.Text;
            string confirm = _txtConfirm.Text;

            if (username.Length < 3 || username.Length > 50)
            {
                _lblError.Text = AppLang.T("Owner.Err.Username");
                return;
            }

            string? policyError = clsUsersService.ValidatePasswordPolicy(password);
            if (policyError != null)
            {
                _lblError.Text = AppLang.T("Owner.Err.Policy");
                return;
            }

            if (password != confirm)
            {
                _lblError.Text = AppLang.T("Owner.Err.Confirm");
                return;
            }

            _busy = true;
            _btnCreate.Enabled = false;
            _btnCreate.Text = AppLang.T("Owner.Button.Creating");
            try
            {
                using var scope = Program.Services.CreateScope();
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                Result<UserVM> result = await usersService.CreateOwnerAsync(username, password);

                if (result.IsSuccess)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    _lblError.Text = AppLang.T("Owner.Err.Failed", result.Message ?? "");
                }
            }
            catch (Exception ex)
            {
                _lblError.Text = AppLang.T("Owner.Err.Unexpected");
                Program.LogError("OWNER", ex);
            }
            finally
            {
                _busy = false;
                _btnCreate.Enabled = true;
                _btnCreate.Text = AppLang.T("Owner.Button.Create");
            }
        }
    }
}