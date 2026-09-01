using Common.DTOs;
using Common.Enums;
using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;
using Sayl.UI.UserControls;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// كشوف الرواتب: اختيار فترة → توليد الكشوف (idempotent) → تحرير الوحدات والخصومات
    /// → صرف المحدد/الكل (guarded — ينشئ مصروفاً بفئة رواتب يتدفق تلقائيًا في التقارير).
    /// صلاحية المحاسب فأعلى.
    /// </summary>
    public class PayrollForm : DialogForm
    {
        private readonly int? _filterEmployeeId;
        private readonly string? _filterEmployeeName;

        private DateTimePicker _dtpFrom = null!;
        private DateTimePicker _dtpTo = null!;
        private Button _btnGenerate = null!;
        private Button _btnPaySelected = null!;
        private Button _btnUnpay = null!;
        private Button _btnPayAll = null!;
        private DataGridView _grid = null!;
        private Label _lblSummary = null!;

        private List<DtoPayrollRecord> _payrolls = new();

        public PayrollForm(int? employeeId = null, string? employeeName = null)
        {
            _filterEmployeeId = employeeId;
            _filterEmployeeName = employeeName;
            BuildUi();
            Load += async (_, _) => await LoadPayrollsAsync();
        }

        private void BuildUi()
        {
            Text = _filterEmployeeId.HasValue
                ? $"كشوف رواتب — {_filterEmployeeName}"
                : "كشوف الرواتب";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(900, 560);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ── شريط الفترة ──
            var topBar = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = AppTheme.FieldBg, Padding = new Padding(12, 0, 12, 0) };

            var lblFrom = new Label { Text = "من", Dock = DockStyle.Right, Width = 40, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };
            var lblTo = new Label { Text = "إلى", Dock = DockStyle.Right, Width = 34, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };

            // افتراضي: الشهر الماضي كاملًا (فترة منتهية قابلة للتوليد فورًا)
            var today = DateTime.Today;
            var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
            _dtpFrom.Value = firstOfThisMonth.AddMonths(-1);
            _dtpTo.Value = firstOfThisMonth.AddDays(-1);

            _btnGenerate = AppTheme.MakeButton("توليد كشوف الفترة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnGenerate.Dock = DockStyle.Right;
            _btnGenerate.Width = 180;
            _btnGenerate.Click += async (_, _) => await GenerateAsync();

            topBar.Controls.Add(_btnGenerate);
            topBar.Controls.Add(lblTo);
            topBar.Controls.Add(_dtpTo);
            topBar.Controls.Add(lblFrom);
            topBar.Controls.Add(_dtpFrom);

            // ── ملخص + أزرار الصرف ──
            var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = AppTheme.CardBg, Padding = new Padding(12, 8, 12, 8) };

            _lblSummary = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AppTheme.PrimaryDark
            };

            _btnPaySelected = AppTheme.MakeButton("صرف المحدد", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 48);
            _btnPaySelected.Dock = DockStyle.Left;
            _btnPaySelected.Width = 150;
            _btnPaySelected.Click += async (_, _) => await PaySelectedAsync();

            _btnUnpay = AppTheme.MakeButton("عكس صرف المحدد", AppTheme.Warning, hoverColor: Color.FromArgb(180, 100, 5), height: 48);
            _btnUnpay.Dock = DockStyle.Left;
            _btnUnpay.Width = 170;
            _btnUnpay.Click += async (_, _) => await UnpaySelectedAsync();

            _btnPayAll = AppTheme.MakeButton("صرف كل المعلق", AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 48);
            _btnPayAll.Dock = DockStyle.Left;
            _btnPayAll.Width = 160;
            _btnPayAll.Click += async (_, _) => await PayAllAsync();

            bottomBar.Controls.Add(_lblSummary);
            bottomBar.Controls.Add(_btnPaySelected);
            bottomBar.Controls.Add(_btnUnpay);
            bottomBar.Controls.Add(_btnPayAll);

            // ── الجدول ──
            _grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

            _grid.Columns.Add("colEmp", "الموظف");
            _grid.Columns.Add("colType", "النوع");
            _grid.Columns.Add("colWorked", "أيام/ساعات عمل");
            _grid.Columns.Add("colHoliday", "إجازات رسمية");
            _grid.Columns.Add("colRate", "المعدل");
            _grid.Columns.Add("colGross", "إجمالي الراتب");
            _grid.Columns.Add("colDed", "خصومات");
            _grid.Columns.Add("colAdd", "إضافات");
            _grid.Columns.Add("colNet", "الصافي");
            _grid.Columns.Add("colStatus", "الحالة");
            _grid.Columns["colEmp"].FillWeight = 20;
            _grid.Columns["colEmp"].ReadOnly = true;
            _grid.Columns["colType"].FillWeight = 9;
            _grid.Columns["colType"].ReadOnly = true;
            _grid.Columns["colWorked"].FillWeight = 12;
            _grid.Columns["colHoliday"].FillWeight = 11;
            _grid.Columns["colRate"].FillWeight = 10;
            _grid.Columns["colRate"].ReadOnly = true;
            _grid.Columns["colGross"].FillWeight = 11;
            _grid.Columns["colGross"].ReadOnly = true;
            _grid.Columns["colDed"].FillWeight = 10;
            _grid.Columns["colAdd"].FillWeight = 10;
            _grid.Columns["colNet"].FillWeight = 12;
            _grid.Columns["colNet"].ReadOnly = true;
            _grid.Columns["colStatus"].FillWeight = 10;
            _grid.Columns["colStatus"].ReadOnly = true;

            foreach (string col in new[] { "colWorked", "colHoliday", "colDed", "colAdd" })
                _grid.Columns[col].DefaultCellStyle.BackColor = AppTheme.FieldBg;

            _grid.CellEndEdit += async (_, args) => await CellEditedAsync(args);
            _grid.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) args.SuppressKeyPress = true; };

            Controls.Add(_grid);
            Controls.Add(bottomBar);
            Controls.Add(topBar);

            // زر إغلاق أعلى (النافذة بلا حدود)
            var btnClose = AppTheme.MakeCircleButton("×", 44,
                back: AppTheme.BorderColor, hoverBack: AppTheme.Danger,
                fore: AppTheme.TextPrimary, parentBack: AppTheme.CardBg, fontSize: 15f);
            btnClose.Bounds = new Rectangle(12, 10, 44, 44);
            btnClose.Click += (_, _) => Close();
            topBar.Controls.Add(btnClose);
        }

        // ───────────────── التحميل ─────────────────
        private async Task LoadPayrollsAsync()
        {
            try
            {
                // كشوف الفترة المختارة (أو كل كشوف الموظف المفلتر)
                using var scope = Program.Services.CreateScope();
                var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();

                DateOnly? from = _filterEmployeeId.HasValue ? null : DateOnly.FromDateTime(_dtpFrom.Value);
                DateOnly? to = _filterEmployeeId.HasValue ? null : DateOnly.FromDateTime(_dtpTo.Value);
                _payrolls = await payrollService.GetPayrollsAsync(from, to, _filterEmployeeId);

                RefreshGrid();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الكشوف.");
            }
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            foreach (var p in _payrolls)
            {
                int row = _grid.Rows.Add(p.EmployeeName,
                    EmployeesControl.TranslateSalaryType(p.SalaryType),
                    p.WorkedUnits.ToString("0.##"),
                    p.HolidayUnits.ToString("0.##"),
                    UiHelpers.Money(p.Rate),
                    UiHelpers.Money(p.Gross),
                    p.Deductions.ToString("0.##"),
                    p.Additions.ToString("0.##"),
                    UiHelpers.Money(p.Net),
                    p.Status == enPayrollStatus.paid ? "مصروف" : "معلق");
                _grid.Rows[row].Tag = p;

                if (p.Status == enPayrollStatus.paid)
                {
                    _grid.Rows[row].Cells["colStatus"].Style.ForeColor = AppTheme.Success;
                    foreach (DataGridViewCell cell in _grid.Rows[row].Cells)
                        cell.Style.BackColor = AppTheme.FieldBg;
                }
                else
                {
                    _grid.Rows[row].Cells["colStatus"].Style.ForeColor = AppTheme.Warning;
                }
            }

            decimal pendingTotal = _payrolls.Where(p => p.Status == enPayrollStatus.pending).Sum(p => p.Net);
            decimal paidTotal = _payrolls.Where(p => p.Status == enPayrollStatus.paid).Sum(p => p.Net);
            _lblSummary.Text = $"المعلق: {UiHelpers.Money(pendingTotal)}    المصروف: {UiHelpers.Money(paidTotal)}";
        }

        // ───────────────── توليد ─────────────────
        private async Task GenerateAsync()
        {
            var from = DateOnly.FromDateTime(_dtpFrom.Value);
            var to = DateOnly.FromDateTime(_dtpTo.Value);

            if (!ConfirmationDialog.Show(this, "توليد كشوف",
                    $"سيتم توليد كشوف لكل الموظفين النشطين للفترة {from:yyyy-MM-dd} ← {to:yyyy-MM-dd}.\nالموظفون الذين لديهم كشوف بالفعل سيُتجاوزون.", dangerStyle: false))
                return;

            try
            {
                Common.Results.Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                    result = await payrollService.GeneratePayrollsAsync(from, to,
                        SessionState.CurrentUser?.UserId ?? 0);
                }

                UiHelpers.ShowResult(this, result);
                await LoadPayrollsAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر توليد الكشوف.");
            }
        }

        // ───────────────── تحرير خلية (كشف معلق) ─────────────────
        private async Task CellEditedAsync(DataGridViewCellEventArgs args)
        {
            if (args.RowIndex < 0 || args.RowIndex >= _payrolls.Count)
                return;

            var record = _payrolls[args.RowIndex];
            if (record.Status == enPayrollStatus.paid)
                return; // محمي — الصف للعرض فقط

            // اقرأ القيم الأربع القابلة للتحرير
            decimal worked = ParseCell(_grid.Rows[args.RowIndex].Cells["colWorked"].Value);
            decimal holiday = ParseCell(_grid.Rows[args.RowIndex].Cells["colHoliday"].Value);
            decimal ded = ParseCell(_grid.Rows[args.RowIndex].Cells["colDed"].Value);
            decimal add = ParseCell(_grid.Rows[args.RowIndex].Cells["colAdd"].Value);

            try
            {
                using var scope = Program.Services.CreateScope();
                var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                var result = await payrollService.UpdatePayrollAsync(record.PayrollId, worked, holiday, ded, add,
                    record.Notes, SessionState.CurrentUser?.UserId ?? 0);

                if (!result.IsSuccess)
                {
                    UiHelpers.ShowError(this, result.Message);
                    await LoadPayrollsAsync(); // إعادة القيمة القديمة
                }
                else
                {
                    await LoadPayrollsAsync(); // تحديث الإجمالي/الصافي المعاد حسابهما
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحديث الكشف.");
                await LoadPayrollsAsync();
            }
        }

        private static decimal ParseCell(object? value) =>
            decimal.TryParse(value?.ToString()?.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

        // ───────────────── صرف ─────────────────
        private async Task PaySelectedAsync()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _payrolls.Count)
            {
                UiHelpers.ShowError(this, "اختر كشفًا من الجدول.");
                return;
            }

            var record = _payrolls[idx];
            if (record.Status == enPayrollStatus.paid)
            {
                UiHelpers.ShowInfo(this, "الكشف مصروف بالفعل.");
                return;
            }

            if (!ConfirmationDialog.Show(this, "صرف راتب",
                    $"سيتم صرف راتب {record.EmployeeName} بمبلغ {UiHelpers.Money(record.Net)} وتسجيله كمصروف.", dangerStyle: false))
                return;

            try
            {
                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                    result = await payrollService.PayPayrollAsync(record.PayrollId,
                        SessionState.CurrentUser?.UserId ?? 0);
                }

                UiHelpers.ShowResult(this, result);
                await LoadPayrollsAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر الصرف.");
            }
        }

        // ───────────────── عكس صرف (خطأ بشري) ─────────────────
        private async Task UnpaySelectedAsync()
        {
            int idx = _grid.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _payrolls.Count)
            {
                UiHelpers.ShowError(this, "اختر كشفًا مصروفًا من الجدول.");
                return;
            }

            var record = _payrolls[idx];
            if (record.Status != enPayrollStatus.paid)
            {
                UiHelpers.ShowInfo(this, "الكشف المحدد ليس مصروفًا.");
                return;
            }

            if (!ConfirmationDialog.Show(this, "عكس صرف راتب",
                    $"سيتم إرجاع كشف {record.EmployeeName} إلى معلق وحذف مصروفه ({UiHelpers.Money(record.Net)}).\nاستخدم هذا لتصحيح صرف خاطئ فقط.", dangerStyle: true))
                return;

            try
            {
                Common.Results.Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                    result = await payrollService.UnpayPayrollAsync(record.PayrollId,
                        SessionState.CurrentUser?.UserId ?? 0);
                }

                UiHelpers.ShowResult(this, result);
                await LoadPayrollsAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر عكس الصرف.");
            }
        }

        private async Task PayAllAsync()
        {
            var pending = _payrolls.Where(p => p.Status == enPayrollStatus.pending).ToList();
            if (pending.Count == 0)
            {
                UiHelpers.ShowInfo(this, "لا توجد كشوف معلقة.");
                return;
            }

            decimal total = pending.Sum(p => p.Net);
            if (!ConfirmationDialog.Show(this, "صرف الكل",
                    $"سيتم صرف {pending.Count} كشفًا بإجمالي {UiHelpers.Money(total)} كمصروفات رواتب.", dangerStyle: false))
                return;

            try
            {
                int paid = 0;
                using (var scope = Program.Services.CreateScope())
                {
                    var payrollService = scope.ServiceProvider.GetRequiredService<clsPayrollService>();
                    foreach (var p in pending)
                    {
                        var r = await payrollService.PayPayrollAsync(p.PayrollId,
                            SessionState.CurrentUser?.UserId ?? 0);
                        if (r.IsSuccess) paid++;
                    }
                }

                UiHelpers.ShowInfo(this, $"تم صرف {paid} من {pending.Count} كشفًا.");
                await LoadPayrollsAsync();
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر الصرف.");
            }
        }
    }
}
