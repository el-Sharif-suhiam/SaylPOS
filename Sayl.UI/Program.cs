using DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;
using Common.DTOs.ViewModel;
using Common.Results;

namespace Sayl.UI
{
    internal static class Program
    {
        public static IServiceProvider Services { get; private set; } = default!;
        public static AppNavigator Navigator { get; private set; } = default!;

        /// <summary>ملف pending-owner المؤجل الوراثي (كانت تكتبه نسخ مثبّت أقدم من
        /// attual). لم يعد يُكتب إطلاقًا — تُبقى هذه الثابتة لحذف أي ملف قديم
        /// باقٍ على أجهزة سبق تركيبها قبل إغلاق صفحة إنشاء الحساب من المثبّت.</summary>
        private static readonly string PendingOwnerFile =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sayl_POS", "pending-owner.json");

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

            // لغة الواجهة: تُقرأ قبل بناء أي نافذة وتبقى ثابتة طوال الجلسة.
            // المصدر: اختيار المستخدم المحفوظ في LocalAppData (زر تبديل اللغة)؛
            // وإن لم يوجد بعد (أول تشغيل) فقيمة "Language" من appsettings.
            var savedLang = AppLang.LoadSaved();
            AppLang.Init(savedLang == AppLanguage.Arabic
                && string.Equals(configuration["Language"], "en", StringComparison.OrdinalIgnoreCase)
                    ? AppLanguage.English
                    : savedLang);

            var connectionString = configuration.GetConnectionString("SaylDb")
                ?? throw new InvalidOperationException("Connection string 'SaylDb' not found in appsettings.json.");

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

            // VULN-47: بوابة حساب المدير الأول — يُنشأ الحساب الافتراضي (admin)
            // صامتًا عند صفر مستخدمين نشطين مع إجبار تغيير كلمة المرور أول دخول
            if (!EnsureOwnerSetup())
                return;

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
                    AppLang.T("Program.Msg.UnexpectedError", ex.Message),
                    AppLang.T("Common.Title.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            }
            catch
            {
                // لا شيء أخطر من فشل إظهار رسالة الخطأ نفسها
            }
        }

        // ── VULN-47: بوابة حساب المدير الأول ──────────────────────────
        /// <summary>
        /// تنشئ حساب المدير الافتراضي (admin/Admin@123) صامتًا عند صفر مستخدمين
        /// نشطين — لا توجد شاشة إنشاء في المثبّت بعد الآن، والحساب يُجبر على
        /// تغيير كلمة المرور في أول دخول (MustChangePassword=1). تُرجع false فقط
        /// إذا فشل الإنشاء التلقائي وأُلغى الحاجز التفاعلي الاحتياطي.
        /// </summary>
        private static bool EnsureOwnerSetup()
        {
            try
            {
                using var scope = Services.CreateScope();
                var usersService = scope.ServiceProvider.GetRequiredService<Core.Services.clsUsersService>();

                // ملف pending-owner الوراثي (نسخة مثبّت أقدم) — تنظيف فقط
                SafeDelete(PendingOwnerFile);

                // ترقية المخطط idempotent قبل أي استعلام مستخدمين (القواعد الأقدم
                // بلا عمود MustChangePassword أو بلا جدول Settings)
                usersService.EnsureSchemaUpgradesAsync().GetAwaiter().GetResult();

                // حسابات نشطة بالفعل = إعادة تثبيت على قاعدة حية: لا شيء يُنشأ.
                if (usersService.HasActiveUsersAsync().GetAwaiter().GetResult())
                    return true;

                // صفر مستخدمين نشطين: الحساب الافتراضي مع إجبار تغيير أول دخول
                var result = usersService.EnsureDefaultAdminAsync().GetAwaiter().GetResult();
                if (result.IsSuccess)
                    return true;

                // فشل الإنشاء التلقائي (قاعدة معطّلة إلخ): شاشة تفاعلية خط الدفاع الأخير
                using var dlg = new OwnerSetupForm(Core.Services.clsUsersService.DefaultAdminUsername);
                if (dlg.ShowDialog() != DialogResult.OK)
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                LogError("OWNER_SETUP", ex);
                // لا نمنع الدخول إذا فشل المسار غير الحرج — التحذير يكفي
                MessageBox.Show(
                    AppLang.T("Owner.Err.Unexpected"),
                    AppLang.T("Common.Title.Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                return true;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* حذف الملف ليس حرجًا */ }
        }
    }
}
