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
            Text = "الشفتات والإجازات الرسمية";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 620);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            _tabs = new TabControl { Dock = DockStyle.Fill };

            // ── تبويب الشفتات ──
            var tabShifts = new TabPage("الشفتات") { BackColor = AppTheme.CardBg };

            var barShifts = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnAddShift = AppTheme.MakeButton("إضافة شفت", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnAddShift.Click += (_, _) => AddShift();
            _btnDeleteShift = AppTheme.MakeButton("حذف المحدد", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnDeleteShift.Click += async (_, _) => await DeleteShiftAsync();
            barShifts.Controls.Add(_btnAddShift);
            barShifts.Controls.Add(_btnDeleteShift);

            _gridShifts = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridShifts);
            _gridShifts.Columns.Add("colName", "الاسم");
            _gridShifts.Columns.Add("colStart", "من");
            _gridShifts.Columns.Add("colEnd", "إلى");
            _gridShifts.Columns.Add("colNight", "يعبر منتصف الليل");
            _gridShifts.Columns.Add("colNotes", "ملاحظات");
            _gridShifts.Columns["colName"].FillWeight = 24;
            _gridShifts.Columns["colStart"].FillWeight = 14;
            _gridShifts.Columns["colEnd"].FillWeight = 14;
            _gridShifts.Columns["colNight"].FillWeight = 18;
            _gridShifts.Columns["colNotes"].FillWeight = 30;

            tabShifts.Controls.Add(_gridShifts);
            tabShifts.Controls.Add(barShifts);

            // ── تبويب الإجازات ──
            var tabHolidays = new TabPage("الإجازات الرسمية") { BackColor = AppTheme.CardBg };

            var barHolidays = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnAddHoliday = AppTheme.MakeButton("إضافة إجازة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnAddHoliday.Click += (_, _) => AddHoliday();
            _btnDeleteHoliday = AppTheme.MakeButton("حذف المحدد", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnDeleteHoliday.Click += async (_, _) => await DeleteHolidayAsync();
            barHolidays.Controls.Add(_btnAddHoliday);
            barHolidays.Controls.Add(_btnDeleteHoliday);

            _gridHolidays = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridHolidays);
            _gridHolidays.Columns.Add("colName", "الاسم");
            _gridHolidays.Columns.Add("colDate", "التاريخ");
            _gridHolidays.Columns.Add("colDays", "المدة (أيام)");
            _gridHolidays.Columns.Add("colAnnual", "سنوية متكررة");
            _gridHolidays.Columns["colName"].FillWeight = 40;
            _gridHolidays.Columns["colDate"].FillWeight = 22;
            _gridHolidays.Columns["colDays"].FillWeight = 18;
            _gridHolidays.Columns["colAnnual"].FillWeight = 20;

            tabHolidays.Controls.Add(_gridHolidays);
            tabHolidays.Controls.Add(barHolidays);

            // ── تبويب التعيينات الحالية ──
            var tabAssign = new TabPage("تعيينات الموظفين") { BackColor = AppTheme.CardBg };

            var barAssign = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 54,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(8, 4, 8, 4)
            };
            _btnEndAssignment = AppTheme.MakeButton("إنهاء تعيين المحدد", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 44);
            _btnEndAssignment.Click += async (_, _) => await EndAssignmentAsync();
            barAssign.Controls.Add(_btnEndAssignment);

            _gridAssignments = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_gridAssignments);
            _gridAssignments.Columns.Add("colEmp", "الموظف");
            _gridAssignments.Columns.Add("colShift", "الشفت");
            _gridAssignments.Columns.Add("colDays", "أيام العمل");
            _gridAssignments.Columns.Add("colFrom", "من تاريخ");
            _gridAssignments.Columns["colEmp"].FillWeight = 28;
            _gridAssignments.Columns["colShift"].FillWeight = 24;
            _gridAssignments.Columns["colDays"].FillWeight = 28;
            _gridAssignments.Columns["colFrom"].FillWeight = 20;

            tabAssign.Controls.Add(_gridAssignments);
            tabAssign.Controls.Add(barAssign);

            _tabs.TabPages.Add(tabShifts);
            _tabs.TabPages.Add(tabHolidays);
            _tabs.TabPages.Add(tabAssign);

            // شريط سفلي ثابت للإغلاق — الـ Tabs تملأ ما فوقه فقط (لا تداخل مع أي زر)
            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 68,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(12, 10, 12, 10)
            };

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 140;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(btnClose);

            Controls.Add(_tabs);
            Controls.Add(bottomBar);
        }

        private static readonly string[] DayNames = { "أحد", "اثنين", "ثلاثاء", "أربعاء", "خميس", "جمعة", "سبت" };

        private static string DaysText(byte bits)
        {
            var names = new List<string>();
            for (int i = 0; i < 7; i++)
                if ((bits & (1 << i)) != 0)
                    names.Add(DayNames[i]);
            return names.Count == 7 ? "كل الأيام" : string.Join("، ", names);
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
                        s.CrossesMidnight ? "نعم" : "", s.Notes ?? "");

                var holidays = await shiftService.GetHolidaysAsync(DateTime.Today.Year);
                _gridHolidays.Rows.Clear();
                foreach (var h in holidays)
                    _gridHolidays.Rows.Add(h.Name, h.HolidayDate.ToString("yyyy-MM-dd"),
                        h.DurationDays, h.IsAnnual ? "نعم" : "");

                var assignments = await shiftService.GetEmployeeShiftsAsync();
                _gridAssignments.Rows.Clear();
                foreach (var a in assignments)
                    _gridAssignments.Rows.Add(a.EmployeeName, a.ShiftName, DaysText(a.DaysOfWeek),
                        a.EffectiveFrom.ToString("yyyy-MM-dd"));
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل البيانات.");
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
            if (idx < 0) { UiHelpers.ShowError(this, "اختر شفتًا."); return; }

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
                UiHelpers.ShowError(this, "تعذر الحذف.");
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
            if (idx < 0) { UiHelpers.ShowError(this, "اختر إجازة."); return; }

            try
            {
                using var scope = Program.Services.CreateScope();
                var shiftService = scope.ServiceProvider.GetRequiredService<clsShiftService>();
                var holidays = await shiftService.GetHolidaysAsync(DateTime.Today.Year);
                var result = await shiftService.DeleteHolidayAsync(holidays[idx].HolidayId);
                UiHelpers.ShowResult(this, result);
                if (result.IsSuccess)
                    await LoadAllAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر الحذف.");
            }
        }

        private async Task EndAssignmentAsync()
        {
            int idx = _gridAssignments.CurrentRow?.Index ?? -1;
            if (idx < 0) { UiHelpers.ShowError(this, "اختر تعيينًا."); return; }

            if (!ConfirmationDialog.Show(this, "إنهاء تعيين",
                    "سيتم إنهاء تعيين الشفت لهذا الموظف. متابعة؟"))
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
                UiHelpers.ShowError(this, "تعذر إنهاء التعيين.");
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
            Text = "إضافة شفت";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 340);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblName = new Label { Text = "اسم الشفت *", Bounds = new Rectangle(30, 20, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 46, 380, 34) };

            var lblStart = new Label { Text = "من الساعة *", Bounds = new Rectangle(235, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var lblEnd = new Label { Text = "إلى الساعة *", Bounds = new Rectangle(30, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _dtpStart = new DateTimePicker { Bounds = new Rectangle(235, 116, 175, 34), Format = DateTimePickerFormat.Time, ShowUpDown = true, RightToLeftLayout = false };
            _dtpEnd = new DateTimePicker { Bounds = new Rectangle(30, 116, 175, 34), Format = DateTimePickerFormat.Time, ShowUpDown = true, RightToLeftLayout = false };
            _dtpStart.Value = DateTime.Today.AddHours(8);
            _dtpEnd.Value = DateTime.Today.AddHours(16);

            var lblHint = new Label
            {
                Text = "الشفت الليلي (مثل 22:00 → 06:00): اجعل وقت النهاية أقدم من البداية",
                Bounds = new Rectangle(30, 158, 380, 24),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 8.5F)
            };

            var lblNotes = new Label { Text = "ملاحظات", Bounds = new Rectangle(30, 188, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtNotes = new TextBox { Bounds = new Rectangle(30, 214, 380, 34) };

            var btnOk = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(240, 266, 170, 48);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 266, 170, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblName, _txtName, lblStart, _dtpStart, lblEnd, _dtpEnd, lblHint, lblNotes, _txtNotes, btnOk, btnCancel });
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "اسم الشفت مطلوب.");
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
                UiHelpers.ShowError(this, "تعذر حفظ الشفت.");
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
            Text = "إضافة إجازة رسمية";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 320);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblName = new Label { Text = "اسم الإجازة *", Bounds = new Rectangle(30, 20, 380, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtName = new TextBox { Bounds = new Rectangle(30, 46, 380, 34) };

            var lblDate = new Label { Text = "التاريخ *", Bounds = new Rectangle(235, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var lblDays = new Label { Text = "المدة (أيام) *", Bounds = new Rectangle(30, 90, 175, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            _dtpDate = new DateTimePicker { Bounds = new Rectangle(235, 116, 175, 34), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };
            _numDays = new NumericUpDown { Bounds = new Rectangle(30, 116, 175, 34), Minimum = 1, Maximum = 30, Value = 1 };

            _chkAnnual = new CheckBox
            {
                Text = "إجازة سنوية متكررة (تتكرر كل عام بنفس اليوم)",
                Bounds = new Rectangle(30, 166, 380, 30),
                ForeColor = AppTheme.TextPrimary
            };

            var btnOk = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(240, 246, 170, 48);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(30, 246, 170, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblName, _txtName, lblDate, _dtpDate, lblDays, _numDays, _chkAnnual, btnOk, btnCancel });
            CancelButton = btnCancel;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "اسم الإجازة مطلوب.");
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
                UiHelpers.ShowError(this, "تعذر حفظ الإجازة.");
            }
        }
    }
}
