using Common.DTOs;
using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    // ═══════════════════════════════════════════════════════════════
    // 📖 ملف يجمع نموذجين حواريين صغيرين — «الإحداثيات اليدوية»
    // (الأساسيات في EmployeeEditForm). نقطتان تعليميتان في الملف:
    //
    // 1) LinkAccountForm: بناء مباشر داخل المنشئ (بلا BuildUi منفصلة) —
    //    مقبول للنوافذ الصغيرة جدًا؛ فور ما يتضخم شيء اسحبه لدالة.
    //
    // 2) EmployeeShiftAssignForm — نمط «شبكة مربعات اختيار»:
    //    7 أيام داخل أيام Panel بخلفية FieldBg، توزيعها حسابيًا:
    //      10 + (i % 3) * 135  ← العمود: 3 أعمدة، 135px خطوة
    //      10 + (i / 3) * 56   ← الصف: ينتقل كل 3 عناصر
    //    كل Check 130×48 (لمس مريح) وافتراضي i<5 = الأحد..الخميس.
    //    CheckedChanged يلوّن المحدد — تغذية بصرية فورية بلا زر.
    //    القراءة: SelectedDays يحوّل الحالة إلى byte (bit i = اليوم i).
    // ═══════════════════════════════════════════════════════════════

    /// <summary>اختيار حساب مستخدم لربطه بموظف (للحسابات غير المرتبطة).</summary>
    public class LinkAccountForm : DialogForm
    {
        private readonly ComboBox _cmbUsers = new();
        private readonly List<UserVM> _users;

        public int? SelectedUserId { get; private set; }

        public LinkAccountForm(string employeeName, List<UserVM> freeUsers)
        {
            _users = freeUsers;

            Text = AppLang.T("LinkAccount.Title", employeeName);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 220);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lbl = new Label
            {
                Text = AppLang.T("LinkAccount.Label.Pick"),
                Bounds = new Rectangle(30, 24, 360, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _cmbUsers.Bounds = new Rectangle(30, 52, 360, 36);
            _cmbUsers.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbUsers.FlatStyle = FlatStyle.Flat;
            foreach (var u in _users)
                _cmbUsers.Items.Add(u.UserName);
            if (_users.Count > 0)
                _cmbUsers.SelectedIndex = 0;

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Link"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(220, 116, 170, 48);
            btnOk.Click += (_, _) =>
            {
                if (_cmbUsers.SelectedIndex < 0 || _cmbUsers.SelectedIndex >= _users.Count)
                {
                    UiHelpers.ShowError(this, AppLang.T("LinkAccount.Msg.PickRequired"));
                    return;
                }
                SelectedUserId = _users[_cmbUsers.SelectedIndex].UserId;
                DialogResult = DialogResult.OK;
                Close();
            };

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 116, 170, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lbl, _cmbUsers, btnOk, btnCancel });
            CancelButton = btnCancel;
        }
    }

    /// <summary>تعيين شفت لموظف مع اختيار أيام الأسبوع.</summary>
    public class EmployeeShiftAssignForm : DialogForm
    {
        private readonly int _employeeId;
        private readonly ComboBox _cmbShifts = new();
        private readonly List<CheckBox> _dayChecks = new();

        public EmployeeShiftAssignForm(int employeeId, string employeeName)
        {
            _employeeId = employeeId;

            Text = AppLang.T("ShiftAssign.Title", employeeName);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 480);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblShift = new Label
            {
                Text = AppLang.T("ShiftAssign.Label.Shift"),
                Bounds = new Rectangle(30, 24, 420, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _cmbShifts.Bounds = new Rectangle(30, 50, 420, 36);
            _cmbShifts.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbShifts.FlatStyle = FlatStyle.Flat;

            var lblDays = new Label
            {
                Text = AppLang.T("ShiftAssign.Label.Days"),
                Bounds = new Rectangle(30, 98, 420, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            string[] days = { AppLang.T("ShiftAssign.Day.Sun"), AppLang.T("ShiftAssign.Day.Mon"), AppLang.T("ShiftAssign.Day.Tue"), AppLang.T("ShiftAssign.Day.Wed"), AppLang.T("ShiftAssign.Day.Thu"), AppLang.T("ShiftAssign.Day.Fri"), AppLang.T("ShiftAssign.Day.Sat") };

            // Checkboxes بحجم ممتاز لشاشة اللمس: 160×52 — نقرة إصبع مريحة بلا مبالغة
            var daysPanel = new Panel
            {
                Bounds = new Rectangle(30, 124, 420, 176),
                BackColor = AppTheme.FieldBg
            };
            for (int i = 0; i < days.Length; i++)
            {
                var chk = new CheckBox
                {
                    Text = days[i],
                    Bounds = new Rectangle(10 + (i % 3) * 135, 10 + (i / 3) * 56, 130, 48),
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    ForeColor = AppTheme.TextPrimary,
                    Checked = i < 5, // افتراضي: الأحد-الخميس
                    FlatStyle = FlatStyle.Standard
                };
                chk.CheckedChanged += (_, _) => chk.ForeColor = chk.Checked ? AppTheme.PrimaryDark : AppTheme.TextPrimary;
                _dayChecks.Add(chk);
                daysPanel.Controls.Add(chk);
            }
            Controls.Add(daysPanel);

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Assign"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(280, 320, 190, 48);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 320, 190, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblShift, _cmbShifts, lblDays, btnOk, btnCancel });
            CancelButton = btnCancel;

            Load += async (_, _) => await LoadShiftsAsync();
        }

        private async Task LoadShiftsAsync()
        {
            try
            {
                List<DtoShift> shifts;
                using (var scope = Program.Services.CreateScope())
                {
                    var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                    shifts = await shiftService.GetShiftsListAsync();
                }

                foreach (var s in shifts)
                    _cmbShifts.Items.Add(s.CrossesMidnight
                        ? $"{s.Name} ({s.StartTime:HH:mm} → {s.EndTime:HH:mm} {AppLang.T("ShiftAssign.CrossesMidnight")})"
                        : $"{s.Name} ({s.StartTime:HH:mm} → {s.EndTime:HH:mm})");
                if (shifts.Count > 0)
                    _cmbShifts.SelectedIndex = 0;
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("ShiftAssign.Msg.LoadFailed"));
            }
        }

        private byte SelectedDays()
        {
            byte bits = 0;
            for (int i = 0; i < _dayChecks.Count; i++)
                if (_dayChecks[i].Checked)
                    bits |= (byte)(1 << i); // bit0=Sunday .. bit6=Saturday
            return bits;
        }

        private async Task SaveAsync()
        {
            if (_cmbShifts.SelectedIndex < 0)
            {
                UiHelpers.ShowError(this, AppLang.T("ShiftAssign.Msg.ShiftRequired"));
                return;
            }

            byte days = SelectedDays();
            if (days < 1)
            {
                UiHelpers.ShowError(this, AppLang.T("ShiftAssign.Msg.OneDayRequired"));
                return;
            }

            try
            {
                Common.Results.Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                    var shifts = await shiftService.GetShiftsListAsync();
                    result = await shiftService.AssignShiftAsync(_employeeId,
                        shifts[_cmbShifts.SelectedIndex].ShiftId, days, AppClock.Today);
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
                UiHelpers.ShowError(this, AppLang.T("ShiftAssign.Msg.AssignFailed"));
            }
        }
    }
}
