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

            // تسجيل الأخطاء غير المتوقعة في ملف للتشخيص
            Application.ThreadException += (_, e) =>
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] THREAD: {e.Exception}\n");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] UNHANDLED: {e.ExceptionObject}\n");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var connectionString = configuration.GetConnectionString("SaylDb")
                ?? throw new InvalidOperationException("لم يتم العثور على جملة الاتصال 'SaylDb' في appsettings.json.");

            var services = new ServiceCollection();
            services.AddSaylServices(connectionString);
            services.AddTransient<LoginForm>();
            services.AddTransient<MainForm>();
            services.AddTransient<PosForm>();
            services.AddTransient<AboutForm>();

            Services = services.BuildServiceProvider();

            var login = Services.GetRequiredService<LoginForm>();
            login.LoginSucceeded += _ => NavigateAfterLogin();

            Navigator = new AppNavigator(login);
            Application.Run(Navigator);
        }

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
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] NAVIGATE: {ex}\n");
                throw;
            }
        }
    }
}
