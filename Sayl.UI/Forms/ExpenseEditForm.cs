using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل مصروف.</summary>
    public class ExpenseEditForm : Form
    {
        private readonly DtoExpense? _existing;

        private TextBox _txtDescription = null!;
        private TextBox _txtAmount = null!;
        private DateTimePicker _dtpDate = null!;
        private ComboBox _cmbCategory = null!;
        private TextBox _txtNotes = null!;

        public ExpenseEditForm(DtoExpense? existing)
        {
            _existing = existing;
            BuildUi();
        }

        // 📖 «الإحداثيات اليدوية» — شرح موسع في EmployeeEditForm. نقاط الشاشة:
        //  • صف مزدوج: المبلغ (يمين 30..215) والتاريخ منتقي (يسار 225..410).
        //  • الفئة ComboBox بخيارات عربية ثابتة — تحفظ نصًا مباشرة (لا ترجمة).
        //  • Value عند التعديل: تحويل DateOnly → DateTime عبر ToDateTime،
        //    والافتراضي DateTime.Today — نفس فكرة حقن القيم في كل النوافذ.
        private void BuildUi()
        {
            Text = _existing == null ? AppLang.T("Expense.AddTitle") : AppLang.T("Expense.EditTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 420);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 440, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblDesc = new Label { Text = AppLang.T("Expense.Label.Description"), Bounds = new Rectangle(30, 58, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtDescription = new TextBox { Bounds = new Rectangle(30, 82, 380, 36), Font = AppTheme.FontBody, Text = _existing?.Description ?? "" };

            var lblAmount = new Label { Text = AppLang.T("Common.Label.Amount") + " *", Bounds = new Rectangle(30, 128, 185, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtAmount = new TextBox { Bounds = new Rectangle(30, 152, 185, 36), Font = AppTheme.FontBody, TextAlign = HorizontalAlignment.Center, Text = _existing?.Amount.ToString("0.##") ?? "" };

            var lblDate = new Label { Text = AppLang.T("Common.Label.Date"), Bounds = new Rectangle(225, 128, 185, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpDate = new DateTimePicker { Bounds = new Rectangle(225, 152, 185, 36), Format = DateTimePickerFormat.Short, Value = _existing?.ExpenseDate.ToDateTime(TimeOnly.MinValue) ?? AppClock.NowLocal.Date };

            var lblCategory = new Label { Text = AppLang.T("Expense.Label.Category"), Bounds = new Rectangle(30, 198, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbCategory = new ComboBox { Bounds = new Rectangle(30, 222, 380, 36), FlatStyle = FlatStyle.Flat };
            _cmbCategory.Items.AddRange(new object[]
            {
                AppLang.T("Expense.Category.Rent"),
                AppLang.T("Expense.Category.Salaries"),
                AppLang.T("Expense.Category.Utilities"),
                AppLang.T("Expense.Category.Transport"),
                AppLang.T("Expense.Category.Maintenance"),
                AppLang.T("Expense.Category.Other")
            });
            _cmbCategory.Text = _existing?.Category ?? "";

            var lblNotes = new Label { Text = AppLang.T("Common.Label.Notes"), Bounds = new Rectangle(30, 268, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtNotes = new TextBox { Bounds = new Rectangle(30, 292, 380, 36), Font = AppTheme.FontBody, Text = _existing?.Notes ?? "" };

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 344, 230, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(280, 344, 130, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, lblDesc, _txtDescription, lblAmount, _txtAmount, lblDate, _dtpDate,
                lblCategory, _cmbCategory, lblNotes, _txtNotes, btnOk, btnCancel
            });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtDescription.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("Expense.Msg.DescriptionRequired"));
                return;
            }

            if (!decimal.TryParse(_txtAmount.Text?.Trim(), out decimal amount) || amount <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Common.Msg.EnterValidAmountOverZero"));
                return;
            }

            var dto = new DtoExpense
            {
                ExpenseId = _existing?.ExpenseId ?? 0,
                Description = _txtDescription.Text.Trim(),
                Amount = amount,
                ExpenseDate = DateOnly.FromDateTime(_dtpDate.Value.Date),
                Category = string.IsNullOrWhiteSpace(_cmbCategory.Text) ? null : _cmbCategory.Text.Trim(),
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
                UserId = SessionState.CurrentUser?.UserId
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var expenseService = scope.ServiceProvider.GetRequiredService<clsExpenseService>();
                if (_existing == null)
                {
                    var added = await expenseService.AddNewExpenseAsync(dto);
                    result = added.IsSuccess ? Result.Ok(AppLang.T("Expense.Msg.Added")) : Result.Fail(added.Message);
                }
                else
                {
                    result = await expenseService.UpdateExpenseAsync(dto);
                }
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("Expense.Msg.SaveFailed") : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, AppLang.T("Expense.Msg.Saved"));
            DialogResult = DialogResult.OK;
        }
    }
}
