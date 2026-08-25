using System.ComponentModel;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// أساس النوافذ المنبثقة بلا حدود: زوايا دائرية، إطار ظاهر يفصل النافذة عن الخلفية،
    /// وظل نظامي (Drop Shadow) يعطي عمقًا. الأصناف الوارثة تضبط CornerRadius عند الحاجة.
    /// </summary>
    public class DialogForm : Form
    {
        private const int CS_DROPSHADOW = 0x20000;
        private Control? _ring;

        /// <summary>نصف قطر تدوير الزوايا.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CornerRadius { get; set; } = 18;

        /// <summary>لون خط الإطار الفاصل.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get; set; } = Color.FromArgb(148, 163, 184);

        /// <summary>سماكة الإطار بالبكسل.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int BorderWidth { get; set; } = 2;

        public DialogForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            // استجابة للشاشات الصغيرة: عند تصغير النافذة تظهر أشرطة تمرير بدل قصّ المحتوى.
            AutoScroll = true;
            MinimumSize = new Size(360, 280);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        // ── تغيير حجم النافذة بلا حدود: نكتشف حواف المؤشر عبر WM_NCHITTEST ──
        private const int WM_NCHITTEST = 0x84;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeGripSize = 7;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int ht = HitTestResizeEdges(m.LParam);
                if (ht != HTCLIENT)
                {
                    m.Result = (IntPtr)ht;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private int HitTestResizeEdges(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            var r = ClientRectangle;

            bool left = pt.X <= ResizeGripSize;
            bool right = pt.X >= r.Width - ResizeGripSize;
            bool top = pt.Y <= ResizeGripSize;
            bool bottom = pt.Y >= r.Height - ResizeGripSize;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        protected override void OnLoad(EventArgs e)
        {
            // 1) لا تسمح للنافذة أن تكون أكبر من مساحة عمل الشاشة (يمنع قصّ الأزرار السفلية).
            ClampToWorkingArea();

            // 2) ثبّت أزرار الإجراءات أسفل النافذة حتى تبقى ظاهرة عند التصغير.
            PinBottomButtons();

            // نطبّق الهيكل قبل معالجات Load للوراثة/المستهلكين ليجدوا الشكل النهائي جاهزًا.
            AppTheme.ApplyRoundedRegion(this, CornerRadius);
            EnsureBorderRing();
            base.OnLoad(e);
        }

        private void ClampToWorkingArea()
        {
            var area = Screen.FromControl(this).WorkingArea;
            int maxW = (int)(area.Width * 0.94);
            int maxH = (int)(area.Height * 0.90);

            if (Width > maxW || Height > maxH)
                Size = new Size(Math.Min(Width, maxW), Math.Min(Height, maxH));
        }

        /// <summary>
        /// الأزرار الموضوعة أسفل النافذة (أكتر من 60% من الارتفاع) ترتبط بالحافة السفلية
        /// بحيث تبقى في متناول اليد عند تصغير النافذة بدل أن تُقصّ خارجًا.
        /// </summary>
        private void PinBottomButtons()
        {
            int threshold = (int)(ClientSize.Height * 0.60);
            foreach (Control c in Controls)
            {
                if (ReferenceEquals(c, _ring) || c is not Button || c.Dock != DockStyle.None)
                    continue;
                if (c.Bounds.Bottom >= threshold && !c.Anchor.HasFlag(AnchorStyles.Bottom))
                    c.Anchor |= AnchorStyles.Bottom;
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (_ring != null && !ReferenceEquals(e.Control, _ring))
                Controls.SetChildIndex(_ring, 0);
        }

        /// <summary>
        /// طبقة إطار مجوّفة (حلقة): تُرسم فوق كل العناصر لكن ثقبها الأوسط يمرّر النقرات للمحتوى.
        /// </summary>
        private void EnsureBorderRing()
        {
            if (_ring != null) return;

            _ring = new Control
            {
                BackColor = BorderColor,
                Enabled = false
            };

            UpdateRing();
            Controls.Add(_ring);
            Controls.SetChildIndex(_ring, 0);
            Resize += (_, _) => UpdateRing();
        }

        private void UpdateRing()
        {
            if (_ring == null) return;

            var r = ClientRectangle;
            _ring.SetBounds(r.X, r.Y, r.Width, r.Height);

            using var outer = AppTheme.CreateRoundedPath(
                new Rectangle(0, 0, r.Width, r.Height), CornerRadius);

            int w = Math.Min(BorderWidth, Math.Min(r.Width, r.Height) / 4);
            if (w <= 0) { _ring.Region = null; return; }

            using var inner = AppTheme.CreateRoundedPath(
                new Rectangle(w, w, r.Width - 2 * w, r.Height - 2 * w),
                Math.Max(CornerRadius - w, 1));

            outer.FillMode = System.Drawing.Drawing2D.FillMode.Alternate;
            outer.AddPath(inner, false);
            _ring.Region?.Dispose();
            _ring.Region = new Region(outer);
        }
    }
}
