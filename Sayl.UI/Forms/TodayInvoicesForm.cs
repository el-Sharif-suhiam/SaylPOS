using Common.DTOs.ViewModel;
using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// نافذة «فواتير اليوم» من شاشة الكاشير: كل الفواتير التي صدرت من هذا
    /// الكاشير (المستخدم الحالي) في يوم البرنامج الحالي فقط — بحث بالوقت:
    /// «16» تعرض كل فواتير الساعة، «16:45» بدقة الدقيقة (تقبل أيضاً 1645
    /// والأرقام العربية). عند اختيار فاتورة تُطبَع نسخة إضافية بالصيغة
    /// المختارة (حرارية أو A4) عبر المسار الموحّد InvoicePrintHelper.
    /// القائمة صفوف مرسومة يدويًا في لوحة مزدوجة التخزين المؤقت — تمرير سلس
    /// بلا وميض ولا تداخل، وكل صف بطاقة بحد دائري بنفس هوية الشاشات.
    /// </summary>
    public class TodayInvoicesForm : DialogForm
    {
        private readonly int _userId;

        private TextBox _txtSearch = null!;
        private Label _lblCount = null!;
        private SmoothListPanel _list = null!;
        private Button _btnPrint = null!;

        private List<InvoiceVM> _all = new();
        private List<Row> _rows = new();
        private int _selected = -1;

        /// <summary>لوحة القائمة — تخزين مزدوج للتخلص من الوميض عند التمرير،
        /// وتخطي الشريط الأفقي (الصفوف تتمدد مع العرض فلا حاجة له).</summary>
        private sealed class SmoothListPanel : Panel
        {
            public SmoothListPanel()
            {
                DoubleBuffered = true;
                HorizontalScroll.Enabled = false;
                HorizontalScroll.Visible = false;
            }
        }

        private sealed class Row
        {
            public InvoiceVM Inv = null!;
            public Panel Panel = null!;
            public bool Hover;
        }

        public TodayInvoicesForm(int userId)
        {
            _userId = userId;

            Text = AppLang.T("TodayInvoices.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 640);
            MinimumSize = new Size(640, 460);
            BackColor = AppTheme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            KeyPreview = true;

            BuildUi();

            Load += async (_, _) => await LoadTodayAsync();
            KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    await PrintSelectedAsync();
                }
            };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.Bg,
                Padding = new Padding(12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // البحث
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // العدد
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // القائمة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));   // الأزرار

            // ── شريط البحث: مربع الوقت + التلميح في سطر واحد ──
            var searchRow = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg };
            var lblSearch = new Label
            {
                Text = AppLang.T("TodayInvoices.SearchLabel"),
                AutoSize = true,
                Font = AppTheme.FontBody,
                ForeColor = AppTheme.TextPrimary,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            _txtSearch = new TextBox
            {
                Font = new Font("Segoe UI", 11F),
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            _txtSearch.Width = 220;
            var lblHint = new Label
            {
                Text = AppLang.T("TodayInvoices.SearchHint"),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = AppTheme.TextMuted,
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };
            searchRow.Controls.Add(lblHint);
            searchRow.Controls.Add(_txtSearch);
            searchRow.Controls.Add(lblSearch);
            searchRow.Resize += (_, _) => LayoutSearchRow(searchRow, lblSearch, _txtSearch, lblHint);
            LayoutSearchRow(searchRow, lblSearch, _txtSearch, lblHint);
            _txtSearch.TextChanged += (_, _) => ApplyFilter();

            _lblCount = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 9F)
            };

            // ── القائمة: لوحة مزدوجة التخزين بتمرير سلس ──
            _list = new SmoothListPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.Bg,
                AutoScroll = true
            };
            _list.Resize += (_, _) => RepositionRows();

            // ── شريط الأزرار ──
            var bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Bg, Padding = new Padding(2, 6, 2, 2) };
            _btnPrint = AppTheme.MakeButton(AppLang.T("TodayInvoices.Button.Print"), AppTheme.Primary,
                hoverColor: AppTheme.PrimaryDark, height: 48);
            _btnPrint.Dock = DockStyle.Left;
            _btnPrint.Width = 170;
            _btnPrint.Enabled = false;
            _btnPrint.Click += async (_, _) => await PrintSelectedAsync();

            var btnClose = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 48);
            btnClose.Dock = DockStyle.Left;
            btnClose.Width = 110;
            btnClose.Click += (_, _) => Close();

            bottomBar.Controls.Add(btnClose);
            bottomBar.Controls.Add(_btnPrint);

            root.Controls.Add(searchRow, 0, 0);
            root.Controls.Add(_lblCount, 0, 1);
            root.Controls.Add(_list, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);

            Controls.Add(root);
        }

        private static void LayoutSearchRow(Panel row, Label lblSearch, TextBox txtSearch, Label lblHint)
        {
            int h = row.ClientSize.Height;
            int xRight = row.ClientSize.Width - 12;
            lblSearch.SetBounds(xRight - lblSearch.PreferredWidth - 4, (h - lblSearch.Height) / 2,
                lblSearch.PreferredWidth + 8, lblSearch.Height);
            txtSearch.SetBounds(lblSearch.Left - txtSearch.Width - 8, (h - txtSearch.Height) / 2,
                txtSearch.Width, txtSearch.Height);
            lblHint.SetBounds(12, (h - lblHint.Height) / 2, Math.Max(10, txtSearch.Left - lblHint.Width - 12), lblHint.Height);
        }

        // ───────────────── التحميل والفلترة ─────────────────

        private async Task LoadTodayAsync()
        {
            try
            {
                List<InvoiceVM> items;
                using (var scope = Program.Services.CreateScope())
                {
                    var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                    // يوم البرنامج الحالي فقط (UTC+3) لهذا الكاشير — ترقيم SQL بلا قيود
                    var r = await invoiceService.GetInvoicesPagedAsync(
                        userId: _userId,
                        fromDate: AppClock.Today,
                        toDate: AppClock.Today,
                        page: 1,
                        pageSize: 500);
                    if (!r.IsSuccess || r.Data == null)
                    {
                        _lblCount.Text = AppLang.T("TodayInvoices.Msg.LoadFailed");
                        return;
                    }
                    items = r.Data.Items;
                }

                _all = items.OrderByDescending(i => AppClock.ToLocal(i.CreatedAt ?? DateTime.MinValue)).ToList();
                ApplyFilter();
            }
            catch
            {
                _lblCount.Text = AppLang.T("TodayInvoices.Msg.LoadFailed");
            }
        }

        /// <summary>فلترة حية بالوقت: «16» = الساعة كاملة، «16:45» = الدقيقة بدقة.
        /// الفارغ = كل فواتير اليوم. الصيغة غير الصالحة تعرض تنبيهًا لا تُمسّ القائمة.</summary>
        private void ApplyFilter()
        {
            IEnumerable<InvoiceVM> result = _all;

            string q = NormalizeDigits(_txtSearch.Text).Trim();
            if (q.Length > 0)
            {
                if (!TryParseTimeQuery(q, out int hour, out int minute, out bool hasMinute, out string? error))
                {
                    _lblCount.ForeColor = AppTheme.Danger;
                    _lblCount.Text = error!;
                    return;
                }

                result = _all.Where(i =>
                {
                    var local = AppClock.ToLocal(i.CreatedAt ?? DateTime.MinValue);
                    if (local.Hour != hour) return false;
                    return !hasMinute || local.Minute == minute;
                });
            }

            var visible = result.ToList();
            _lblCount.ForeColor = AppTheme.TextMuted;
            _lblCount.Text = AppLang.T("TodayInvoices.Count", visible.Count);
            RebuildRows(visible);
        }

        private static string NormalizeDigits(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                sb.Append(c switch
                {
                    >= '٠' and <= '٩' => (char)('0' + (c - '٠')), // ٠-٩ → 0-9
                    '٫' => '.',                                   // فاصل عشري عربي
                    '٬' => '.',                                   // فاصل آلاف عربي
                    _ => c
                });
            }
            return sb.ToString();
        }

        /// <summary>الصيغ المقبولة: «16» أو «04» (ساعة) — «16:45» أو «1645» أو «945»
        /// (ساعة:دقيقة). السeparator: : أو . أو /. يُرجع خطأ بوصفه رسالة جاهزة.</summary>
        private static bool TryParseTimeQuery(string q, out int hour, out int minute, out bool hasMinute, out string? error)
        {
            hour = minute = 0;
            hasMinute = false;
            error = null;

            string s = q.Replace(':', '.').Replace('/', '.').Trim();
            string[] parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 || parts.Any(p => !p.All(char.IsDigit)))
            {
                error = AppLang.T("TodayInvoices.Msg.InvalidTime");
                return false;
            }

            if (parts.Length == 2)
            {
                hasMinute = true;
                if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)
                    || hour is < 0 or > 23 || minute is < 0 or > 59)
                {
                    error = AppLang.T("TodayInvoices.Msg.InvalidTime");
                    return false;
                }
                return true;
            }

            string token = parts.Length == 1 ? parts[0] : "";
            if (token.Length <= 2)
            {
                if (!int.TryParse(token, out hour) || hour is < 0 or > 23)
                {
                    error = AppLang.T("TodayInvoices.Msg.InvalidTime");
                    return false;
                }
                return true;
            }

            if (token.Length is 3 or 4)
            {
                hasMinute = true;
                if (!int.TryParse(token[..(token.Length - 2)], out hour) ||
                    !int.TryParse(token[^2..], out minute) ||
                    hour is < 0 or > 23 || minute is < 0 or > 59)
                {
                    error = AppLang.T("TodayInvoices.Msg.InvalidTime");
                    return false;
                }
                return true;
            }

            error = AppLang.T("TodayInvoices.Msg.InvalidTime");
            return false;
        }

        // ───────────────── القائمة: صفوف مرسومة ─────────────────

        private void RebuildRows(List<InvoiceVM> visible)
        {
            _list.SuspendLayout();
            foreach (Control c in _list.Controls)
                c.Dispose();
            _list.Controls.Clear();

            _rows.Clear();
            _selected = -1;
            _btnPrint.Enabled = false;

            if (visible.Count == 0)
            {
                var empty = new Label
                {
                    Text = AppLang.T("TodayInvoices.Empty"),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = AppTheme.TextMuted,
                    BackColor = AppTheme.Bg,
                    Font = new Font("Segoe UI", 11F)
                };
                _list.Controls.Add(empty);
                _list.ResumeLayout();
                return;
            }

            foreach (var inv in visible)
            {
                var row = new Row { Inv = inv };
                row.Panel = MakeRowPanel(row);
                _list.Controls.Add(row.Panel);
                _rows.Add(row);
            }
            RepositionRows();
            _list.ResumeLayout();
        }

        private Panel MakeRowPanel(Row row)
        {
            var panel = new Panel
            {
                BackColor = AppTheme.CardBg,
                Height = RowHeight,
                Cursor = Cursors.Hand
            };
            panel.Paint += (_, e) => DrawRow(panel, e.Graphics, row);
            panel.MouseDown += (_, _) => SelectRow(row);
            panel.MouseDoubleClick += async (_, _) => await PrintSelectedAsync();
            panel.MouseEnter += (_, _) => { row.Hover = true; panel.Invalidate(); };
            panel.MouseLeave += (_, _) => { row.Hover = false; panel.Invalidate(); };
            return panel;
        }

        private const int RowHeight = 64;
        private const int RowGap = 8;

        /// <summary>مواضع الصفوف يدويًا — التمرير عبر AutoScroll للوحة الحاوية.</summary>
        private void RepositionRows()
        {
            int w = _list.ClientSize.Width - 16;
            if (w < 100) return;
            int y = 2;
            foreach (var row in _rows)
            {
                row.Panel.SetBounds(8, y, w, RowHeight);
                row.Panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                y += RowHeight + RowGap;
            }
        }

        /// <summary>رسم صف واحد: رقم الفاتورة يمينًا، النوع والدفع، العميل،
        /// الوقت، والمبلغ يسارًا — المحاذاة تنعكس كاملة مع RTL.</summary>
        private void DrawRow(Panel panel, Graphics g, Row row)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, panel.Width - 1, panel.Height - 1);
            using var path = AppTheme.CreateRoundedPath(rect, 10);

            Color fill = _rows.IndexOf(row) == _selected
                ? Color.FromArgb(239, 246, 255)          // مختار: تلميح أزرق فاتح
                : row.Hover ? Color.FromArgb(248, 250, 252) : AppTheme.CardBg;
            using (var b = new SolidBrush(fill)) { g.FillPath(b, path); }
            using (var pen = new Pen(_rows.IndexOf(row) == _selected ? AppTheme.Primary : AppTheme.BorderColor))
            { g.DrawPath(pen, path); }

            var inv = row.Inv;
            bool isReturn = inv.Type == "customer_return" || inv.Type == "supplier_return";
            Color mainColor = isReturn ? AppTheme.Danger : AppTheme.TextPrimary;

            string no = AppLang.T("Common.Label.InvoiceNo", inv.InvoiceId);
            string type = InvoiceDetailsForm.TranslateType(inv.Type);
            string pay = InvoiceDetailsForm.TranslatePayment(inv.PaymentStatus);
            string party = inv.PartyName ?? AppLang.T("Common.Party.WalkIn");
            string time = AppClock.ToLocal(inv.CreatedAt ?? DateTime.MinValue).ToString("HH:mm");
            string amount = UiHelpers.Money(inv.NetAmount);

            var fontNo = new Font("Segoe UI", 10.5F, FontStyle.Bold);
            var fontSmall = new Font("Segoe UI", 8.5F);
            var fontBody = new Font("Segoe UI", 10F);
            var fontAmount = new Font("Segoe UI", 11F, FontStyle.Bold);
            try
            {
                int xRight = panel.Width - 14;
                int cy = panel.Height / 2;

                TextRenderer.DrawText(g, no, fontNo, new Point(xRight, cy), mainColor,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Default);
                int xNo = xRight - TextRenderer.MeasureText(no, fontNo).Width - 14;

                TextRenderer.DrawText(g, $"{type}  •  {pay}", fontSmall, new Point(xNo, cy), AppTheme.TextMuted,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Default);
                int xType = xNo - TextRenderer.MeasureText($"{type}  •  {pay}", fontSmall).Width - 14;

                TextRenderer.DrawText(g, party, fontBody, new Point(xType, cy), mainColor,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Default);

                TextRenderer.DrawText(g, time, fontBody, new Point(150, cy), AppTheme.TextMuted,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Default);
                TextRenderer.DrawText(g, amount, fontAmount, new Point(14, cy), mainColor,
                    TextFormatFlags.RightToLeft | TextFormatFlags.Default);
            }
            finally
            {
                fontNo.Dispose(); fontSmall.Dispose(); fontBody.Dispose(); fontAmount.Dispose();
            }
        }

        private void SelectRow(Row row)
        {
            int idx = _rows.IndexOf(row);
            if (idx == _selected) return;
            _selected = idx;
            _btnPrint.Enabled = true;
            foreach (var r in _rows) r.Panel.Invalidate();
        }

        private async Task PrintSelectedAsync()
        {
            if (_selected < 0 || _selected >= _rows.Count)
            {
                UiHelpers.ShowInfo(this, AppLang.T("TodayInvoices.NoSelection"));
                return;
            }

            // نسخة إضافية: اختيار الصيغة أولًا (حرارية / A4) ثم الطباعة بالمسار الموحّد
            var fmt = InvoiceFormatPicker.Show(this, allowSkip: false);
            if (fmt.HasValue)
                await InvoicePrintHelper.PrintAsync(_rows[_selected].Inv.InvoiceId, this, fmt.Value);
        }
    }
}
