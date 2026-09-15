using Common.DTOs;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>إضافة/تعديل منتج. مرر منتجًا موجودًا للتعديل الجزئي، أو null للإضافة.</summary>
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

        public ProductEditForm(DtoProduct? existing)
        {
            _existing = existing;
            BuildUi();
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
            ClientSize = new Size(460, 470);
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
            // ── صف مزدوج: الوحدة (يمين 30..220) والنوع قائمة (يسار 240..430) ──
            _txtUnit = MakeField(AppLang.T("Products.Edit.Unit"), x, 174, 190, _existing?.Unit ?? "");

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

            // ── الأزرار: حفظ (يمين 30) أعرض، إلغاء (يسار 260) أضيق ──
            var btnOk = AppTheme.MakeButton(AppLang.T("Common.Button.Save"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnOk.Bounds = new Rectangle(x, 360, 220, 50);
            btnOk.Click += async (_, _) => await SaveAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Cancel"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnCancel.Bounds = new Rectangle(260, 360, 170, 50);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إلغاء

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtName, _txtBarcode, _txtUnit, _cmbType, lblType,
                _txtBasePrice, _txtCostPrice, _txtReorder, btnOk, btnCancel
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
            int.TryParse(_txtReorder.Text?.Trim(), out int reorder);

            var dto = new DtoProduct
            {
                ProductId = _existing?.ProductId ?? 0,
                Name = _txtName.Text.Trim(),
                Barcode = string.IsNullOrWhiteSpace(_txtBarcode.Text) ? null : _txtBarcode.Text.Trim(),
                Unit = string.IsNullOrWhiteSpace(_txtUnit.Text) ? null : _txtUnit.Text.Trim(),
                Type = _cmbType.SelectedItem?.ToString() ?? "stock",
                BasePrice = basePrice,
                CostPrice = costPrice,
                ReorderLevel = reorder > 0 ? reorder : null,
                IsActive = _existing?.IsActive ?? true
            };

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                if (_existing == null)
                {
                    var added = await productService.AddNewProductAsync(dto);
                    result = added.IsSuccess ? Result.Ok(AppLang.T("Products.Msg.Saved")) : Result.Fail(added.Message);
                }
                else
                {
                    result = await productService.UpdateProductAsync(dto);
                }
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("Common.Msg.SaveFailed") : result.Message);
                return;
            }

            UiHelpers.ShowResult(this, result, AppLang.T("Products.Msg.Saved"));
            DialogResult = DialogResult.OK;
        }
    }
}
