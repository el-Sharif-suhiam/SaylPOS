using System.Drawing.Drawing2D;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// الهوية البصرية الموحدة للتطبيق: ألوان، خطوط، ومساعدات تنسيق.
    /// </summary>
    internal static class AppTheme
    {
        // ── الألوان (هوية سيل: كحلي #004080 + برتقالي #FF8C00) ──
        public static readonly Color Primary = Color.FromArgb(0, 64, 128);          // #004080
        public static readonly Color PrimaryDark = Color.FromArgb(0, 51, 95);       // #00335F
        public static readonly Color PrimaryLight = Color.FromArgb(220, 233, 247);  // تدرج فاتح كحلي
        public static readonly Color Accent = Color.FromArgb(255, 140, 0);          // #FF8C00
        public static readonly Color AccentDark = Color.FromArgb(216, 118, 0);      // #D87600
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color CardBg = Color.White;
        public static readonly Color SidebarBg = Color.FromArgb(0, 43, 84);         // #002B54
        public static readonly Color SidebarHover = Color.FromArgb(11, 59, 105);    // #0B3B69
        public static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
        public static readonly Color FieldBg = Color.FromArgb(249, 250, 251);
        public static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        public static readonly Color TextOnDark = Color.FromArgb(229, 231, 235);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color SuccessDark = Color.FromArgb(15, 114, 51);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);

        // ── الخطوط (Segoe UI يدعم العربية جيدًا) ──
        public static Font FontTitle => new("Segoe UI", 18F, FontStyle.Bold);
        public static Font FontHeading => new("Segoe UI", 12.5F, FontStyle.Bold);
        public static Font FontBody => new("Segoe UI", 10F);
        public static Font FontButton => new("Segoe UI", 11F, FontStyle.Bold);

        // ── مقاسات ──
        /// <summary>أصغر مقاس ملموس للأزرار (متطلبات اللمس).</summary>
        public const int TouchMin = 48;
        public const int Radius = 14;

        public static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(new Rectangle(0, 0, 1, 1));
                return path;
            }

            radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
            int d = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(d, d));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>تدوير زوايا أي عنصر تحكم مع التحديث التلقائي عند تغيّر المقاس.</summary>
        public static void ApplyRoundedRegion(Control control, int radius = Radius)
        {
            void Update()
            {
                var old = control.Region;
                using var path = CreateRoundedPath(control.ClientRectangle, radius);
                control.Region = new Region(path);
                old?.Dispose();
            }

            control.Resize += (_, _) => Update();
            Update();
        }

        public static Button MakeButton(string text, Color backColor, Color? foreColor = null,
            Color? hoverColor = null, int height = TouchMin)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = foreColor ?? Color.White,
                Font = FontButton,
                Height = height,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hoverColor ?? ControlPaint.Dark(backColor, 0.05f);
            b.FlatAppearance.MouseDownBackColor = hoverColor ?? ControlPaint.Dark(backColor, 0.1f);
            return b;
        }

        /// <summary>
        /// زر دائري مثالي مرسوم بتنعيم كامل (Anti-Alias) بدل قصّ Region المسنن.
        /// parentBack يطابق لون خلفية النافذة لتبقى زوايا المربع غير مرئية.
        /// </summary>
        public static Button MakeCircleButton(string text, int diameter, Color back, Color hoverBack,
            Color fore, Color parentBack, float fontSize = 13f)
        {
            bool hovered = false;
            var b = new Button
            {
                Text = text,
                Size = new Size(diameter, diameter),
                FlatStyle = FlatStyle.Flat,
                ForeColor = fore,
                BackColor = parentBack,
                Font = new Font("Segoe UI", fontSize, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = parentBack;
            b.FlatAppearance.MouseDownBackColor = parentBack;

            b.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(1, 1, b.Width - 3, b.Height - 3);
                using var brush = new SolidBrush(hovered ? hoverBack : back);
                g.FillEllipse(brush, rect);
                TextRenderer.DrawText(g, text, b.Font, rect, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            };
            b.MouseEnter += (_, _) => { hovered = true; b.Invalidate(); };
            b.MouseLeave += (_, _) => { hovered = false; b.Invalidate(); };
            return b;
        }

        /// <summary>
        /// زر تبديل اللغة (عربي/English) — كبسولة متناظرة (زوايا دائرية كاملة)
        /// بحجم ثابت 64×34 تعرض لغة الهدف «Eng» أو «Ara» — النقر يحفظ
        /// الاختيار ويعيد تشغيل البرنامج. الرسم متجهي Anti-Alias كامل
        /// (نفس أسلوب أزرار «؟» و«×») فيظهر مستديرًا تمامًا في كل الدقة.
        /// مصدر واحد لثلاث شاشات: الدخول/الرئيسية/POS.
        /// </summary>
        public static Button MakeLanguageSwitchButton()
        {
            bool toEnglish = !AppLang.IsEnglish;
            string display = toEnglish ? "Eng" : "Ara";

            var b = new Button
            {
                Size = new Size(64, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;

            bool hovered = false;

            b.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // الكبسولة: مستطيل بارتفاع كامل وعرض ناقص الارتفاع (زوايا نصف دائرة)
                // — نصف القطر = الارتفاع÷2 يعطي تناظرًا مثاليًا في الزوايا الأربع.
                int h = b.Height - 3;                       // 31 — نصف قطر الزاوية
                int w = b.Width - 3;                        // 61 — جسم الكبسولة
                var body = new Rectangle(1, 1, w, h);
                using var path = CreateRoundedPath(body, h / 2);

                // الحشوة: كحلي داكن → أفتح قليلًا عند المرور (hover)
                using var fill = new SolidBrush(hovered ? Color.FromArgb(45, 90, 150) : Color.FromArgb(28, 66, 110));
                g.FillPath(fill, path);

                using var border = new Pen(hovered ? Color.FromArgb(147, 197, 253) : Color.FromArgb(191, 219, 254), 1.6f);
                g.DrawPath(border, path);

                using var font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
                TextRenderer.DrawText(g, display, font, body, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            };
            b.MouseEnter += (_, _) => { hovered = true; b.Invalidate(); };
            b.MouseLeave += (_, _) => { hovered = false; b.Invalidate(); };
            b.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    AppLang.T("Lang.Msg.RestartBody"),
                    AppLang.T("Lang.Msg.RestartTitle"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                if (confirm == DialogResult.Yes)
                    AppLang.SwitchAndRestart(toEnglish ? AppLanguage.English : AppLanguage.Arabic);
            };
            return b;
        }
    }
}
