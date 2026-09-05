using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل موظف: بيانات + نوع الراتب (بالساعة/يومي/شهري) + المعدل.</summary>
    public class EmployeeEditForm : DialogForm
    {
        private readonly DtoEmployee? _existing;

        private TextBox _txtName = null!;
        private TextBox _txtPhone = null!;
        private TextBox _txtEmail = null!;
        private ComboBox _cmbSalaryType = null!;
        private TextBox _txtSalary = null!;
        private TextBox _txtPosition = null!;
        private TextBox _txtDepartment = null!;
        private DateTimePicker _dtpHireDate = null!;
        private DateTimePicker _dtpResignDate = null!;
        private Label _lblResign = null!;
        private ComboBox _cmbStatus = null!;
        private TextBox _txtAddress = null!;
        private Button _btnSave = null!;

        public EmployeeEditForm(DtoEmployee? existing = null)
        {
            _existing = existing;
            BuildUi();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود (لغير الملم بواجهات WinForms):
        //
        // هذه النافذة تُبنى بأسلوب «الإحداثيات اليدوية»: كل عنصر يوضع
        // بموضع محدد (Bounds = يسار، أعلى، عرض، ارتفاع بالبكسل).
        //
        // كيف تغيّر أي شيء بأمان:
        // • نص عنصر: غيّر ما بين علامتي التنصيص "..." فقط.
        // • حجم/موضع عنصر: عدّل أرقام Bounds. مثال: Rectangle(30, y+24, 240, 34)
        //   يعني: يبدأ من 30 بكسل من اليسار، y+24 من الأعلى، عرضه 240 وارتفاعه 34.
        // • المسافة بين الحقول: المتغير y يتقدم تلقائيًا (y += 64 داخل
        //   MakeField) — زيادة 64 تعني حقلاً أعلى/أسفل أبعد.
        // • ترتيب الحقول: تسلسل أسطر MakeField(...) هو ترتيب ظهورها.
        // • الألوان: كلها من AppTheme (لوحة موحدة) — لا تكتب لونًا يدويًا.
        //
        // قواعد النافذة (لا تحتاج تعديلها غالبًا):
        // RightToLeft.Yes = كل شيء يبدأ من اليمين (عربي).
        // ShowInTaskbar=false = نافذة حوارية (لا تظهر بشريط المهام).
        // CenterParent = تفتح وسط النافذة الأم.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── عنوان النافذة وحجمها ──
            // Text: عنوان شريط النافذة — يتغير حسب (إضافة/تعديل).
            // ClientSize: حجم منطقة المحتوى (بدون إطار النافذة) بالبكسل.
            //             نافذة أضيق؟ صغّر العرض 560. أطول؟ كبّر 640.
            // BackColor: خلفية النافذة — AppTheme.CardBg هي خلفية البطاقات
            //            البيضاء الموحدة في كل النظام.
            Text = _existing == null ? "إضافة موظف" : "تعديل موظف";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 640);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ── العنوان الكبير أعلى النافذة ──
            // Label نصي فقط (غير قابل للنقر).
            // Bounds: من اليسار 0 ومن الأعلى 14 — عرض كامل 560 وارتفاع 30.
            // TextAlign=MiddleCenter: يوسّط النص أفقيًا وعموديًا.
            // لتغيير حجم خط العنوان: عدّل AppTheme.FontHeading في AppTheme.cs
            // (يتغير في كل النظام) — أو استبدله هنا بخط مخصص لهذه الشاشة فقط.
            var lblTitle = new Label
            {
                Text = Text,
                Bounds = new Rectangle(0, 14, 560, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            // ── الحقول النصية المتتالية (الاسم/الجوال/البريد...) ──
            // y مؤشر يتقدم تلقائيًا لكل حقل جديد — لا تحسبه يدويًا.
            // MakeField(التسمية، ...) ينشئ: تسمية رمادية فوق مربع إدخال.
            // لإضافة حقل جديد: انسخ سطرًا وغيّر التسمية، وسيتوضع تلقائيًا.
            // (تذكير: الحقل الجديد يجب أن يُعلن أيضًا أعلى الملف مع بقية الحقول
            //  ويُقرأ/يُحفظ داخل SaveAsync ليتصل بقاعدة البيانات.)
            int y = 56;
            _txtName = MakeField("الاسم *", ref y, out _);
            _txtPhone = MakeField("الجوال", ref y, out _);
            _txtEmail = MakeField("البريد الإلكتروني", ref y, out _);

            // ── نوع الراتب + المعدل: صف مزدوج (يمين يسار) ──
            // وضعنا عنصرين في نفس الصف: التسمية+القائمة يمينًا (390..530)
            // والتسمية+الرقم يسارًا (30..270). المسافة الوسطية فاصلة.
            // Bounds يسار العنصر الأيمن = 390 (أي أن عرضه من 390 إلى 530).
            var lblType = new Label
            {
                Text = "نوع الراتب *",
                Bounds = new Rectangle(300, y, 230, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            var lblSalary = new Label
            {
                Text = "المعدل *",
                Bounds = new Rectangle(30, y, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            // القائمة المنسدلة: Items = الخيارات، SelectedIndex=0 يعني الافتراضي
            // أول خيار. أضف/احذف خيارًا من AddRange وسيظهر تلقائيًا.
            // ⚠️ إذا أضفت خيارًا جديدًا: يجب تحديث الترتيب في LoadExisting/SaveAsync
            //    (الترجمة "شهري/يومي/بالساعة" ↔ قاعدة البيانات).
            _cmbSalaryType = new ComboBox
            {
                Bounds = new Rectangle(390, y + 24, 140, 34),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _cmbSalaryType.Items.AddRange(new object[] { "شهري", "يومي", "بالساعة" });
            _cmbSalaryType.SelectedIndex = 0;
            // SelectedIndexChanged: يُنفّذ تلقائيًا عند تغيير اختيار المستخدم —
            // هنا نستدعي UpdateSalaryLabel لتغيير نص "المعدل (شهري/يومي...)".
            _cmbSalaryType.SelectedIndexChanged += (_, _) => UpdateSalaryLabel();

            _txtSalary = new TextBox
            {
                Bounds = new Rectangle(30, y + 24, 240, 34),
                TextAlign = HorizontalAlignment.Center
            };
            _txtSalary.Text = "0";
            y += 64; // انتهى الصف المزدوج — تقدّم لموضع الصف التالي

            _txtPosition = MakeField("المسمى الوظيفي *", ref y, out _);
            _txtDepartment = MakeField("القسم", ref y, out _);

            var lblHire = new Label
            {
                Text = "تاريخ التعيين",
                Bounds = new Rectangle(300, y, 230, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            var lblStatus = new Label
            {
                Text = "الحالة",
                Bounds = new Rectangle(30, y, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _dtpHireDate = new DateTimePicker
            {
                Bounds = new Rectangle(390, y + 24, 140, 34),
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                RightToLeftLayout = false
            };
            _cmbStatus = new ComboBox
            {
                Bounds = new Rectangle(30, y + 24, 240, 34),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _cmbStatus.Items.AddRange(new object[] { "نشط", "في إجازة", "مستقيل" });
            _cmbStatus.SelectedIndex = 0;
            y += 64;

            // ── تاريخ الاستقالة (يظهر/يختفي حسب الحالة) ──
            // نمط «الحقل المشروط»: Visible=false عند البناء ثم يظهر فقط عند
            // اختيار «مستقيل». لجعل حقل ظاهرًا دائمًا: احذف سطري Visible=false.
            // DateTimePicker: منتقي تاريخ — CustomFormat="yyyy-MM-dd" يضبط
            // صيغة العرض (مثال "dd/MM/yyyy" لو أردت اليوم-الشهر-السنة).
            // RightToLeftLayout=false: التقويم الداخلي يبقى منطقي القراءة.
            // تاريخ الاستقالة — يظهر فقط عند «مستقيل» (يقطع حساب الراتب الشهري عنده)
            var lblResign = new Label
            {
                Text = "تاريخ الاستقالة",
                Bounds = new Rectangle(300, y, 230, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _dtpResignDate = new DateTimePicker
            {
                Bounds = new Rectangle(390, y + 24, 140, 34),
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                RightToLeftLayout = false,
                Visible = false
            };
            lblResign.Visible = false;
            _lblResign = lblResign;
            _cmbStatus.SelectedIndexChanged += (_, _) =>
            {
                bool resigned = _cmbStatus.SelectedIndex == 2;
                lblResign.Visible = resigned;
                _dtpResignDate.Visible = resigned;
                if (resigned && _existing?.ResignDate == null)
                    _dtpResignDate.Value = AppClock.NowLocal.Date; // افتراضي: اليوم — قابل للتعديل
            };
            y += 8; // الحقل مخفي عادة — لا يستهلك الصف الكامل

            _txtAddress = MakeField("العنوان", ref y, out _);

            // ── أزرار الحفظ والإغلاق (أسفل النافذة) ──
            // MakeButton(النص، لون الخلفية): زر موحّد المظهر من AppTheme.
            //   • AppTheme.Primary = الكحلي (زر الإجراء الأساسي).
            //   • AppTheme.Bg + foreColor=TextPrimary = زر ثانوي أبيض.
            // Bounds: (يسار 190، أعلى y+16، عرض 180، ارتفاع 48) — الارتفاع 48
            //         هو الحد الأدنى المريح للمس (لا تصغّره).
            // Click += ...: ما يحدث عند الضغط — هنا استدعاء دالة الحفظ.
            // لتحريك الزرين: عدّل X الأول (190). عرض أكبر؟ عدّل 180.
            _btnSave = AppTheme.MakeButton("حفظ", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnSave.Bounds = new Rectangle(190, y + 16, 180, 48);
            _btnSave.Click += async (_, _) => await SaveAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(30, y + 16, 140, 48);
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle,
                _txtName, _txtPhone, _txtEmail,
                lblType, _cmbSalaryType, lblSalary, _txtSalary,
                _txtPosition, _txtDepartment,
                lblHire, _dtpHireDate, lblStatus, _cmbStatus,
                _txtAddress,
                _btnSave, btnClose
            });

            // التسميات المرفقة تُضاف من MakeField
            CancelButton = btnClose;

            if (_existing != null)
                LoadExisting();
            else
                UpdateSalaryLabel();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 MakeField: مصنع الحقول النصية الموحّد لهذه الشاشة.
        // ينشئ: تسمية رمادية (النص = caption) + مربع إدخال تحتها.
        // بعد الإنشاء يتقدم y بمقدار 64 بكسل جاهزًا للحقل التالي.
        // لتعديل عرض الحقول: غيّر 500 (عرض كل من التسمية والمربع).
        // لزيادة التباعد بين الحقول: غيّر y += 64 إلى رقم أكبر.
        // انتبه: التسمية تُضاف للنافذة هنا داخل الدالة، أما المربع
        // فيُضاف عبر Controls.AddRange في BuildUi — لا تضفه مرتين.
        // ═══════════════════════════════════════════════════════════════
        private TextBox MakeField(string caption, ref int y, out Label label)
        {
            label = new Label
            {
                Text = caption,
                Bounds = new Rectangle(30, y, 500, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            var txt = new TextBox { Bounds = new Rectangle(30, y + 24, 500, 34) };
            Controls.Add(label);
            y += 64;
            return txt;
        }

        private void UpdateSalaryLabel()
        {
            // تحديث نص الملصق وفق النوع — المرونة بلا رسائل حوارية
            foreach (Control c in Controls)
            {
                if (c is Label l && (l.Text == "المعدل *" || l.Text.StartsWith("المعدل (")))
                {
                    l.Text = _cmbSalaryType.SelectedIndex switch
                    {
                        1 => "المعدل (يومي)",
                        2 => "المعدل (بالساعة)",
                        _ => "المعدل (شهري)"
                    };
                }
            }
        }

        private void LoadExisting()
        {
            _txtName.Text = _existing!.Name;
            _txtPhone.Text = _existing.Phone ?? "";
            _txtEmail.Text = _existing.Email ?? "";
            _cmbSalaryType.SelectedIndex = _existing.SalaryType switch
            {
                enSalaryType.daily => 1,
                enSalaryType.hourly => 2,
                _ => 0
            };
            _txtSalary.Text = (_existing.Salary ?? 0).ToString("0.##");
            _txtPosition.Text = _existing.Position;
            _txtDepartment.Text = _existing.Department ?? "";
            if (_existing.HireDate.HasValue)
                _dtpHireDate.Value = _existing.HireDate.Value.ToDateTime(TimeOnly.MinValue);
            _cmbStatus.SelectedIndex = _existing.Status switch
            {
                "on_leave" => 1,
                "resigned" => 2,
                _ => 0
            };
            if (_existing.ResignDate.HasValue)
            {
                _dtpResignDate.Value = _existing.ResignDate.Value.ToDateTime(TimeOnly.MinValue);
                // إظهار تاريخ الاستقالة إن كانت الحالة مستقيلًا (SelectedIndexChanged يظهره تلقائيًا)
            }
            _txtAddress.Text = _existing.Address ?? "";
            UpdateSalaryLabel();
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, "الاسم مطلوب.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_txtPosition.Text))
            {
                UiHelpers.ShowError(this, "المسمى الوظيفي مطلوب.");
                return;
            }

            if (!decimal.TryParse(_txtSalary.Text?.Trim(), out decimal salary) || salary < 0)
            {
                UiHelpers.ShowError(this, "أدخل معدل راتب صحيحًا (صفر أو أكثر).");
                return;
            }

            string status = _cmbStatus.SelectedIndex switch
            {
                1 => "on_leave",
                2 => "resigned",
                _ => "active"
            };

            var dto = new DtoEmployee
            {
                EmployeeId = _existing?.EmployeeId ?? 0,
                Name = _txtName.Text.Trim(),
                Phone = string.IsNullOrWhiteSpace(_txtPhone.Text) ? null : _txtPhone.Text.Trim(),
                Email = string.IsNullOrWhiteSpace(_txtEmail.Text) ? null : _txtEmail.Text.Trim(),
                Salary = salary,
                SalaryType = _cmbSalaryType.SelectedIndex switch
                {
                    1 => enSalaryType.daily,
                    2 => enSalaryType.hourly,
                    _ => enSalaryType.monthly
                },
                HireDate = DateOnly.FromDateTime(_dtpHireDate.Value),
                ResignDate = status == "resigned" ? DateOnly.FromDateTime(_dtpResignDate.Value) : null,
                Address = string.IsNullOrWhiteSpace(_txtAddress.Text) ? null : _txtAddress.Text.Trim(),
                Status = status,
                Position = _txtPosition.Text.Trim(),
                Department = string.IsNullOrWhiteSpace(_txtDepartment.Text) ? null : _txtDepartment.Text.Trim()
            };

            _btnSave.Enabled = false;
            try
            {
                Result result;
                using (var scope = Program.Services.CreateScope())
                {
                    var service = scope.ServiceProvider.GetRequiredService<clsEmployeeService>();
                    result = _existing == null
                        ? await service.AddNewEmployeeAsync(dto)
                        : await service.UpdateEmployeeAsync(dto);
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
                UiHelpers.ShowError(this, "تعذر الاتصال بقاعدة البيانات.");
            }
            finally
            {
                _btnSave.Enabled = true;
            }
        }
    }
}
