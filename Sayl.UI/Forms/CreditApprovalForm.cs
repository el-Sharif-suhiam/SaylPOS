using Common.DTOs;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة اعتماد المدير لتجاوز حد ائتمان العميل (Task 3):
    /// تعرض موقف العميل الائتماني كاملًا قبل التأكيد (الرصيد الحالي، الحد،
    /// الجديد المطلوب، المشروع، الفائض المتجاوز)، ثم سببًا إلزاميًا
    /// واعتماد مدير (اسم + كلمة مرور — إعادة مصادقة دائمًا حتى للمدير
    /// المسجَّل دخوله: الواجهة ليست حد ثقة والتحقق على الخادم).
    /// تُرجع عبر Reason/ManagerUsername/ManagerPassword عند DialogResult.OK.
    /// لا منطق أعمال هنا: فحوص الحقول تنسيق للراحة، والقرار كله في
    /// clsCreditApprovalService (مصادقة BCrypt + دور مدير + سجل معتمد).
    /// </summary>
    public class CreditApprovalForm : DialogForm
    {
        private readonly CreditApprovalRequiredInfo _info;

        private TextBox _txtReason = null!;
        private TextBox _txtUsername = null!;
        private TextBox _txtPassword = null!;
        private Button _btnTogglePassword = null!;
        private Button _btnOk = null!;
        private Label _lblError = null!;

        /// <summary>سبب الاعتماد كما كتبه المدير (مشذَّب).</summary>
        public string Reason { get; private set; } = "";

        public string ManagerUsername { get; private set; } = "";

        /// <summary>كلمة مرور المدير — تُقرأ مرة واحدة من النافذة وتُصَفَّر عند الإغلاق
        /// (لا تُخزَّن ولا تُسجَّل في أي مكان آخر).</summary>
        public string ManagerPassword { get; private set; } = "";

        public CreditApprovalForm(string customerName, CreditApprovalRequiredInfo info)
        {
            _info = info;
            BuildUi(customerName);
            FormClosed += (_, _) => { ManagerPassword = ""; };
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — نمط PaymentForm (إحداثيات يدوية):
        //   [العنوان] [بطاقة الموقف الائتماني: 5 أسطر تسمية/قيمة]
        //   [سبب الاعتماد (مربع نص)] [اعتماد المدير: اسم + كلمة مرور]
        //   [سطر الخطأ] [زر الاعتماد] [إلغاء]
        // الالتزام: كل لون/خط من AppTheme، كل نص من AppLang، RTL كامل.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi(string customerName)
        {
            Text = AppLang.T("CreditApproval.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 640);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label
            {
                Text = AppLang.T("CreditApproval.Title"),
                Bounds = new Rectangle(0, 18, 460, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Warning
            };

            // ── بطاقة الموقف الائتماني: القرار قبل التأكيد ──
            var lblCustomer = new Label
            {
                Text = $"{AppLang.T("CreditApproval.Label.Customer")}: {customerName}",
                Bounds = new Rectangle(30, 60, 400, 26),
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            AddPositionRow(96, AppLang.T("CreditApproval.Label.CurrentOutstanding"), UiHelpers.Money(_info.CurrentOutstanding), AppTheme.TextPrimary);
            AddPositionRow(128, AppLang.T("CreditApproval.Label.CreditLimit"), UiHelpers.Money(_info.CreditLimit), AppTheme.TextPrimary);
            AddPositionRow(160, AppLang.T("CreditApproval.Label.NewCreditAmount"), UiHelpers.Money(_info.NewCreditAmount), AppTheme.Primary);
            AddPositionRow(192, AppLang.T("CreditApproval.Label.ProjectedOutstanding"), UiHelpers.Money(_info.ProjectedOutstanding), AppTheme.TextPrimary);
            AddPositionRow(224, AppLang.T("CreditApproval.Label.ExcessAmount"), UiHelpers.Money(_info.ExcessAmount), AppTheme.Danger);

            var lblReason = new Label
            {
                Text = AppLang.T("CreditApproval.Label.Reason"),
                Bounds = new Rectangle(30, 262, 400, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtReason = new TextBox
            {
                Bounds = new Rectangle(30, 286, 400, 64),
                Multiline = true,
                MaxLength = 500
            };

            var lblManager = new Label
            {
                Text = AppLang.T("CreditApproval.Label.Manager"),
                Bounds = new Rectangle(30, 360, 400, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            var lblUsername = new Label
            {
                Text = AppLang.T("CreditApproval.Label.Username"),
                Bounds = new Rectangle(30, 386, 400, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtUsername = new TextBox
            {
                Bounds = new Rectangle(30, 408, 400, 34)
            };

            var lblPassword = new Label
            {
                Text = AppLang.T("CreditApproval.Label.Password"),
                Bounds = new Rectangle(30, 450, 400, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            // لوحة كلمة المرور: المربع + زر إظهار/إخفاء (نمط LoginForm)
            var passPanel = new Panel
            {
                Bounds = new Rectangle(30, 472, 400, 34)
            };
            _txtPassword = new TextBox
            {
                Dock = DockStyle.Fill,
                UseSystemPasswordChar = true
            };
            _btnTogglePassword = new Button
            {
                Text = AppLang.T("Login.Button.Show"),
                Dock = DockStyle.Left,
                Width = 70,
                FlatStyle = FlatStyle.Flat
            };
            _btnTogglePassword.FlatAppearance.BorderSize = 0;
            _btnTogglePassword.Click += (_, _) =>
            {
                bool hidden = _txtPassword.UseSystemPasswordChar;
                _txtPassword.UseSystemPasswordChar = !hidden;
                _btnTogglePassword.Text = hidden ? AppLang.T("Login.Button.Hide") : AppLang.T("Login.Button.Show");
            };
            passPanel.Controls.Add(_txtPassword);
            passPanel.Controls.Add(_btnTogglePassword);

            _lblError = new Label
            {
                Text = "",
                Bounds = new Rectangle(30, 514, 400, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Danger
            };

            _btnOk = AppTheme.MakeButton(AppLang.T("CreditApproval.Button.Confirm"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnOk.Bounds = new Rectangle(30, 546, 240, 52);
            _btnOk.Click += (_, _) => Confirm();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 546, 140, 52);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء الاعتماد والبيع

            Controls.AddRange(new Control[]
            {
                lblTitle, lblCustomer, lblReason, lblManager, lblUsername, lblPassword,
                _txtReason, _txtUsername, passPanel, _lblError, _btnOk, btnCancel
            });

            AcceptButton = _btnOk;      // Enter = اعتماد
            CancelButton = btnCancel;
        }

        /// <summary>سطر تسمية/قيمة في بطاقة الموقف الائتماني (نفس تنسيق PaymentForm).</summary>
        private void AddPositionRow(int y, string caption, string value, Color valueColor)
        {
            var lblCaption = new Label
            {
                Text = caption,
                Bounds = new Rectangle(30, y, 240, 26),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            var lblValue = new Label
            {
                Text = value,
                Bounds = new Rectangle(274, y, 156, 26),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                ForeColor = valueColor
            };
            Controls.Add(lblCaption);
            Controls.Add(lblValue);
        }

        private void Confirm()
        {
            Reason = _txtReason.Text?.Trim() ?? "";
            ManagerUsername = _txtUsername.Text?.Trim() ?? "";
            ManagerPassword = _txtPassword.Text ?? "";

            // فحص تنسيق للراحة فقط — القواعد النهائية في الخدمة (سبب 2..500 + مصادقة)
            if (Reason.Length < 2)
            {
                _lblError.Text = AppLang.T("CreditApproval.Msg.ReasonRequired");
                _txtReason.Focus();
                return;
            }

            if (ManagerUsername.Length == 0 || ManagerPassword.Length == 0)
            {
                _lblError.Text = AppLang.T("CreditApproval.Msg.CredentialsRequired");
                _txtUsername.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
        }
    }
}
