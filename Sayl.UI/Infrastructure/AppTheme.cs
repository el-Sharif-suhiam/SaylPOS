using System.Drawing.Drawing2D;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// الهوية البصرية الموحدة للتطبيق: ألوان، خطوط، ومساعدات تنسيق.
    /// </summary>
    internal static class AppTheme
    {
        // ── الألوان ──
        public static readonly Color Primary = Color.FromArgb(37, 99, 235);
        public static readonly Color PrimaryDark = Color.FromArgb(29, 78, 216);
        public static readonly Color PrimaryLight = Color.FromArgb(219, 234, 254);
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color CardBg = Color.White;
        public static readonly Color SidebarBg = Color.FromArgb(17, 24, 39);
        public static readonly Color SidebarHover = Color.FromArgb(31, 41, 55);
        public static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
        public static readonly Color FieldBg = Color.FromArgb(249, 250, 251);
        public static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        public static readonly Color TextOnDark = Color.FromArgb(229, 231, 235);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
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
    }
}
