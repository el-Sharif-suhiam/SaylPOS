using Common.DTOs;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using DependencyInjection;
using Sayl.Core.Services;
namespace consol_app
{
    internal class Program
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        //public static List<DtoCustomer> GetCustomersAsync(SaylDbContext context, string search="")
        //{
        //    var query = context.Parties.Where(c => 
        //       string.IsNullOrWhiteSpace(search) || c.Name.Contains(search) || c.Email.Contains(search)

        //    ).Include(s => s.Customer).Select(c => new DtoCustomer
        //    {
        //        Id = c.PartyId,
        //        PartyType = c.PartyType,
        //        Name = c.Name,
        //        Phone1 = c.Phone1,
        //        AddressLine1 = c.AddressLine1,
        //        AddressLine2 = c.AddressLine2,
        //        Balance = c.Customer.Balance,
        //        CreatedAt = c.CreatedAt,
        //        Email = c.Email,
        //        IsActive = c.IsActive,
        //        Notes = c.Notes,
        //        Phone2 = c.Phone2,
        //        UpdatedAt = c.UpdatedAt,
        //        CreditLimit = c.Customer.CreditLimit
        //    }); ;

        //    List<DtoCustomer> customers = query.ToList();
            
        //    return customers;
        //}

        static async Task  Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            //IConfiguration configuration = new ConfigurationBuilder()
            //           .SetBasePath(Directory.GetCurrentDirectory())              // Current output folder
            //           .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Load appsettings.json
            //           .Build();
            var services = new ServiceCollection();

            // a) تسجيل DbContext نفسه (وليس Factory) مع عمر Scoped
            //services.AddDbContext<SaylDbContext>(options =>
            //    options.UseSqlServer("Server=.;Database=SaylDB;User Id=sa;Password=sa123456;TrustServerCertificate=True;Encrypt=False;")
            //);

            services.AddSaylServices("Server=.;Database=SaylDB;User Id=sa;Password=sa123456;TrustServerCertificate=True;Encrypt=False;");
            //services.AddSaylBusinessServices();
            // b) تسجيل كل واجهات البيانات مع كلاسات التنفيذ
            //services.AddScoped<ICustomerData, CustomerData>();
            //services.AddScoped<IProductData, ProductData>();
            // ... تكرر لبقية الواجهات

            // c) لو عندك كلاسات Logic تسجلها أيضاً
            //services.AddScoped<clsCustomerLogic>();

            
            ServiceProvider = services.BuildServiceProvider();


            //var options = new DbContextOptionsBuilder<SaylDbContext>()
            //     .LogTo(Console.WriteLine, LogLevel.Information)
            //     .EnableSensitiveDataLogging()
            //            .Options;
            //using var context = new SaylDbContext(options);

            //bool canConnect = context.Database.CanConnect();

            //if (!canConnect)
            //{
            //    Console.WriteLine("Could not connect to TrainingCenterDB.");
            //    return;
            //}

            var logic = ServiceProvider.GetRequiredService<clsCustomerService>();
            var customers = await logic.GetCustomersListAsync();

            
            foreach (var item in customers)
            {
                Console.WriteLine($"{item.PartyId} , {item.Party.Name} , {item.Party.Email} = {item.Balance} , {item.CreditLimit}");
            }

            var item1 = await logic.GetCustomerByIdAsync(1);
            Console.WriteLine($"{item1.PartyId} , {item1.Party.Name} , {item1.Party.Email} = {item1.Balance} , {item1.CreditLimit}");


            //Console.WriteLine("Connected successfully to TrainingCenterDB.");
            //Console.WriteLine(canConnect);
            Console.WriteLine();



        }
    }
}


//// ========================================================
//// Build configuration so the console app can read
//// appsettings.json from the output folder
//// ========================================================
//IConfiguration configuration = new ConfigurationBuilder()
//    .SetBasePath(Directory.GetCurrentDirectory())              // Current output folder
//    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Load appsettings.json
//    .Build();

// ========================================================
// Read the connection string from appsettings.json
// ========================================================
//string? connectionString = configuration.GetConnectionString("DefaultConnection");

//// Validate that the connection string exists
//if (string.IsNullOrWhiteSpace(connectionString))
//{
//    Console.WriteLine("Connection string 'DefaultConnection' was not found.");
//    return;
//}

// ========================================================
// Build DbContextOptions manually for AppDbContext
// ========================================================

// ========================================================
// Create AppDbContext manually
// ========================================================
//using var context = new AppDbContext(options);

// ========================================================
// Check whether the database connection works
// ========================================================


// ========================================================
// Call retrieval method
// ========================================================
//RetrieveAndPrintStudents(context);


///// <summary>
///// Retrieves all students from the database, prints the generated SQL,
///// then prints the returned student data.
///// </summary>
///// <param name="context">The EF Core database context.</param>
//static void RetrieveAndPrintStudents(AppDbContext context)
//{
//    // Build the query first (no execution yet)
//    var query = context.Students
//        .OrderBy(s => s.StudentId);

//    // Show generated SQL before execution
//    PrintGeneratedSql("Students", query.ToQueryString());

//    // Execute query
//    var students = query.ToList();

//    // If no data exists, stop here
//    if (students.Count == 0)
//    {
//        Console.WriteLine("No students found in the database.");
//        Console.WriteLine();
//        return;
//    }

//    Console.WriteLine("Students List:");
//    Console.WriteLine("--------------");

//    foreach (var student in students)
//    {
//        Console.WriteLine(
//            $"Id: {student.StudentId}, " +
//            $"Name: {student.FirstName} {student.LastName}, " +
//            $"Email: {student.Email}, " +
//            $"Status: {student.Status}, " +
//            $"Phone: {student.PhoneNumber ?? "N/A"}");
//    }

//    Console.WriteLine();
//    Console.WriteLine($"Total Students: {students.Count}");
//    Console.WriteLine(new string('=', 70));
//    Console.WriteLine();
//}


//static void PrintGeneratedSql(string tableName, string sqlQuery)
//{
//    Console.WriteLine($"Generated SQL Query for {tableName}:");
//    Console.WriteLine(new string('-', 40));
//    Console.WriteLine(sqlQuery);
//    Console.WriteLine();
//}


