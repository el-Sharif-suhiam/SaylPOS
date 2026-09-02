using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
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
        private TextBox _txtWelcome = null!;
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

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود (لغير الملم بواجهات WinForms):
        //
        // هذا القسم مبني بأسلوب «الإحداثيات اليدوية» (راجع EmployeeEditForm
        // للشرح الموسع): كل عنصر يوضع بموضع محدد —
        // Bounds = Rectangle(يسار، أعلى، عرض، ارتفاع) بالبكسل.
        //
        // البنية (من أعلى لأسفل):
        //   ┌────────────────────────────────────┐
        //   │ شريط العنوان (Dock=Top, 70px)      │ ← عنوان القسم
        //   ├────────────────────────────────────┤
        //   │ الجسم (Dock=Fill) — حقول الإعدادات │ ← يمرر عموديًا لو صغرت النافذة
        //   └────────────────────────────────────┘
        //
        // ⚠️ ترتيب الإضافة معكوس كعادة الإرساء: Controls.Add(body) أولًا
        //    ثم Controls.Add(panel) — لأن آخر ما يُضاف يُرسّى أولًا (Top).
        //
        // داخل الجسم: المتغير y هو «مؤشر الصف الحالي» — يتقدم تلقائيًا
        // مع كل AddField (+64). الحقول اليدوية تحت تستعمل y+46/y+92/...
        // أيضًا — عدّل القفزات لتقريب/تبعيد العناصر.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── شريط العنوان العلوي ──
            // Dock=Top: يلتصق بأعلى القسم. Height=70 ارتفاعه الثابت.
            // Padding: هامش داخلي (يمين، أعلى، يسار، أسفل) — يبعد النص عن الحواف.
            // TextAlign=MiddleRight: النص لليمين (عربي RTL).
            var panel = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = AppTheme.CardBg, Padding = new Padding(20, 16, 20, 8) };
            var lblTitle = new Label { Text = "إعدادات النظام", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 14F, FontStyle.Bold), ForeColor = AppTheme.TextPrimary };
            panel.Controls.Add(lblTitle);

            // ── الجسم: كل الحقول هنا — Dock=Fill يملأ ما تبقى تحت الشريط ──
            // AutoScroll يفعّله DialogForm؟ لا — هنا UserControl: لو صغّرت
            // النافذة تظهر أشرطة تمرير تلقائيًا لأن العناصر بإحداثيات ثابتة.
            var body = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(40, 10, 40, 10) };

            // y يبدأ من 16 — أول صف للحقول. كل AddField يزيده تلقائيًا 64.
            int y = 16;
            // الحقول ذات التسمية الكاملة العرض (full: true) — انظر AddField أسفل الملف.
            _txtStoreName = AddField(body, "اسم المتجر (يظهر على الإيصال)", 40, ref y, full: true);
            _txtStorePhone = AddField(body, "هاتف المتجر", 40, ref y, full: true);
            _txtVat = AddField(body, "الرقم الضريبي", 40, ref y, full: true);
            _txtFooter = AddField(body, "رسالة أسفل الإيصال", 40, ref y, full: true);
            _txtWelcome = AddField(body, "رسالة شاشة الزبون (الشاشة الثانية)", 40, ref y, full: true);

            // ── نسبة الضريبة: تسمية يمين (40..300) + مربع رقم قصير يسار (320..440) ──
            // لاحظ النمط: التسمية TextAl을=MiddleRight لتلتصق بمربعها الأيسر.
            // Rectangle(40, y, 260, 22) = من اليسار 40، من الأعلى y، عرض 260، ارتفاع 22.
            var lblTax = new Label { Text = "نسبة الضريبة %", Bounds = new Rectangle(40, y, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtTax = new TextBox { Bounds = new Rectangle(320, y - 2, 120, 34), TextAlign = HorizontalAlignment.Center };
            body.Controls.Add(lblTax);
            body.Controls.Add(_txtTax);

            // ── مجلد النسخ الاحتياطي: y+46 يعني «46 بكسل تحت الحقل السابق» ──
            // القفزة أكبر من 64 العادية لأن سطر الضريبة أقصر (مربع واحد).
            var lblBackup = new Label { Text = "مجلد النسخ الاحتياطي", Bounds = new Rectangle(40, y + 46, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _txtBackupFolder = new TextBox { Bounds = new Rectangle(320, y + 44, 420, 34) };
            body.Controls.Add(lblBackup);
            body.Controls.Add(_txtBackupFolder);

            // ── قائمة الطابعات: تعبّأ من نظام ويندوز عند البناء ──
            // DropDownStyle=DropDownList: قائمة اختيار فقط — لا كتابة يدوية.
            // GetInstalledPrinters قد ترجع فارغة (خدمة الطباعة متوقفة) —
            // القائمة تبقى فارغة بلا انفجار.
            var lblPrinter = new Label { Text = "طابعة الإيصالات", Bounds = new Rectangle(40, y + 92, 260, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            _cmbPrinter = new ComboBox { Bounds = new Rectangle(320, y + 90, 420, 34), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            foreach (var p in ReceiptPrinter.GetInstalledPrinters())
                _cmbPrinter.Items.Add(p);
            body.Controls.Add(lblPrinter);
            body.Controls.Add(_cmbPrinter);

            // ── مفتاح الطباعة التلقائية: CheckBox ──
            // TextAlign=MiddleRight: النص يمين المربع — عربي.
            // Bounds عرضه 420 يغطي مساحة مربعي الحقول السابقين.
            _chkAutoPrint = new CheckBox { Text = "طباعة الإيصال تلقائيًا بعد كل عملية بيع", Bounds = new Rectangle(320, y + 134, 420, 28), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextPrimary };
            body.Controls.Add(_chkAutoPrint);

            // ── صف الأزرار الأول: حفظ (يمين) ونسخ احتياطي (يسار) ──
            // Height=48: حد اللمس الأدنى — لا تصغّره.
            // «يمين/يسار» هنا بالإحداثيات: 320 = يمين الشاشة (RTL)،
            // 540 = يساره — على عكس LTR المعروف.
            var btnSave = AppTheme.MakeButton("حفظ الإعدادات", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSave.Bounds = new Rectangle(320, y + 176, 200, 48);
            // Click += async (_,_) => ...: ربط حدث النقر بعملية غير متزامنة —
            // اللامدا تتلقى (مرسل الحدث، بياناته) ونتجاهلهما بـ _.
            btnSave.Click += async (_, _) => await SaveAsync();

            var btnBackup = AppTheme.MakeButton("نسخة احتياطية الآن", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            btnBackup.Bounds = new Rectangle(540, y + 176, 200, 48);
            btnBackup.Click += async (_, _) => await BackupNowAsync();

            // ── صف الأزرار الثاني: استعادة (يمين) + إغلاق سنة (يسار، أدمن فقط) ──
            var btnRestore = AppTheme.MakeButton("استعادة نسخة احتياطية", AppTheme.Warning, hoverColor: Color.FromArgb(180, 100, 5));
            btnRestore.Bounds = new Rectangle(320, y + 232, 200, 44);
            btnRestore.Click += async (_, _) => await RestoreNowAsync();

            // زر إغلاق السنة المالية — يُضاف فقط إذا كان المستخدم أدمن:
            // هذا نمط «زر شرطي» — البناء داخل if يخفيه عن بقية الأدوار.
            // إن أردت زرًا شرطيًا آخر انسخ البنية: if (الشرط) { إنشاء + Add }.
            if (SessionState.IsAdmin)
            {
                var btnFiscal = AppTheme.MakeButton("إغلاق السنة المالية", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover, height: 44);
                btnFiscal.Bounds = new Rectangle(540, y + 232, 200, 44);
                // هنا الحدث متزامن (بلا async): ShowDialog يوقف هذه الشاشة
                // حتى تُغلق نافذة الإغلاق — وهذا مقصود (نافذة حوارية).
                btnFiscal.Click += (_, _) =>
                {
                    using var form = new FiscalYearCloseForm();
                    if (form.ShowDialog(this) == DialogResult.OK)
                        _lblStatus.Text = "";
                };
                body.Controls.Add(btnFiscal);
            }

            // ── شريط الحالة: نص أخضر/أحمر أسفل الأزرار — يُحدَّث من SaveAsync ──
            // لا Bounds للـ BackColor — Label شفاف فوق خلفية الجسم.
            _lblStatus = new Label { Text = "", Bounds = new Rectangle(540, y + 240, 420, 26), ForeColor = AppTheme.Success };

            body.Controls.Add(btnSave);
            body.Controls.Add(btnBackup);
            body.Controls.Add(btnRestore);
            body.Controls.Add(_lblStatus);

            // ترتيب الإرساء معكوس: body أولًا (Fill) ثم panel (Top) —
            // آخر المضاف يُرسّى أولًا، فالشريط يأخذ حافة Top وbody يملأ الباقي.
            Controls.Add(body);
            Controls.Add(panel);
        }

        // 📖 AddField: مصنع الحقول النصية لهذا القسم (نفس فكرة MakeField
        // في EmployeeEditForm): ينشئ تسمية رمادية + مربع إدخال تحتها،
        // يضيفهما إلى parent، ويزحزح y بمقدار 64 للصف التالي.
        // ref y: يُمرَّر بالمرجع — التعديل يظهر عند المستدعي.
        // full: محجوز لتمييز الحقول كاملة العرض (المربع 540px موحّد هنا).
        private TextBox AddField(Panel parent, string caption, int x, ref int y, bool full)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, 540, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, 540, 34) };
            parent.Controls.Add(lbl);
            parent.Controls.Add(txt);
            y += 64; // ارتفاع التسمية (22) + المربع (34) + فاصل (8)
            return txt;
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();

                _txtStoreName.Text = await settings.GetAsync(clsSettingsService.KeyStoreName, "سيل");
                _txtStorePhone.Text = await settings.GetAsync(clsSettingsService.KeyStorePhone);
                _txtVat.Text = await settings.GetAsync(clsSettingsService.KeyVatNumber);
                _txtFooter.Text = await settings.GetAsync(clsSettingsService.KeyReceiptFooter);
                _txtWelcome.Text = await settings.GetAsync(clsSettingsService.KeyWelcomeMessage, "شكرًا لزيارتكم");
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
                await settings.SetAsync(clsSettingsService.KeyWelcomeMessage, _txtWelcome.Text?.Trim() ?? "");
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
