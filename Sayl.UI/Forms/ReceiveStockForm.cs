using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>استلام مخزون جديد: منتج + رقم دفعة + كمية + تاريخ انتهاء (اختياري).</summary>
    public class ReceiveStockForm : DialogForm
    {
        private TextBox _txtSearch = null!;
        private ListBox _lstProducts = null!;
        private TextBox _txtBatch = null!;
        private TextBox _txtQty = null!;
        private DateTimePicker _dtpExpiry = null!;
        private CheckBox _chkHasExpiry = null!;

        private List<Common.DTOs.DtoProduct> _products = new();

        public ReceiveStockForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadProductsAsync("");
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء بالكود (لغير الملم بواجهات WinForms):
        // أسلوب «الإحداثيات اليدوية» — راجع EmployeeEditForm لشرح الأساسيات
        // (Bounds = يسار، أعلى، عرض، ارتفاع — وكيف تعدّل بأمان).
        //
        // البنية عمودية بسيطة: بحث → قائمة منتجات → (دفعة | كمية) →
        // انتهاء شرطي → (استلام | إغلاق).
        // «صف مزدوج» في الوسط: دفعة يمين (30..270) وكمية يسار (290..530)
        // — انظر التعليق عند الحقلين.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = AppLang.T("ReceiveStock.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 620);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            var lblTitle = new Label
            {
                Text = AppLang.T("ReceiveStock.Title"),
                Bounds = new Rectangle(0, 18, 560, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            // ── البحث: مربع يمين + زر يسار (كل عنصر بإحداثياته) ──
            // PlaceholderText: النص الرمادي الإرشادي داخل المربع الفارغ.
            _txtSearch = new TextBox
            {
                Bounds = new Rectangle(30, 58, 380, 40),
                Font = AppTheme.FontBody
            };
            _txtSearch.PlaceholderText = AppLang.T("ReceiveStock.Search.Placeholder");

            var btnSearch = AppTheme.MakeButton(AppLang.T("Common.Button.Search"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSearch.Bounds = new Rectangle(420, 58, 110, 40);
            btnSearch.Click += async (_, _) => await LoadProductsAsync(_txtSearch.Text?.Trim() ?? "");

            var lblProducts = new Label
            {
                Text = AppLang.T("ReceiveStock.Label.Product"),
                Bounds = new Rectangle(30, 106, 500, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            // ── قائمة المنتجات: ListBox بلا Dock — Bounds ثابتة 500×170 ──
            // IntegralHeight=false: لا يقفز الارتفاع ليلائم صفوف كاملة.
            // نقر مزدوج على منتج = قفزة ذكية لمربع الكمية (توفير نقرة).
            _lstProducts = new ListBox
            {
                Bounds = new Rectangle(30, 130, 500, 170),
                Font = new Font("Segoe UI", 11F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            _lstProducts.DoubleClick += (_, _) => { if (_lstProducts.SelectedIndex >= 0) _txtQty.Focus(); };

            // ── صف مزدوج: الدفعة يمينًا والكمية يسارًا ──
            // نفس أسلوب EmployeeEditForm: تسميتان فوق مربعين في صف واحد —
            // يمين: Left=30 عرض 240 / يسار: Left=290 عرض 240 (فاصل 20).
            // علامة * = إلزامي — يتكرر التحقق منهما في ReceiveAsync.
            var lblBatch = new Label
            {
                Text = AppLang.T("ReceiveStock.Label.Batch"),
                Bounds = new Rectangle(30, 312, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtBatch = new TextBox
            {
                Bounds = new Rectangle(30, 336, 240, 40),
                Font = AppTheme.FontBody
            };

            var lblQty = new Label
            {
                Text = AppLang.T("Common.Label.Qty") + " *",
                Bounds = new Rectangle(290, 312, 240, 22),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _txtQty = new TextBox
            {
                Bounds = new Rectangle(290, 336, 240, 40),
                Font = AppTheme.FontBody,
                TextAlign = HorizontalAlignment.Center // الرقم وسط — أسرع قراءة
            };

            // ── الانتهاء الشرطي: المفتاح يفعّل منتقي التاريخ ──
            // نمط «إدخال يُفتح بمفتاح»: _dtpExpiry معطّل (Enabled=false)
            // حتى يعلّم المستخدم المفتاح — CheckedChanged يقلب Enabled.
            // لا نحذف/نخفي العنصر — تعطيله يبصر المستخدم بوجوده.
            _chkHasExpiry = new CheckBox
            {
                Text = AppLang.T("ReceiveStock.Label.HasExpiry"),
                Bounds = new Rectangle(290, 388, 240, 26),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };

            _dtpExpiry = new DateTimePicker
            {
                Bounds = new Rectangle(30, 386, 240, 40),
                Format = DateTimePickerFormat.Short,
                Enabled = false
            };
            _chkHasExpiry.CheckedChanged += (_, _) => _dtpExpiry.Enabled = _chkHasExpiry.Checked;

            // ── صف الأزرار: استلام (يمين) + إغلاق (يسار) ──
            // Height=52: أكبر من حد اللمس 48 — زر العمل الأساسي يبرز.
            var btnReceive = AppTheme.MakeButton(AppLang.T("ReceiveStock.Button.Receive"), AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            btnReceive.Bounds = new Rectangle(30, 452, 240, 52);
            btnReceive.Click += async (_, _) => await ReceiveAsync();

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(290, 452, 240, 52);
            btnClose.DialogResult = DialogResult.Cancel; // Esc = إغلاق

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtSearch, btnSearch, lblProducts, _lstProducts,
                lblBatch, _txtBatch, lblQty, _txtQty, _chkHasExpiry, _dtpExpiry,
                btnReceive, btnClose
            });

            CancelButton = btnClose;
            // AcceptButton=البحث (لا الاستلام!): Enter داخل مربع البحث ينفذ
            // البحث — الاستلام عبر النقر فقط منعًا لضغطة Enter عرضة.
            CancelButton = btnClose;
            AcceptButton = btnSearch;
        }

        private async Task LoadProductsAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                var products = await productService.GetProductsListAsync(search, type: "stock");

                _products = products;
                _lstProducts.Items.Clear();
                foreach (var p in products)
                    _lstProducts.Items.Add($"{p.Name}  [{p.Barcode ?? AppLang.T("ReceiveStock.NoBarcode")}]");

                if (_products.Count == 0)
                    UiHelpers.ShowInfo(this, AppLang.T("ReceiveStock.Msg.NoMatches"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.LoadFailed"));
            }
        }

        private async Task ReceiveAsync()
        {
            if (_lstProducts.SelectedIndex < 0 || _lstProducts.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.PickProduct"));
                return;
            }

            var product = _products[_lstProducts.SelectedIndex];

            if (string.IsNullOrWhiteSpace(_txtBatch.Text))
            {
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.BatchRequired"));
                _txtBatch.Focus();
                return;
            }

            if (!int.TryParse(_txtQty.Text?.Trim(), out int qty) || qty <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("ReceiveStock.Msg.InvalidQty"));
                _txtQty.Focus();
                return;
            }

            var request = new DtoStockReceiveRequest
            {
                ProductId = product.ProductId,
                BatchNumber = _txtBatch.Text.Trim(),
                Quantity = qty,
                ExpiryDate = _chkHasExpiry.Checked
                    ? DateOnly.FromDateTime(_dtpExpiry.Value.Date)
                    : null
            };

            int actorUserId = SessionState.CurrentUser?.UserId ?? 0;
            if (actorUserId <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("Purchase.Msg.NoUser"));
                return;
            }

            Result result = await UiHelpers.UseServiceAsync(s => s.ReceiveStockAsync(request, actorUserId: actorUserId));
            UiHelpers.ShowResult(this, result, AppLang.T("ReceiveStock.Msg.Done", qty, product.Name));

            if (result.IsSuccess)
            {
                _txtBatch.Clear();
                _txtQty.Clear();
                _chkHasExpiry.Checked = false;
                _txtBatch.Focus();
            }
        }
    }
}
