using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// قائمة الفواتير المعلقة: استكمال (مع الدفع) أو إلغاء.
    /// </summary>
    public class SuspendedInvoicesForm : DialogForm
    {
        private DataGridView _grid = null!;
        private Button _btnResume = null!;
        private Button _btnCancelInvoice = null!;
        private Label _lblCount = null!;

        private List<Common.DTOs.ViewModel.InvoiceVM> _invoices = new();

        /// <summary>يصبح true إذا تم استكمال أو إلغاء أي فاتورة (لتحديث شاشة البيع).</summary>
        public bool Changed { get; private set; }

        public SuspendedInvoicesForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadInvoicesAsync();
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 دليل البناء — نمط «الجذر المجزّأ» (راجع PayrollForm للشرح الكامل):
        //
        //   ┌──────────────────────────────────────┐
        //   │ صف0 (Absolute 52): العنوان + العداد    │
        //   ├──────────────────────────────────────┤
        //   │ صف1 (Percent 100): جدول الفواتير       │ ← يتمدد مع النافذة
        //   ├──────────────────────────────────────┤
        //   │ صف2 (Absolute 64): استكمال|إلغاء|إغلاق│ ← لا يُقص أبدًا
        //   └──────────────────────────────────────┘
        //
        // (كانت الإحداثيات الثابتة تترك فراغًا تحت الجدول عند تحجيم
        //  النافذة — نفس علة الشاشات القديمة المعالجة.)
        //
        // نقطة تعليمية — ربط الجدول بالبيانات:
        // _invoices (قائمة) و _grid.Rows (صفوف) متوازيتان بالفهرس —
        // الصف المحدد CurrentRow.Index يفهرس القائمة مباشرة. الحماية:
        // كل زر يتحقق أولًا CurrentRow != null وفهرسه < عدد القائمة.
        //
        // نمط «تغيير خارجي»: الخاصية Changed تخبر المستدعي (POS) أن
        // شيئًا حصل فيعيد تحميل سلة السحب — أفضل من رفع الأحداث هنا.
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "الفواتير المعلقة";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(560, 420);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ══ الجذر: عمود واحد + ثلاثة صفوف (العنوان / الجدول المرن / الأزرار) ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // العنوان + العداد
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // الجدول
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // الأزرار

            // ── العنوان وسطًا + عدّاد معلقات يساره (تسمية حية) ──
            var header = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg };
            var lblTitle = new Label
            {
                Text = "الفواتير المعلقة",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };
            // العداد: يتحدث بعد كل تحميل — «N فاتورة معلقة» فوق الجدول
            _lblCount = new Label
            {
                Text = "",
                Dock = DockStyle.Left,
                Width = 170,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = AppTheme.Warning
            };
            header.Controls.Add(lblTitle);
            header.Controls.Add(_lblCount);

            // ── جدول الفواتير (الصف المرن — يتمدد ويتمرر وحده) ──
            _grid = new DataGridView { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            UiHelpers.StyleGrid(_grid);
            _grid.Columns.Add("colId", "رقم الفاتورة");
            _grid.Columns.Add("colDate", "التاريخ");
            _grid.Columns.Add("colCustomer", "العميل");
            _grid.Columns.Add("colItems", "البنود");
            _grid.Columns.Add("colTotal", "الصافي");
            _grid.Columns["colId"].FillWeight = 14;
            _grid.Columns["colDate"].FillWeight = 22;
            _grid.Columns["colCustomer"].FillWeight = 30;
            _grid.Columns["colItems"].FillWeight = 12;
            _grid.Columns["colTotal"].FillWeight = 22;
            // الأرقام وسط الصفوف المالية — أسرع قراءة للكاشير
            _grid.Columns["colItems"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns["colTotal"].DefaultCellStyle.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            // نقر مزدوج على صف = استكماله (اختصار نقرتين لواحدة)
            _grid.DoubleClick += async (_, _) => await ResumeAsync();
            // لا تحديد نص متشابك: الصف كله وحدة اختيار (StyleGrid يضبط FullRowSelect)

            // ── صف الأزرار: ثلاثة أعمدة — استكمال (يمين، أساسي) |
            //    إلغاء (وسط، أحمر) | إغلاق (يسار، هادئ) ──
            // ⚠️ RowCount=1 إلزامي: بدونه يعتبر TLP نفسه قابلًا للنمو
            //    فيبتلع ارتفاع الصف المطلق 64 (علة الزر الأزرق بالمرتجع).
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));

            _btnResume = AppTheme.MakeButton("استكمال الفاتورة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark, height: 50);
            _btnResume.Dock = DockStyle.Fill;
            _btnResume.Margin = new Padding(0, 6, 4, 6);
            _btnResume.Click += async (_, _) => await ResumeAsync();

            _btnCancelInvoice = AppTheme.MakeButton("إلغاء الفاتورة", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28), height: 50);
            _btnCancelInvoice.Dock = DockStyle.Fill;
            _btnCancelInvoice.Margin = new Padding(4, 6, 4, 6);
            _btnCancelInvoice.Click += async (_, _) => await CancelInvoiceAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 50);
            btnClose.Dock = DockStyle.Fill;
            btnClose.Margin = new Padding(4, 6, 0, 6);
            btnClose.DialogResult = DialogResult.Cancel; // Esc = إغلاق

            // RTL: العمود 2 (استكمال) يمينًا، 1 (إلغاء) وسطًا، 0 (إغلاق) يسارًا
            buttons.Controls.Add(_btnResume, 2, 0);
            buttons.Controls.Add(_btnCancelInvoice, 1, 0);
            buttons.Controls.Add(btnClose, 0, 0);

            // ── تجميع صفوف الجذر ──
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(buttons, 0, 2);

            Controls.Add(root);
            CancelButton = btnClose;
        }

        private async Task LoadInvoicesAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoicesListAsync(status: enInvoicesStatus.suspended);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _invoices = result.Data;
                _grid.Rows.Clear();
                foreach (var inv in _invoices)
                {
                    _grid.Rows.Add(inv.InvoiceId,
                        inv.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                        inv.PartyName ?? "عميل مجهول",
                        inv.ItemsCount,
                        UiHelpers.Money(inv.NetAmount));
                }

                // العداد + تلميح فراغ: القائمة الفارغة لا تترك الشاشة ميتة
                _lblCount.Text = _invoices.Count > 0 ? $"{_invoices.Count} فاتورة معلقة" : "";
                if (_invoices.Count == 0)
                    UiHelpers.ShowInfo(this, "لا توجد فواتير معلقة حاليًا.");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الفواتير المعلقة.");
            }
        }

        private async Task ResumeAsync()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _invoices.Count)
            {
                UiHelpers.ShowError(this, "اختر فاتورة من القائمة.");
                return;
            }

            var invoice = _invoices[_grid.CurrentRow.Index];

            using var payment = new PaymentForm(invoice.NetAmount, hasCustomer: invoice.PartyId.HasValue);
            if (payment.ShowDialog(this) != DialogResult.OK || payment.PaymentsList.Count == 0)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var checkoutService = scope.ServiceProvider.GetRequiredService<clsCheckoutService>();
                result = await checkoutService.CompleteSuspendedInvoiceAsync(invoice.InvoiceId, payment.PaymentsList,
                    actorUserId: SessionState.CurrentUser?.UserId ?? 0);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشل استكمال الفاتورة." : result.Message);
                return;
            }

            Changed = true;

            // اكتملت الفاتورة — سؤال واحد: هل يريد الكاشير طباعتها؟
            if (ConfirmationDialog.Show(this, "طباعة الفاتورة", "هل تريد طباعة الفاتورة؟", dangerStyle: false))
                await InvoicePrintHelper.PrintAsync(invoice.InvoiceId, this);

            await LoadInvoicesAsync();
        }

        private async Task CancelInvoiceAsync()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _invoices.Count)
            {
                UiHelpers.ShowError(this, "اختر فاتورة من القائمة.");
                return;
            }

            var invoice = _invoices[_grid.CurrentRow.Index];
            bool confirmed = ConfirmationDialog.Show(this, "إلغاء فاتورة معلقة",
                $"سيتم إلغاء الفاتورة رقم {invoice.InvoiceId} وتحرير المخزون المحجوز. هل أنت متأكد؟");
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var checkoutService = scope.ServiceProvider.GetRequiredService<clsCheckoutService>();
                result = await checkoutService.CancelSuspendedInvoiceAsync(invoice.InvoiceId,
                    SessionState.CurrentUser?.UserId ?? 0);
            }

            UiHelpers.ShowResult(this, result, "تم إلغاء الفاتورة.");
            if (result.IsSuccess)
            {
                Changed = true;
                await LoadInvoicesAsync();
            }
        }
    }
}
