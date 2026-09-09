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
    public class LoginForm : DialogForm
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
        private Label _lblConnectionStatus = null!;

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

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «شاشة بلا إطار + بطاقة» (نافذة تسجيل الدخول):
        //
        // البنية (كلها بإحداثيات يدوية — Bounds بالبكسل):
        //   ┌──────────────────────────────────┐
        //   │ الشعار والاسم (وسط، داكن)          │ ← فوق البطاقة
        //   │ ┌────────────────────────────┐   │
        //   │ │ البطاقة البيضاء (480×342)     │   │ ← الحقول وزر الدخول هنا
        //   │ └────────────────────────────┘   │
        //   │ حالة الاتصال + حقوق (وسط، أسفل)  │
        //   └──────────────────────────────────┘
        //   زرا «؟» و«×» الدائريان: زوايا النافذة (أعلى يمين وأعلى يسار).
        //
        // لماذا DialogForm + CornerRadius=26؟ نافذة بلا شريط عنوان
        // (FormBorderStyle=None من الأساس) — لذلك نحتاج:
        //  • زر إغلاق يدوي (btnClose) — لا يوجد زر × نظامي.
        //  • السحب يدوي — اقرأ StartDrag أسفل الملف.
        //
        // قاعدة الألوان: الخلفية SidebarBg (كحلي داكن) والبطاقة CardBg
        // (أبيض) — كل لون من AppTheme، لا أرقام يدوية.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── قواعد النافذة ──
            // KeyPreview=true: النافذة ترى المفاتيح قبل العناصر —
            // يسمح لـ AcceptButton/Escape بالعمل قبل تركيز أي عنصر.
            // CornerRadius=26: زوايا النافذة الدائرية (خاصية DialogForm).
            Text = "تسجيل الدخول — سيل";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 660);
            BackColor = AppTheme.SidebarBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            KeyPreview = true;
            CornerRadius = 26;
            Icon = Brand.AppIcon;

            // السحب: أي ضغطة يسار على الخلفية تنقل النافذة (نافذة بلا شريط عنوان)
            MouseDown += StartDrag;

            // ── الشعار (رسم متجهي — موجة + سهم + ميزان) ──
            // الوسط الأفقي: (عرض النافذة 560 الفعلي − 84) ÷ 2 — لاحظ أن
            // Bounds تستخدم إحداثيات منطقة العميل RTL.
            var logo = Brand.MakeLogo(84, AppTheme.SidebarBg);
            logo.Bounds = new Rectangle((560 - 84) / 2, 22, 84, 84);

            var lblBrand = new Label
            {
                Text = "سيل",
                Bounds = new Rectangle(0, 105, 560, 38),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.Transparent,   // شفاف — يظهر لون النافذة خلفه
                Font = new Font("Segoe UI", 17F, FontStyle.Bold)
            };

            var lblTagline = new Label
            {
                Text = "نظام سيل لإدارة المبيعات المتدفقة",
                Bounds = new Rectangle(0, 150, 560, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(156, 163, 175),
                BackColor = Color.Transparent
            };

            // العناصر الثلاثة أعلاه تعترض نقرات الماوس — نعيد توجيهها للسحب
            // حتى لا «تقتل» منطقة السحب (النافذة تتحرك من أي مكان فارغ).
            logo.MouseDown += StartDrag;
            lblBrand.MouseDown += StartDrag;
            lblTagline.MouseDown += StartDrag;

            // ── البطاقة البيضاء: حاوية الحقول ──
            // Bounds(40, 198, 480, 388): ارتفاع أكبر من السابق (342) —
            // استوعبنا رابط «تغيير كلمة المرور» أسفل زر الدخول.
            var card = new Panel
            {
                Bounds = new Rectangle(40, 198, 480, 388),
                BackColor = AppTheme.CardBg
            };
            AppTheme.ApplyRoundedRegion(card, 18);
            card.MouseDown += StartDrag; // سحب من البطاقة أيضًا (بلا حقول)

            // ── داخل البطاقة: العنوان ثم حقلان (مستخدم/كلمة مرور) ثم زر ──
            // كل الإحداثيات هنا نسبية للبطاقة (24 = هامش داخلي)، لا للنافذة.
            var lblCardTitle = new Label
            {
                Text = "تسجيل الدخول",
                Bounds = new Rectangle(24, 24, 432, 34),
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary,
                BackColor = Color.Transparent
            };

            var lblUserCaption = new Label
            {
                Text = "اختر المستخدم",
                Bounds = new Rectangle(24, 66, 432, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            // قائمة المستخدمين: DropDownList = اختيار فقط (لا كتابة حرة).
            // الاحتياط هنا أن Items تُعبأ لاحقًا في LoadUsersAsync —
            // عند البناء تكون فارغة، وهذا طبيعي.
            _cmbUsers = new ComboBox
            {
                Bounds = new Rectangle(24, 92, 432, 44),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = AppTheme.FontBody,
                BackColor = AppTheme.FieldBg
            };

            var lblPassCaption = new Label
            {
                Text = "كلمة المرور",
                Bounds = new Rectangle(24, 148, 432, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            // ── حقل كلمة المرور: لوحة مزدوجة (مربع + زر إظهار) ──
            // لماذا لوحة؟ TextBox لا يستطيع احتواء زر بداخله — نضع
            // passPanel (خلفية موحدة FieldBg) ونرسو بداخلها:
            //   _txtPassword بـ Dock=Fill يأخذ المساحة كلها،
            //   _btnTogglePassword بـ Dock=Left يلتصق يساره.
            // ⚠️ قاعدة الإرساء: Fill يُضاف أولًا (يُرسّى أخيرًا) — والزر
            //    يأخذ حافته قبلًا فيظهر بجانب المربع لا فوقه.
            // UseSystemPasswordChar=true: النقاط • بدل الحروف.
            var passPanel = new Panel
            {
                Bounds = new Rectangle(24, 174, 432, 44),
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
            // تبديل الإظهار: نقطة نقر واحدة تعكس الحالة وتغير نص الزر
            _btnTogglePassword.Click += (_, _) =>
            {
                bool hidden = _txtPassword.UseSystemPasswordChar;
                _txtPassword.UseSystemPasswordChar = !hidden;
                _btnTogglePassword.Text = hidden ? "إخفاء" : "إظهار";
            };

            // الترتيب مهم: المربع أولًا (Fill) ثم الزر (Left) —
            // آخر المضاف يُرسّى أولًا فيأخذ الزر حافته اليسرى أولًا.
            passPanel.Controls.Add(_txtPassword);
            passPanel.Controls.Add(_btnTogglePassword);

            // ── زر الدخول: Height=54 كبير قصدًا (زر أساسي) ──
            _btnLogin = AppTheme.MakeButton("دخول", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnLogin.Bounds = new Rectangle(24, 240, 432, 54);

            // ── أسفل البطاقة: رابط تغيير كلمة المرور (الشكل القوي —
            // المستخدم يدير سرّه بنفسه: اسم + القديمة + الجديدة).
            // ⚠️ لا تحويل لـ ثانوي هنا: الإحداثيات كلها Bounds يدوية —
            // أضفناه INSIDE البطاقة أسفل زر الدخول مع رفع ارتفاعها.
            var lnkChangePassword = new Button
            {
                Text = "تغيير كلمة المرور",
                Bounds = new Rectangle(24, 300, 432, 34),
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.Primary,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Underline),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            lnkChangePassword.FlatAppearance.BorderSize = 0;
            lnkChangePassword.Click += (_, _) => OpenChangePasswordDialog();

            _lblError = new Label
            {
                Text = "",
                Bounds = new Rectangle(24, 342, 432, 48),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Danger,
                Visible = false
            };

            // تجميع البطاقة — الترتيب هنا غير مهم (كلها Bounds يدوية)
            card.Controls.Add(lblCardTitle);
            card.Controls.Add(lblUserCaption);
            card.Controls.Add(_cmbUsers);
            card.Controls.Add(lblPassCaption);
            card.Controls.Add(passPanel);
            card.Controls.Add(_btnLogin);
            card.Controls.Add(lnkChangePassword);
            card.Controls.Add(_lblError);

            // ── الزرا الدائريان (MakeCircleButton): بلا إطار نظامي نحتاج أزرار زوايا ──
            // «؟» أعلى اليمين (16,16) و«×» أعلى اليسار (العرض−60).
            // GetRequiredService<AboutForm>: AboutForm مسجّل كخدمة (يفتح مرة واحدة).
            var btnAbout = AppTheme.MakeCircleButton("؟", 44,
                back: AppTheme.SidebarHover, hoverBack: AppTheme.PrimaryDark,
                fore: AppTheme.TextOnDark, parentBack: AppTheme.SidebarBg, fontSize: 16f);
            btnAbout.Bounds = new Rectangle(16, 16, 44, 44);
            btnAbout.Click += (_, _) =>
                Program.Services.GetRequiredService<AboutForm>().ShowDialog(this);

            var btnClose = AppTheme.MakeCircleButton("×", 44,
                back: AppTheme.SidebarHover, hoverBack: AppTheme.Danger,
                fore: AppTheme.TextOnDark, parentBack: AppTheme.SidebarBg, fontSize: 15f);
            // ClientSize.Width - 60: يحسب اليسار ديناميكيًا — لو غيّرت عرض
            // النافذة يبقى الزر ملتصقًا بالزاوية بلا تعديل يدوي.
            btnClose.Bounds = new Rectangle(ClientSize.Width - 60, 16, 44, 44);
            btnClose.Click += (_, _) => Close();

            // ── أسفل النافذة: ساعة البرنامج + حالة الاتصال ثم سطر الحقوق ──
            // 🕐 VULN-45: الساعة تُعرض من اللحظة الأولى — «توقيت البرنامج»
            // الموحد (UTC+3) الذي ستُخزن به كل الجلسات والفواتير.
            var lblClock = new Label
            {
                Bounds = new Rectangle(0, 560, 560, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(191, 219, 254),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            AppClockLabel.Attach(lblClock);
            Controls.Add(lblClock);

            _lblConnectionStatus = new Label
            {
                Text = "جارٍ التحقق من الاتصال بقاعدة البيانات...",
                Bounds = new Rectangle(0, 586, 560, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.TextMuted
            };

            var lblFooter = new Label
            {
                Text = "© سيل 2026 — جميع الحقوق محفوظة",
                Bounds = new Rectangle(0, 616, 560, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(107, 114, 128)
            };

            // ── تجميع النافذة ──
            // كلها Bounds — الترتيب لا يؤثر على المواضع، لكنه يؤثر على
            // ترتيب الـ Tab (المضاف أولًا يتقدم بالتنقل) — عناصر فوق أولًا.
            Controls.Add(logo);
            Controls.Add(lblBrand);
            Controls.Add(lblTagline);
            Controls.Add(card);
            Controls.Add(btnAbout);
            Controls.Add(btnClose);
            Controls.Add(_lblConnectionStatus);
            Controls.Add(lblFooter);

            // AcceptButton: Enter في أي مكان = زر الدخول.
            // CancelButton: Escape = زر الإغلاق. أضفهما لكل نموذج.
            AcceptButton = _btnLogin;
            CancelButton = btnClose;

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

                _cmbUsers.SelectedIndex = _cmbUsers.Items.Count > 0 ? 0 : -1;

                if (_cmbUsers.Items.Count == 0)
                {
                    SetConnectionStatus(false);
                    ShowError("لا يوجد مستخدمون نشطون في النظام.");
                }
                else
                {
                    SetConnectionStatus(true);
                }
            }
            catch
            {
                SetConnectionStatus(false);
                ShowError("تعذر تحميل قائمة المستخدمين. تحقق من الاتصال بقاعدة البيانات.");
            }
            finally
            {
                UseWaitCursor = false;
                _btnLogin.Enabled = _cmbUsers.Items.Count > 0;
                // منع تركيز القائمة تلائيًا عند الفتح (يمنع ظهورها باللون الأزرق المحدد)
                ActiveControl = _txtPassword;
            }
        }

        private static string FormatDisplayName(UserVM user)
        {
            // اسم الدخول + الدور: يبقى واضحًا مهما كانت لغة اسم الموظف
            return $"{user.UserName} ({user.RoleName})";
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
                    SessionState.SetUser(result.Data);
                    SetBusy(false);
                    LoginSucceeded?.Invoke(result.Data);
                    return;
                }

                ShowError(string.IsNullOrEmpty(result.Message) ? "فشل تسجيل الدخول." : result.Message);
                _txtPassword.SelectAll();
                _txtPassword.Focus();
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
        // تغيير كلمة المرور الذاتي (الشكل القوي) — صندوق صغير بثلاثة
        // حقول: اسم المستخدم + القديمة + الجديدة. خدمة ChangePasswordAsync
        // تتولى: التحقق بالـ BCrypt + سياسة الكلمة + تقييد المحاولات
        // (RegisterFailedLogin على القديمة الخاطئة) + سجل التدقيق.
        // بعد النجاح: مسح حقل كلمة الدخول ليُدخل بالجديدة.
        private void OpenChangePasswordDialog()
        {
            string presetUser = _cmbUsers.SelectedItem is ComboItem sel ? sel.User.UserName : "";

            using var dlg = new Form
            {
                Text = "تغيير كلمة المرور",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 300),
                MaximizeBox = false,
                MinimizeBox = false,
                RightToLeft = RightToLeft.Yes,
                Font = AppTheme.FontBody,
                BackColor = AppTheme.CardBg
            };

            var lblUser = new Label { Text = "اسم المستخدم *", Bounds = new Rectangle(30, 22, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txtUser = new TextBox { Bounds = new Rectangle(30, 46, 360, 36), Font = AppTheme.FontBody, Text = presetUser };

            var lblOld = new Label { Text = "كلمة المرور الحالية *", Bounds = new Rectangle(30, 92, 360, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txtOld = new TextBox { Bounds = new Rectangle(30, 116, 360, 36), Font = AppTheme.FontBody, UseSystemPasswordChar = true };

            var lblNew = new Label { Text = "كلمة المرور الجديدة * (8 أحرف على الأقل، حروف وأرقام)", Bounds = new Rectangle(30, 162, 360, 40), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txtNew = new TextBox { Bounds = new Rectangle(30, 200, 360, 36), Font = AppTheme.FontBody, UseSystemPasswordChar = true };

            var btnOk = AppTheme.MakeButton("تغيير", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 250, 220, 44);
            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(270, 250, 120, 44);
            btnCancel.DialogResult = DialogResult.Cancel;

            var lblMsg = new Label { Text = "", Bounds = new Rectangle(30, 196, 360, 0), Visible = false }; //占位 — لا يُستخدم (الرسائل تظهر برسائل الحوار)

            btnOk.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(txtUser.Text) ||
                    string.IsNullOrWhiteSpace(txtOld.Text) ||
                    string.IsNullOrWhiteSpace(txtNew.Text))
                {
                    UiHelpers.ShowError(dlg, "جميع الحقول مطلوبة.");
                    return;
                }

                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var usersService = scope.ServiceProvider.GetRequiredService<clsUsersService>();
                    result = await usersService.ChangePasswordAsync(txtUser.Text.Trim(), txtOld.Text, txtNew.Text);
                }

                UiHelpers.ShowResult(dlg, result, "تم تغيير كلمة المرور بنجاح.");
                if (result.IsSuccess)
                {
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
            };

            dlg.Controls.AddRange(new Control[] { lblUser, txtUser, lblOld, txtOld, lblNew, txtNew, btnOk, btnCancel, lblMsg });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtPassword.Text = ""; // يُدخل بالكلمة الجديدة
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

        private void SetConnectionStatus(bool connected)
        {
            _lblConnectionStatus.Text = connected
                ? "متصل بقاعدة البيانات"
                : "غير متصل بقاعدة البيانات";
            _lblConnectionStatus.ForeColor = connected ? AppTheme.Success : AppTheme.Danger;
        }

        // 📖 StartDrag: سحب النافذة من أي نقطة (البديل عن شريط عنوان المفقود).
        // النافذة FormBorderStyle=None فلا يوجد شريط عنوان يسحبها —
        // نستدعي رسالة ويندوز WM_NCLBUTTONDOWN مع HTCAPTION «كأن النقر
        // وقع على شريط العنوان» — ويندوز يتولى السحب كاملًا مجانًا.
        // نربط هذا على: النافذة نفسها + كل Label/Panel (انظر BuildUi).
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
