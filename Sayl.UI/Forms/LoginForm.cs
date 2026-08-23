using System.Runtime.InteropServices;
using Common.DTOs;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// شاشة تسجيل الدخول: اختيار المستخدم من القائمة + كلمة المرور.
    /// عند النجاح تُطلق حدث LoginSucceeded ويتولى Program فتح الواجهة المناسبة.
    /// </summary>
    public class LoginForm : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private ComboBox _cmbUsers = null!;
        private TextBox _txtPassword = null!;
        private Button _btnTogglePassword = null!;
        private Button _btnLogin = null!;
        private Label _lblError = null!;

        private List<UserVM> _users = new();
        private List<DtoRole> _roles = new();
        private bool _busy;

        /// <summary>يُطلق بعد نجاح تسجيل الدخول مع بيانات المستخدم.</summary>
        public event Action<UserVM>? LoginSucceeded;

        private sealed class ComboItem
        {
            public string Text { get; init; } = "";
            public UserVM User { get; init; } = null!;

            public override string ToString() => Text;
        }

        public LoginForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadUsersAsync();
        }

        // ─────────────────────────────────────────────────
        // بناء الواجهة
        // ─────────────────────────────────────────────────
        private void BuildUi()
        {
            Text = "تسجيل الدخول — سيل";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 620);
            BackColor = AppTheme.SidebarBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            KeyPreview = true;
            AppTheme.ApplyRoundedRegion(this, 26);

            MouseDown += StartDrag;

            var lblBrand = new Label
            {
                Text = "سيل",
                Bounds = new Rectangle(0, 52, 480, 64),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 32F, FontStyle.Bold)
            };

            var lblTagline = new Label
            {
                Text = "نظام المبيعات وإدارة المخزون",
                Bounds = new Rectangle(0, 118, 480, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(156, 163, 175),
                BackColor = Color.Transparent
            };

            lblBrand.MouseDown += StartDrag;
            lblTagline.MouseDown += StartDrag;

            var card = new Panel
            {
                Bounds = new Rectangle(40, 168, 400, 372),
                BackColor = AppTheme.CardBg
            };
            AppTheme.ApplyRoundedRegion(card, 18);
            card.MouseDown += StartDrag;

            var lblCardTitle = new Label
            {
                Text = "تسجيل الدخول",
                Bounds = new Rectangle(24, 24, 352, 34),
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary,
                BackColor = Color.Transparent
            };

            var lblUserCaption = new Label
            {
                Text = "اختر المستخدم",
                Bounds = new Rectangle(24, 66, 352, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _cmbUsers = new ComboBox
            {
                Bounds = new Rectangle(24, 92, 352, 44),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = AppTheme.FontBody,
                BackColor = AppTheme.FieldBg
            };

            var lblPassCaption = new Label
            {
                Text = "كلمة المرور",
                Bounds = new Rectangle(24, 148, 352, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            var passPanel = new Panel
            {
                Bounds = new Rectangle(24, 174, 352, 44),
                BackColor = AppTheme.FieldBg
            };

            _txtPassword = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = true,
                Font = AppTheme.FontBody,
                BackColor = AppTheme.FieldBg
            };

            _btnTogglePassword = new Button
            {
                Text = "إظهار",
                Dock = DockStyle.Left,
                Width = 60,
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.Primary,
                BackColor = AppTheme.FieldBg,
                Cursor = Cursors.Hand
            };
            _btnTogglePassword.FlatAppearance.BorderSize = 0;
            _btnTogglePassword.Click += (_, _) =>
            {
                bool hidden = _txtPassword.UseSystemPasswordChar;
                _txtPassword.UseSystemPasswordChar = !hidden;
                _btnTogglePassword.Text = hidden ? "إخفاء" : "إظهار";
            };

            passPanel.Controls.Add(_txtPassword);
            passPanel.Controls.Add(_btnTogglePassword);

            _btnLogin = AppTheme.MakeButton("دخول", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnLogin.Bounds = new Rectangle(24, 240, 352, 54);

            _lblError = new Label
            {
                Text = "",
                Bounds = new Rectangle(24, 304, 352, 48),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Danger,
                Visible = false
            };

            card.Controls.Add(lblCardTitle);
            card.Controls.Add(lblUserCaption);
            card.Controls.Add(_cmbUsers);
            card.Controls.Add(lblPassCaption);
            card.Controls.Add(passPanel);
            card.Controls.Add(_btnLogin);
            card.Controls.Add(_lblError);

            var btnAbout = new Button
            {
                Text = "؟",
                Bounds = new Rectangle(16, 16, 42, 42),
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.TextOnDark,
                BackColor = AppTheme.SidebarHover,
                Font = AppTheme.FontHeading,
                Cursor = Cursors.Hand
            };
            btnAbout.FlatAppearance.BorderSize = 0;
            btnAbout.FlatAppearance.MouseOverBackColor = AppTheme.PrimaryDark;
            AppTheme.ApplyRoundedRegion(btnAbout, 21);
            btnAbout.Click += (_, _) =>
                Program.Services.GetRequiredService<AboutForm>().ShowDialog(this);

            var lblFooter = new Label
            {
                Text = "© سيل 2026 — جميع الحقوق محفوظة",
                Bounds = new Rectangle(0, 566, 480, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(107, 114, 128)
            };

            Controls.Add(lblBrand);
            Controls.Add(lblTagline);
            Controls.Add(card);
            Controls.Add(btnAbout);
            Controls.Add(lblFooter);

            AcceptButton = _btnLogin;

            _btnLogin.Click += async (_, _) => await TryLoginAsync();
        }

        // ─────────────────────────────────────────────────
        // تحميل البيانات وتسجيل الدخول
        // ─────────────────────────────────────────────────
        private async Task LoadUsersAsync()
        {
            UseWaitCursor = true;
            _btnLogin.Enabled = false;
            try
            {
                using var scope = Program.Services.CreateScope();
                var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();

                _users = await usersService.GetUsersAsync();
                _roles = await roleService.GetRolesListAsync();

                foreach (var user in _users.Where(u => u.IsActive))
                    _cmbUsers.Items.Add(new ComboItem { User = user, Text = FormatDisplayName(user) });

                if (_cmbUsers.Items.Count == 0)
                    ShowError("لا يوجد مستخدمون نشطون في النظام.");
            }
            catch
            {
                ShowError("تعذر تحميل قائمة المستخدمين. تحقق من الاتصال بقاعدة البيانات.");
            }
            finally
            {
                UseWaitCursor = false;
                _btnLogin.Enabled = _cmbUsers.Items.Count > 0;
            }
        }

        private static string FormatDisplayName(UserVM user)
        {
            string display = string.IsNullOrWhiteSpace(user.Name) || user.Name == "No Employee"
                ? user.UserName
                : user.Name;
            return $"{display} ({user.RoleName})";
        }

        private async Task TryLoginAsync()
        {
            if (_busy)
                return;

            HideError();

            if (_cmbUsers.SelectedItem is not ComboItem selected)
            {
                ShowError("اختر مستخدمًا من القائمة.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtPassword.Text))
            {
                ShowError("أدخل كلمة المرور.");
                _txtPassword.Focus();
                return;
            }

            SetBusy(true);
            try
            {
                Result<UserVM> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    result = await usersService.LoginAsync(selected.User.UserName, _txtPassword.Text);
                }

                if (result.IsSuccess && result.Data != null)
                {
                    SessionState.SetUser(result.Data, _roles);
                    LoginSucceeded?.Invoke(result.Data);
                }
                else
                {
                    ShowError(string.IsNullOrEmpty(result.Message) ? "فشل تسجيل الدخول." : result.Message);
                    _txtPassword.SelectAll();
                    _txtPassword.Focus();
                }
            }
            catch
            {
                ShowError("حدث خطأ غير متوقع أثناء الاتصال بالخادم.");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ─────────────────────────────────────────────────
        // مساعدات
        // ─────────────────────────────────────────────────
        private void SetBusy(bool busy)
        {
            _busy = busy;
            UseWaitCursor = busy;
            _btnLogin.Enabled = !busy && _cmbUsers.Items.Count > 0;
            _cmbUsers.Enabled = !busy;
            _txtPassword.Enabled = !busy;
        }

        private void ShowError(string message)
        {
            _lblError.Text = message;
            _lblError.Visible = true;
        }

        private void HideError() => _lblError.Visible = false;

        private void StartDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                _ = SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }
    }
}
