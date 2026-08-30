using System.Drawing;
using System.Windows.Forms;

namespace PosLicensing.KeygenTool
{
    /// <summary>
    /// هوية بصرية مصغّرة مطابقة لهوية سيل (كحلي #004080 + برتقالي #FF8C00)
    /// — نسخة مستقلة عن Sayl.UI حتى تبقى الأداة مشروعاً منفصلاً.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Primary = Color.FromArgb(0, 64, 128);
        public static readonly Color PrimaryDark = Color.FromArgb(0, 51, 95);
        public static readonly Color PrimaryLight = Color.FromArgb(220, 233, 247);
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color CardBg = Color.White;
        public static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
        public static readonly Color FieldBg = Color.FromArgb(249, 250, 251);
        public static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);

        public static Font FontTitle => new("Segoe UI", 16F, FontStyle.Bold);
        public static Font FontHeading => new("Segoe UI", 11F, FontStyle.Bold);
        public static Font FontBody => new("Segoe UI", 10F);
        public static Font FontSmall => new("Segoe UI", 8.5F);
        public static Font FontButton => new("Segoe UI", 10.5F, FontStyle.Bold);
        public static Font FontMono => new("Consolas", 9F);
        public static Font FontMonoBig => new("Consolas", 13F, FontStyle.Bold);

        public const int TouchMin = 44;

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

        public static Label MakeLabel(string text, Font font = null, Color? color = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font ?? FontBody,
                ForeColor = color ?? TextPrimary
            };
        }

        public static TextBox MakeField(bool multiline = false, Font font = null)
        {
            return new TextBox
            {
                Multiline = multiline,
                BackColor = FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = font ?? FontBody
            };
        }
    }
}
