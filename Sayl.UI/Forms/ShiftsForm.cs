using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إدارة الشفتات (تعريفات الجدول) + الإجازات الرسمية المدفوعة. أدمن فقط.</summary>
    public class ShiftsForm : DialogForm
    {
        private TabControl _tabs = null!;
        private DataGridView _gridShifts = null!;
        private DataGridView _gridHolidays = null!;
        private DataGridView _gridAssignments = null!;
        private Button _btnAddShift = null!;
        private Button _btnDeleteShift = null!;
        private Button _btnAddHoliday = null!;
        private Button _btnDeleteHoliday = null!;
        private Button _btnEndAssignment = null!;

        public ShiftsForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadAllAsync();
        }

        private void BuildUi()
        {
            Text = AppLang.T("Shifts.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 620);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ═══════════════════════════════════════════════════════════════
            // 📖 نمط «التبويبات»: TabControl يملأ كامل النافذة (Dock=Fill)
            // وكل TabPage لوحة مستقلة تُبنى كنافذة مصغرة:
            //   [شريط أزرار Dock=Top] + [جدول Dock=Fill]
            //
            // • لإضافة تبويب جديد: انسخ بنية أي تبويب (Tab + شريط + جدول)
            //   ثم _tabs.TabPages.Add(التبويب الجديد).
            // • ترتيب الأزرار داخل الشريط: FlowDirection=RightToLeft يعني
            //   أول Controls.Add يظهر أقصى اليمين — عدّل ترتيب الأسطر للترتيب.
            // • ارتفاع الشريط: Height=54 — زر 44 + هامش.
            // • FlowLayoutPanel خاصية AutoScroll: لو كثرت الأزرار لفّت تلقائيًا.
            // ═══════════════════════════════════════════════════════════════
            _tabs = new TabControl { Dock = DockStyle.Fill };

            // ── تبويب الشفتات ──
            var tabShifts = new TabPage(AppLang.T("Shifts.Tab.Shifts")) { BackColor = AppTheme.CardBg };

            var barShifts = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnAddShift = AppTheme.MakeButton(AppLang.T("Shifts.Button.AddShift"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnAddShift.Click += (_, _) => AddShift();
            _btnDeleteShift = AppTheme.MakeButton(AppLang.T("Shifts.Button.DeleteSelected"), AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnDeleteShift.Click += async (_, _) => await DeleteShiftAsync();
            barShifts.Controls.Add(_btnAddShift);
            barShifts.Controls.Add(_btnDeleteShift);

            _gridShifts = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridShifts);
            _gridShifts.Columns.Add("colName", AppLang.T("Shifts.Col.Name"));
            _gridShifts.Columns.Add("colStart", AppLang.T("Shifts.Col.From"));
            _gridShifts.Columns.Add("colEnd", AppLang.T("Shifts.Col.To"));
            // VULN-37: العنوان كان «يعبر منتصف الليل» موحيًا بأن الرواتب تعالج
            // العبور — القرار المعماري الموثق: الشفتات بيانات تنظيمية فقط
            // (الرواتب تحسب أيام العمل/الإجازات لا الساعات) — فسمّينا العمود
            // بما هو فعلًا: «شفت ليلي» معلومة عرض لا وعد حساب.
            _gridShifts.Columns.Add("colNight", AppLang.T("Shifts.Col.Night"));
            _gridShifts.Columns.Add("colNotes", AppLang.T("Shifts.Col.Notes"));
            _gridShifts.Columns["colName"].FillWeight = 24;
            _gridShifts.Columns["colStart"].FillWeight = 14;
            _gridShifts.Columns["colEnd"].FillWeight = 14;
            _gridShifts.Columns["colNight"].FillWeight = 18;
            _gridShifts.Columns["colNotes"].FillWeight = 30;

            tabShifts.Controls.Add(_gridShifts);
            tabShifts.Controls.Add(barShifts);

            // ── تبويب الإجازات ──
            var tabHolidays = new TabPage(AppLang.T("Shifts.Tab.Holidays")) { BackColor = AppTheme.CardBg };

            var barHolidays = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnAddHoliday = AppTheme.MakeButton(AppLang.T("Shifts.Button.AddHoliday"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnAddHoliday.Click += (_, _) => AddHoliday();
            _btnDeleteHoliday = AppTheme.MakeButton(AppLang.T("Shifts.Button.DeleteSelected"), AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnDeleteHoliday.Click += async (_, _) => await DeleteHolidayAsync();
            barHolidays.Controls.Add(_btnAddHoliday);
            barHolidays.Controls.Add(_btnDeleteHoliday);

            _gridHolidays = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridHolidays);
            _gridHolidays.Columns.Add("colName", AppLang.T("Shifts.Col.Name"));
            _gridHolidays.Columns.Add("colDate", AppLang.T("Shifts.Col.Date"));
            _gridHolidays.Columns.Add("colDays", AppLang.T("Shifts.Col.Duration"));
            _gridHolidays.Columns.Add("colAnnual", AppLang.T("Shifts.Col.Annual"));
            _gridHolidays.Columns["colName"].FillWeight = 40;
            _gridHolidays.Columns["colDate"].FillWeight = 22;
            _gridHolidays.Columns["colDays"].FillWeight = 18;
            _gridHolidays.Columns["colAnnual"].FillWeight = 20;

            tabHolidays.Controls.Add(_gridHolidays);
            tabHolidays.Controls.Add(barHolidays);

            // ── تبويب التعيينات الحالية ──
            var tabAssign = new TabPage(AppLang.T("Shifts.Tab.Assignments")) { BackColor = AppTheme.CardBg };

            var barAssign = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnEndAssignment = AppTheme.MakeButton(AppLang.T("Shifts.Button.EndAssignment"), AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnEndAssignment.Click += async (_, _) => await EndAssignmentAsync();
            barAssign.Controls.Add(_btnEndAssignment);

            _gridAssignments = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridAssignments);
            _gridAssignments.Columns.Add("colEmp", AppLang.T("Shifts.Col.Employee"));
            _gridAssignments.Columns.Add("colShift", AppLang.T("Shifts.Col.Shift"));
            _gridAssignments.Columns.Add("colDays", AppLang.T("Shifts.Col.Days"));
            _gridAssignments.Columns.Add("colFrom", AppLang.T("Shifts.Col.FromDate"));
            _gridAssignments.Columns["colEmp"].FillWeight = 28;
            _gridAssignments.Columns["colShift"].FillWeight = 24;
            _gridAssignments.Columns["colDays"].FillWeight = 28;
            _gridAssignments.Columns["colFrom"].FillWeight = 20;

            tabAssign.Controls.Add(_gridAssignments);
            tabAssign.Controls.Add(barAssign);

            _tabs.TabPages.Add(tabShifts);
            _tabs.TabPages.Add(tabHolidays);
            _tabs.TabPages.Add(tabAssign);

            // ══ بنية الجزأين (نمط PeriodicReportForm) ══
            // TableLayoutPanel جذر واحد: صف علوي مرن للـ Tabs + صف سفلي مطلق (68px)
            // للإغلاق — الصف المطلق محجوز دائمًا ولا يدخل منطقة التمرير أبدًا.
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // الأعلى: كل شيء
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));    // الأسفل: الإغلاق فقط

            _tabs.Dock = DockStyle.Fill;

            var bottomBar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(12, 10, 12, 10)
            };

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 140;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(btnClose);

            root.Controls.Add(_tabs, 0, 0);
            root.Controls.Add(bottomBar, 0, 1);

            // وحيد في النافذة — الإرساء يوزع الصفوف قبل أي حساب تمرير
            Controls.Add(root);
        }

        private static readonly string[] DayNames =
        {
            AppLang.T("Shifts.Day.Short0"), AppLang.T("Shifts.Day.Short1"), AppLang.T("Shifts.Day.Short2"),
            AppLang.T("Shifts.Day.Short3"), AppLang.T("Shifts.Day.Short4"), AppLang.T("Shifts.Day.Short5"),
            AppLang.T("Shifts.Day.Short6")
        };

        private static string DaysText(byte bits)
        {
            var names = new List<string>();
            for (int i = 0; i < 7; i++)
                if ((bits & (1 << i)) != 0)
                    names.Add(DayNames[i]);
            return names.Count == 7 ? AppLang.T("Shifts.Days.All") : string.Join(AppLang.IsEnglish ? ", " : "، ", names);
        }

        private async Task LoadAllAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();

                var shifts = await shiftService.GetShiftsListAsync();
                _gridShifts.Rows.Clear();
                foreach (var s in shifts)
                    _gridShifts.Rows.Add(s.Name, s.StartTime.ToString("HH:mm"), s.EndTime.ToString("HH:mm"),
                        s.CrossesMidnight ? AppLang.T("Common.Label.Yes") : "", s.Notes ?? "");

                var holidays = await shiftService.GetHolidaysAsync(AppClock.Today.Year);
                _gridHolidays.Rows.Clear();
                foreach (var h in holidays)
                    _gridHolidays.Rows.Add(h.Name, h.HolidayDate.ToString("yyyy-MM-dd"),
                        h.DurationDays, h.IsAnnual ? AppLang.T("Common.Label.Yes") : "");

                var assignments = await shiftService.GetEmployeeShiftsAsync();
                _gridAssignments.Rows.Clear();
                foreach (var a in assignments)
                    _gridAssignments.Rows.Add(a.EmployeeName, a.ShiftName, DaysText(a.DaysOfWeek),
                        a.EffectiveFrom.ToString("yyyy-MM-dd"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.LoadFailed"));
            }
        }

        // ───────────── إضافة شفت (حوار مبسط) ─────────────
        private void AddShift()
        {
            using var form = new ShiftEditForm();
            if (form.ShowDialog(this) == DialogResult.OK)
                _ = LoadAllAsync();
        }

        private async Task DeleteShiftAsync()
        {
            int idx = _gridShifts.CurrentRow?.Index ?? -1;
            if (idx < 0) { UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.PickShift")); return; }

            try
            {
                using var scope = Program.Services.CreateScope();
                var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                var shifts = await shiftService.GetShiftsListAsync();
                var result = await shiftService.DeleteShiftAsync(shifts[idx].ShiftId);
                UiHelpers.ShowResult(this, result);
                if (result.IsSuccess)
                    await LoadAllAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.DeleteFailed"));
            }
        }

        private void AddHoliday()
        {
            using var form = new HolidayEditForm();
            if (form.ShowDialog(this) == DialogResult.OK)
                _ = LoadAllAsync();
        }

        private async Task DeleteHolidayAsync()
        {
            int idx = _gridHolidays.CurrentRow?.Index ?? -1;
            if (idx < 0) { UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.PickHoliday")); return; }

            try
            {
                using var scope = Program.Services.CreateScope();
                var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                var holidays = await shiftService.GetHolidaysAsync(AppClock.Today.Year);
                var result = await shiftService.DeleteHolidayAsync(holidays[idx].HolidayId);
                UiHelpers.ShowResult(this, result);
                if (result.IsSuccess)
                    await LoadAllAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.DeleteFailed"));
            }
        }

        private async Task EndAssignmentAsync()
        {
            int idx = _gridAssignments.CurrentRow?.Index ?? -1;
            if (idx < 0) { UiHelpers.ShowError(this, AppLang.T("Shifts.Msg.PickAssignment")); return; }

            if (!ConfirmationDialog.Show(this, AppLang.T("Shifts.EndAssign.Title"),
                    AppLang.T("Shifts.EndAssign.Confirm")))
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                var assignments = await shiftService.GetEmployeeShiftsAsync();
                var result = await shiftService.EndAssignmentAsync(assignments[idx].EmployeeShiftId);
                UiHelpers.ShowResult(this, result);
                if (result.IsSuccess)
                    await LoadAllAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Shifts.EndAssign.Failed"));
            }
        }
    }

    /// <summary>إضافة شفت: اسم + وقتا البداية والنهاية (End &lt; Start = ليلي يعبر منتصف الليل).</summary>
    public class ShiftEditForm : DialogForm
    {
        private TextBox _txtName = null!;
        private DateTimePicker _dtpStart = null!;
        private DateTimePicker _dtpEnd = null!;
        private TextBox _txtNotes = null!;

        public ShiftEditForm()
        {
            Text = AppLang.T("ShiftEdit.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 340);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblName = new Label { Text = AppLang.T("ShiftEdit.Label.Name"), Bounds = new Rectangle(30, 20, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 46, 380, 34) };

            var lblStart = new Label { Text = AppLang.T("ShiftEdit.Label.Start"), Bounds = new Rectangle(235, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var lblEnd = new Label { Text = AppLang.T("ShiftEdit.Label.End"), Bounds = new Rectangle(30, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _dtpStart = new DateTimePicker { Bounds = new Rectangle(235, 116, 175, 34), Format = DateTimePickerFormat.Time, ShowUpDown = true, RightToLeftLayout = false };
            _dtpEnd = new DateTimePicker { Bounds = new Rectangle(30, 116, 175, 34), Format = DateTimePickerFormat.Time, ShowUpDown = true, RightToLeftLayout = false };
            _dtpStart.Value = AppClock.NowLocal.Date.AddHours(8);
            _dtpEnd.Value = AppClock.NowLocal.Date.AddHours(16);

            var lblHint = new Label
            {
                Text = AppLang.T("ShiftEdit.NightHint"),
                Bounds = new Rectangle(30, 158, 380, 24),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 8.5F)
            };

            var lblNotes = new Label { Text = AppLang.T("ShiftEdit.Label.Notes"), Bounds = new Rectangle(30, 188, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtNotes = new TextBox { Bounds = new Rectangle(30, 214, 380, 34) };

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(240, 266, 170, 48);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 266, 170, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblName, _txtName, lblStart, _dtpStart, lblEnd, _dtpEnd, lblHint, lblNotes, _txtNotes, btnOk, btnCancel });
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("ShiftEdit.Msg.NameRequired"));
                return;
            }

            var dto = new DtoShift
            {
                Name = _txtName.Text.Trim(),
                StartTime = TimeOnly.FromDateTime(_dtpStart.Value),
                EndTime = TimeOnly.FromDateTime(_dtpEnd.Value),
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim()
            };

            try
            {
                Common.Results.Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                    result = await shiftService.AddShiftAsync(dto);
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
                UiHelpers.ShowError(this, AppLang.T("ShiftEdit.Msg.SaveFailed"));
            }
        }
    }

    /// <summary>إضافة إجازة رسمية مدفوعة.</summary>
    public class HolidayEditForm : DialogForm
    {
        private TextBox _txtName = null!;
        private DateTimePicker _dtpDate = null!;
        private NumericUpDown _numDays = null!;
        private CheckBox _chkAnnual = null!;

        public HolidayEditForm()
        {
            Text = AppLang.T("HolidayEdit.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 320);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblName = new Label { Text = AppLang.T("HolidayEdit.Label.Name"), Bounds = new Rectangle(30, 20, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 46, 380, 34) };

            var lblDate = new Label { Text = AppLang.T("HolidayEdit.Label.Date"), Bounds = new Rectangle(235, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var lblDays = new Label { Text = AppLang.T("HolidayEdit.Label.Days"), Bounds = new Rectangle(30, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _dtpDate = new DateTimePicker { Bounds = new Rectangle(235, 116, 175, 34), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };
            _numDays = new NumericUpDown { Bounds = new Rectangle(30, 116, 175, 34), Minimum = 1, Maximum = 30, Value = 1 };

            _chkAnnual = new CheckBox
            {
                Text = AppLang.T("HolidayEdit.Label.Annual"),
                Bounds = new Rectangle(30, 166, 380, 30),
                ForeColor = AppTheme.TextPrimary
            };

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(240, 246, 170, 48);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 246, 170, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblName, _txtName, lblDate, _dtpDate, lblDays, _numDays, _chkAnnual, btnOk, btnCancel });
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("HolidayEdit.Msg.NameRequired"));
                return;
            }

            var dto = new DtoOfficialHoliday
            {
                Name = _txtName.Text.Trim(),
                HolidayDate = DateOnly.FromDateTime(_dtpDate.Value),
                DurationDays = (int)_numDays.Value,
                IsAnnual = _chkAnnual.Checked
            };

            try
            {
                Common.Results.Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                    result = await shiftService.AddHolidayAsync(dto);
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
                UiHelpers.ShowError(this, AppLang.T("HolidayEdit.Msg.SaveFailed"));
            }
        }
    }
}
