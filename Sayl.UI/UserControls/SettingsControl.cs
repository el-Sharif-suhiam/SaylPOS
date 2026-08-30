using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>إعدادات النظام (للمدير): بيانات المتجر، الضريبة، الطباعة، والنسخ الاحتياطي.</summary>
    internal class SettingsControl : UserControl
    {
        private TextBox _txtStoreName = null!;
        private TextBox _txtStorePhone = null!;
        private TextBox _txtVat = null!;
        private TextBox _txtFooter = null!;
        private TextBox _txtTax = null!;
        private TextBox _txtBackupFolder = null!;
        private ComboBox _cmbPrinter = null!;
        private CheckBox _chkAutoPrint = null!;
        private Label _lblStatus = null!;

        public SettingsControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
            BuildUi();
            Load += async (_, _) => await LoadSettingsAsync();
        }

        private void BuildUi()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = AppTheme.CardBg, Padding = new Padding(20, 16, 20, 8) };
            var lblTitle = new Label { Text = "إعدادات النظام", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 14F, FontStyle.Bold), ForeColor = AppTheme.TextPrimary };
            panel.Controls.Add(lblTitle);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(40, 10, 40, 10) };

            int y = 16;
            _txtStoreName = AddField(body, "اسم المتجر (يظهر على الإيصال)", 40, ref y, full: true);
            _txtStorePhone = AddField(body, "هاتف المتجر", 40, ref y, full: true);
            _txtVat = AddField(body, "الرقم الضريبي", 40, ref y, full: true);
            _txtFooter = AddField(body, "رسالة أسفل الإيصال", 40, ref y, full: true);

            var lblTax = new Label { Text = "نسبة الضريبة %", Bounds = new Rectangle(40, y, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtTax = new TextBox { Bounds = new Rectangle(320, y - 2, 120, 34), TextAlign = HorizontalAlignment.Center };
            body.Controls.Add(lblTax);
            body.Controls.Add(_txtTax);

            var lblBackup = new Label { Text = "مجلد النسخ الاحتياطي", Bounds = new Rectangle(40, y + 46, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtBackupFolder = new TextBox { Bounds = new Rectangle(320, y + 44, 420, 34) };
            body.Controls.Add(lblBackup);
            body.Controls.Add(_txtBackupFolder);

            var lblPrinter = new Label { Text = "طابعة الإيصالات", Bounds = new Rectangle(40, y + 92, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbPrinter = new ComboBox { Bounds = new Rectangle(320, y + 90, 420, 34), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var p in ReceiptPrinter.GetInstalledPrinters())
                _cmbPrinter.Items.Add(p);
            body.Controls.Add(lblPrinter);
            body.Controls.Add(_cmbPrinter);

            _chkAutoPrint = new CheckBox { Text = "طباعة الإيصال تلقائيًا بعد كل عملية بيع", Bounds = new Rectangle(320, y + 134, 420, 28), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextPrimary };
            body.Controls.Add(_chkAutoPrint);

            var btnSave = AppTheme.MakeButton("حفظ الإعدادات", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(320, y + 176, 200, 48);
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnBackup = AppTheme.MakeButton("نسخة احتياطية الآن", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            btnBackup.Bounds = new Rectangle(540, y + 176, 200, 48);
            btnBackup.Click += async (_, _) => await BackupNowAsync();

            var btnRestore = AppTheme.MakeButton("استعادة نسخة احتياطية", AppTheme.Warning, hoverColor: Color.FromArgb(180, 100, 5));
            btnRestore.Bounds = new Rectangle(320, y + 232, 200, 44);
            btnRestore.Click += async (_, _) => await RestoreNowAsync();

            _lblStatus = new Label { Text = "", Bounds = new Rectangle(540, y + 240, 420, 26), ForeColor = AppTheme.Success };

            body.Controls.Add(btnSave);
            body.Controls.Add(btnBackup);
            body.Controls.Add(btnRestore);
            body.Controls.Add(_lblStatus);

            Controls.Add(body);
            Controls.Add(panel);
        }

        private TextBox AddField(Panel parent, string caption, int x, ref int y, bool full)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, 540, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, 540, 34) };
            parent.Controls.Add(lbl);
            parent.Controls.Add(txt);
            y += 64;
            return txt;
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();

                _txtStoreName.Text = await settings.GetAsync(clsSettingsService.KeyStoreName);
                _txtStorePhone.Text = await settings.GetAsync(clsSettingsService.KeyStorePhone);
                _txtVat.Text = await settings.GetAsync(clsSettingsService.KeyVatNumber);
                _txtFooter.Text = await settings.GetAsync(clsSettingsService.KeyReceiptFooter);
                decimal tax = await settings.GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0m);
                _txtTax.Text = tax > 0 ? tax.ToString("0.##") : "";
                _txtBackupFolder.Text = await settings.GetAsync(clsSettingsService.KeyBackupFolder,
                    clsBackupService.DefaultBackupFolder);
                _chkAutoPrint.Checked = await settings.GetBoolAsync(clsSettingsService.KeyAutoPrintReceipt, false);
                string printer = await settings.GetAsync(clsSettingsService.KeyReceiptPrinter);
                int idx = _cmbPrinter.Items.IndexOf(printer);
                _cmbPrinter.SelectedIndex = idx >= 0 ? idx : -1;
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الإعدادات.");
            }
        }

        private async Task SaveAsync()
        {
            if (!decimal.TryParse(_txtTax.Text?.Trim(), out decimal tax) || tax < 0 || tax > 100)
            {
                UiHelpers.ShowError(this, "أدخل نسبة ضريبة صحيحة (0-100).");
                return;
            }

            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();

                await settings.SetAsync(clsSettingsService.KeyStoreName, _txtStoreName.Text?.Trim() ?? "");
                await settings.SetAsync(clsSettingsService.KeyStorePhone, _txtStorePhone.Text?.Trim() ?? "");
                await settings.SetAsync(clsSettingsService.KeyVatNumber, _txtVat.Text?.Trim() ?? "");
                await settings.SetAsync(clsSettingsService.KeyReceiptFooter, _txtFooter.Text?.Trim() ?? "");
                await settings.SetAsync(clsSettingsService.KeyTaxPercent, tax.ToString("0.##"));
                await settings.SetAsync(clsSettingsService.KeyBackupFolder, _txtBackupFolder.Text?.Trim() ?? "");
                await settings.SetAsync(clsSettingsService.KeyAutoPrintReceipt, _chkAutoPrint.Checked.ToString());
                await settings.SetAsync(clsSettingsService.KeyReceiptPrinter, _cmbPrinter.SelectedItem?.ToString() ?? "");

                _lblStatus.ForeColor = AppTheme.Success;
                _lblStatus.Text = "تم حفظ الإعدادات بنجاح.";
            }
            catch
            {
                _lblStatus.ForeColor = AppTheme.Danger;
                _lblStatus.Text = "فشل حفظ الإعدادات.";
            }
        }

        private async Task BackupNowAsync()
        {
            try
            {
                string folder = _txtBackupFolder.Text?.Trim() ?? clsBackupService.DefaultBackupFolder;

                Result<string> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var backupService = scope.ServiceProvider.GetRequiredService<clsBackupService>();
                    result = await backupService.BackupAsync(folder);

                    if (result.IsSuccess)
                    {
                        var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                        await settings.SetAsync(clsSettingsService.KeyLastBackupDate, DateTime.Today.ToString("yyyy-MM-dd"));
                    }
                }

                _lblStatus.ForeColor = result.IsSuccess ? AppTheme.Success : AppTheme.Danger;
                _lblStatus.Text = result.IsSuccess ? $"تم النسخ: {Path.GetFileName(result.Data)}" : result.Message;
            }
            catch
            {
                _lblStatus.ForeColor = AppTheme.Danger;
                _lblStatus.Text = "فشل النسخ الاحتياطي.";
            }
        }

        private async Task RestoreNowAsync()
        {
            string folder = _txtBackupFolder.Text?.Trim() ?? clsBackupService.DefaultBackupFolder;

            using var dlg = new OpenFileDialog
            {
                Title = "اختر ملف النسخة الاحتياطية",
                Filter = "نسخة احتياطية (*.bak)|*.bak|كل الملفات (*.*)|*.*",
                InitialDirectory = Directory.Exists(folder) ? folder : null
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            var confirm = MessageBox.Show(this,
                "تحذير: الاستعادة ستستبدل جميع البيانات الحالية " +
                "ببيانات النسخة المختارة، ولن يمكن التراجع.\n\n" +
                "تأكد من:\n" +
                "- إغلاق جميع جلسات البيع على كل الأجهزة\n" +
                "- أخذ نسخة احتياطية جديدة للبيانات الحالية أولًا\n\n" +
                "هل تريد المتابعة بالاستعادة؟",
                "تأكيد الاستعادة", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (confirm != DialogResult.Yes)
                return;

            try
            {
                Result<string> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var backupService = scope.ServiceProvider.GetRequiredService<clsBackupService>();
                    result = await backupService.RestoreAsync(dlg.FileName);
                }

                if (!result.IsSuccess)
                {
                    _lblStatus.ForeColor = AppTheme.Danger;
                    _lblStatus.Text = result.Message;
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                var restart = MessageBox.Show(this,
                    "تمت استعادة النسخة الاحتياطية بنجاح.\n\n" +
                    "يُنصح بإعادة تشغيل البرنامج الآن لضمان قراءة أحدث البيانات.\n\n" +
                    "هل تريد إعادة تشغيل البرنامج الآن؟",
                    "تمت الاستعادة", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

                if (restart == DialogResult.Yes)
                    Application.Restart();
                else
                {
                    _lblStatus.ForeColor = AppTheme.Success;
                    _lblStatus.Text = $"تمت الاستعادة: {Path.GetFileName(result.Data)}";
                }
            }
            catch
            {
                _lblStatus.ForeColor = AppTheme.Danger;
                _lblStatus.Text = "فشلت الاستعادة.";
            }
        }
    }
}
