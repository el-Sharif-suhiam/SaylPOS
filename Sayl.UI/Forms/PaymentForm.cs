using Common.DTOs.RequestModels;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة الدفع: طريقة الدفع، المبلغ المستلم، والباقي.
    /// تُرجع DtoPaymentInfo عبر الخاصية PaymentInfo عند النجاح.
    /// </summary>
    public class PaymentForm : Form
    {
        private readonly decimal _netTotal;
        private readonly bool _hasCustomer;

        private ComboBox _cmbMethod = null!;
        private TextBox _txtReceived = null!;
        private Label _lblChange = null!;
        private Button _btnOk = null!;
        private Button _btnCancel = null!;
        private Label _lblCreditWarning = null!;

        public DtoPaymentInfo? PaymentInfo { get; private set; }

        public PaymentForm(decimal netTotal, bool hasCustomer)
        {
            _netTotal = netTotal;
            _hasCustomer = hasCustomer;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "الدفع";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 430);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label
            {
                Text = "إتمام الدفع",
                Bounds = new Rectangle(0, 20, 420, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            var lblTotalCaption = new Label
            {
                Text = "المطلوب",
                Bounds = new Rectangle(30, 66, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            var lblTotal = new Label
            {
                Text = UiHelpers.Money(_netTotal),
                Bounds = new Rectangle(30, 88, 360, 40),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17F, FontStyle.Bold),
                ForeColor = AppTheme.Primary,
                BackColor = AppTheme.FieldBg
            };

            var lblMethod = new Label
            {
                Text = "طريقة الدفع",
                Bounds = new Rectangle(30, 140, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _cmbMethod = new ComboBox
            {
                Bounds = new Rectangle(30, 164, 360, 40),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = AppTheme.FontBody
            };
            _cmbMethod.Items.AddRange(new object[] { "نقدي", "شبكة (بطاقة)", "تحويل بنكي" });
            _cmbMethod.SelectedIndex = 0;

            var lblReceived = new Label
            {
                Text = "المبلغ المستلم",
                Bounds = new Rectangle(30, 216, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtReceived = new TextBox
            {
                Bounds = new Rectangle(30, 240, 360, 40),
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                Text = UiHelpers.Money(_netTotal)
            };
            _txtReceived.TextChanged += (_, _) => UpdateChange();

            _lblChange = new Label
            {
                Text = "",
                Bounds = new Rectangle(30, 290, 360, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading
            };

            _lblCreditWarning = new Label
            {
                Text = "المبلغ أقل من المطلوب: سيُسجَّل الباقي دينًا على العميل.",
                Bounds = new Rectangle(30, 322, 360, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Warning,
                Visible = false
            };

            _btnOk = AppTheme.MakeButton("تأكيد الدفع", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnOk.Bounds = new Rectangle(30, 356, 220, 52);
            _btnOk.Click += (_, _) => Confirm();

            _btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            _btnCancel.Bounds = new Rectangle(260, 356, 130, 52);
            _btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, lblTotalCaption, lblTotal, lblMethod, _cmbMethod,
                lblReceived, _txtReceived, _lblChange, _lblCreditWarning, _btnOk, _btnCancel
            });

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
            ActiveControl = _txtReceived;
            _txtReceived.SelectAll();

            UpdateChange();
        }

        private decimal Received
        {
            get { return decimal.TryParse(_txtReceived.Text?.Trim(), out var v) && v >= 0 ? v : -1m; }
        }

        private void UpdateChange()
        {
            decimal received = Received;
            if (received < 0)
            {
                _lblChange.Text = "أدخل مبلغًا صحيحًا";
                _lblChange.ForeColor = AppTheme.Danger;
                _lblCreditWarning.Visible = false;
                return;
            }

            decimal change = received - _netTotal;
            if (change >= 0)
            {
                _lblChange.Text = $"الباقي: {UiHelpers.Money(change)}";
                _lblChange.ForeColor = AppTheme.Success;
                _lblCreditWarning.Visible = false;
            }
            else
            {
                _lblChange.Text = $"النقص: {UiHelpers.Money(-change)}";
                _lblChange.ForeColor = AppTheme.Danger;
                _lblCreditWarning.Visible = true;
            }
        }

        private void Confirm()
        {
            decimal received = Received;
            if (received < 0)
            {
                UiHelpers.ShowError(this, "أدخل مبلغًا صحيحًا.");
                return;
            }

            if (received < _netTotal && !_hasCustomer)
            {
                UiHelpers.ShowError(this, "البيع بالدين يتطلب اختيار عميل أولًا.");
                return;
            }

            string method = _cmbMethod.SelectedIndex switch
            {
                1 => "card",
                2 => "bank_transfer",
                _ => "cash"
            };

            PaymentInfo = new DtoPaymentInfo
            {
                Amount = received,
                PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                Method = method
            };

            DialogResult = DialogResult.OK;
        }
    }
}
