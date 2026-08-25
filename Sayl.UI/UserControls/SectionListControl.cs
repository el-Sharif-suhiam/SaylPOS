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

        protected abstract string Title { get; }
        protected virtual bool HasSearch => true;

        protected SectionListControl()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.Bg;
        }

        protected sealed override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (Controls.Count == 0)
            {
                BuildUi();
                BuildToolbar();
            }
            _ = LoadDataAsync();
        }

        /// <summary>يُستدعى بعد بناء الواجهة لإضافة أزرار شريط الأدوات.</summary>
        protected virtual void BuildToolbar()
        {
        }

        protected abstract Task LoadDataAsync();

        protected virtual void OnSearch() => _ = LoadDataAsync();

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

        protected void AddToolbarGap(int width = 8)
        {
            var spacer = new Panel { Dock = DockStyle.Left, Width = width, BackColor = Color.Transparent };
            _toolbar.Controls.Add(spacer);
            spacer.BringToFront();
        }

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
            TxtSearch.PlaceholderText = "بحث... ثم Enter";
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

            Controls.Add(Grid);
            Controls.Add(_toolbar);
        }
    }
}
