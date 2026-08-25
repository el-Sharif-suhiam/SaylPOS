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

        /// <summary>عرض نافذة جديدة وإخفاء الحالية (الإغلاق يبقى حدثًا عند إغلاق النافذة الحالية فقط).</summary>
        public void Show(Form next)
        {
            var previous = _current;
            _current = next;

            next.FormClosed += OnCurrentFormClosed;
            next.Show();
            previous?.Hide();
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
