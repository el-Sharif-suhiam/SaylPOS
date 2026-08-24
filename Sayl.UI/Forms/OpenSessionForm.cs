using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>فتح جلسة بيع جديدة: إدخال النقد الافتتاحي في الدرج.</summary>
    public class OpenSessionForm : Form
    {
        private TextBox _txtOpening = null!;

        public decimal OpeningAmount { get; private set; }

        public OpenSessionForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "فتح جلسة بيع";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 320);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label { Text = "فتح جلسة بيع جديدة", Bounds = new Rectangle(0, 18, 440, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblInfo = new Label { Text = "أدخل النقد الافتتاحي الموجود في الدرج", Bounds = new Rectangle(30, 62, 380, 24), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _txtOpening = new TextBox { Bounds = new Rectangle(30, 92, 380, 46), Font = new Font("Segoe UI", 15F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center, Text = "0" };

            var lblHint = new Label { Text = "سيظهر هذا المبلغ في تقرير إغلاق الجلسة (Z)", Bounds = new Rectangle(30, 148, 380, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.TextMuted };

            var btnOpen = AppTheme.MakeButton("فتح الجلسة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOpen.Bounds = new Rectangle(30, 200, 240, 52);
            btnOpen.Click += (_, _) => Confirm();

            var btnCancel = AppTheme.MakeButton("فتح بدون نقد افتتاحي", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 262, 380, 40);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, lblInfo, _txtOpening, lblHint, btnOpen, btnCancel });
            AcceptButton = btnOpen;
            CancelButton = btnCancel;
            ActiveControl = _txtOpening;
            _txtOpening.SelectAll();
        }

        private void Confirm()
        {
            if (!decimal.TryParse(_txtOpening.Text?.Trim(), out decimal amount) || amount < 0)
            {
                UiHelpers.ShowError(this, "أدخل مبلغًا صحيحًا (صفر أو أكثر).");
                return;
            }

            OpeningAmount = amount;
            DialogResult = DialogResult.OK;
        }
    }
}
