using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل دور (اسم + قيمة صلاحية رقمية).</summary>
    public class RoleEditForm : Form
    {
        private readonly DtoRole? _existing;

        private TextBox _txtName = null!;
        private TextBox _txtValue = null!;

        public RoleEditForm(DtoRole? existing)
        {
            _existing = existing;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = _existing == null ? "إضافة دور" : "تعديل دور";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 260);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 400, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblName = new Label { Text = "اسم الدور *", Bounds = new Rectangle(30, 58, 340, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 82, 340, 36), Font = AppTheme.FontBody, Text = _existing?.Name ?? "" };

            var lblValue = new Label { Text = "قيمة الصلاحية * (100 مدير، 75 محاسب، 50 كاشير)", Bounds = new Rectangle(30, 128, 340, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtValue = new TextBox { Bounds = new Rectangle(30, 152, 340, 36), Font = AppTheme.FontBody, TextAlign = HorizontalAlignment.Center, Text = _existing?.RoleValue.ToString() ?? "" };

            var btnOk = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(30, 200, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(270, 200, 100, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, lblName, _txtName, lblValue, _txtValue, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "اسم الدور مطلوب.");
                return;
            }

            if (!int.TryParse(_txtValue.Text?.Trim(), out int roleValue) || roleValue < 0)
            {
                UiHelpers.ShowError(this, "أدخل قيمة صلاحية صحيحة.");
                return;
            }

            var dto = new DtoRole
            {
                RoleId = _existing?.RoleId ?? 0,
                Name = _txtName.Text.Trim(),
                RoleValue = roleValue
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                if (_existing == null)
                {
                    var added = await roleService.AddNewRoleAsync(dto);
                    result = added.IsSuccess ? Result.Ok("تم حفظ الدور بنجاح.") : Result.Fail(added.Message);
                }
                else
                {
                    result = await roleService.UpdateRoleAsync(dto);
                }
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
