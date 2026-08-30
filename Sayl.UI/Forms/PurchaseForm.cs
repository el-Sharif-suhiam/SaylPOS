using System.Globalization;
using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// فاتورة شراء جديدة: مورد + بنود (منتج مخزني، دفعة، كمية، تكلفة، انتهاء اختياري)
    /// + خصم وضريبة + دفع كلي/جزئي. تُنشئ الفاتورة عبر clsPurchaseService داخل معاملة واحدة
    /// فتُحدَّث الدفعات والمخزون ورصيد المورد تلقائيًا في الخدمة.
    /// </summary>
    public class PurchaseForm : DialogForm
    {
        private TextBox _txtSupplierSearch = null!;
        private ListBox _lstSuppliers = null!;
        private List<Supplier> _suppliers = new();

        private TextBox _txtProductSearch = null!;
        private ListBox _lstProducts = null!;
        private List<DtoProduct> _products = new();

        private DataGridView _grid = null!;
        private TextBox _txtDiscount = null!;
        private TextBox _txtTaxPercent = null!;
        private TextBox _txtPaid = null!;
        private ComboBox _cmbMethod = null!;
        private TextBox _txtNotes = null!;

        private Label _lblSubtotal = null!;
        private Label _lblNet = null!;
        private Label _lblRemaining = null!;
        private Button _btnSave = null!;

        private decimal _net;
        private bool _saved;

        public PurchaseForm()
        {
            Text = "فاتورة شراء جديدة";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(780, 600);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            BuildUi();
            Load += async (_, _) =>
            {
                await LoadSuppliersAsync("");
                await LoadProductsAsync("");
                await LoadDefaultTaxAsync();
            };
        }

        // ── بناء الواجهة ──
        private void BuildUi()
        {
            var lblTitle = new Label
            {
                Text = "فاتورة شراء جديدة",
                Bounds = new Rectangle(0, 14, ClientSize.Width, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ── المورد ──
            var lblSupplier = MakeCaption("المورد *", new Rectangle(470, 52, 400, 22));
            _txtSupplierSearch = new TextBox { Bounds = new Rectangle(740, 76, 130, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _txtSupplierSearch.PlaceholderText = "بحث...";
            _txtSupplierSearch.KeyDown += async (_, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    await LoadSuppliersAsync(_txtSupplierSearch.Text?.Trim() ?? "");
                }
            };
            var btnSupplierSearch = AppTheme.MakeButton("بحث", AppTheme.PrimaryLight,
                foreColor: AppTheme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 34);
            btnSupplierSearch.Bounds = new Rectangle(470, 76, 90, 34);
            btnSupplierSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSupplierSearch.Click += async (_, _) => await LoadSuppliersAsync(_txtSupplierSearch.Text?.Trim() ?? "");

            _lstSuppliers = new ListBox
            {
                Bounds = new Rectangle(470, 114, 400, 96),
                Font = new Font("Segoe UI", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // ── المنتجات ──
            var lblProduct = MakeCaption("المنتج المخزني (بحث بالاسم أو الباركود)", new Rectangle(30, 52, 400, 22));
            _txtProductSearch = new TextBox { Bounds = new Rectangle(300, 76, 130, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _txtProductSearch.PlaceholderText = "بحث...";
            _txtProductSearch.KeyDown += async (_, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    await LoadProductsAsync(_txtProductSearch.Text?.Trim() ?? "");
                }
            };
            var btnProductSearch = AppTheme.MakeButton("بحث", AppTheme.PrimaryLight,
                foreColor: AppTheme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 34);
            btnProductSearch.Bounds = new Rectangle(30, 76, 90, 34);
            btnProductSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnProductSearch.Click += async (_, _) => await LoadProductsAsync(_txtProductSearch.Text?.Trim() ?? "");

            _lstProducts = new ListBox
            {
                Bounds = new Rectangle(30, 114, 400, 96),
                Font = new Font("Segoe UI", 10.5F),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _lstProducts.DoubleClick += (_, _) => AddSelectedItem();

            // ── جدول البنود ──
            _grid = new DataGridView
            {
                Bounds = new Rectangle(30, 222, 840, 240),
                Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Left
            };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add("colProduct", "المنتج");
            _grid.Columns.Add("colBatch", "رقم الدفعة *");
            _grid.Columns.Add("colQty", "الكمية");
            _grid.Columns.Add("colCost", "التكلفة");
            _grid.Columns.Add("colExpiry", "الانتهاء yyyy-mm-dd");
            _grid.Columns.Add("colTotal", "الإجمالي");
            _grid.Columns.Add("colDelete", "");
            _grid.Columns["colProduct"].ReadOnly = true;
            _grid.Columns["colProduct"].FillWeight = 30;
            _grid.Columns["colBatch"].FillWeight = 16;
            _grid.Columns["colQty"].FillWeight = 9;
            _grid.Columns["colQty"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colCost"].FillWeight = 11;
            _grid.Columns["colCost"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colExpiry"].FillWeight = 13;
            _grid.Columns["colExpiry"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colTotal"].ReadOnly = true;
            _grid.Columns["colTotal"].FillWeight = 12;
            _grid.Columns["colDelete"].ReadOnly = true;
            _grid.Columns["colDelete"].FillWeight = 9;
            _grid.CellEndEdit += (_, _) => RefreshRowTotals();
            _grid.CellClick += (_, args) =>
            {
                if (args.RowIndex < 0 || args.ColumnIndex != _grid.Columns["colDelete"].Index) return;
                if (args.RowIndex < _grid.Rows.Count && !_grid.Rows[args.RowIndex].IsNewRow)
                    _grid.Rows.RemoveAt(args.RowIndex);
                RecalcTotals();
            };

            // ── الإجماليات ──
            var lblDiscount = MakeCaption("الخصم", new Rectangle(640, 474, 230, 22));
            lblDiscount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _txtDiscount = new TextBox { Bounds = new Rectangle(500, 498, 150, 34), TextAlign = HorizontalAlignment.Center, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _txtDiscount.Text = "0";
            _txtDiscount.TextChanged += (_, _) => RecalcTotals();

            var lblTax = MakeCaption("الضريبة %", new Rectangle(320, 474, 140, 22));
            lblTax.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _txtTaxPercent = new TextBox { Bounds = new Rectangle(180, 498, 120, 34), TextAlign = HorizontalAlignment.Center, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _txtTaxPercent.Text = "0";
            _txtTaxPercent.TextChanged += (_, _) => RecalcTotals();

            _lblSubtotal = new Label
            {
                Text = "المجموع: 0.00",
                Bounds = new Rectangle(30, 474, 260, 24),
                ForeColor = AppTheme.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _lblNet = new Label
            {
                Text = "الصافي: 0.00",
                Bounds = new Rectangle(30, 502, 260, 30),
                Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
                ForeColor = AppTheme.PrimaryDark,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            // ── الدفع ──
            var lblPaid = MakeCaption("المدفوع الآن (0 = آجل)", new Rectangle(640, 546, 230, 22));
            lblPaid.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _txtPaid = new TextBox { Bounds = new Rectangle(500, 570, 150, 34), TextAlign = HorizontalAlignment.Center, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _txtPaid.Text = "0";
            _txtPaid.TextChanged += (_, _) => UpdateRemaining();

            var lblMethod = MakeCaption("طريقة الدفع", new Rectangle(320, 546, 140, 22));
            lblMethod.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cmbMethod = new ComboBox
            {
                Bounds = new Rectangle(180, 568, 120, 34),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _cmbMethod.Items.AddRange(new object[] { "نقدي", "بطاقة" });
            _cmbMethod.SelectedIndex = 0;

            _lblRemaining = new Label
            {
                Text = "المتبقي على المورد: 0.00",
                Bounds = new Rectangle(30, 572, 260, 26),
                ForeColor = AppTheme.Warning,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            // ── ملاحظات ──
            var lblNotes = MakeCaption("ملاحظات", new Rectangle(640, 618, 230, 22));
            lblNotes.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _txtNotes = new TextBox
            {
                Bounds = new Rectangle(180, 642, 690, 40),
                Multiline = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Left
            };

            // ── الأزرار ──
            _btnSave = AppTheme.MakeButton("حفظ الفاتورة", AppTheme.Success, hoverColor: Color.FromArgb(21, 128, 61));
            _btnSave.Bounds = new Rectangle(520, 664, 180, 48);
            _btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnSave.Click += async (_, _) => await SaveAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(320, 664, 160, 48);
            btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                lblTitle,
                lblSupplier, _txtSupplierSearch, btnSupplierSearch, _lstSuppliers,
                lblProduct, _txtProductSearch, btnProductSearch, _lstProducts,
                _grid,
                lblDiscount, _txtDiscount, lblTax, _txtTaxPercent,
                _lblSubtotal, _lblNet,
                lblPaid, _txtPaid, lblMethod, _cmbMethod, _lblRemaining,
                lblNotes, _txtNotes,
                _btnSave, btnClose
            });

            AcceptButton = btnProductSearch;
            CancelButton = btnClose;
        }

        private static Label MakeCaption(string text, Rectangle bounds)
        {
            return new Label
            {
                Text = text,
                Bounds = bounds,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
        }

        // ── التحميل ──
        private async Task LoadSuppliersAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var supplierService = scope.ServiceProvider.GetRequiredService<clsSupplierService>();
                _suppliers = await supplierService.GetSupplierListAsync(search);

                _lstSuppliers.Items.Clear();
                foreach (var s in _suppliers)
                    _lstSuppliers.Items.Add($"{s.Party.Name}  —  رصيد: {UiHelpers.Money(s.Balance ?? 0)}");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الموردين.");
            }
        }

        private async Task LoadProductsAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var productService = scope.ServiceProvider.GetRequiredService<clsProductService>();
                _products = await productService.GetProductsListAsync(search, type: "stock");

                _lstProducts.Items.Clear();
                foreach (var p in _products)
                    _lstProducts.Items.Add($"{p.Name}  [{p.Barcode ?? "بدون باركود"}]");

                if (_products.Count == 0)
                    _lstProducts.Items.Add("لا توجد منتجات مطابقة.");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل المنتجات.");
            }
        }

        private async Task LoadDefaultTaxAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<clsSettingsService>();
                decimal tax = await settings.GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0);
                _txtTaxPercent.Text = tax.ToString("0.##");
            }
            catch
            {
                // الضريبة الافتراضية ليست حرجة — نبقيها صفر عند الفشل
            }
        }

        // ── إضافة بند ──
        private void AddSelectedItem()
        {
            if (_lstProducts.SelectedIndex < 0 || _lstProducts.SelectedIndex >= _products.Count)
            {
                UiHelpers.ShowInfo(this, "اختر منتجًا من القائمة أولًا (أو اضغط Enter بعد البحث ثم انقر نقرًا مزدوجًا على المنتج).");
                return;
            }

            var p = _products[_lstProducts.SelectedIndex];
            int rowIndex = _grid.Rows.Add(p.Name, string.Empty, "1",
                p.CostPrice.ToString("0.##"), string.Empty, string.Empty, "حذف");
            _grid.Rows[rowIndex].Tag = p.ProductId;
            _grid.Rows[rowIndex].Cells["colDelete"].Style.ForeColor = AppTheme.Danger;
            _grid.CurrentCell = _grid.Rows[rowIndex].Cells["colBatch"];
            RecalcTotals();
        }

        // ── الحسابات ──
        private void RefreshRowTotals()
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                decimal qty = ParseDecimal(row.Cells["colQty"].Value);
                decimal cost = ParseDecimal(row.Cells["colCost"].Value);
                row.Cells["colTotal"].Value = UiHelpers.Money(qty * cost);
            }
            RecalcTotals();
        }

        private void RecalcTotals()
        {
            decimal total = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                total += ParseDecimal(row.Cells["colQty"].Value) * ParseDecimal(row.Cells["colCost"].Value);
            }

            decimal discount = Math.Max(0, ParseDecimal(_txtDiscount.Text));
            decimal taxPercent = Math.Max(0, ParseDecimal(_txtTaxPercent.Text));
            decimal afterDiscount = total - discount;
            if (afterDiscount < 0) afterDiscount = 0;
            decimal tax = taxPercent > 0 ? afterDiscount * taxPercent / 100 : 0;
            _net = afterDiscount + tax;

            _lblSubtotal.Text = "المجموع: " + UiHelpers.Money(total);
            _lblNet.Text = "الصافي: " + UiHelpers.Money(_net);
            UpdateRemaining();
        }

        private void UpdateRemaining()
        {
            decimal paid = Math.Max(0, ParseDecimal(_txtPaid.Text));
            decimal remaining = _net - paid;
            if (remaining < 0) remaining = 0;
            _lblRemaining.Text = "المتبقي على المورد: " + UiHelpers.Money(remaining);
        }

        private static decimal ParseDecimal(object? value)
        {
            return decimal.TryParse(value?.ToString()?.Trim(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out decimal d) ? d : 0;
        }

        private static bool TryParseExpiry(string? text, out DateOnly? value, out string error)
        {
            value = null;
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (DateOnly.TryParseExact(text.Trim(),
                    new[] { "yyyy-MM-dd", "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                value = parsed;
                return true;
            }

            error = text.Trim();
            return false;
        }

        // ── الحفظ ──
        private async Task SaveAsync()
        {
            if (_lstSuppliers.SelectedIndex < 0 || _lstSuppliers.SelectedIndex >= _suppliers.Count)
            {
                UiHelpers.ShowError(this, "اختر المورد من القائمة.");
                return;
            }

            if (SessionState.CurrentUser == null)
            {
                UiHelpers.ShowError(this, "لا يوجد مستخدم مسجل.");
                return;
            }

            var items = new List<DtoPurchaseItem>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow || row.Tag == null) continue;

                string productName = row.Cells["colProduct"].Value?.ToString() ?? "?";
                string batch = row.Cells["colBatch"].Value?.ToString()?.Trim() ?? "";

                if (batch.Length == 0)
                {
                    UiHelpers.ShowError(this, $"رقم الدفعة مطلوب للمنتج: {productName}");
                    _grid.CurrentCell = row.Cells["colBatch"];
                    return;
                }

                int qty = (int)ParseDecimal(row.Cells["colQty"].Value);
                if (qty <= 0)
                {
                    UiHelpers.ShowError(this, $"الكمية يجب أن تكون أكبر من صفر للمنتج: {productName}");
                    _grid.CurrentCell = row.Cells["colQty"];
                    return;
                }

                if (!TryParseExpiry(row.Cells["colExpiry"].Value?.ToString(), out DateOnly? expiry, out string bad))
                {
                    UiHelpers.ShowError(this, $"تاريخ الانتهاء غير صالح ({bad}) للمنتج: {productName}\nاستخدم الصيغة yyyy-mm-dd");
                    _grid.CurrentCell = row.Cells["colExpiry"];
                    return;
                }

                items.Add(new DtoPurchaseItem
                {
                    ProductId = (int)row.Tag,
                    BatchNumber = batch,
                    Quantity = qty,
                    UnitCost = Math.Max(0, ParseDecimal(row.Cells["colCost"].Value)),
                    ExpiryDate = expiry
                });
            }

            if (items.Count == 0)
            {
                UiHelpers.ShowError(this, "أضف بندًا واحدًا على الأقل إلى الفاتورة.");
                return;
            }

            decimal paid = Math.Max(0, ParseDecimal(_txtPaid.Text));

            var request = new DtoPurchaseRequest
            {
                SupplierId = _suppliers[_lstSuppliers.SelectedIndex].PartyId,
                UserId = SessionState.CurrentUser.UserId,
                Items = items,
                DiscountAmount = Math.Max(0, ParseDecimal(_txtDiscount.Text)),
                TaxPercent = Math.Max(0, ParseDecimal(_txtTaxPercent.Text)),
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
                Payment = paid > 0
                    ? new DtoPaymentInfo
                    {
                        Amount = paid <= _net ? paid : _net,
                        PaymentDate = DateOnly.FromDateTime(DateTime.Today),
                        Method = _cmbMethod.SelectedIndex == 1 ? "card" : "cash"
                    }
                    : null
            };

            _btnSave.Enabled = false;
            try
            {
                Result<int> result;
                using (var scope = Program.Services.CreateScope())
                {
                    var purchaseService = scope.ServiceProvider.GetRequiredService<clsPurchaseService>();
                    result = await purchaseService.CreatePurchaseInvoiceAsync(request);
                }

                UiHelpers.ShowResult(this, result,
                    $"تم إنشاء فاتورة الشراء رقم {result.Data} بنجاح.");

                if (result.IsSuccess)
                {
                    _saved = true;
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // إغلاق عبر X أو Esc بعد نجاح الحفظ لا يجب أن يبدو إلغاءً
            if (_saved) DialogResult = DialogResult.OK;
            base.OnFormClosing(e);
        }
    }
}
