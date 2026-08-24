using Common.DTOs.RequestModels;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة الدفع: دفعة واحدة أو دفعات مقسومة (نقدي + شبكة...) مع حساب المتبقي.
    /// تُرجع قائمة الدفعات عبر الخاصية PaymentsList عند النجاح.
    /// </summary>
    public class PaymentForm : Form
    {
        private readonly decimal _netTotal;
        private readonly bool _hasCustomer;

        private readonly List<(ComboBox method, TextBox amount)> _rows = new();
        private FlowLayoutPanel _rowsPanel = null!;
        private Label _lblChange = null!;
        private Label _lblCreditWarning = null!;
        private Button _btnOk = null!;

        public List<DtoPaymentInfo> PaymentsList { get; private set; } = new();

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
            ClientSize = new Size(460, 560);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label
            {
                Text = "إتمام الدفع",
                Bounds = new Rectangle(0, 18, 460, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            var lblTotalCaption = new Label
            {
                Text = "المطلوب",
                Bounds = new Rectangle(30, 58, 400, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            var lblTotal = new Label
            {
                Text = UiHelpers.Money(_netTotal),
                Bounds = new Rectangle(30, 80, 400, 40),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                ForeColor = AppTheme.Primary,
                BackColor = AppTheme.FieldBg
            };

            var lblRows = new Label
            {
                Text = "الدفعات",
                Bounds = new Rectangle(30, 130, 400, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _rowsPanel = new FlowLayoutPanel
            {
                Bounds = new Rectangle(30, 154, 400, 170),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = AppTheme.CardBg
            };

            var btnAddRow = AppTheme.MakeButton("+ إضافة دفعة أخرى (تقسيم)", AppTheme.SidebarBg,
                hoverColor: AppTheme.SidebarHover, height: 40);
            btnAddRow.Bounds = new Rectangle(30, 330, 400, 40);
            btnAddRow.Click += (_, _) => AddPaymentRow(null, 0);

            _lblChange = new Label
            {
                Text = "",
                Bounds = new Rectangle(30, 380, 400, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading
            };

            _lblCreditWarning = new Label
            {
                Text = "المبلغ أقل من المطلوب: سيُسجَّل الباقي دينًا على العميل.",
                Bounds = new Rectangle(30, 410, 400, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Warning,
                Visible = false
            };

            _btnOk = AppTheme.MakeButton("تأكيد الدفع", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnOk.Bounds = new Rectangle(30, 444, 240, 52);
            _btnOk.Click += (_, _) => Confirm();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 444, 140, 52);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, lblTotalCaption, lblTotal, lblRows, _rowsPanel,
                btnAddRow, _lblChange, _lblCreditWarning, _btnOk, btnCancel
            });

            AcceptButton = _btnOk;
            CancelButton = btnCancel;

            // الصف الأول: كامل المبلغ نقدي
            AddPaymentRow("cash", _netTotal);
        }

        private void AddPaymentRow(string? method, decimal amount)
        {
            var row = new Panel { Width = 376, Height = 44, Margin = new Padding(2, 3, 2, 3) };

            var cmb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Width = 130,
                Left = 242,
                Top = 6
            };
            cmb.Items.AddRange(new object[] { "نقدي", "شبكة (بطاقة)", "تحويل بنكي" });
            cmb.SelectedIndex = method switch
            {
                "card" => 1,
                "bank_transfer" => 2,
                _ => 0
            };

            var txt = new TextBox
            {
                Width = 150,
                Left = 84,
                Top = 8,
                TextAlign = HorizontalAlignment.Center,
                Text = amount > 0 ? UiHelpers.Money(amount) : ""
            };

            var btnRemove = new Button
            {
                Text = "×",
                Width = 40,
                Height = 30,
                Left = 38,
                Top = 6,
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.Danger,
                Cursor = Cursors.Hand
            };
            btnRemove.FlatAppearance.BorderSize = 0;

            row.Controls.Add(cmb);
            row.Controls.Add(txt);
            row.Controls.Add(btnRemove);

            txt.TextChanged += (_, _) => UpdateChange();

            _rows.Add((cmb, txt));
            _rowsPanel.Controls.Add(row);
            btnRemove.Click += (_, _) =>
            {
                if (_rows.Count <= 1)
                    return;
                _rows.Remove((cmb, txt));
                _rowsPanel.Controls.Remove(row);
                UpdateChange();
            };

            txt.Focus();
            txt.SelectAll();
        }

        private decimal PaidTotal => _rows.Sum(r =>
            decimal.TryParse(r.amount.Text?.Trim(), out var v) && v > 0 ? v : 0m);

        private void UpdateChange()
        {
            decimal paid = PaidTotal;
            decimal change = paid - _netTotal;

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
            var payments = new List<DtoPaymentInfo>();
            foreach (var (method, amount) in _rows)
            {
                if (!decimal.TryParse(amount.Text?.Trim(), out var value) || value <= 0)
                    continue;

                string methodValue = method.SelectedIndex switch
                {
                    1 => "card",
                    2 => "bank_transfer",
                    _ => "cash"
                };

                payments.Add(new DtoPaymentInfo
                {
                    Amount = value,
                    PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                    Method = methodValue
                });
            }

            decimal paid = payments.Sum(p => p.Amount);

            if (paid <= 0)
            {
                UiHelpers.ShowError(this, "أدخل مبلغ دفعة واحد على الأقل.");
                return;
            }

            if (paid > _netTotal + 0.005m)
            {
                UiHelpers.ShowError(this, $"مجموع الدفعات ({UiHelpers.Money(paid)}) أكبر من المطلوب ({UiHelpers.Money(_netTotal)}).");
                return;
            }

            if (paid < _netTotal - 0.005m && !_hasCustomer)
            {
                UiHelpers.ShowError(this, "البيع بالدين يتطلب اختيار عميل أولًا.");
                return;
            }

            PaymentsList = payments;
            DialogResult = DialogResult.OK;
        }
    }
}
