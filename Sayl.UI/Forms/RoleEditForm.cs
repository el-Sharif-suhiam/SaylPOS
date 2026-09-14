using Common.DTOs;
using Common.Results;
using Common.Utils;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// إضافة/تعديل دور — نظام البتات (انظر AppPermissions):
    /// • checkboxes للشاشات المتاحة فقط (بلا شاشة الأدوار أبدًا — للمدير
    ///   وحده، فسقف أي دور مخصص 3071 بنيويًا).
    /// • عند التعديل تُفكّ القيمة الحالية إلى مربعات معلّمة.
    /// • الأدوار الأساسية (مدير/كاشير/محاسب) لا تصل هنا (محمية خدميًا).
    /// </summary>
    public class RoleEditForm : Form
    {
        private readonly DtoRole? _existing;

        private TextBox _txtName = null!;
        private CheckBox[] _screenChecks = null!;
        private Label _lblSummary = null!;

        public RoleEditForm(DtoRole? existing)
        {
            _existing = existing;
            BuildUi();
        }

        // 📖 شبكة checkboxes: صف لكل شاشة (بت واحد = خانة اختيار).
        // عند التعديل: كل خانة تُعلَّم إذا Has(existing.RoleValue, bit).
        // عند الحفظ: OR تراكمي يبني القناع (النقر المزدوج آمن بالبناء).
        // الملخص الحي أسفل الشبكة يقرأ بصريًا ما يختاره المدير.
        private void BuildUi()
        {
            Text = _existing == null ? AppLang.T("RoleEdit.AddTitle") : AppLang.T("RoleEdit.EditTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 560);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            var lblName = new Label { Text = AppLang.T("RoleEdit.Label.Name"), Bounds = new Rectangle(30, 58, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 82, 400, 36), Font = AppTheme.FontBody, Text = _existing?.Name ?? "" };

            var lblScreens = new Label { Text = AppLang.T("RoleEdit.Label.Screens"), Bounds = new Rectangle(30, 126, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            // شبكة الخانات — من كتالوج AppPermissions.EditableScreens (بلا الأدوار)
            var screens = AppPermissions.EditableScreens;
            _screenChecks = new CheckBox[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                int y = 156 + i * 34;
                _screenChecks[i] = new CheckBox
                {
                    Text = AppLang.PermScreenName(screens[i].Bit),
                    Tag = screens[i].Bit,
                    Bounds = new Rectangle(150, y, 280, 30),
                    TextAlign = ContentAlignment.MiddleRight,
                    Checked = _existing != null && AppPermissions.Has(_existing.RoleValue, screens[i].Bit),
                    FlatStyle = FlatStyle.Standard,
                    ForeColor = AppTheme.TextPrimary,
                    Font = new Font("Segoe UI", 10F)
                };
                _screenChecks[i].CheckedChanged += (_, _) => UpdateSummary();
                Controls.Add(_screenChecks[i]);
            }

            // الملخص الحي: يعرض عدد الشاشات المختارة + قيمة القناع الرقمية
            _lblSummary = new Label
            {
                Text = "",
                Bounds = new Rectangle(30, 156 + screens.Length * 34 + 6, 400, 24),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.PrimaryDark,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            int btnY = 156 + screens.Length * 34 + 40;
            btnOk.Bounds = new Rectangle(30, btnY, 260, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(310, btnY, 120, 50);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, lblName, _txtName, lblScreens, _lblSummary, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // ملخص أولي
            UpdateSummary();
        }

        /// <summary>قيمة القناع الحالية من الخانات (OR تراكمي).</summary>
        private int CurrentMask()
        {
            int mask = 0;
            foreach (var chk in _screenChecks)
                if (chk.Checked && chk.Tag is int bit)
                    mask |= bit;
            return mask;
        }

        private void UpdateSummary()
        {
            int mask = CurrentMask();
            int count = _screenChecks.Count(c => c.Checked);
            _lblSummary.Text = count == 0
                ? AppLang.T("RoleEdit.Summary.Empty")
                : AppLang.T("RoleEdit.Summary.Selected", count, _screenChecks.Length, mask);
        }

        private async Task SaveAsync()
        {
            string name = _txtName.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                UiHelpers.ShowError(this, AppLang.T("RoleEdit.Msg.NameRequired"));
                return;
            }

            // منع الدور الفارغ: صلاحية واحدة على الأقل (الكاشير 0 دور هوية لا يُبنى هنا)
            int mask = CurrentMask();
            if (mask == 0)
            {
                UiHelpers.ShowError(this, AppLang.T("RoleEdit.Msg.OneScreenRequired"));
                return;
            }

            var dto = new DtoRole
            {
                RoleId = _existing?.RoleId ?? 0,
                Name = name,
                RoleValue = mask
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var roleService = scope.ServiceProvider.GetRequiredService<clsRoleService>();
                if (_existing == null)
                {
                    var added = await roleService.AddNewRoleAsync(dto, SessionState.CurrentUser?.UserId ?? 0);
                    result = added.IsSuccess ? Result.Ok(AppLang.T("RoleEdit.Msg.Saved")) : Result.Fail(added.Message);
                }
                else
                {
                    result = await roleService.UpdateRoleAsync(dto, SessionState.CurrentUser?.UserId ?? 0);
                }
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("Common.Msg.SaveFailed") : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, AppLang.T("Common.Msg.Saved"));
            DialogResult = DialogResult.OK;
        }
    }
}
