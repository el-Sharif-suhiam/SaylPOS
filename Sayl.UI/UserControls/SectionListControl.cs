using Common.DTOs;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>
    /// أساس أقسام العرض/الإدارة: شريط أدوات (بحث + أزرار) وجدول بيانات.
    /// الأقسام الفرعية تضيف أزرارها وتنفذ LoadDataAsync.
    /// </summary>
    internal abstract class SectionListControl : UserControl
    {
        protected DataGridView Grid = null!;
        protected TextBox TxtSearch = null!;
        protected readonly Panel _toolbar = new();
        protected readonly PagerControl _pager = new();

        // ── حالة الترقيم: المصدر الوحيد في القسم (الشريط يعرض ما هنا) ──
        protected int _currentPage = 1;
        protected int _pageSize = 20;

        /// <summary>الأقسام التي تدعم soft-delete: مفتاح «عرض المعطّلين» يظهر في شريط الأدوات.</summary>
        protected virtual bool SupportsShowDeleted => false;

        /// <summary>الافتراضي: تُعرض السجلات النشطة فقط — المفتاح يظهر المعطّلين/المحذوفين.</summary>
        protected bool ShowInactive { get; private set; }

        protected abstract string Title { get; }
        protected virtual bool HasSearch => true;

        protected SectionListControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 نمط «القسم الجاهز» — أساس كل أقسام العرض/الإدارة بالنظام:
        //
        // أي UserControl يرث هذه الفئة يحصل مجانًا على: شريط أدوات
        // (عنوان يمين + بحث يساره) + جدول DataGrid يملأ الباقي + بحث
        // Enter + مفتاح «المعطلون» اختياريًا. الابن يفعل ثلاثة أشياء فقط:
        //   1. Title => "اسم القسم"
        //   2. override BuildToolbar() + AddToolbarButton(...)
        //   3. override LoadDataAsync() — يعبّئ Grid
        //
        // ⚙️ آلية البناء «المؤجل»: كل شيء يُبنى في OnLoad لا المنشئ —
        // لأن الابن يضبط Title/أزراره في المنشئ قبل أن يبني الأساس.
        // (Controls.Count == 0 حارس يمنع البناء المزدوج عند إعادة التحميل.)
        //
        // 🎯 الحيلة الأهم — AddToolbarButton و Dock=Left + BringToFront:
        //  الأزرار تُرسى يسارًا لكن «آخر Add» يُرسّى أولًا... بدل عكس
        //  ترتيب الأسطر يدويًا في كل قسم، نجعل BringToFront يقلب الترتيب
        //  بعد كل إضافة: النتيجة = أول AddToolbarButton بالكود يظهر
        //  أقصى اليمين بصريًا (أقرب للعنوان) — الكود يقرأ كترتيب بشري.
        // ═══════════════════════════════════════════════════════════════
        protected sealed override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (Controls.Count == 0)
            {
                BuildUi();
                BuildToolbar();
                if (SupportsShowDeleted)
                    BuildShowDeletedToggle();
            }
            _ = LoadDataAsync();
        }

        /// <summary>يُستدعى بعد بناء الواجهة لإضافة أزرار شريط الأدوات.</summary>
        protected virtual void BuildToolbar()
        {
        }

        protected abstract Task LoadDataAsync();

        protected virtual void OnSearch()
        {
            _currentPage = 1; // بحث جديد = من أول صفحة
            _ = LoadDataAsync();
        }
        // ^ «_ =» discard: استدعاء async بلا انتظار (fire-and-forget) —
        // يمنع تحذير المترجم دون async void (التي تبتلع الاستثناءات).

        /// <summary>
        /// بوابة الترقيم الموحدة — كل قسم يستدعيها بعد الصفحة المؤرشفة:
        ///   • صفحة ناقصة (حُذف آخر سجلات الصفحة) → يصحّح العداد ويعيد
        ///     التحميل من الصفحة الصحيحة ويعيد false (لا تُربط بيانات هذه الدفعة).
        ///   • صفحة سليمة → يحدّث _currentPage/_pageSize ويعرض حالة الشريط
        ///     ويعيد true، فيربط الابن Items فقط ويبني الجدول.
        /// </summary>
        protected bool ApplyPaged<T>(PagedResult<T> paged)
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling(paged.TotalCount / (double)Math.Max(1, paged.PageSize)));
            if (paged.Page > totalPages)
            {
                _currentPage = totalPages;
                _ = LoadDataAsync();
                return false;
            }

            _currentPage = paged.Page;
            _pageSize = paged.PageSize;
            _pager.SetNavigation(paged.TotalCount, _currentPage);
            return true;
        }

        // 📖 AddToolbarButton: زر شريط أدوات قسم — Height=42 (لمس مريح)،
        // Dock=Left + BringToFront (انظر الحيلة أعلى الفصل). Width معامل:
        // 130 الافتراضي يكفي أزرارًا قصيرة؛ مرر 150+ لنص أطول.
        protected void AddToolbarButton(string text, Color back, Color hover, EventHandler onClick, int width = 130)
        {
            var btn = AppTheme.MakeButton(text, back, hoverColor: hover, height: 42);
            btn.Dock = DockStyle.Left;
            btn.Width = width;
            btn.Margin = new Padding(4, 0, 0, 0);
            btn.Padding = new Padding(4, 0, 4, 0);
            btn.Click += onClick;
            _toolbar.Controls.Add(btn);
            btn.BringToFront();
        }

        // 📖 AddToolbarGap: فاصل فارغ (Panel شفاف بعرض 8) — نفس حيلة
        // BringToFront: يظهر مسافة بين مجموعة أزرار ومجموعة تالية.
        protected void AddToolbarGap(int width = 8)
        {
            var spacer = new Panel { Dock = DockStyle.Left, Width = width, BackColor = Color.Transparent };
            _toolbar.Controls.Add(spacer);
            spacer.BringToFront();
        }

        /// <summary>
        /// مفتاح «عرض المعطّلين» — حجم ممتاز للمس (44px)، بحالة مرئية واضحة:
        /// افتراضيًا مطفأ (النشطون فقط). عند تفعيله تتحدث القائمة فورًا.
        /// يظهر فقط في الأقسام التي تدعم soft-delete (SupportsShowDeleted).
        /// </summary>
        private void BuildShowDeletedToggle()
        {
            var chk = new CheckBox
            {
                Text = AppLang.T("Section.ShowInactive"),
                Dock = DockStyle.Left,
                Width = 150,
                Height = 44,
                TextAlign = ContentAlignment.MiddleRight,
                Checked = false,
                FlatStyle = FlatStyle.Standard,
                ForeColor = AppTheme.TextPrimary,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(6, 6, 6, 6)
            };

            chk.CheckedChanged += (_, _) =>
            {
                ShowInactive = chk.Checked;
                chk.ForeColor = chk.Checked ? AppTheme.Warning : AppTheme.TextPrimary;
                _currentPage = 1;
                _ = LoadDataAsync();
            };

            _toolbar.Controls.Add(chk);
            chk.BringToFront();
        }

        // ═══ الأساس المشترك: شريط أدوات Top (66px) + جدول Fill ═══
        // الشريط: بحث Dock=Right عرض 320 ثم عنوان Dock=Right يمينه —
        // (RTL: أول Add = أقصى اليمين، فالإضافة بالعكس: البحث أولا.)
        // الجدول: Grid بـ Dock=Fill يُضاف قبل الشريط (يرسو آخرًا) —
        // فالشريط يأخذ Top والجدول يملأ تحته.
        private void BuildUi()
        {
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = 66;
            _toolbar.BackColor = AppTheme.CardBg;
            _toolbar.Padding = new Padding(12, 12, 12, 8);

            TxtSearch = new TextBox
            {
                Dock = DockStyle.Right,
                Width = 320,
                Font = AppTheme.FontBody
            };
            TxtSearch.PlaceholderText = AppLang.T("Section.Search.Placeholder");
            TxtSearch.KeyDown += (_, args) =>
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    OnSearch();
                }
            };
            _toolbar.Controls.Add(TxtSearch);

            var lblTitle = new Label
            {
                Text = Title,
                Dock = DockStyle.Right,
                Width = 160,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = AppTheme.TextPrimary,
                Padding = new Padding(0, 0, 8, 0)
            };
            _toolbar.Controls.Add(lblTitle);

            Grid = new DataGridView { Dock = DockStyle.Fill, BackColor = AppTheme.CardBg, Padding = new Padding(12) };
            UiHelpers.StyleGrid(Grid);

            // شريط الترقيم: أسفل القائمة — تنقّل يعيد التحميل، وتغيير
            // الحجم يعود لصفحة 1. الحالة الفعلية يعرضها ApplyPaged بعد كل تحميل.
            _pager.PageRequested += page =>
            {
                _currentPage = page;
                _ = LoadDataAsync();
            };
            _pager.PageSizeSelected += size =>
            {
                _pageSize = size;
                _currentPage = 1;
                _ = LoadDataAsync();
            };

            // ترتيب الإرساء الخلفي: الشريط (أعلى) ← الصفحات (أسفل) ← الجدول (يملأ)
            Controls.Add(Grid);
            Controls.Add(_pager);
            Controls.Add(_toolbar);
        }
    }
}
