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

        private void BuildUi()
        {
            Text = "قبض / صرف نقد";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 400);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label { Text = "حركة نقدية", Bounds = new Rectangle(0, 18, 440, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _rbIn = new RadioButton { Text = "قبض (دخول نقد)", Bounds = new Rectangle(230, 62, 180, 30), Checked = true, ForeColor = AppTheme.TextPrimary };
            _rbOut = new RadioButton { Text = "صرف (خروج نقد)", Bounds = new Rectangle(30, 62, 180, 30), ForeColor = AppTheme.TextPrimary };

            var lblAmount = new Label { Text = "المبلغ *", Bounds = new Rectangle(30, 104, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtAmount = new TextBox { Bounds = new Rectangle(30, 128, 380, 42), Font = new Font("Segoe UI", 13F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };

            var lblReason = new Label { Text = "السبب *", Bounds = new Rectangle(30, 180, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtReason = new TextBox { Bounds = new Rectangle(30, 204, 380, 60), Multiline = true, Font = AppTheme.FontBody };

            var btnSave = AppTheme.MakeButton("حفظ الحركة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(30, 284, 240, 50);
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 284, 120, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, _rbIn, _rbOut, lblAmount, _txtAmount, lblReason, _txtReason, btnSave, btnCancel });
            CancelButton = btnCancel;
            ActiveControl = _txtAmount;
        }

        private async Task SaveAsync()
        {
            if (!decimal.TryParse(_txtAmount.Text?.Trim(), out decimal amount) || amount <= 0)
            {
                UiHelpers.ShowError(this, "أدخل مبلغًا صحيحًا أكبر من صفر.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtReason.Text))
            {
                UiHelpers.ShowError(this, "السبب مطلوب.");
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
