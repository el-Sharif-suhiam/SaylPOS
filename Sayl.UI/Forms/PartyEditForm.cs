using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل طرف (عميل 'C' أو مورد 'S'). التعديل جزئي حسب الحقول المرسلة.</summary>
    public class PartyEditForm : Form
    {
        private readonly char _partyType;
        private readonly DtoParty? _existing;

        private TextBox _txtName = null!;
        private TextBox _txtPhone1 = null!;
        private TextBox _txtPhone2 = null!;
        private TextBox _txtEmail = null!;
        private TextBox _txtAddress = null!;
        private TextBox _txtNotes = null!;

        public PartyEditForm(char partyType, DtoParty? existing)
        {
            _partyType = partyType;
            _existing = existing;
            BuildUi();
        }

        // 📖 «الإحداثيات اليدوية» — شرح موسع في EmployeeEditForm. نقاط الشاشة:
        //  • نفس نافذة لعميل ومورد: _partyType ('C'/'S') يغيّر العنوان فقط —
        //    البنية واحدة والحقول واحدة (كل الأطراف أسماء وجوالات وعناوين).
        //  • صف مزدوج واحد: الجوالان (يمين 30..225 / يسار 235..430).
        //  • MakeField هنا بنسخة ثالثة (x و y معاملان صريحان) — نفس فكرة
        //    ProductEditForm: أبسط من مؤشر y المتقدم في EmployeeEditForm.
        private void BuildUi()
        {
            string kind = _partyType == 'C' ? "عميل" : "مورد";
            Text = _existing == null ? $"إضافة {kind}" : $"تعديل {kind}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 540);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            _txtName = MakeField("الاسم *", 30, 58, 400, _existing?.Name ?? "");
            _txtPhone1 = MakeField("الجوال 1", 30, 116, 195, _existing?.Phone1 ?? "");
            _txtPhone2 = MakeField("الجوال 2", 235, 116, 195, _existing?.Phone2 ?? "");
            _txtEmail = MakeField("البريد الإلكتروني", 30, 174, 400, _existing?.Email ?? "");
            _txtAddress = MakeField("العنوان", 30, 232, 400, _existing?.AddressLine1 ?? "");
            _txtNotes = MakeField("ملاحظات", 30, 290, 400, _existing?.Notes ?? "");

            var btnOk = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 420, 240, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(290, 420, 140, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtName, _txtPhone1, _txtPhone2, _txtEmail, _txtAddress, _txtNotes, btnOk, btnCancel
            });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private TextBox MakeField(string caption, int x, int y, int width, string value)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, width, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, width, 36), Font = AppTheme.FontBody, Text = value };
            Controls.Add(lbl);
            Controls.Add(txt);
            return txt;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "الاسم مطلوب.");
                return;
            }

            var dto = new DtoParty
            {
                PartyId = _existing?.PartyId ?? 0,
                PartyType = _existing?.PartyType ?? _partyType,
                Name = _txtName.Text.Trim(),
                Phone1 = _txtPhone1.Text?.Trim(),
                Phone2 = _txtPhone2.Text?.Trim(),
                Email = _txtEmail.Text?.Trim(),
                AddressLine1 = _txtAddress.Text?.Trim(),
                Notes = _txtNotes.Text?.Trim(),
                IsActive = _existing?.IsActive ?? true,
                CreatedAt = _existing?.CreatedAt ?? DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                // VULN-26: نمرر الفاعل — الخدمة تفحص الصلاحية بنفسها
                int actorId = SessionState.CurrentUser?.UserId ?? 0;
                result = _existing == null
                    ? await partyService.AddNewPartyWithRoleAsync(dto, actorUserId: actorId)
                    : await partyService.UpdatePartyAsync(dto, actorUserId: actorId);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشل الحفظ." : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, "تم الحفظ بنجاح.");
            DialogResult = DialogResult.OK;
        }
    }
}
