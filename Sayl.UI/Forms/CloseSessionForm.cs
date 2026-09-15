using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إغلاق جلسة البيع (تقرير Z): ملخص النقد + العدّ الفعلي + الفرق.</summary>
    public class CloseSessionForm : DialogForm
    {
        private readonly int _sessionId;

        private Label _lblSummary = null!;
        private TextBox _txtCounted = null!;
        private Label _lblDifference = null!;
        private Button _btnClose = null!;
        private SessionCashSummary? _summary;

        public bool Closed { get; private set; }

        public CloseSessionForm(int sessionId)
        {
            _sessionId = sessionId;
            BuildUi();
            Load += async (_, _) => await LoadSummaryAsync();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — «الإحداثيات اليدوية» (راجع EmployeeEditForm):
        //
        // تدفق محاسبي بسيط عموديًا: [بطاقة ملخص الجلسة] ← [العدّ الفعلي]
        // ← [سطر الفرق الحي] ← [إغلاق | رجوع].
        //
        // نقطتان بارزتان هنا:
        // 1) _lblSummary تُعبأ لاحقًا من LoadSummaryAsync (نص متعدد الأسطر
        //    \n داخل Label واحد بدل 6 Labels — أسهل تحديثًا).
        // 2) _lblDifference Label «حي» — يحدَّث مع كل كتابة في المربع.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("CloseSession.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 520);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label { Text = AppLang.T("CloseSession.Title"), Bounds = new Rectangle(0, 18, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            // بطاقة الملخص: خلفية FieldBg + Padding داخلي — تظهر كصندوق.
            // TextAlign=TopRight: الأسطر تبدأ يمينًا أعلى (عربي طبيعي).
            // ارتفاع 190 يحتوي 6 أسطر متوقعة من الملخص.
            _lblSummary = new Label
            {
                Text = AppLang.T("CloseSession.Loading"),
                Bounds = new Rectangle(30, 58, 400, 190),
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11.5F),
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(12, 10, 12, 10)
            };

            var lblCounted = new Label { Text = AppLang.T("CloseSession.Label.Counted"), Bounds = new Rectangle(30, 258, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            // خط 14 عريض: أهم إدخال — الكاشير يضغط الآلة الحاسبة ويقرأ من بعيد
            _txtCounted = new TextBox { Bounds = new Rectangle(30, 282, 400, 42), Font = new Font("Segoe UI", 14F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };

            // سطر الفرق: فارغ عند البناء — يمتلئ حيًا (انظر CountedChanged
            // في LoadSummaryAsync/الأحداث) — العنوان والنص إحداثيات ثابتة.
            _lblDifference = new Label { Text = "", Bounds = new Rectangle(30, 334, 400, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading };

            // Danger أحمر: إغلاق الجلسة فعل نهائي (لا جلسة بيع تعود)
            _btnClose = AppTheme.MakeButton(AppLang.T("CloseSession.Button.Close"), AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28));
            _btnClose.Bounds = new Rectangle(30, 380, 240, 52);
            _btnClose.Click += async (_, _) => await CloseSessionAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("CloseSession.Button.Back"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 380, 140, 52);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = رجوع

            Controls.AddRange(new Control[] { lblTitle, _lblSummary, lblCounted, _txtCounted, _lblDifference, _btnClose, btnCancel });
            CancelButton = btnCancel;
            ActiveControl = _txtCounted; // يفتح جاهزًا للكتابة — العدّ أولًا
        }

        private async Task LoadSummaryAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var cashService = scope.ServiceProvider.GetRequiredService<clsCashService>();
                var result = await cashService.GetSessionCashSummaryAsync(_sessionId);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _summary = result.Data;
                var s = _summary;
                _lblSummary.Text =
                    AppLang.T("CloseSession.Summary.Opening", UiHelpers.Money(s.OpeningAmount)) + "\n" +
                    AppLang.T("CloseSession.Summary.CashSales", UiHelpers.Money(s.CashSales)) + "\n" +
                    AppLang.T("CloseSession.Summary.CashReturns", UiHelpers.Money(s.CashReturns)) + "\n" +
                    AppLang.T("CloseSession.Summary.CashIn", UiHelpers.Money(s.CashIn)) + "\n" +
                    AppLang.T("CloseSession.Summary.CashOut", UiHelpers.Money(s.CashOut)) + "\n" +
                    AppLang.T("CloseSession.Summary.Expected", UiHelpers.Money(s.ExpectedCash));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("CloseSession.Msg.LoadFailed"));
            }
        }

        private async Task CloseSessionAsync()
        {
            if (!decimal.TryParse(_txtCounted.Text?.Trim(), out decimal counted) || counted < 0)
            {
                UiHelpers.ShowError(this, AppLang.T("CloseSession.Msg.EnterCounted"));
                return;
            }

            if (_summary == null)
            {
                UiHelpers.ShowError(this, AppLang.T("CloseSession.Msg.SummaryNotLoaded"));
                return;
            }

            decimal difference = counted - _summary.ExpectedCash;
            string diffText = difference == 0
                ? AppLang.T("CloseSession.Diff.None")
                : difference > 0
                    ? AppLang.T("CloseSession.Diff.Over", UiHelpers.Money(difference))
                    : AppLang.T("CloseSession.Diff.Short", UiHelpers.Money(-difference));

            bool confirmed = ConfirmationDialog.Show(this, AppLang.T("CloseSession.Form.Title"),
                AppLang.T("CloseSession.Confirm.Body", UiHelpers.Money(_summary.ExpectedCash), UiHelpers.Money(counted), diffText));
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var posService = scope.ServiceProvider.GetRequiredService<clsPosSessionService>();
                result = await posService.CloseSessionAsync(_sessionId, counted);
            }

            UiHelpers.ShowResult(this, result, AppLang.T("CloseSession.Msg.Closed"));
            if (result.IsSuccess)
            {
                Closed = true;
                DialogResult = DialogResult.OK;
            }
        }
    }
}
