using Sayl.UI.Infrastructure;

namespace Sayl.UI.Controls
{
    /// <summary>
    /// شريط ترقيم الصفحات الموحد — يُرسى أسفل أي قائمة:
    /// أزرار تنقل (أولى/سابق/تالي/أخيرة) + عداد «صفحة X من Y» + اختيار
    /// حجم الصفحة (10/20/50). لا يحمل حالة بحد ذاته — المصدر هو حقل
    /// _currentPage/_pageSize في المستضيف (SectionListControl أو الفورم):
    ///   • PageRequested(page)  → المستضيف يعيد تحميل الصفحة.
    ///   • PageSizeSelected(size) → المستضيف يضبط الحجم ويعود لصفحة 1.
    /// عرض البيانات فعل المستضيف؛ هذا الشريط يعرض الحالة بعد كل تحميل.
    /// </summary>
    public class PagerControl : UserControl
    {
        private readonly Label _lblInfo = new();
        private readonly ComboBox _cmbSize = new();
        private readonly Button _btnFirst, _btnPrev, _btnNext, _btnLast;
        private bool _suppress;

        /// <summary>طلب تنقّل: قيمته الصفحة المطلوبة (1-based) — المستضيف يعيد التحميل.</summary>
        public event Action<int>? PageRequested;

        /// <summary>تغيير حجم الصفحة: قيمة الحجم الجديد — المستضيف يعود لصفحة 1.</summary>
        public event Action<int>? PageSizeSelected;

        /// <summary>حجم الصفحة الحالي (عنصر التحكم هو المصدر الوحيد له).</summary>
        public int PageSize => (int)_cmbSize.SelectedItem;

        public int CurrentPage { get; private set; } = 1;
        public int TotalPages { get; private set; } = 1;
        public int TotalCount { get; private set; }

        public PagerControl()
        {
            Dock = DockStyle.Bottom;
            Height = 38;
            BackColor = AppTheme.CardBg;
            Padding = new Padding(8, 4, 8, 4);

            // ── أزرار التنقل: مجموعة يسرى ثابتة (أولى/سابق/تالي/أخيرة) ──
            // AutoSize على الحاوية والأزرار: عرض عربي (الأولى/السابق…) أطول
            // من الإنكليزية — الحجم الثابت كان يقتطع النص. الأزرار تتمدد مع النص.
            var nav = new Panel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(0, 2, 0, 2)
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = AppTheme.CardBg
            };

            (_btnFirst, _btnPrev, _btnNext, _btnLast) = (MakeNavButton(), MakeNavButton(), MakeNavButton(), MakeNavButton());
            _btnFirst.Text = AppLang.T("Paging.First");
            _btnPrev.Text = AppLang.T("Paging.Prev");
            _btnNext.Text = AppLang.T("Paging.Next");
            _btnLast.Text = AppLang.T("Paging.Last");

            _btnFirst.Click += (_, _) => RequestPage(1);
            _btnPrev.Click += (_, _) => RequestPage(CurrentPage - 1);
            _btnNext.Click += (_, _) => RequestPage(CurrentPage + 1);
            _btnLast.Click += (_, _) => RequestPage(TotalPages);

            foreach (var b in new[] { _btnFirst, _btnPrev, _btnNext, _btnLast })
                flow.Controls.Add(b);
            nav.Controls.Add(flow);

            // ── حجم الصفحة: مجموعة يسرى (عنوان + قائمة 10/20/50) ──
            var sizePanel = new Panel { Dock = DockStyle.Left, Width = 165, BackColor = AppTheme.CardBg, Padding = new Padding(0, 2, 0, 2) };
            var sizeFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = AppTheme.CardBg
            };

            var lblSize = new Label
            {
                Text = AppLang.T("Paging.Label"),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 7, 0, 0),
                ForeColor = AppTheme.TextMuted
            };
            _cmbSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSize.FlatStyle = FlatStyle.Flat;
            _cmbSize.Font = AppTheme.FontBody;
            _cmbSize.Items.AddRange(new object[] { 10, 20, 50 });
            _cmbSize.SelectedItem = 20;
            _cmbSize.Width = 58;
            _cmbSize.Margin = new Padding(6, 5, 0, 5);
            _cmbSize.SelectedValueChanged += (_, _) =>
            {
                if (_suppress) return;
                PageSizeSelected?.Invoke(PageSize);
            };

            sizeFlow.Controls.Add(lblSize);
            sizeFlow.Controls.Add(_cmbSize);
            sizePanel.Controls.Add(sizeFlow);

            // ── العداد: الوسط (يملأ الفراغ) ──
            _lblInfo.Dock = DockStyle.Fill;
            _lblInfo.TextAlign = ContentAlignment.MiddleRight;
            _lblInfo.ForeColor = AppTheme.TextMuted;
            _lblInfo.Font = AppTheme.FontBody;

            Controls.Add(nav);
            Controls.Add(_lblInfo);
            Controls.Add(sizePanel);
        }

        private static Button MakeNavButton()
        {
            var b = new Button
            {
                FlatStyle = FlatStyle.Flat,
                BackColor = AppTheme.BorderColor,
                ForeColor = AppTheme.TextPrimary,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(48, 28),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(2),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AppTheme.PrimaryLight;
            return b;
        }

        private void RequestPage(int page)
        {
            int p = Math.Clamp(page, 1, TotalPages);
            if (p == CurrentPage) return;
            CurrentPage = p;
            PageRequested?.Invoke(p);
        }

        /// <summary>
        /// يعرض حالة الترقيم بعد كل تحميل. TotalPages لا يقل عن 1
        /// (قائمة فارغة = «صفحة 1 من 1») — الأزرار تُعطَّل عند الأطراف.
        /// </summary>
        public void SetNavigation(int totalCount, int currentPage)
        {
            TotalCount = totalCount;
            TotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)Math.Max(1, PageSize)));
            CurrentPage = Math.Clamp(currentPage, 1, TotalPages);

            _suppress = true;
            _lblInfo.Text = AppLang.T("Paging.Info", CurrentPage, TotalPages, TotalCount);
            _btnFirst.Enabled = _btnPrev.Enabled = CurrentPage > 1;
            _btnNext.Enabled = _btnLast.Enabled = CurrentPage < TotalPages;
            _suppress = false;
        }
    }
}