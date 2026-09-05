using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة سداد دفعة: إما لفاتورة محددة (PayInvoiceAsync) أو على رصيد مورد بدون فاتورة (PaySupplierAsync).
    /// تعرض المتبقي وتتحقق من صحة المبلغ قبل الحفظ.
    /// </summary>
    public class SupplierPaymentForm : DialogForm
    {
        private readonly int? _invoiceId;
        private readonly string? _partyName;
        private readonly int? _partyId;
        private readonly decimal _remaining;

        private TextBox _txtAmount = null!;
        private ComboBox _cmbMethod = null!;
        private TextBox _txtNotes = null!;
        private Label _lblInfo = null!;
        private Button _btnSave = null!;

        private SupplierPaymentForm(int? invoiceId, int? partyId, string? partyName, decimal remaining)
        {
            _invoiceId = invoiceId;
            _partyId = partyId;
            _partyName = partyName;
            _remaining = remaining;

            Text = "سداد دفعة";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 430);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();
        }

        /// <summary>سداد لفاتورة شراء محددة.</summary>
        public static SupplierPaymentForm ForInvoice(int invoiceId, string partyName, decimal remaining)
            => new(invoiceId, null, partyName, remaining);

        /// <summary>سداد على رصيد مورد بدون فاتورة.</summary>
        public static SupplierPaymentForm ForSupplierBalance(int partyId, string partyName, decimal balance)
            => new(null, partyId, partyName, balance);

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — «الإحداثيات اليدوية + Anchor»
        // (الأساسيات في EmployeeEditForm). نقطتان جديدتان هنا:
        //
        // 1) نمط «مصنعان ثابتان»: ForInvoice / ForSupplierBalance — بدل
        //    منشئ غامض بمعاملات nullable، كل حالة لها اسم واضح. القاعدة:
        //    منشئ خاص + دوال إنشاء مسمّاة = كود يقرأ نفسه.
        //
        // 2) Anchor — أول ظهور له: النافذة قابلة للتحجيم، فالعناصر
        //    «ترتس» للحواف: Top|Left|Right يعني يتمدد أفقيًا مع النافذة،
        //    وBottom|Left|Right للزر يبقيه ملتصقًا بالقاع. (أي عنصر بلا
        //    Anchor = Top|Left افتراضيًا — يبقى جامدًا مكانه).
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            var lblTitle = new Label
            {
                Text = _invoiceId.HasValue ? $"سداد فاتورة شراء #{_invoiceId}" : "سداد رصيد مورد",
                Bounds = new Rectangle(0, 14, ClientSize.Width, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _lblInfo = new Label
            {
                Text = _invoiceId.HasValue
                    ? $"المورد: {_partyName}\nالمتبقي على الفاتورة: {UiHelpers.Money(_remaining)}"
                    : $"المورد: {_partyName}\nالرصيد المستحق: {UiHelpers.Money(_remaining)}",
                Bounds = new Rectangle(30, 56, 360, 52),
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 10.5F),
                ForeColor = AppTheme.TextMuted
            };

            var lblAmount = new Label
            {
                Text = "المبلغ",
                Bounds = new Rectangle(30, 118, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _txtAmount = new TextBox
            {
                Bounds = new Rectangle(110, 144, 280, 34),
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblMethod = new Label
            {
                Text = "طريقة الدفع",
                Bounds = new Rectangle(30, 190, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _cmbMethod = new ComboBox
            {
                Bounds = new Rectangle(110, 216, 280, 34),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cmbMethod.Items.AddRange(new object[] { "نقدي", "شبكة (بطاقة)", "تحويل بنكي" });
            _cmbMethod.SelectedIndex = 0;

            var lblNotes = new Label
            {
                Text = "ملاحظات",
                Bounds = new Rectangle(30, 262, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _txtNotes = new TextBox
            {
                Bounds = new Rectangle(110, 288, 280, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _btnSave = AppTheme.MakeButton("حفظ السداد", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            _btnSave.Bounds = new Rectangle(110, 336, 280, 48);
            _btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _btnSave.Click += async (_, _) => await SaveAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(30, 336, 70, 48);
            btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, _lblInfo,
                lblAmount, _txtAmount,
                lblMethod, _cmbMethod,
                lblNotes, _txtNotes,
                _btnSave, btnClose
            });

            CancelButton = btnClose;
        }

        private async Task SaveAsync()
        {
            if (!decimal.TryParse(_txtAmount.Text?.Trim(), out decimal amount) || amount <= 0)
            {
                UiHelpers.ShowError(this, "أدخل مبلغًا صحيحًا أكبر من صفر.");
                _txtAmount.Focus();
                return;
            }

            if (amount > _remaining)
            {
                UiHelpers.ShowError(this, $"المبلغ أكبر من المتبقي ({UiHelpers.Money(_remaining)}).");
                _txtAmount.Focus();
                _txtAmount.SelectAll();
                return;
            }

            string method = _cmbMethod.SelectedIndex switch
            {
                1 => "card",
                2 => "bank_transfer",
                _ => "cash"
            };

            var request = new DtoPaymentRequest
            {
                InvoiceId = _invoiceId,
                PartyType = 'S',
                PartyId = _partyId,
                Amount = amount,
                PaymentDate = AppClock.Today,
                Method = method,
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim()
            };

            _btnSave.Enabled = false;
            try
            {
                Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var paymentService = scope.ServiceProvider.GetRequiredService<clsPaymentService>();
                    result = _invoiceId.HasValue
                        ? await paymentService.PayInvoiceAsync(request)
                        : await paymentService.PaySupplierAsync(request);
                }

                UiHelpers.ShowResult(this, result);
                if (result.IsSuccess)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر الاتصال بقاعدة البيانات.");
            }
            finally
            {
                _btnSave.Enabled = true;
            }
        }
    }
}
