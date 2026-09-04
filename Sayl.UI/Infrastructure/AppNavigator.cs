namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// إدارة التنقل بين نوافذ المستوى الأعلى (تسجيل الدخول ⇄ الواجهات الرئيسية).
    /// يبقى التطبيق حيًّا ما دامت النافذة الحالية مفتوحة، ويخفي النافذة السابقة عند فتح التالية.
    /// </summary>
    internal class AppNavigator : ApplicationContext
    {
        private Form? _current;

        public AppNavigator(Form initial)
        {
            Show(initial);
        }

        /// <summary>عرض نافذة جديدة وإخفاء الحالية (الإغلاق يبقى حدثًا عند إغلاق النافذة الحالية فقط).
        /// VULN-39: النافذة السابقة كانت تُخفى فقط فتبقى بذاكرتها كاملة
        /// (MainForm محمولة الجداول) لكل دورة دخول/خروج — تسريب بطيء على
        /// جهاز كاشير مشترك. الآن: LoginForm المرجعة تُتلف (كشف الدخول لا
        /// يخفي عمدًا أبدًا — إن أُخفيت فلموتها آمن)، والنماذج الكبيرة
        /// تُخفى وتُتلف عند إغلاق بديلتها.</summary>
        public void Show(Form next)
        {
            var previous = _current;
            _current = next;

            next.FormClosed += OnCurrentFormClosed;
            next.Show();

            if (previous != null && !ReferenceEquals(previous, next))
            {
                // إخفاء أولًا (قطع المرئية) ثم تلف — النموذج القديم لن يعود
                previous.Hide();
                previous.Dispose();
            }
        }

        private void OnCurrentFormClosed(object? sender, FormClosedEventArgs e)
        {
            if (!ReferenceEquals(sender, _current))
                return;

            _current = null;
            ExitThread();
        }
    }
}
