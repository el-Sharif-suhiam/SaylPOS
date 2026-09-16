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
        private Button _btnRefresh = null!;
        private DataGridView _grid = null!;
        private Label _lblSummary = null!;

        private List<DtoPayrollRecord> _payrolls = new();
        private PagerControl _pager = null!;
        private int _currentPage = 1;
        private int _pageSize = 20;

        // فهرس الكشف الكامل لصف الشبكة المعروض (ترقيم العرض فقط — الخلفية كاملة)
        private int VisibleIndex(int gridRow) => (_currentPage - 1) * _pageSize + gridRow;

        public PayrollForm(int? employeeId = null, string? employeeName = null)
        {
            _filterEmployeeId = employeeId;
            _filterEmployeeName = employeeName;
            BuildUi();
            Load += async (_, _) => await LoadPayrollsAsync();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «الجذر المجزّأ» (الأهم في النظام):
        //
        // هذه الشاشة تجسّد البنية التي تحل مشكلة «زر يختفي بالتمرير»:
        //
        //   ┌─────────────────────────────┐
        //   │ صف0 (Absolute 60): شريط الفترة │ ← ارتفاع ثابت دائمًا
        //   ├─────────────────────────────┤
        //   │ صف1 (Percent 100): الجدول     │ ← يأخذ كل المساحة المتبقية
        //   ├─────────────────────────────┤
        //   │ صف2 (Absolute 68): الأزرار    │ ← ثابت أسفل الشاشة — لا يتمرر أبدًا
        //   └─────────────────────────────┘
        //
        // قواعد ذهبية لهذا النمط:
        // 1. TableLayoutPanel واحد فقط يملأ النافذة (Controls.Add(root) وحده).
        // 2. الصف المطلق (Absolute) محجوز دائمًا مهما صغرت النافذة —
        //    عكس Dock=Bottom الذي قد تبتلعه منطقة التمرير.
        // 3. الصف المرن (Percent 100) هو الوحيد الذي يتقلص — ضع فيه ما يُمرَّر.
        // 4. Padding=12 على الجذر = هامش موحد عن حواف النافذة.
        //
        // لتعديل ارتفاع شريط: غيّر 60. لتعديل ارتفاع صف الأزرار: غيّر 68.
        // لإضافة صف رابع: أضف RowStyle + Controls.Add(عنصر, 0, 3) —
        //    وانتبه: ترتيب إضافة RowStyles يجب أن يطابق رقم الصف.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── قواعد النافذة (نفس نمط كل النوافذ الحوارية) ──
            // MinimumSize: أصغر مقاس مسموح — يمنع قصّ الأزرار عند التصغير.
            Text = _filterEmployeeId.HasValue
                ? AppLang.T("Payroll.Title.Filtered", _filterEmployeeName)
                : AppLang.T("Payroll.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1100, 700);
            MinimumSize = new Size(900, 560);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ══ الجذر: هامش خارجي + ثلاثة صفوف مطلقة (فترة / جدول مرن / أزرار) ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(12) // هامش عن الحواف — لا شيء ملتصق
            };
            // عمود واحد (العرض 100%) — هذه شاشة قوائم عمودية.
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // الصفوف الأربعة بالترتيب — الأرقام = الارتفاقات بالبكسل.
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));    // الفترة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // الجدول
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));     // الترقيم
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));     // الأزرار

            // ── صف الفترة (يوضع لاحقًا في خلية الصف 0) ──
            // عناصره بإرساء Dock داخل اللوحة — الترتيب مهم:
            // في RTL، كل Controls.Add بإرساء Right يتراصف من اليمين لليسار.
            // أول Add يوضع أقصى اليمين، التالي إلى يساره... (اقرأ الأسطر بترتيبها)
            var topBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.FieldBg, Padding = new Padding(10, 8, 10, 8) };

            var lblFrom = new Label { Text = AppLang.T("Payroll.Label.From"), Dock = DockStyle.Right, Width = 40, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpFrom = new DateTimePicker { Dock = DockStyle.Right, Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };
            var lblTo = new Label { Text = AppLang.T("Payroll.Label.To"), Dock = DockStyle.Right, Width = 34, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpTo = new DateTimePicker { Dock = DockStyle.Right, Width = 140, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", RightToLeftLayout = false };

            // افتراضي: الشهر الماضي كاملًا (فترة منتهية قابلة للتوليد فورًا)
            var today = AppClock.NowLocal.Date;
            var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
            _dtpFrom.Value = firstOfThisMonth.AddMonths(-1);
            _dtpTo.Value = firstOfThisMonth.AddDays(-1);

            _btnGenerate = AppTheme.MakeButton(AppLang.T("Payroll.Button.Generate"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 44);
            _btnGenerate.Dock = DockStyle.Right;
            _btnGenerate.Width = 180;
            _btnGenerate.Click += async (_, _) => await GenerateAsync();

            topBar.Controls.Add(_btnGenerate);
            topBar.Controls.Add(lblTo);
            topBar.Controls.Add(_dtpTo);
            topBar.Controls.Add(lblFrom);
            topBar.Controls.Add(_dtpFrom);

            // ── صف الأزرار السفلي (مفصول — خارج منطقة التمرير دائمًا) ──
            // نمط الترتيب هنا معاكس لشريط الفترة: DockStyle.Left داخل RTL
            // يرتصف من اليمين (أول Add = أقصى اليمين).
            // _lblSummary بإرساء Fill يملأ المساحة المتبقية بين الأزرار
            // ويظهر أرقام المعلق/المصروف — عدّل نصه فقط في RefreshGrid.
            var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(2, 8, 2, 8) };

            _lblSummary = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = AppTheme.PrimaryDark
            };

            // الأزرار: Height=48 (حد اللمس الأدنى — لا تصغّره).
            // Width هو عرض الزر — عدّله لو غيّرت نصًا أطول.
            // لترتيب مختلف: غيّر ترتيب أسطر Add أسفل اللوحة (أول سطر = أقصى اليمين).
            _btnPaySelected = AppTheme.MakeButton(AppLang.T("Payroll.Button.PaySelected"), AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61), height: 48);
            _btnPaySelected.Dock = DockStyle.Left;
            _btnPaySelected.Width = 140;
            _btnPaySelected.Click += async (_, _) => await PaySelectedAsync();

            _btnRefresh = AppTheme.MakeButton(AppLang.T("Payroll.Button.Refresh"), AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 48);
            _btnRefresh.Dock = DockStyle.Left;
            _btnRefresh.Width = 110;
            _btnRefresh.Click += async (_, _) => await LoadPayrollsAsync();

            // DialogResult=Cancel: يجعل Esc يغلق النافذة — أضفه لكل زر إغلاق.
            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 120;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(_lblSummary);
            bottomBar.Controls.Add(_btnPaySelected);
            bottomBar.Controls.Add(_btnRefresh);
            bottomBar.Controls.Add(btnClose);

            // ── الجدول (يوضع في خلية الصف المرن — يتمدد ويتمرر وحده) ──
            // StyleGrid: تنسيق موحد (رأس كحلي/صفوف متناوبة/إرتفاع 40).
            // ReadOnly=false: بعض خلاياه قابلة للتحرير (المحاسب يعدل مباشرة).
            // EditMode=EditOnKeystrokeOrF2: الكتابة فوق الخلية تحررها فورًا،
            //   أو F2 — الأشهر للكاشير/المحاسب.
            _grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

            // ── أعمدة الجدول ──
            // نمط الإضافة: (اسم تقني، نص الرأس). الاسم التقني يُستخدم في
            // الأحداث والتنسيق — لا تكرره، والنص هو ما يراه المستخدم.
            // FillWeight: نسبة عرض العمود من الجدول — مجموعها مئة تقريبًا
            //   (غيّرها لتوسيع/تضييق عمود دون كسر الباقي).
            // ReadOnly=true على العمود: عرض فقط (المحاسيب/الصافي محسوبة).
            _grid.Columns.Add("colEmp", AppLang.T("Payroll.Col.Employee"));
            _grid.Columns.Add("colType", AppLang.T("Payroll.Col.Type"));
            _grid.Columns.Add("colWorked", AppLang.T("Payroll.Col.Worked"));
            _grid.Columns.Add("colHoliday", AppLang.T("Payroll.Col.Holidays"));
            _grid.Columns.Add("colRate", AppLang.T("Payroll.Col.Rate"));
            _grid.Columns.Add("colGross", AppLang.T("Payroll.Col.Gross"));
            _grid.Columns.Add("colDed", AppLang.T("Payroll.Col.Deductions"));
            _grid.Columns.Add("colAdd", AppLang.T("Payroll.Col.Additions"));
            _grid.Columns.Add("colNet", AppLang.T("Payroll.Col.Net"));
            _grid.Columns.Add("colStatus", AppLang.T("Payroll.Col.Status"));
            var colUnpay = new DataGridViewButtonColumn
            {
                Name = "colUnpay",
                HeaderText = "",
                Text = AppLang.T("Payroll.Col.Unpay"),
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat,
                FillWeight = 7
            };
            colUnpay.DefaultCellStyle.ForeColor = AppTheme.Warning;
            colUnpay.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            _grid.Columns.Add(colUnpay);

            _grid.Columns["colEmp"].FillWeight = 18;
            _grid.Columns["colEmp"].ReadOnly = true;
            _grid.Columns["colType"].FillWeight = 8;
            _grid.Columns["colType"].ReadOnly = true;
            _grid.Columns["colWorked"].FillWeight = 11;
            _grid.Columns["colHoliday"].FillWeight = 10;
            _grid.Columns["colRate"].FillWeight = 9;
            _grid.Columns["colRate"].ReadOnly = true;
            _grid.Columns["colGross"].FillWeight = 10;
            _grid.Columns["colGross"].ReadOnly = true;
            _grid.Columns["colDed"].FillWeight = 9;
            _grid.Columns["colAdd"].FillWeight = 9;
            _grid.Columns["colNet"].FillWeight = 10;
            _grid.Columns["colNet"].ReadOnly = true;
            _grid.Columns["colStatus"].FillWeight = 9;
            _grid.Columns["colStatus"].ReadOnly = true;

            foreach (string col in new[] { "colWorked", "colHoliday", "colDed", "colAdd" })
                _grid.Columns[col].DefaultCellStyle.BackColor = AppTheme.FieldBg;

            _grid.CellEndEdit += async (_, args) => await CellEditedAsync(args);
            _grid.CellClick += async (_, args) => await UnpayCellClickAsync(args);
            _grid.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) args.SuppressKeyPress = true; };

            // شريط الترقيم: تنقّل يعيد رسم العرض فقط (الخلفية _payrolls كاملة أوفر للإجماليات)
            _pager = new PagerControl();
            _pager.PageRequested += page =>
            {
                _currentPage = page;
                RefreshGrid();
            };
            _pager.PageSizeSelected += size =>
            {
                _pageSize = size;
                _currentPage = 1;
                RefreshGrid();
            };

            root.Controls.Add(topBar, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(_pager, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);

            Controls.Add(root); // وحيد — الصفوف المطلقة لا تدخل التمرير أبدًا
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

                _currentPage = 1; // فلتر جديد = من أول صفحة
                RefreshGrid();
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Payroll.Msg.LoadFailed"));
            }
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();

            int totalPages = Math.Max(1, (int)Math.Ceiling(_payrolls.Count / (double)Math.Max(1, _pageSize)));
            _currentPage = Math.Clamp(_currentPage, 1, totalPages);

            foreach (var p in _payrolls.Skip((_currentPage - 1) * _pageSize).Take(_pageSize))
            {
                bool paid = p.Status == enPayrollStatus.paid;
                int row = _grid.Rows.Add(p.EmployeeName,
                    EmployeesControl.TranslateSalaryType(p.SalaryType),
                    p.WorkedUnits.ToString("0.##"),
                    p.HolidayUnits.ToString("0.##"),
                    UiHelpers.Money(p.Rate),
                    UiHelpers.Money(p.Gross),
                    p.Deductions.ToString("0.##"),
                    p.Additions.ToString("0.##"),
                    UiHelpers.Money(p.Net),
                    paid ? AppLang.T("Payroll.Status.Paid") : AppLang.T("Payroll.Status.Pending"),
                    paid ? AppLang.T("Payroll.Col.Unpay") : "");   // زر العكس لكل صف — يظهر للمصروف فقط
                _grid.Rows[row].Tag = p;

                if (paid)
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
            _lblSummary.Text = AppLang.T("Payroll.Summary", UiHelpers.Money(pendingTotal), UiHelpers.Money(paidTotal));

            _pager.SetNavigation(_payrolls.Count, _currentPage);
        }

        // ───────────────── عكس صرف الصف (زر لكل سطر — نمط الشفتات) ─────────────────
        private async Task UnpayCellClickAsync(DataGridViewCellEventArgs args)
        {
            if (args.RowIndex < 0 || args.ColumnIndex != _grid.Columns["colUnpay"].Index)
                return;

            int pi = VisibleIndex(args.RowIndex);
            if (pi < 0 || pi >= _payrolls.Count)
                return;

            var record = _payrolls[pi];
            if (record.Status != enPayrollStatus.paid)
                return; // المعلق لا يظهر زر عكس أصلًا

            if (!ConfirmationDialog.Show(this, AppLang.T("Payroll.Unpay.Title"),
                    AppLang.T("Payroll.Unpay.Confirm", record.EmployeeName, UiHelpers.Money(record.Net)), dangerStyle: true))
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
                UiHelpers.ShowError(this, AppLang.T("Payroll.Unpay.Failed"));
            }
        }

        // ───────────────── توليد ─────────────────
        private async Task GenerateAsync()
        {
            var from = DateOnly.FromDateTime(_dtpFrom.Value);
            var to = DateOnly.FromDateTime(_dtpTo.Value);

            if (!ConfirmationDialog.Show(this, AppLang.T("Payroll.Generate.Title"),
                    AppLang.T("Payroll.Generate.Confirm", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd")), dangerStyle: false))
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
                UiHelpers.ShowError(this, AppLang.T("Payroll.Generate.Failed"));
            }
        }

        // ───────────────── تحرير خلية (كشف معلق) ─────────────────
        private async Task CellEditedAsync(DataGridViewCellEventArgs args)
        {
            int pi = VisibleIndex(args.RowIndex);
            if (args.RowIndex < 0 || pi >= _payrolls.Count)
                return;

            var record = _payrolls[pi];
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
                UiHelpers.ShowError(this, AppLang.T("Payroll.Update.Failed"));
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
            int pi = VisibleIndex(idx);
            if (pi < 0 || pi >= _payrolls.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Payroll.Pay.PickFirst"));
                return;
            }

            var record = _payrolls[pi];
            if (record.Status == enPayrollStatus.paid)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Payroll.Pay.AlreadyPaid"));
                return;
            }

            if (!ConfirmationDialog.Show(this, AppLang.T("Payroll.Pay.Title"),
                    AppLang.T("Payroll.Pay.Confirm", record.EmployeeName, UiHelpers.Money(record.Net)), dangerStyle: false))
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
                UiHelpers.ShowError(this, AppLang.T("Payroll.Pay.Failed"));
            }
        }

        // ───────────────── عكس الصرف أصبح زر «عكس» لكل صف مصروف (UnpayCellClickAsync) ─────────────────
    }
}
