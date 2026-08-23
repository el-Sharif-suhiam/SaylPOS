using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// واجهة الكاشير — عنصر نائب في المرحلة الأولى.
    /// سيتم بناؤها كاملة في المرحلة الثانية (بحث المنتجات، السلة، الدفع، التعليق...).
    /// </summary>
    public class PosForm : Form
    {
        public PosForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "سيل — نقاط البيع";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1024, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = AppTheme.SidebarBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;

            var lblTitle = new Label
            {
                Text = "واجهة نقاط البيع (الكاشير)",
                Font = AppTheme.FontTitle,
                ForeColor = Color.White,
                AutoSize = true
            };

            var lblSubtitle = new Label
            {
                Text = "قيد التطوير — سيتم بناؤها كاملة في المرحلة الثانية.",
                Font = AppTheme.FontBody,
                ForeColor = Color.FromArgb(156, 163, 175),
                AutoSize = true
            };

            var user = SessionState.CurrentUser;
            var lblUser = new Label
            {
                Text = user == null ? "" : $"المستخدم: {user.UserName} — {user.RoleName}",
                Font = AppTheme.FontBody,
                ForeColor = AppTheme.TextOnDark,
                AutoSize = true
            };

            var btnLogout = AppTheme.MakeButton("تسجيل الخروج", AppTheme.Danger,
                hoverColor: Color.FromArgb(185, 28, 28));
            btnLogout.AutoSize = false;
            btnLogout.Size = new Size(220, 52);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(CenterInPanel(lblTitle), 0, 1);
            layout.Controls.Add(CenterInPanel(lblSubtitle), 0, 2);
            layout.Controls.Add(CenterInPanel(lblUser), 0, 3);
            layout.Controls.Add(CenterInPanel(btnLogout), 0, 5);
            Controls.Add(layout);
            Resize += (_, _) => RecenterAll(layout);

            btnLogout.Click += async (_, _) => await LogoutAsync();
        }

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

        private static Panel CenterInPanel(Control inner)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            panel.Controls.Add(inner);
            panel.Resize += (_, _) => Recenter(panel);
            return panel;
        }

        private static void Recenter(Panel panel)
        {
            foreach (Control inner in panel.Controls)
            {
                inner.Location = new Point(
                    (panel.ClientSize.Width - inner.Width) / 2,
                    (panel.ClientSize.Height - inner.Height) / 2);
            }
        }

        private static void RecenterAll(TableLayoutPanel layout)
        {
            foreach (Control cell in layout.Controls)
            {
                if (cell is Panel panel)
                    Recenter(panel);
            }
        }
    }
}
