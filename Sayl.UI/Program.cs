using DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI
{
    internal static class Program
    {
        public static IServiceProvider Services { get; private set; } = default!;
        public static AppNavigator Navigator { get; private set; } = default!;

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // ترميزات Windows (CP1256 للعربية على الطابعات الحرارية)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // تسجيل الأخطاء غير المتوقعة في ملف للتشخيص + إظهارها للمستخدم (لا فشل صامت)
            Application.ThreadException += (_, e) =>
            {
                LogError("THREAD", e.Exception);
                ShowUnexpectedError(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogError("UNHANDLED", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            // لغة الواجهة: تُقرأ قبل بناء أي نافذة وتبقى ثابتة طوال الجلسة
            // (التبديل = تغيير "Language" في appsettings + إعادة تشغيل).
            // أي قيمة غير "en" = عربي — القاعدة الآمنة.
            AppLang.Init(string.Equals(configuration["Language"], "en",
                StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.English
                : AppLanguage.Arabic);

            var connectionString = configuration.GetConnectionString("SaylDb")
                ?? throw new InvalidOperationException("لم يتم العثور على جملة الاتصال 'SaylDb' في appsettings.json.");

            var services = new ServiceCollection();
            services.AddSaylServices(connectionString);

            // مصنع LoginForm: يربط حدث النجاح بكل نسخة جديدة (بعد تسجيل الخروج تُبنى نسخة جديدة).
            services.AddTransient<LoginForm>(_ =>
            {
                var login = new LoginForm();
                login.LoginSucceeded += _ => NavigateAfterLogin();
                return login;
            });

            services.AddTransient<MainForm>();
            services.AddTransient<PosForm>();
            services.AddTransient<AboutForm>();
            services.AddTransient<ReceiveStockForm>();

            Services = services.BuildServiceProvider();

            // بوابة التفعيل: لا يعمل البرنامج إلا بعد تفعيل صالح لهذا الجهاز
            if (!PosLicensing.ActivationManager.IsActivated())
            {
                using var activation = new ActivationForm();
                if (activation.ShowDialog() != DialogResult.OK)
                    return;
            }

            Navigator = new AppNavigator(Services.GetRequiredService<LoginForm>());
            Application.Run(Navigator);
        }

        // 📖 تفرع ما بعد الدخول — نظام البتات (انظر AppPermissions):
        // • الكاشير (قيمة 0) يُحدَّد بالهوية لا بالبت → PosForm مباشرة.
        //   (فحص الهوية هنا وعند كل أمر POS في الخدمات — لا بت عليها أبدًا)
        // • غيره → MainForm: الأقسام تُبنى من قناع دوره بفحص Can(bit).
        private static void NavigateAfterLogin()
        {
            try
            {
                Form next = SessionState.IsCashier
                    ? Services.GetRequiredService<PosForm>()
                    : Services.GetRequiredService<MainForm>();

                Navigator.Show(next);
            }
            catch (Exception ex)
            {
                LogError("NAVIGATE", ex);
                ShowUnexpectedError(ex);
            }
        }

        /// <summary>سجل الأخطاء مع التاريخ الكامل.
        /// VULN-30: كان يكتب بجوار الـ exe — على Program Files يرفض ويندوز
        /// الكتابة فينفجر «معالج الأخطاء» نفسه ويقتل التطبيق بدل إنقاذه.
        /// الآن: %LocalAppData%\Sayl_POS — مكتوب دائمًا لأي مستخدم.</summary>
        public static void LogError(string source, Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Sayl_POS");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "ui-error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n");
            }
            catch
            {
                // فشل التسجيل لا يجوز أن يفجّر معالج الأخطاء — الرسالة المرئية تظل
            }
        }

        /// <summary>رسالة خطأ مرئية بالعربية عند أي خطأ غير متوقع في الواجهة.</summary>
        private static void ShowUnexpectedError(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "حدث خطأ غير متوقع:\n" + ex.Message +
                    "\n\nتم تسجيل التفاصيل في ملف ui-error.log داخل مجلد Sayl_POS في بيانات التطبيق.",
                    "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            }
            catch
            {
                // لا شيء أخطر من فشل إظهار رسالة الخطأ نفسها
            }
        }
    }
}
