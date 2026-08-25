using System.Drawing.Drawing2D;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// الهوية البصرية للعلامة: الشعار والأيقونة من الموارد المضمّنة + الألوان الرسمية.
    /// Brand Colors: #004080 كحلي، #FF8C00 برتقالي، #FFFFFF أبيض.
    /// </summary>
    internal static class Brand
    {
        /// <summary>الشعار بخلفية شفافة (موجة + سهم + ميزان).</summary>
        public static Image? Logo
        {
            get
            {
                if (_logo != null) return _logo;
                try
                {
                    using var s = Open("sayl-logo.png");
                    _logo = Image.FromStream(s); // يبقى الـ Stream حيًّا طوال عمر التطبيق
                }
                catch { _logo = null; }
                return _logo;
            }
        }
        private static Image? _logo;

        /// <summary>أيقونة التطبيق لشرائط النوافذ وشريط المهام.</summary>
        public static Icon? AppIcon
        {
            get
            {
                if (_icon != null) return _icon;
                try
                {
                    using var s = Open("sayl.ico");
                    using var tmp = new Icon(s);
                    _icon = (Icon)tmp.Clone();
                }
                catch { _icon = null; }
                return _icon;
            }
        }
        private static Icon? _icon;

        private static Stream? Open(string assetName) =>
            typeof(Brand).Assembly.GetManifestResourceStream($"Sayl.UI.Assets.{assetName}");

        /// <summary>PictureBox جاهز لعرض الشعار بحجم محدد مع الحفاظ على النسبة.</summary>
        public static PictureBox MakeLogo(int size, Color parentBack)
        {
            return new PictureBox
            {
                Size = new Size(size, size),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = parentBack,
                Image = Logo
            };
        }

        /// <summary>رسم دائري أبيض خلف الشعار عند وضعه على خلفية داكنة (يُستدعى من Paint).</summary>
        public static void DrawLogoCircle(Graphics g, Rectangle rect, Color circleColor)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(circleColor);
            g.FillEllipse(b, rect);
        }
    }
}
