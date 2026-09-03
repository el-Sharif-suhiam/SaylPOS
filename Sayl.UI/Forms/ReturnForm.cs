using Common.DTOs.RequestModels;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// مرتجع بيع في نقطة البيع: إدخال رقم الفاتورة، اختيار الكميات المرتجعة، تنفيذ المرتجع.
    /// </summary>
    public class ReturnForm : DialogForm
    {
        private TextBox _txtInvoiceId = null!;
        private Button _btnLoad = null!;
        private DataGridView _grid = null!;
        private Label _lblInfo = null!;

        private int _originalInvoiceId;
        private List<Common.DTOs.ViewModel.InvoiceItemVM> _items = new();

        public bool Returned { get; private set; }

        public ReturnForm()
        {
            BuildUi();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «الجذر المجزّأ» (راجع PayrollForm للشرح الكامل):
        //
        //   ┌──────────────────────────────────────┐
        //   │ صف0 (Absolute 52): العنوان            │
        //   ├──────────────────────────────────────┤
        //   │ صف1 (Absolute 56): رقم الفاتورة + زر   │ ← صف إدخال ثابت
        //   ├──────────────────────────────────────┤
        //   │ صف2 (Percent 100): جدول البنود         │ ← يتمدد مع النافذة
        //   ├──────────────────────────────────────┤
        //   │ صف3 (Absolute 60): سطر المعلومات الحي  │ ← بطاقة معلومات
        //   ├──────────────────────────────────────┤
        //   │ صف4 (Absolute 64): تنفيذ | إغلاق      │ ← لا يُقص أبدًا
        //   └──────────────────────────────────────┘
        //
        // لماذا ليس Bounds يدوية؟ الإحداثيات الثابتة تترك فراغات عند
        // التكبير وتقصّ عند التصغير (DialogForm يقلّص النافذة تلقائيًا
        // لشاشات صغيرة) — الصف المرن وحده يتحرك مع النافذة بسلاسة.
        //
        // نمط «الجدول المُحمَّل»: الجدول يُبنى فارغًا بـ 4 أعمدة، وبعد
        // «تحميل الفاتورة» تُضاف صفوفه — عمود «كمية المرتجع» وحده قابل
        // للتحرير (كل الأعمدة الأخرى عرض فقط). المستخدم يكتب الكميات
        // مباشرة في خلايا الجدول — لا حقول إدخال منفصلة.
        //
        // ⚠️ متغيرات الحالة _originalInvoiceId و _items تربط الجدول
        //    بالبيانات: فهرس الصف = فهرس البند (LoadInvoiceAsync فقط
        //    يكتب فيهما — راجع ExecuteReturnAsync للقراءة).
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── قواعد النافذة (نفس نمط كل النوافذ الحوارية) ──
            Text = "مرتجع بيع";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 640);
            MinimumSize = new Size(560, 480);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ══ الجذر: عمود واحد + خمسة صفوف (راجع الخريطة أعلاه) ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // العنوان
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // صف رقم الفاتورة
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // الجدول
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));   // سطر المعلومات
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // الأزرار

            // ── العنوان ──
            var lblTitle = new Label
            {
                Text = "مرتجع بيع",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            // ── صف رقم الفاتورة: التسمية+المربع يمينًا، زر التحميل يسارًا ──
            // ⚠️ عمودا TLP بـ RTL: العمود 0 يظهر يمينًا (عكسي!):
            //    العمود 0 (Percent 100) = idBox التسمية+المربع — يمينًا.
            //    العمود 1 (Absolute 180) = زر التحميل — يسارًا بعرض ثابت.
            // ⚠️ RowCount=1 إلزامي: بدونه يعتبر TLP نفسه «قابلًا للنمو»
            //    فتتبدل الأدوار ويظهر زر أزرق ضخم يبتلع الصف (علة سابقة).
          

            var inputRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 8, 0, 8)
            };

            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); // التسمية
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); // مربع الرقم
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // زر التحميل

            

            var lblInvoice = new Label
            {
                Text = "رقم فاتورة البيع الأصلية",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                AutoEllipsis = true
            };

            _txtInvoiceId = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                BackColor = AppTheme.FieldBg,
            };


            // حزمة التسمية+المربع في العمود المرن (0 = يمين بصريًا)
            //var idBox = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            // ⚠️ ترتيب الإرساء داخل اللوحة (RTL): كل Right يتراصف من اليمين —
            //    أول Add أقصى اليمين. نريد المربع أقصى اليمين فالتسمية يساره:
            //    إذًا نضيف التسمية (Right) أولًا ثم المربع (Right) يُرسّى
            //    بعدها إلى يمينها. (الترتيب البديل يجعل التسمية أقصى اليمين — خطأ).





            _btnLoad = AppTheme.MakeButton("تحميل الفاتورة", AppTheme.Primary,hoverColor: AppTheme.PrimaryDark);
            _btnLoad.TextAlign = ContentAlignment.MiddleCenter; 
            _btnLoad.Dock = DockStyle.Fill;
            _btnLoad.Click += async (_, _) => await LoadInvoiceAsync();

            inputRow.Controls.Add(lblInvoice, 0, 0);
            inputRow.Controls.Add(_txtInvoiceId, 1, 0);
            inputRow.Controls.Add(_btnLoad, 2, 0);
            // ── جدول البنود (الصف المرن — يتمدد مع النافذة) ──
            _grid = new DataGridView { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add("colProduct", "المنتج");
            _grid.Columns.Add("colSold", "المباع");
            _grid.Columns.Add("colPrice", "السعر");
            var colReturnQty = new DataGridViewTextBoxColumn { Name = "colReturnQty", HeaderText = "كمية المرتجع", ReadOnly = false };
            _grid.Columns.Add(colReturnQty);
            _grid.Columns["colProduct"].FillWeight = 45;
            _grid.Columns["colSold"].FillWeight = 15;
            _grid.Columns["colPrice"].FillWeight = 18;
            _grid.Columns["colReturnQty"].FillWeight = 22;
            // عمود الكمية «دعوة بصرية» للتحرير: خلفية مختلفة عن بقية الجدول
            // (نفس حيلة سلة الكاشير في PosForm.StyleCartGrid).
            _grid.Columns["colReturnQty"].DefaultCellStyle.BackColor = AppTheme.FieldBg;
            _grid.Columns["colReturnQty"].DefaultCellStyle.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _grid.Columns["colReturnQty"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            // حارس الإدخال: أي نص غير رقمي يُرفض قبل وصوله للبيانات
            _grid.CellValidating += (_, args) =>
            {
                if (_grid.Columns[args.ColumnIndex].Name != "colReturnQty")
                    return;
                if (!int.TryParse(args.FormattedValue?.ToString()?.Trim() ?? "", out int _))
                    args.Cancel = true;
            };

            // ── سطر المعلومات: بطاقة رمادية تتحدث بعد التحميل ──
            _lblInfo = new Label
            {
                Text = "أدخل رقم فاتورة البيع ثم اضغط تحميل.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 10F),
                ForeColor = AppTheme.TextMuted,
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(10, 8, 10, 8)
            };

            // ── صف الأزرار: تنفيذ (يمين، تحذيري) | إغلاق (يسار) ──
            // عمودان 55/45 — الأزرار Dock=Fill تتمدد مع النافذة.
            // ⚠️ RTL يقلب الأعمدة: العمود 0 يظهر يمينًا — لذلك «تنفيذ»
            //    (الزر الرئيسي، يمينًا بصريًا) في العمود 0، و«إغلاق» في 1.
            // ⚠️ RowCount=1: نفس حيلة inputRow — بدونها ينكمش الصف المطلق.
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            var btnReturn = AppTheme.MakeButton("تنفيذ المرتجع", AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9), height: 50);
            btnReturn.Dock = DockStyle.Fill;
            btnReturn.Margin = new Padding(0, 6, 4, 6);
            btnReturn.Click += async (_, _) => await ExecuteReturnAsync();

            var btnCancel = AppTheme.MakeButton("إغلاق", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 50);
            btnCancel.Dock = DockStyle.Fill;
            btnCancel.Margin = new Padding(4, 6, 0, 6);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = إغلاق

            buttons.Controls.Add(btnReturn, 0, 0);   // العمود 0 = يمين بصريًا
            buttons.Controls.Add(btnCancel, 1, 0);   // العمود 1 = يسار بصريًا

            // ── تجميع صفوف الجذر (الترتيب يطابق RowStyles أعلاه) ──
            root.Controls.Add(lblTitle, 0, 0);
            root.Controls.Add(inputRow, 0, 1);
            root.Controls.Add(_grid, 0, 2);
            root.Controls.Add(_lblInfo, 0, 3);
            root.Controls.Add(buttons, 0, 4);

            Controls.Add(root);
            CancelButton = btnCancel;
            AcceptButton = _btnLoad; // Enter = تحميل (الخطوة الأولى دومًا)
            ActiveControl = _txtInvoiceId;
        }

        private async Task LoadInvoiceAsync()
        {
            if (!int.TryParse(_txtInvoiceId.Text?.Trim(), out int invoiceId) || invoiceId <= 0)
            {
                UiHelpers.ShowError(this, "أدخل رقم فاتورة صحيحًا.");
                return;
            }

            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                if (result.Data.Type != "sale" || result.Data.Status != "completed")
                {
                    UiHelpers.ShowError(this, "المرتجع مسموح فقط على فواتير بيع مكتملة.");
                    return;
                }

                _originalInvoiceId = invoiceId;
                _items = result.Data.Items;

                _grid.Rows.Clear();
                foreach (var item in _items)
                    _grid.Rows.Add(item.ProductName, item.Quantity, UiHelpers.Money(item.ItemPrice), 0);

                _lblInfo.Text = $"فاتورة #{invoiceId} — العميل: {result.Data.PartyName ?? "مجهول"} — أدخل كمية المرتجع لكل بند.";
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الفاتورة.");
            }
        }

        private async Task ExecuteReturnAsync()
        {
            if (_originalInvoiceId <= 0 || _items.Count == 0)
            {
                UiHelpers.ShowError(this, "حمّل الفاتورة أولًا.");
                return;
            }

            var returnItems = new List<DtoReturnItem>();
            decimal returnTotal = 0;

            for (int i = 0; i < _grid.Rows.Count && i < _items.Count; i++)
            {
                if (!int.TryParse(_grid.Rows[i].Cells["colReturnQty"].Value?.ToString(), out int qty) || qty <= 0)
                    continue;

                var item = _items[i];
                if (qty > item.Quantity)
                {
                    UiHelpers.ShowError(this, $"كمية المرتجع للمنتج '{item.ProductName}' أكبر من المباع ({item.Quantity}).");
                    return;
                }

                returnItems.Add(new DtoReturnItem { ProductId = item.ProductId, Quantity = qty });
                returnTotal += item.ItemPrice * qty;
            }

            if (returnItems.Count == 0)
            {
                UiHelpers.ShowError(this, "أدخل كمية مرتجعة لبند واحد على الأقل.");
                return;
            }

            bool confirmed = ConfirmationDialog.Show(this, "تنفيذ مرتجع",
                $"سيتم إرجاع {returnItems.Count} بند بقيمة {UiHelpers.Money(returnTotal)} للمخزون. متابعة؟");
            if (!confirmed)
                return;

            var request = new DtoReturnRequest
            {
                OriginalInvoiceId = _originalInvoiceId,
                UserId = SessionState.CurrentUser?.UserId ?? 0,
                Items = returnItems,
                Notes = null
            };

            Result<int> result;
            using (var scope = Program.Services.CreateScope())
            {
                var returnService = scope.ServiceProvider.GetRequiredService<clsReturnService>();
                result = await returnService.CreateCustomerReturnAsync(request);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشل تنفيذ المرتجع." : result.Message);
                return;
            }

            UiHelpers.ShowInfo(this, $"تم تنفيذ المرتجع بنجاح. رقم المرتجع: {result.Data}");
            Returned = true;
            DialogResult = DialogResult.OK;
        }
    }
}
