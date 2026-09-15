using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>قبض/صرف نقد داخل جلسة البيع (معاملات خارج المبيعات).</summary>
    public class CashMovementForm : DialogForm
    {
        private readonly int _sessionId;

        private RadioButton _rbIn = null!;
        private RadioButton _rbOut = null!;
        private TextBox _txtAmount = null!;
        private TextBox _txtReason = null!;

        public bool Saved { get; private set; }

        public CashMovementForm(int sessionId)
        {
            _sessionId = sessionId;
            BuildUi();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — «الإحداثيات اليدوية» (راجع EmployeeEditForm):
        //
        // أبسط نموذج حواري في النظام — لكنه يعرض نمطًا جديدًا:
        // زرا RadioButton حصريان (قبض/صرف) — اختيار واحد إجباريًا:
        //   • WinForms يصنع «مجموعة» تلقائيًا من كل الأزرار بنفس الأب —
        //     فتحديد أحدهما يلغي الآخر دون أي كود.
        //   • Checked=true على «قبض» = الافتراضي عند الفتح.
        //   • القراءة عند الحفظ: _rbIn.Checked ? 'I' : 'O' — حرف واحد.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("CashMove.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 400);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label { Text = AppLang.T("CashMove.Title"), Bounds = new Rectangle(0, 18, 440, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            // زرا الخيار: قبض يمين (230..410)، صرف يسار (30..210) —
            // كلاهما في نفس الصف 62. لا Click handlers — القراءة عند الحفظ فقط.
            _rbIn = new RadioButton { Text = AppLang.T("CashMove.In"), Bounds = new Rectangle(230, 62, 180, 30), Checked = true, ForeColor = AppTheme.TextPrimary };
            _rbOut = new RadioButton { Text = AppLang.T("CashMove.Out"), Bounds = new Rectangle(30, 62, 180, 30), ForeColor = AppTheme.TextPrimary };

            // المبلغ: خط 13 عريض وسط — أكبر إدخال بالنافذة (قراءة الكاشير)
            var lblAmount = new Label { Text = AppLang.T("Common.Label.Amount") + " *", Bounds = new Rectangle(30, 104, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtAmount = new TextBox { Bounds = new Rectangle(30, 128, 380, 42), Font = new Font("Segoe UI", 13F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };

            // السبب: Multiline=true مع ارتفاع 60 — سطران بدل واحد (شرح الحركة)
            var lblReason = new Label { Text = AppLang.T("CashMove.Label.Reason"), Bounds = new Rectangle(30, 180, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtReason = new TextBox { Bounds = new Rectangle(30, 204, 380, 60), Multiline = true, Font = AppTheme.FontBody };

            var btnSave = AppTheme.MakeButton(AppLang.T("CashMove.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(30, 284, 240, 50);
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 284, 120, 50);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء

            Controls.AddRange(new Control[] { lblTitle, _rbIn, _rbOut, lblAmount, _txtAmount, lblReason, _txtReason, btnSave, btnCancel });
            CancelButton = btnCancel;
            // ActiveControl: التركيز الابتدائي — يفتح على المبلغ مباشرة
            // (النوع معروف غالبًا، أول ما يكتبه الكاشير هو الرقم).
            ActiveControl = _txtAmount;
        }

        private async Task SaveAsync()
        {
            if (!decimal.TryParse(_txtAmount.Text?.Trim(), out decimal amount) || amount <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Common.Msg.EnterValidAmountOverZero"));
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtReason.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("CashMove.Msg.ReasonRequired"));
                return;
            }

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var cashService = scope.ServiceProvider.GetRequiredService<clsCashService>();
                result = await cashService.AddMovementAsync(
                    _sessionId,
                    _rbIn.Checked ? 'I' : 'O',
                    amount,
                    _txtReason.Text.Trim(),
                    SessionState.CurrentUser?.UserId ?? 0);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
            {
                Saved = true;
                DialogResult = DialogResult.OK;
            }
        }
    }
}
