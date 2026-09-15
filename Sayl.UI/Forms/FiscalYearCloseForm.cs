using Common.DTOs.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// معالج إغلاق السنة المالية (أدمن): جرد → فحوصات الجاهزية → أرشفة
    /// (نسخة احتياطية + تقرير PDF سنوي) → قلب السنة — دون حذف أي سجل.
    /// التاريخ القديم يبقى للقراءة والمقارنة؛ التعديل قبله يُقفل.
    /// </summary>
    public class FiscalYearCloseForm : DialogForm
    {
        private readonly TableLayoutPanel _root = null!;
        private readonly Panel _pageBody = null!;
        private Button _btnNext = null!;
        private Button _btnBack = null!;
        private Label _lblStep = null!;

        private int _step;
        private FiscalCloseCheckVM? _checks;

        // النتائج عبر الخطوات
        private DateOnly _fiscalStart;
        private string? _backupFile;
        private string? _reportFile;
        private DateTimePicker _dtpNewYear = null!;

        public FiscalYearCloseForm()
        {
            Text = AppLang.T("Fiscal.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 560);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ═══════════════════════════════════════════════════════════════
            // 📖 دليل البناء — نمط «المعالج (Wizard)»: أثمن فكرة في هذا الملف.
            //
            // النافذة هيكل واحد ثابت (بنٍى مرة واحدة):
            //   صف0 (52): عنوان الخطوة الحالية
            //   صف1 (مرن): _pageBody — لوحة «الصفحة» البديلة
            //   صف2 (64): شريط التنقل (التالي/رجوع/إلغاء)
            //
            // ⚙️ آلية تبديل الصفحات (انظر Go أسفل):
            //   _pageBody.Controls.Clear() يهدم صفحة الخطوة السابقة
            //   ثم BuildXxxPage() تبني الجديدة داخل نفس اللوحة.
            //   كل صفحة دالة مستقلة بناها (Label/أزرار/FiscalData) —
            //   لا تشارك حالة إلا حقول النتائج (_checks/_backupFile/...).
            //   💡 لإضافة خطوة: case جديد في Go + دالة BuildXxxPage.
            //
            // نمط الشريط السفلي: أزرار Dock=Left داخل RTL — أول Add أقصى
            // اليمين («التالي» أولًا يمينًا، ثم «رجوع» يساره، ثم «إلغاء»).
            // ═══════════════════════════════════════════════════════════════
            // جذر: ترويسة الخطوة + متن + شريط تنقل مطلق
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(14)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // ترويسة الخطوة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // المتن
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // التنقل

            _lblStep = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary
            };

            _pageBody = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(16, 12, 16, 12) };

            var navBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(2, 8, 2, 8) };

            _btnNext = AppTheme.MakeButton(AppLang.T("Fiscal.Button.Next"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 48);
            _btnNext.Dock = DockStyle.Left;
            _btnNext.Width = 150;
            _btnNext.Click += async (_, _) => await NextAsync();

            _btnBack = AppTheme.MakeButton(AppLang.T("Fiscal.Button.Back"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            _btnBack.Dock = DockStyle.Left;
            _btnBack.Width = 110;
            _btnBack.Click += (_, _) => Go(_step - 1);

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 110;
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += (_, _) => Close();

            navBar.Controls.Add(_btnNext);
            navBar.Controls.Add(_btnBack);
            navBar.Controls.Add(btnClose);

            root.Controls.Add(_lblStep, 0, 0);
            root.Controls.Add(_pageBody, 0, 1);
            root.Controls.Add(navBar, 0, 2);

            Controls.Add(root);

            Load += async (_, _) =>
            {
                using var scope = Program.Services.CreateScope();
                var fiscal = scope.ServiceProvider.GetRequiredService<clsFiscalYearService>();
                _fiscalStart = await fiscal.GetFiscalYearStartAsync();
                var lastClose = await fiscal.GetLastCloseDateAsync();
                _lblStep.Text = lastClose == null
                    ? AppLang.T("Fiscal.Step.Header", _fiscalStart)
                    : AppLang.T("Fiscal.Step.HeaderWithLast", _fiscalStart, lastClose.Value);
                Go(0);
            };
        }

        // ───────────────── الخطوات ─────────────────
        // 📖 Go: «مبدّل الصفحات» — القلب النابض لنمط المعالج:
        //  1. يخفي «رجوع» في الخطوة الأولى (_step > 0 شرط الظهور).
        //  2. يهدم القديم بـ Controls.Clear() — عناصر الصفحة تُرمى للGC.
        //  3. يقلب نص الزر الرئيسي في الخطوة الأخيرة («إغلاق السنة الآن»).
        //  4. switch يبني الصفحة الجديدة — بعض الصفحات async (فحوصات
        //     قاعدة بيانات) فتُستدعى بالنمط `_ = BuildXxxAsync()` —
        //     discard حتى لا تُنتظر (الصفحة تظهر فورًا وتتعبأ تدريجيًا).
        // NextAsync هو «الحارس»: يرفض الانتقال لو فشل شرط الخطوة الحالية.
        private void Go(int step)
        {
            if (step < 0) return;
            _step = step;
            _btnBack.Visible = _step > 0;
            _pageBody.Controls.Clear();
            _btnNext.Text = _step == 3 ? AppLang.T("Fiscal.Button.CloseNow") : AppLang.T("Fiscal.Button.Next");

            switch (_step)
            {
                case 0: BuildIntroPage(); break;
                case 1: _ = BuildChecksPageAsync(); break;
                case 2: _ = BuildArchivePageAsync(); break;
                case 3: BuildClosePage(); break;
            }
        }

        private async Task NextAsync()
        {
            if (_step == 1 && (_checks == null || !_checks.CanClose))
            {
                UiHelpers.ShowError(this, AppLang.T("Fiscal.Msg.FixErrorsFirst"));
                return;
            }
            if (_step == 3)
            {
                await CloseYearAsync();
                return;
            }
            Go(_step + 1);
        }

        // ── صفحة 0: مقدمة ──
        private void BuildIntroPage()
        {
            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopRight,
                ForeColor = AppTheme.TextPrimary,
                Text = AppLang.T("Fiscal.Intro.Body", _fiscalStart)
            };
            _pageBody.Controls.Add(lbl);
        }

        // ── صفحة 1: الفحوصات ──
        private async Task BuildChecksPageAsync()
        {
            _pageBody.Controls.Clear();
            var lblLoading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Text = AppLang.T("Fiscal.Checks.Loading"), ForeColor = AppTheme.TextMuted };
            _pageBody.Controls.Add(lblLoading);

            try
            {
                using var scope = Program.Services.CreateScope();
                var fiscal = scope.ServiceProvider.GetRequiredService<clsFiscalYearService>();
                _checks = await fiscal.GetCloseReadinessAsync();
            }
            catch
            {
                _checks = null;
                UiHelpers.ShowError(this, AppLang.T("Fiscal.Checks.MsgFailed"));
                return;
            }

            _pageBody.Controls.Clear();

            var grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(grid);
            grid.Columns.Add("colType", AppLang.T("Fiscal.Checks.ColType"));
            grid.Columns.Add("colItem", AppLang.T("Fiscal.Checks.ColItem"));
            grid.Columns["colType"].FillWeight = 16;
            grid.Columns["colItem"].FillWeight = 84;

            void AddLine(string type, string item, Color color)
            {
                int r = grid.Rows.Add(type, item);
                grid.Rows[r].Cells["colType"].Style.ForeColor = color;
                grid.Rows[r].Cells["colItem"].Style.ForeColor = color;
            }

            if (_checks!.Errors.Count == 0 && _checks.Warnings.Count == 0)
                AddLine(AppLang.T("Fiscal.Checks.Ready"), AppLang.T("Fiscal.Checks.ReadyBody"), AppTheme.Success);

            foreach (var e in _checks.Errors)
                AddLine(AppLang.T("Fiscal.Checks.Blocker"), e, AppTheme.Danger);
            foreach (var w in _checks.Warnings)
                AddLine(AppLang.T("Fiscal.Checks.Warning"), w, AppTheme.Warning);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = _checks.CanClose ? AppTheme.Success : AppTheme.Danger,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Text = _checks.CanClose
                    ? AppLang.T("Fiscal.Checks.CanClose")
                    : AppLang.T("Fiscal.Checks.CannotClose")
            };

            _pageBody.Controls.Add(grid);
            _pageBody.Controls.Add(hint);
        }

        // ── صفحة 2: الأرشفة ──
        private async Task BuildArchivePageAsync()
        {
            _pageBody.Controls.Clear();

            var lbl = new Label
            {
                Dock = DockStyle.Top,
                Height = 100,
                TextAlign = ContentAlignment.TopRight,
                ForeColor = AppTheme.TextPrimary,
                Text = AppLang.T("Fiscal.Archive.Intro")
            };
            _pageBody.Controls.Add(lbl);

            var btnBackup = AppTheme.MakeButton(AppLang.T("Fiscal.Archive.BackupNow"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 46);
            btnBackup.Dock = DockStyle.Top;
            btnBackup.Click += async (_, _) => await TakeBackupAsync(btnBackup);

            var btnReport = AppTheme.MakeButton(AppLang.T("Fiscal.Archive.GenerateReport"), AppTheme.Accent, hoverColor: AppTheme.AccentDark, height: 46);
            btnReport.Dock = DockStyle.Top;
            btnReport.Click += async (_, _) => await GenerateAnnualReportAsync(btnReport);

            var lblStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 56,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Name = "lblArchiveStatus",
                Text = AppLang.T("Fiscal.Archive.StatusMissing")
            };

            _pageBody.Controls.Add(lblStatus);
            _pageBody.Controls.Add(btnBackup);
            _pageBody.Controls.Add(btnReport);
        }

        private async Task TakeBackupAsync(Button btn)
        {
            btn.Enabled = false;
            try
            {
                using var scope = Program.Services.CreateScope();
                var backupService = scope.ServiceProvider.GetRequiredService<clsBackupService>();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                string folder = await settings.GetAsync(clsSettingsService.KeyBackupFolder, clsBackupService.DefaultBackupFolder);
                var r = await backupService.BackupAsync(folder);
                if (r.IsSuccess)
                {
                    _backupFile = r.Data;
                    await settings.SetAsync(clsSettingsService.KeyLastBackupDate, AppClock.NowLocal.Date.ToString("yyyy-MM-dd"));
                }
                UiHelpers.ShowResult(this, r);
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Fiscal.Archive.MsgBackupFailed"));
            }
            finally
            {
                btn.Enabled = true;
                UpdateArchiveStatus();
            }
        }

        private async Task GenerateAnnualReportAsync(Button btn)
        {
            btn.Enabled = false;
            try
            {
                var today = AppClock.Today;

                using var scope = Program.Services.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<clsReportService>();
                var r = await reportService.GetPeriodicReportAsync(_fiscalStart, today,
                    Common.DTOs.ViewModel.enPeriodReportType.Yearly, includeInvoiceDetails: false);

                if (!r.IsSuccess || r.Data == null)
                {
                    UiHelpers.ShowError(this, AppLang.T("Fiscal.Archive.MsgReportFailed", r.Message));
                    return;
                }

                using var save = new SaveFileDialog
                {
                    Title = AppLang.T("Fiscal.Archive.SaveTitle"),
                    Filter = AppLang.T("Fiscal.Archive.PdfFilter"),
                    FileName = AppLang.T("Fiscal.Archive.FileName", _fiscalStart, today) + ".pdf"
                };
                if (save.ShowDialog(this) != DialogResult.OK) return;

                // 📖 التوليد خارج مؤشر الواجهة — تقرير سنة كاملة قد يستغرق ثوانيًا
                // (استدعاؤه متزامنًا هنا كان يجمّد النافذة بالكامل)
                string storeName = AppLang.T("Common.Default.Store");
                try
                {
                    using var nameScope = Program.Services.CreateScope();
                    var nameSettings = nameScope.ServiceProvider.GetRequiredService<clsSettingsService>();
                    storeName = await nameSettings.GetAsync(clsSettingsService.KeyStoreName, AppLang.T("Common.Default.Store"));
                }
                catch { /* الترويسة الافتراضية كافية */ }

                var data = r.Data;
                await Task.Run(() => PdfService.ExportPeriodicReport(data, storeName, save.FileName));

                _reportFile = save.FileName;
                UiHelpers.ShowInfo(this, AppLang.T("Fiscal.Archive.ReportDone", save.FileName));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Fiscal.Archive.MsgReportFailedShort"));
            }
            finally
            {
                btn.Enabled = true;
                UpdateArchiveStatus();
            }
        }

        private void UpdateArchiveStatus()
        {
            var lbl = _pageBody.Controls.Find("lblArchiveStatus", false).FirstOrDefault() as Label;
            if (lbl == null) return;
            string b = _backupFile != null ? AppLang.T("Fiscal.Archive.BackupOk", System.IO.Path.GetFileName(_backupFile)) : AppLang.T("Fiscal.Archive.BackupMissing");
            string r = _reportFile != null ? AppLang.T("Fiscal.Archive.ReportOk", System.IO.Path.GetFileName(_reportFile)) : AppLang.T("Fiscal.Archive.ReportOptional");
            lbl.Text = $"{b}\n{r}";
            lbl.ForeColor = _backupFile != null ? AppTheme.Success : AppTheme.Warning;
        }

        // ── صفحة 3: القلب ──
        private void BuildClosePage()
        {
            _pageBody.Controls.Clear();

            var lbl = new Label
            {
                Dock = DockStyle.Top,
                Height = 84,
                TextAlign = ContentAlignment.TopRight,
                ForeColor = AppTheme.TextPrimary,
                Text = AppLang.T("Fiscal.Close.Intro")
            };
            _pageBody.Controls.Add(lbl);

            var row = new Panel { Dock = DockStyle.Top, Height = 60 };
            var lblDate = new Label { Text = AppLang.T("Fiscal.Close.NewYearFrom"), Dock = DockStyle.Right, Width = 170, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _dtpNewYear = new DateTimePicker
            {
                Dock = DockStyle.Right,
                Width = 160,
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Value = AppClock.NowLocal.Date,
                RightToLeftLayout = false
            };
            row.Controls.Add(_dtpNewYear);
            row.Controls.Add(lblDate);
            _pageBody.Controls.Add(row);
        }

        private async Task CloseYearAsync()
        {
            var newStart = DateOnly.FromDateTime(_dtpNewYear.Value);

            if (_backupFile == null &&
                !ConfirmationDialog.Show(this, AppLang.T("Fiscal.Close.NoBackup.Title"),
                    AppLang.T("Fiscal.Close.NoBackup.Body"), dangerStyle: true))
                return;

            if (!ConfirmationDialog.Show(this, AppLang.T("Fiscal.Close.ConfirmTitle"),
                    AppLang.T("Fiscal.Close.ConfirmBody", newStart), dangerStyle: true))
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var fiscal = scope.ServiceProvider.GetRequiredService<clsFiscalYearService>();
                var r = await fiscal.CloseYearAsync(SessionState.CurrentUser?.UserId ?? 0, newStart);
                UiHelpers.ShowResult(this, r);
                if (r.IsSuccess)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Fiscal.Close.MsgFailed"));
            }
        }
    }
}
