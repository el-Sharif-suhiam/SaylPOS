using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إغلاق جلسة البيع (تقرير Z): ملخص النقد + العدّ الفعلي + الفرق.</summary>
    public class CloseSessionForm : Form
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

        private void BuildUi()
        {
            Text = "إغلاق الجلسة";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 520);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label { Text = "إغلاق جلسة البيع (تقرير Z)", Bounds = new Rectangle(0, 18, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _lblSummary = new Label
            {
                Text = "جارٍ التحميل...",
                Bounds = new Rectangle(30, 58, 400, 190),
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 11.5F),
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(12, 10, 12, 10)
            };

            var lblCounted = new Label { Text = "النقد المعدود فعليًا", Bounds = new Rectangle(30, 258, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtCounted = new TextBox { Bounds = new Rectangle(30, 282, 400, 42), Font = new Font("Segoe UI", 14F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };

            _lblDifference = new Label { Text = "", Bounds = new Rectangle(30, 334, 400, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading };

            _btnClose = AppTheme.MakeButton("إغلاق الجلسة", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28));
            _btnClose.Bounds = new Rectangle(30, 380, 240, 52);
            _btnClose.Click += async (_, _) => await CloseSessionAsync();

            var btnCancel = AppTheme.MakeButton("رجوع", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 380, 140, 52);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, _lblSummary, lblCounted, _txtCounted, _lblDifference, _btnClose, btnCancel });
            CancelButton = btnCancel;
            ActiveControl = _txtCounted;
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
                    $"الافتتاحي: {UiHelpers.Money(s.OpeningAmount)}\n" +
                    $"المبيعات نقدي: {UiHelpers.Money(s.CashSales)}\n" +
                    $"المرتجعات نقدي: {UiHelpers.Money(s.CashReturns)}\n" +
                    $"قبض نقد (داخل): {UiHelpers.Money(s.CashIn)}\n" +
                    $"صرف نقد (خارج): {UiHelpers.Money(s.CashOut)}\n" +
                    $"النقد المتوقع: {UiHelpers.Money(s.ExpectedCash)}";
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل ملخص الجلسة.");
            }
        }

        private async Task CloseSessionAsync()
        {
            if (!decimal.TryParse(_txtCounted.Text?.Trim(), out decimal counted) || counted < 0)
            {
                UiHelpers.ShowError(this, "أدخل المبلغ المعدود.");
                return;
            }

            if (_summary == null)
            {
                UiHelpers.ShowError(this, "لم يتم تحميل ملخص الجلسة.");
                return;
            }

            decimal difference = counted - _summary.ExpectedCash;
            string diffText = difference == 0
                ? "لا يوجد فرق."
                : difference > 0 ? $"زيادة: {UiHelpers.Money(difference)}" : $"عجز: {UiHelpers.Money(-difference)}";

            bool confirmed = ConfirmationDialog.Show(this, "إغلاق الجلسة",
                $"النقد المتوقع: {UiHelpers.Money(_summary.ExpectedCash)} — المعدود: {UiHelpers.Money(counted)}\n{diffText}\nهل تريد إغلاق الجلسة؟");
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var posService = scope.ServiceProvider.GetRequiredService<clsPosSessionService>();
                result = await posService.CloseSessionAsync(_sessionId, counted);
            }

            UiHelpers.ShowResult(this, result, "تم إغلاق الجلسة بنجاح.");
            if (result.IsSuccess)
            {
                Closed = true;
                DialogResult = DialogResult.OK;
            }
        }
    }
}
