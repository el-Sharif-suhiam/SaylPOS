using System.Text;
using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل منتج. مرر منتجًا موجودًا للتعديل الجزئي، أو null للإضافة.
    /// المرحلة 6: حقل الوحدة = وحدة الأساس (المرآة القانونية لـ Product.Unit)،
    /// وتحته محرر الوحدات البديلة (شبكة + إضافة/تعديل/تعطيل). الحفظ بعد المنتج:
    /// منتج جديد يُنشأ أولًا ثم تُحفظ وحداته، وفشل وحدة لا يُلغي المنتج المحفوظ.</summary>
    public class ProductEditForm : Form
    {
        private readonly DtoProduct? _existing;

        private TextBox _txtName = null!;
        private TextBox _txtBarcode = null!;
        private TextBox _txtUnit = null!;
        private ComboBox _cmbType = null!;
        private TextBox _txtBasePrice = null!;
        private TextBox _txtCostPrice = null!;
        private TextBox _txtReorder = null!;

        // ═══ المرحلة 6: محرر الوحدات البديلة ═══
        // الصفوف نموذج محلي (UnitRow) تُعرض في شبكة للقراءة فقط — التحرير كله
        // عبر زرّي إضافة/تعديل (نافذة صغيرة) فلا تتعارض لوحة الماسح مع خلايا
        // الشبكة. مسار التعديل يحمّل صفوف القاعدة (المعطلة ظاهرة أيضًا) ويقارن
        // عند الحفظ: صف بلا معرف = إضافة، صف مُغيَّر = تحديث، صف مُعطَّل =
        // تعطيل عبر الخدمة (لا حذف من الواجهة — الأمان التاريخي).
        private DataGridView _gridUnits = null!;
        private List<UnitRow> _unitRows = new();

        /// <summary>صف وحدة في المحرر: ProductUnitId = 0 يعني لم تُحفظ بعد
        /// (تُضاف بعد نجاح حفظ المنتج). أصل القيم (Orig*) يُحفظ عند التحميل
        /// ليُرسل تحديث فقط للمُغيَّر فعليًا. PendingDeactivate = طلب تعطيل
        /// في هذه الجلسة (يُنفَّذ عبر DeactivateProductUnitAsync عند الحفظ).</summary>
        private sealed class UnitRow
        {
            public int ProductUnitId;
            public string Name = "";
            public decimal Factor = 1m;
            public decimal? Price;
            public bool OrigActive;
            public bool InDb;
            public bool PendingDeactivate;
            public string OrigName = "";
            public decimal OrigFactor;
            public decimal? OrigPrice;
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 التقاط المسح — بسيط بلا تعقيد:
        //  1) القارئ "لوحة مفاتيح" — كل حرف يصل يُحفظ فورًا في _scanBuf
        //     ولا يصل لأي حقل (لا تسريب أصلًا).
        //  2) عند مرور 500ms دون حرف جديد = القارئ انتهى من إرسال الكود.
        //  3) عندها فقط يُملأ حقل الباركود بالقيمة كاملة — بلا Enter.
        //     الكتابة اليدوية البطيئة تُعرض في حقلها كالمعتاد.
        // ═══════════════════════════════════════════════════════════════
        private const int ManualConfirmMs = 80;  // مهلة التفريق: حرف واحد بطيء = كتابة يدوية
        private const int ReaderDoneMs = 500;    // فجوة تأكيد انتهاء القارئ

        private readonly StringBuilder _scanBuf = new();
        private Control? _heldControl;
        private readonly System.Windows.Forms.Timer _burstTimer = new() { Interval = ManualConfirmMs };
        private readonly System.Windows.Forms.Timer _doneTimer = new() { Interval = ReaderDoneMs };

        public ProductEditForm(DtoProduct? existing)
        {
            _existing = existing;
            BuildUi();
            KeyPreview = true;
            _burstTimer.Tick += (_, _) => ReplayManual();
            _doneTimer.Tick += (_, _) => CommitScan();
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (char.IsControl(e.KeyChar)) return;

            _scanBuf.Append(e.KeyChar);          // يُخزَّن كاملًا قبل أي شيء
            e.Handled = true;                    // لا يصل لأي حقل — لا تسريب
            if (_scanBuf.Length == 1)
            {
                _heldControl = ActiveControl;
                _burstTimer.Stop();
                _burstTimer.Start();
            }
            else
            {
                _burstTimer.Stop();              // نبضة قارئ مؤكدة — تابع الجمع
                _doneTimer.Stop();
                _doneTimer.Start();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Enter الماسح الإضافي لا يضغط «حفظ» أثناء تجميع الكود
            if (keyData == Keys.Enter && _scanBuf.Length > 0)
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ReplayManual()
        {
            _burstTimer.Stop();
            if (_scanBuf.Length >= 2) return;    // نبضة قارئ — يديرها CommitScan

            if (_heldControl is TextBox tb)
            {
                string s = _scanBuf.ToString();
                int pos = tb.SelectionStart;
                tb.Text = tb.Text.Insert(pos, s);
                tb.SelectionStart = pos + s.Length;
            }
            _scanBuf.Clear();
            _heldControl = null;
        }

        private void CommitScan()
        {
            _doneTimer.Stop();
            if (_scanBuf.Length >= 2)            // اكتمل الكود: القارئ انتهى
            {
                _txtBarcode.Text = _scanBuf.ToString();
                _txtBarcode.Focus();
            }
            _scanBuf.Clear();
            _heldControl = null;
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود — أسلوب «الإحداثيات اليدوية»
        // (الشرح الموسع في EmployeeEditForm — هنا نقاط هذه الشاشة):
        //  • كل عنصر بموضع مطلق: Bounds = Rectangle(يسار، أعلى، عرض، ارتفاع).
        //  • MakeField(التسمية، x، y، العرض، القيمة): مصنع موحد يرسم
        //    تسمية رمادية + مربع إدخال تحتها (y+22) — انظر أسفل الملف.
        //  • النافذة Form عادية (ليست DialogForm): FixedDialog = إطار
        //    حواري كلاسيكي قياسي بلا زوايا دائرية — نمط قديم لكنه صلب.
        //  • لتمييز الإضافة عن التعديل: العنوان + قيم الحقول الموجودة
        //    تُحقن من _existing (null = حقول فارغة).
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = _existing == null ? AppLang.T("Products.Edit.AddTitle") : AppLang.T("Products.Edit.EditTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 700);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            // ثوابت التخطيط: x=30 هامش الحقول، w=400 عرض الحقل الكامل —
            // غيّرها هنا فيتغير كل شيء (لا أرقام سحرية مبعثرة).
            int x = 30, w = 400;

            var lblTitle = new Label { Text = Text, Bounds = new Rectangle(0, 16, 460, 32), TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.FontHeading, ForeColor = AppTheme.TextPrimary };

            // ── الحقول الفردية كاملة العرض (400) — فوق بعضها ──
            // * = إلزامي (الاسم وسعر البيع يتحقق منهما SaveAsync).
            _txtName = MakeField(AppLang.T("Products.Edit.Name"), x, 58, w, _existing?.Name ?? "");
            _txtBarcode = MakeField(AppLang.T("Products.Edit.Barcode"), x, 116, w, _existing?.Barcode ?? "");
            _txtBarcode.MaxLength = 50;
            // ── صف مزدوج: وحدة الأساس (يمين 30..220) والنوع قائمة (يسار 240..430) ──
            // المرحلة 6: الحقل = وحدة الأساس صراحةً (مرآة Product.Unit وصف
            // ProductUnits الأساس) — إلزامي للمنتجات المخزنية (تحقق الحفظ).
            _txtUnit = MakeField(AppLang.T("Products.Edit.BaseUnit"), x, 174, 190, _existing?.Unit ?? "");
            _txtUnit.MaxLength = 20;

            // النوع: قائمة إنجليزية مباشرة (stock/service/manufactured) —
            // تُحفظ كما هي في قاعدة البيانات بلا ترجمة (عرض مبسّط).
            // SelectedIndex عند التعديل: فهرس القيمة المخزنة أو 0.
            _cmbType = new ComboBox
            {
                Bounds = new Rectangle(240, 196, 190, 36),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            _cmbType.Items.AddRange(new object[] { "stock", "service", "manufactured" });
            _cmbType.SelectedIndex = _existing == null ? 0 : Math.Max(0, _cmbType.Items.IndexOf(_existing.Type));

            var lblType = new Label { Text = AppLang.T("Products.Edit.Type"), Bounds = new Rectangle(240, 174, 190, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };

            // ── صف مزدوج ثانٍ: سعر البيع (يمين) والتكلفة (يسار) ──
            // "0.##": يظهر 5 لا 5.00 — ألطف لأسعار الوحدات الصحيحة.
            _txtBasePrice = MakeField(AppLang.T("Products.Edit.SellPrice"), x, 244, 190, _existing?.BasePrice.ToString("0.##") ?? "");
            _txtCostPrice = MakeField(AppLang.T("Products.Edit.Cost"), 240, 244, 190, _existing?.CostPrice.ToString("0.##") ?? "");
            _txtReorder = MakeField(AppLang.T("Products.Edit.Reorder"), 240, 302, 190, _existing?.ReorderLevel?.ToString() ?? "");

            // ── المرحلة 6: قسم الوحدات البديلة ──
            // شبكة للقراءة فقط (الاسم/المعامل/السعر/الحالة) + أزرار إضافة/
            // تعديل/تعطيل — التحرير عبر نافذة صغيرة، فالشبكة لا تلتقط لوحة
            // المفاتيح ولا يتعارض معها التقاط الماسح العام.
            var lblUnits = new Label { Text = AppLang.T("Products.Units.Title"), Bounds = new Rectangle(x, 366, 400, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var lblUnitsHint = new Label { Text = AppLang.T("Products.Units.Hint"), Bounds = new Rectangle(x, 388, 400, 18), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted, Font = new Font("Segoe UI", 8.5F) };

            _gridUnits = new DataGridView
            {
                Bounds = new Rectangle(x, 408, 400, 150),
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = AppTheme.CardBg,
                BorderStyle = BorderStyle.FixedSingle
            };
            UiHelpers.StyleGrid(_gridUnits);
            _gridUnits.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUName", HeaderText = AppLang.T("Products.Units.ColName"), ReadOnly = true, FillWeight = 34 });
            _gridUnits.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUFactor", HeaderText = AppLang.T("Products.Units.ColFactor"), ReadOnly = true, FillWeight = 22 });
            _gridUnits.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUPrice", HeaderText = AppLang.T("Products.Units.ColPrice"), ReadOnly = true, FillWeight = 24 });
            _gridUnits.Columns.Add(new DataGridViewTextBoxColumn { Name = "colUActive", HeaderText = AppLang.T("Products.Units.ColActive"), ReadOnly = true, FillWeight = 20 });

            var btnUnitAdd = AppTheme.MakeButton(AppLang.T("Products.Units.Add"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnUnitAdd.Bounds = new Rectangle(x, 566, 130, 36);
            btnUnitAdd.Click += (_, _) => AddUnitRow();

            var btnUnitEdit = AppTheme.MakeButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.SidebarHover);
            btnUnitEdit.Bounds = new Rectangle(170, 566, 120, 36);
            btnUnitEdit.Click += (_, _) => EditUnitRow();

            var btnUnitRemove = AppTheme.MakeButton(AppLang.T("Products.Units.Remove"), AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9));
            btnUnitRemove.Bounds = new Rectangle(300, 566, 130, 36);
            btnUnitRemove.Click += (_, _) => DeactivateUnitRow();

            // ── الأزرار: حفظ (يمين 30) أعرض، إلغاء (يسار 260) أضيق ──
            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(x, 614, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(260, 614, 170, 50);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtName, _txtBarcode, _txtUnit, _cmbType, lblType,
                _txtBasePrice, _txtCostPrice, _txtReorder,
                lblUnits, lblUnitsHint, _gridUnits, btnUnitAdd, btnUnitEdit, btnUnitRemove,
                btnOk, btnCancel
            });
            AcceptButton = btnOk;      // Enter = حفظ
            CancelButton = btnCancel;  // Esc = إلغاء
        }

        // 📖 MakeField: مصنع (تسمية + مربع) — التسمية عند (x,y) والمربع
        // تحتها (y+22). الفرق عن EmployeeEditForm.MakeField: هنا y معامل
        // صريح لا متغير متقدم — أبسط لكن تحسب الصفوف بنفسك.
        private TextBox MakeField(string caption, int x, int y, int width, string value)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, width, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, width, 36), Font = AppTheme.FontBody, Text = value };
            Controls.Add(lbl);
            Controls.Add(txt);
            return txt;
        }

        private async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("Common.Msg.NameRequired"));
                return;
            }

            if (!decimal.TryParse(_txtBasePrice.Text?.Trim(), out decimal basePrice) || basePrice < 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Msg.SellPriceInvalid"));
                return;
            }

            decimal.TryParse(_txtCostPrice.Text?.Trim(), out decimal costPrice);
            // حد الطلب كميات عشرية: تحليل عشري + تقريب إلى 3 منازل (AwayFromZero)
            decimal.TryParse(_txtReorder.Text?.Trim(), out decimal reorderRaw);
            decimal reorder = Math.Round(reorderRaw, 3, MidpointRounding.AwayFromZero);

            string selectedType = _cmbType.SelectedItem?.ToString() ?? "stock";

            // المرحلة 6: وحدة الأساس إلزامية للمنتجات المخزنية (نفس قاعدة الخدمة
            // — الواجهة تعرض الرسالة الودية قبل الوصول للخدمة).
            if (selectedType == "stock" && string.IsNullOrWhiteSpace(_txtUnit.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Msg.BaseUnitRequired"));
                _txtUnit.Focus();
                return;
            }

            var dto = new DtoProduct
            {
                ProductId = _existing?.ProductId ?? 0,
                Name = _txtName.Text.Trim(),
                Barcode = string.IsNullOrWhiteSpace(_txtBarcode.Text) ? null : _txtBarcode.Text.Trim(),
                Unit = string.IsNullOrWhiteSpace(_txtUnit.Text) ? null : _txtUnit.Text.Trim(),
                Type = selectedType,
                BasePrice = basePrice,
                CostPrice = costPrice,
                ReorderLevel = reorder > 0m ? reorder : null,
                IsActive = _existing?.IsActive ?? true
            };

            int actorUserId = SessionState.CurrentUser?.UserId ?? 0;
            if (actorUserId <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Purchase.Msg.NoUser"));
                return;
            }

            Result result;
            string? unitError = null;
            using (var scope = Program.Services.CreateScope())
            {
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                if (_existing == null)
                {
                    // المرحلة 6: المنتج يُنشأ أولًا ثم تُحفظ وحداته البديلة —
                    // فشل وحدة لا يُلغي المنتج المحفوظ (يُعرض خطأ الوحدة المُخفقة
                    // بالاسم ويبقى المنتج قائمًا — المستخدم يعيد المحاولة بالتعديل).
                    var added = await productService.AddNewProductAsync(dto, actorUserId: actorUserId);
                    result = added.IsSuccess ? Result.Ok(AppLang.T("Products.Msg.Saved")) : Result.Fail(added.Message);
                    if (added.IsSuccess)
                        unitError = await SaveUnitRowsAsync(productService, added.Data, actorUserId);
                }
                else
                {
                    result = await productService.UpdateProductAsync(dto, actorUserId: actorUserId);
                    if (result.IsSuccess)
                        unitError = await SaveUnitRowsAsync(productService, _existing.ProductId, actorUserId);
                }
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("Common.Msg.SaveFailed") : result.Message);
                return;
            }

            // المنتج حُفظ لكن سلسلة الوحدات توقفت عند أول فشل — نُظهر سببها
            // بالاسم ونُغلق بنجاح لأن حفظ المنتج نفسه تم (سياسة المرحلة 6).
            if (unitError != null)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.SavedWithUnitError", unitError));
                DialogResult = DialogResult.OK;
                return;
            }

            UiHelpers.ShowResult(this, result, AppLang.T("Products.Msg.Saved"));
            DialogResult = DialogResult.OK;
        }

        // ═══ المرحلة 6: عمليات محرر الوحدات ═══

        /// <summary>تطبيق صفوف المحرر على القاعدة بالتسلسل — يتوقف عند أول
        /// فشل ويعيد رسالة تُسمّي الوحدة المُخفقة: صف بلا معرف = إضافة،
        /// صف قاعدي مُغيَّر = تحديث، صف مُعلَّم للتعطيل = تعطيل. لا حذف من
        /// الواجهة (الأمان التاريخي — الحذف مسار خدمة يُرفض للمستخدَم).</summary>
        private async Task<string?> SaveUnitRowsAsync(clsProductService productService, int productId, int actorUserId)
        {
            foreach (var row in _unitRows)
            {
                if (row.ProductUnitId == 0)
                {
                    var added = await productService.AddProductUnitAsync(productId, row.Name, row.Factor, row.Price, actorUserId);
                    if (!added.IsSuccess)
                        return $"«{row.Name}»: {added.Message}";
                    row.ProductUnitId = added.Data;
                }
                else if (row.InDb && row.PendingDeactivate && row.OrigActive)
                {
                    var deact = await productService.DeactivateProductUnitAsync(row.ProductUnitId, actorUserId);
                    if (!deact.IsSuccess)
                        return $"«{row.Name}»: {deact.Message}";
                }
                else if (row.InDb && !row.PendingDeactivate &&
                         (row.Name != row.OrigName || row.Factor != row.OrigFactor || row.Price != row.OrigPrice))
                {
                    var upd = await productService.UpdateProductUnitAsync(row.ProductUnitId, row.Name, row.Factor, row.Price, actorUserId);
                    if (!upd.IsSuccess)
                        return $"«{row.Name}»: {upd.Message}";
                }
            }
            return null;
        }

        /// <summary>فحص تكرار اسم محلي سريع (الخدمة تبقى الحكم النهائي بقياد
        /// الفردية في القاعدة) — يعطي المستخدم الخطأ فورًا قبل الحفظ.</summary>
        private bool IsDuplicateName(string name, UnitRow? except) =>
            _unitRows.Any(r => !ReferenceEquals(r, except) && r.Name == name);

        /// <summary>إضافة وحدة بديلة جديدة (صف بلا معرف — يُحفظ بعد المنتج).</summary>
        private void AddUnitRow()
        {
            using var dlg = new UnitInputForm(null, 1m, null);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            if (IsDuplicateName(dlg.UnitName, null))
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.Duplicate", dlg.UnitName));
                return;
            }

            _unitRows.Add(new UnitRow
            {
                ProductUnitId = 0,
                Name = dlg.UnitName,
                Factor = dlg.Factor,
                Price = dlg.Price,
                OrigActive = true,
                InDb = false
            });
            ReloadGrid();
        }

        private void EditUnitRow()
        {
            int idx = _gridUnits.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _unitRows.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.PickRow"));
                return;
            }

            var row = _unitRows[idx];
            using var dlg = new UnitInputForm(row.Name, row.Factor, row.Price);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            if (IsDuplicateName(dlg.UnitName, row))
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.Duplicate", dlg.UnitName));
                return;
            }

            row.Name = dlg.UnitName;
            row.Factor = dlg.Factor;
            row.Price = dlg.Price;
            ReloadGrid();
        }

        /// <summary>التعطيل (لا الحذف): دورة الحياة الآمنة — الفواتير القديمة
        /// تحفظ لقطتها والمعاملات الجديدة ترفض الوحدة المعطلة خادميًا.
        /// التعليمة تُطبَّق عند الحفظ مع بقية العمليات (صف قاعدي يُعلَّم
        /// PendingDeactivate؛ صف منتج جديد بلا معرف يُزال محليًا فقط).</summary>
        private void DeactivateUnitRow()
        {
            int idx = _gridUnits.CurrentRow?.Index ?? -1;
            if (idx < 0 || idx >= _unitRows.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.PickRow"));
                return;
            }

            var row = _unitRows[idx];
            if (row.PendingDeactivate || (row.InDb && !row.OrigActive))
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.RowInactive"));
                return;
            }

            if (!ConfirmationDialog.Show(this, AppLang.T("Products.Units.Remove"),
                    AppLang.T("Products.Units.RemoveConfirm", row.Name)))
                return;

            if (row.InDb)
                row.PendingDeactivate = true; // يُنفَّذ عبر الخدمة عند الحفظ
            else
                _unitRows.Remove(row);        // لم يُحفظ أصلًا — إزالة محلية
            ReloadGrid();
        }

        /// <summary>تحميل وحدات المنتج للتعديل — المعطلة ظاهرة أيضًا
        /// (includeInactive: true) بأسعارها المخزنة الخام. فشل التحميل لا
        /// يمنع تحرير بقية حقول المنتج.</summary>
        private async Task LoadUnitRowsAsync()
        {
            if (_existing == null)
                return;

            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                var units = await productService.GetProductUnitsAsync(_existing.ProductId, includeInactive: true);
                _unitRows = units
                    .Where(u => !u.IsBase)
                    .Select(u => new UnitRow
                    {
                        ProductUnitId = u.ProductUnitId,
                        Name = u.Name,
                        Factor = u.FactorToBase,
                        Price = u.PricePerUnit,
                        OrigActive = u.IsActive,
                        InDb = true,
                        OrigName = u.Name,
                        OrigFactor = u.FactorToBase,
                        OrigPrice = u.PricePerUnit
                    })
                    .ToList();
                ReloadGrid();
            }
            catch
            {
                // فشل تحميل الوحدات لا يمنع تحرير المنتج نفسه
            }
        }

        private void ReloadGrid()
        {
            _gridUnits.Rows.Clear();
            foreach (var u in _unitRows)
            {
                bool active = u.InDb ? u.OrigActive && !u.PendingDeactivate : true;
                _gridUnits.Rows.Add(
                    u.Name,
                    u.Factor.ToString("0.######"),
                    u.Price?.ToString("0.##") ?? "-",
                    active ? AppLang.T("Common.Status.Active") : AppLang.T("Common.Status.Inactive"));
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = LoadUnitRowsAsync();
        }
    }

    /// <summary>نافذة إدخال/تحرير وحدة بديلة (المرحلة 6) — نموذج صغير مستقل:
    /// بلا KeyPreview فالكتابة الطبيعية تعمل فيه دائمًا (التقاط الماسح في
    /// النافذة الأم لا يمتد هنا). التحقق هنا ودي وسبق، والحكم النهائي
    /// بقواعد الخدمة (ValidateUnitFields + قيد الفردية في القاعدة).</summary>
    public class UnitInputForm : Form
    {
        private readonly TextBox _txtName = null!;
        private readonly TextBox _txtFactor = null!;
        private readonly TextBox _txtPrice = null!;

        public string UnitName { get; private set; } = "";
        public decimal Factor { get; private set; } = 1m;
        public decimal? Price { get; private set; }

        /// <summary>initial قيم سطر قائم للتحرير، أو null لإضافة وحدة جديدة
        /// (primitives لا UnitRow — النوع خاص بالنافذة الأم).</summary>
        internal UnitInputForm(string? initialName, decimal initialFactor, decimal? initialPrice)
        {
            Text = AppLang.T("Products.Units.DialogTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 250);
            MaximizeBox = false;
            MinimizeBox = false;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            BackColor = AppTheme.CardBg;

            int x = 30, w = 300;

            _txtName = MakeField(AppLang.T("Common.Label.Name"), x, 20, w, initialName ?? "");
            _txtName.MaxLength = 20;
            _txtFactor = MakeField(AppLang.T("Products.Units.ColFactor"), x, 78, w, initialFactor.ToString("0.######"));
            _txtPrice = MakeField(AppLang.T("Products.Units.ColPrice"), x, 136, w, initialPrice?.ToString("0.##") ?? "");

            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(x, 192, 150, 44);
            btnOk.Click += (_, _) => Confirm();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(190, 192, 140, 44);
            btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _txtName, _txtFactor, _txtPrice, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void Confirm()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.NameRequired"));
                return;
            }

            string name = _txtName.Text.Trim();
            if (name.Length > 20)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.NameTooLong"));
                return;
            }

            if (!decimal.TryParse(_txtFactor.Text?.Trim(), out decimal factor) || factor <= 0m)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.FactorInvalid"));
                return;
            }

            // دقة مفرطة: أكثر من 6 خانات تُرفض هنا كما في الخدمة (لا تقريب صامت)
            if (Math.Round(factor, 6) != factor)
            {
                UiHelpers.ShowError(this, AppLang.T("Products.Units.FactorPrecision"));
                return;
            }

            decimal? price = null;
            if (!string.IsNullOrWhiteSpace(_txtPrice.Text))
            {
                if (!decimal.TryParse(_txtPrice.Text?.Trim(), out decimal p) || p < 0m)
                {
                    UiHelpers.ShowError(this, AppLang.T("Products.Units.PriceInvalid"));
                    return;
                }
                price = p;
            }

            UnitName = name;
            Factor = factor;
            Price = price;
            DialogResult = DialogResult.OK;
        }

        private TextBox MakeField(string caption, int x, int y, int width, string value)
        {
            var lbl = new Label { Text = caption, Bounds = new Rectangle(x, y, width, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
            var txt = new TextBox { Bounds = new Rectangle(x, y + 22, width, 36), Font = AppTheme.FontBody, Text = value };
            Controls.Add(lbl);
            Controls.Add(txt);
            return txt;
        }
    }
}
