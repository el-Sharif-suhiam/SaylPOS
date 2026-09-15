using Common.DTOs.RequestModels;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة الدفع: دفعة واحدة أو دفعات مقسومة (نقدي + شبكة...) مع حساب المتبقي.
    /// تُرجع قائمة الدفعات عبر الخاصية PaymentsList عند النجاح.
    /// </summary>
    public class PaymentForm : DialogForm
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

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — نمط «الصفوف الديناميكية» (فريد في النظام):
        //
        // معظم النوافذ إحصائية (Bounds ثابتة) — هذه النافذة مختلفة:
        // منطقة الدفعات _rowsPanel تستقبل صفوفًا تُنشأ وتُحذف وقت التشغيل.
        //
        // البنية (إحداثيات يدوية — راجع EmployeeEditForm للأساسيات):
        //   [العنوان] [المبلغ المطلوب — رقم ضخم وسط بطاقة]
        //   [لوحة الصفوف: FlowLayoutPanel عمودي + AutoScroll]  ← القلب
        //   [زر + إضافة دفعة] [سطر الباقي/النقص الحي] [تحذير الدَّين]
        //   [تأكيد] [إلغاء]
        //
        // لماذا FlowDirection=TopDown؟ كل صف جديد يُضاف أسفل سابقه،
        // ولو زادت عن ارتفاع اللوحة يظهر شريط تمرير تلقائي (AutoScroll)
        // — لا حساب مواضع يدوي إطلاقًا.
        //
        // الصف الواحد (AddPaymentRow) لوحة صغيرة: [× حذف] [مبلغ] [طريقة]
        // ومصيرها محفوظ في قائمة _rows (متوازية للواجهة) — الحذف يشيل
        // من الاثنتين معًا. النتيجة النهائية تُقرأ من _rows في Confirm.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("Payment.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 560);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label
            {
                Text = AppLang.T("Payment.Title"),
                Bounds = new Rectangle(0, 18, 460, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            // ── المبلغ المطلوب: أكبر رقم بالنافذة — قرار المستخدم ──
            // بطاقة FieldBg (16pt Primary) — المطلوب هو المرجع البصري.
            var lblTotalCaption = new Label
            {
                Text = AppLang.T("Payment.Label.Due"),
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
                Text = AppLang.T("Payment.Label.Payments"),
                Bounds = new Rectangle(30, 130, 400, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            // ── لوحة الصفوف الديناميكية — قلب النافذة ──
            // Bounds ثابتة (30, 154, 400, 170) لكن محتواها متغير:
            // TopDown: العمودية، WrapContents=false: لا التفاف أفقي،
            // AutoScroll=true: شريط تمرير عند تجاوز 170px.
            _rowsPanel = new FlowLayoutPanel
            {
                Bounds = new Rectangle(30, 154, 400, 170),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = AppTheme.CardBg
            };

            var btnAddRow = AppTheme.MakeButton(AppLang.T("Payment.Button.AddRow"), AppTheme.SidebarBg,
                hoverColor: AppTheme.SidebarHover, height: 40);
            btnAddRow.Bounds = new Rectangle(30, 330, 400, 40);
            // (null, 0): صف جديد فارخ بلا طريقة افتراضية — المستخدم يختار
            btnAddRow.Click += (_, _) => AddPaymentRow(null, 0);

            // ── سطر الحساب الحي: يظهر الباقي للعميل أو النقص بالأحمر ──
            // يتحدث مع كل TextChanged في أي صف (انظر AddPaymentRow).
            _lblChange = new Label
            {
                Text = "",
                Bounds = new Rectangle(30, 380, 400, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading
            };

            // تحذير الدَّين: يظهر فقط عند النقص — Visible=false افتراضيًا
            // (نمط «عنصر يظهر شرطيًا» — لا حذف/إنشاء، مجرد تبديل ظهور).
            _lblCreditWarning = new Label
            {
                Text = AppLang.T("Payment.CreditWarning"),
                Bounds = new Rectangle(30, 410, 400, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Warning,
                Visible = false
            };

            _btnOk = AppTheme.MakeButton(AppLang.T("Payment.Button.Confirm"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnOk.Bounds = new Rectangle(30, 444, 240, 52);
            _btnOk.Click += (_, _) => Confirm();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 444, 140, 52);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء

            Controls.AddRange(new Control[]
            {
                lblTitle, lblTotalCaption, lblTotal, lblRows, _rowsPanel,
                btnAddRow, _lblChange, _lblCreditWarning, _btnOk, btnCancel
            });

            AcceptButton = _btnOk;      // Enter في أي مكان = تأكيد
            CancelButton = btnCancel;

            // الصف الأول يُولد جاهزًا: كامل المبلغ نقدي — أسرع طريق للكاشير
            AddPaymentRow("cash", _netTotal);
        }

        // 📖 AddPaymentRow: مصنع صف دفعة ديناميكي — يبني لوحة 376×44 داخلها:
        //   [× حذف يسار] [مربع المبلغ وسط] [قائمة الطريقة يمين]
        // (Lefts: 38 / 84 / 242 داخل اللوحة — عربية RTL منطقية).
        // يربط TextChanged بالتحديث الحي، يسجل الصف في _rows و _rowsPanel،
        // وزر الحذف يشيل من الاثنتين (مع منع حذف الصف الأخير — دفعة واحدة
        // على الأقل دائمًا). أخيرًا يركز المربع ويحدد النص للكتابة فوقه.
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
            cmb.Items.AddRange(new object[]
            {
                AppLang.T("Common.Method.Cash"),
                AppLang.T("Common.Method.Card"),
                AppLang.T("Common.Method.BankTransfer")
            });
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

        // PaidTotal: مجموع كل المربعات المفهومة — TryParse يحمي من نص
        // غير رقمي (يحسبه صفرًا) — أسهل من منع الكتابة أصلًا.
        private decimal PaidTotal => _rows.Sum(r =>
            decimal.TryParse(r.amount.Text?.Trim(), out var v) && v > 0 ? v : 0m);

        private void UpdateChange()
        {
            decimal paid = PaidTotal;
            decimal change = paid - _netTotal;

            if (change >= 0)
            {
                _lblChange.Text = AppLang.T("Payment.Label.Change", UiHelpers.Money(change));
                _lblChange.ForeColor = AppTheme.Success;
                _lblCreditWarning.Visible = false;
            }
            else
            {
                _lblChange.Text = AppLang.T("Payment.Label.Short", UiHelpers.Money(-change));
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
                    PaymentDate = AppClock.Today,
                    Method = methodValue
                });
            }

            decimal paid = payments.Sum(p => p.Amount);

            if (paid <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Payment.Msg.OnePaymentRequired"));
                return;
            }

            if (paid > _netTotal + 0.005m)
            {
                UiHelpers.ShowError(this, AppLang.T("Payment.Msg.Overpaid", UiHelpers.Money(paid), UiHelpers.Money(_netTotal)));
                return;
            }

            if (paid < _netTotal - 0.005m && !_hasCustomer)
            {
                UiHelpers.ShowError(this, AppLang.T("Payment.Msg.CreditNeedsCustomer"));
                return;
            }

            PaymentsList = payments;
            DialogResult = DialogResult.OK;
        }
    }
}
