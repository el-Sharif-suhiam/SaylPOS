using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;
using Sayl.UI.UserControls;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// النافذة الرئيسية للمدير/المحاسب: شريط جانبي + لوحة محتوى تستضيف UserControl لكل قسم.
    /// الأقسام تُستضاف كـ UserControl داخل اللوحة الرئيسية وتُبدَّل عند التنقل.
    /// </summary>
    public class MainForm : Form
    {
        private const int SidebarWidth = 260;

        private readonly Dictionary<string, Func<UserControl>> _sections = new();
        private readonly List<Button> _navButtons = new();

        private Label _lblPageTitle = null!;
        private Label _lblUserInfo = null!;
        private Panel _hostPanel = null!;
        private UserControl? _currentControl;
        private Button? _activeNavButton;

        public MainForm()
        {
            BuildSections();
            BuildUi();

            Shown += async (_, _) =>
            {
                SelectSection(_sections.Keys.First());
                await RunDailyBackupIfDueAsync();
            };

            FormClosing += async (_, args) =>
            {
                if (_backupOnCloseStarted) return;
                _backupOnCloseStarted = true;
                args.Cancel = true;
                await BackupOnCloseAsync();
                Close();
            };
        }

        private bool _backupOnCloseStarted;

        /// <summary>نسخة احتياطية يومية عند فتح البرنامج (إن لم تُجرَ اليوم).</summary>
        private async Task RunDailyBackupIfDueAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                string lastBackup = await settings.GetAsync(clsSettingsService.KeyLastBackupDate);
                if (lastBackup == DateTime.Today.ToString("yyyy-MM-dd"))
                    return;

                string folder = await settings.GetAsync(clsSettingsService.KeyBackupFolder,
                    clsBackupService.DefaultBackupFolder);

                var backupService = scope.ServiceProvider.GetRequiredService<clsBackupService>();
                var result = await backupService.BackupAsync(folder);
                if (result.IsSuccess)
                    await settings.SetAsync(clsSettingsService.KeyLastBackupDate, DateTime.Today.ToString("yyyy-MM-dd"));
            }
            catch
            {
                // فشل النسخ اليومي لا يعطل عمل البرنامج
            }
        }

        /// <summary>نسخة احتياطية عند إغلاق البرنامج.</summary>
        private async Task BackupOnCloseAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                string folder = await settings.GetAsync(clsSettingsService.KeyBackupFolder,
                    clsBackupService.DefaultBackupFolder);

                var backupService = scope.ServiceProvider.GetRequiredService<clsBackupService>();
                var result = await backupService.BackupAsync(folder);
                if (result.IsSuccess)
                    await settings.SetAsync(clsSettingsService.KeyLastBackupDate, DateTime.Today.ToString("yyyy-MM-dd"));
            }
            catch
            {
                // نغلق البرنامج حتى لو فشل النسخ (لا نحبس المستخدم)
            }
        }

        // ─────────────────────────────────────────────────
        // تعريف الأقسام
        // ─────────────────────────────────────────────────
        private void BuildSections()
        {
            _sections["لوحة التحكم"] = () => new DashboardControl();
            _sections["المنتجات"] = () => new ProductsControl();
            _sections["المخزون"] = () => new InventoryControl();
            _sections["العملاء"] = () => new CustomersControl();
            _sections["الموردون"] = () => new SuppliersControl();
            _sections["فواتير البيع"] = () => new InvoicesControl(enInvoicesType.sale);
            _sections["فواتير الشراء"] = () => new InvoicesControl(enInvoicesType.purchase);
            _sections["المصروفات"] = () => new ExpensesControl();
            _sections["التقارير"] = () => new ReportsControl();

            if (SessionState.IsAdmin)
            {
                _sections["المستخدمون"] = () => new UsersControl();
                _sections["الأدوار والصلاحيات"] = () => new RolesControl();
                _sections["الإعدادات"] = () => new SettingsControl();
            }
        }

        // ─────────────────────────────────────────────────
        // بناء الواجهة
        // ─────────────────────────────────────────────────
        private void BuildUi()
        {
            Text = "سيل — لوحة التحكم";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(960, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            Icon = Brand.AppIcon;

            var sidebar = new Panel
            {
                Dock = DockStyle.Right,
                Width = SidebarWidth,
                BackColor = AppTheme.SidebarBg,
                Padding = new Padding(12, 16, 12, 12)
            };

            var contentArea = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg };

            BuildSidebar(sidebar);
            BuildContent(contentArea);

            // ترتيب الإرساء معكوس: آخر عنصر يُرسّى أولًا — Fill أولًا ثم الحواف
            Controls.Add(contentArea);
            Controls.Add(sidebar);
        }

        private void BuildSidebar(Panel sidebar)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));

            // العلامة التجارية: شعار + اسم
            var brandPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            var sidebarLogo = Brand.MakeLogo(42, AppTheme.SidebarBg);
            sidebarLogo.Dock = DockStyle.Left;

            var lblApp = new Label
            {
                Text = "سيل",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomRight,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                Padding = new Padding(0, 0, 10, 0)
            };
            brandPanel.Controls.Add(lblApp);
            brandPanel.Controls.Add(sidebarLogo);
            layout.Controls.Add(brandPanel, 0, 0);

            // قائمة الأقسام
            var menuLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new Padding(4, 12, 4, 8)
            };
            menuLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;
            foreach (var name in _sections.Keys)
            {
                menuLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                var btn = MakeNavButton(name);
                _navButtons.Add(btn);
                menuLayout.Controls.Add(btn, 0, row);
                row++;
            }
            // صف فارغ يمتص المساحة الزائدة
            menuLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            menuLayout.RowCount = row + 1;
            layout.Controls.Add(menuLayout, 0, 1);

            // أسفل الشريط: حول البرنامج + تسجيل الخروج
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            var btnAbout = MakeGhostButton("حول البرنامج", AppTheme.TextOnDark);
            btnAbout.Click += (_, _) =>
                Program.Services.GetRequiredService<AboutForm>().ShowDialog(this);

            var btnLogout = MakeGhostButton("تسجيل الخروج", Color.FromArgb(248, 113, 113));
            btnLogout.Click += async (_, _) => await LogoutAsync();

            footer.Controls.Add(btnAbout, 0, 0);
            footer.Controls.Add(btnLogout, 0, 2);
            layout.Controls.Add(footer, 0, 2);

            sidebar.Controls.Add(layout);
        }

        private Button MakeNavButton(string name)
        {
            var btn = new Button
            {
                Text = name,
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.TextOnDark,
                BackColor = AppTheme.SidebarBg,
                Font = new Font("Segoe UI", 11F),
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = name
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = AppTheme.SidebarHover;
            btn.FlatAppearance.MouseDownBackColor = AppTheme.SidebarHover;
            btn.Padding = new Padding(6, 0, 18, 0);

            btn.MouseEnter += (_, _) =>
            {
                if (!ReferenceEquals(btn, _activeNavButton))
                    btn.BackColor = AppTheme.SidebarHover;
            };
            btn.MouseLeave += (_, _) =>
            {
                if (!ReferenceEquals(btn, _activeNavButton))
                    btn.BackColor = AppTheme.SidebarBg;
            };
            btn.Click += (_, _) => SelectSection(name);

            return btn;
        }

        private static Button MakeGhostButton(string text, Color foreColor)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = foreColor,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Dock = DockStyle.Fill,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 81);
            btn.FlatAppearance.MouseOverBackColor = AppTheme.SidebarHover;
            btn.FlatAppearance.MouseDownBackColor = AppTheme.SidebarHover;
            return btn;
        }

        private void BuildContent(Panel contentArea)
        {
            // رأس الصفحة: عنوان القسم يمينًا + بيانات المستخدم يسارًا
            // (مع RightToLeft.Yes تنعكس أعمدة الجدول: العمود الأول يظهر يمينًا)
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = AppTheme.CardBg,
                ColumnCount = 2
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

            _lblPageTitle = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextPrimary,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                Margin = new Padding(24, 0, 8, 0)
            };

            _lblUserInfo = new Label
            {
                Text = BuildUserInfoText(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.TextMuted,
                Margin = new Padding(24, 0, 0, 0)
            };

            header.Controls.Add(_lblPageTitle, 0, 0);
            header.Controls.Add(_lblUserInfo, 1, 0);

            _hostPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.Bg,
                Padding = new Padding(20)
            };

            contentArea.Controls.Add(_hostPanel);
            contentArea.Controls.Add(header);
        }

        private static string BuildUserInfoText()
        {
            var user = SessionState.CurrentUser;
            if (user == null)
                return "";
            string display = string.IsNullOrWhiteSpace(user.Name) || user.Name == "No Employee"
                ? user.UserName
                : user.Name;
            return $"{display} — {user.RoleName}";
        }

        // ─────────────────────────────────────────────────
        // التنقل بين الأقسام
        // ─────────────────────────────────────────────────
        private void SelectSection(string name)
        {
            if (!_sections.ContainsKey(name))
                return;

            foreach (var btn in _navButtons)
                btn.BackColor = ReferenceEquals(btn.Tag, name) ? AppTheme.Primary : AppTheme.SidebarBg;

            _activeNavButton = _navButtons.FirstOrDefault(b => ReferenceEquals(b.Tag, name));

            _lblPageTitle.Text = name;

            _currentControl?.Dispose();
            _currentControl = _sections[name]();
            _hostPanel.Controls.Add(_currentControl);
            _currentControl.BringToFront();
        }

        // ─────────────────────────────────────────────────
        // تسجيل الخروج
        // ─────────────────────────────────────────────────
        private async Task LogoutAsync()
        {
            bool confirmed = ConfirmationDialog.Show(this, "تسجيل الخروج",
                "هل أنت متأكد من رغبتك في تسجيل الخروج؟");
            if (!confirmed)
                return;

            try
            {
                int? userId = SessionState.CurrentUser?.UserId;
                if (userId.HasValue)
                {
                    using var scope = Program.Services.CreateScope();
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    await usersService.LogoutAsync(userId.Value);
                }
            }
            catch
            {
                // نتجاهل فشل تسجيل الخروج في السجل ونكمل الخروج من الواجهة.
            }

            SessionState.Clear();
            Program.Navigator.Show(Program.Services.GetRequiredService<LoginForm>());
        }
    }
}
