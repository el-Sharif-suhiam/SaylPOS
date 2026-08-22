//using Microsoft.EntityFrameworkCore.SqlServer;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Microsoft.Extensions.Logging;
namespace DependencyInjection
{
    public static class DependencyInjectionExtensions
    {
        public static IServiceCollection AddSaylServices(this IServiceCollection services, string connectionString)
        {
            services.AddDbContext<SaylDbContext>(options =>
                options.UseSqlServer(connectionString)
                .LogTo(Console.WriteLine, LogLevel.Information)
                .EnableSensitiveDataLogging()
            );
            services.AddScoped<clsCustomerService>();
            services.AddScoped<clsPartyService>();
            services.AddScoped<clsEmployeeService>();
            services.AddScoped<clsSupplierService>();
            services.AddScoped<clsUsersService>();
            services.AddScoped<clsCheckoutService>();
            services.AddScoped<clsProductService>();
            services.AddScoped<clsRoleService>();
            services.AddScoped<clsExpenseService>();
            services.AddScoped<clsReportService>();
            services.AddScoped<clsStockService>();
            services.AddScoped<clsPosSessionService>();
            services.AddScoped<clsPaymentService>();
            services.AddScoped<clsReturnService>();
            services.AddScoped<clsPurchaseService>();
            services.AddScoped<clsInvoiceService>();
            return services;
        }

        //public static IServiceCollection AddSaylBusinessServices(this IServiceCollection services, string connectionString)
        //{
        //    services.AddDbContext<SaylDbContext>(options =>
        //       options.UseSqlServer(connectionString)
        //   );
        //    services.AddScoped<clsCustomersServices>();
        //    // ... أضف باقي كلاسات المنطق

        //    return services;
        //}
    }
}
