using Common.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    /// <summary>
    /// نسخ احتياطي لقاعدة البيانات (أساسي لتشغيل أوفلاين: البيانات على جهاز واحد).
    /// نسخ عند الإغلاق + نسخ يومي، مع الاحتفاظ بآخر N نسخة.
    /// </summary>
    public class clsBackupService
    {
        private readonly SaylDbContext _dbContext;

        public clsBackupService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<Result<string>> BackupAsync(string backupFolder, int keepLast = 30)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupFolder))
                    return Result<string>.Fail("مسار النسخ الاحتياطي غير محدد.");

                Directory.CreateDirectory(backupFolder);
                await EnsureSqlServiceCanWriteAsync(backupFolder);

                string dbName = _dbContext.Database.GetDbConnection().Database;
                string fileName = $"{dbName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                string fullPath = Path.Combine(backupFolder, fileName);

                string sql = "BACKUP DATABASE [" + dbName.Replace("]", "]]") + "] TO DISK = @path WITH INIT, NAME = @name";
                var parameters = new[]
                {
                    new SqlParameter("@path", fullPath),
                    new SqlParameter("@name", $"Sayl automatic backup {DateTime.Now:yyyy-MM-dd HH:mm}")
                };

                await _dbContext.Database.ExecuteSqlRawAsync(sql, parameters);

                await CleanupOldBackupsAsync(backupFolder, keepLast);

                return Result<string>.Ok(fullPath, "تم إنشاء نسخة احتياطية بنجاح.");
            }
            catch (Exception ex)
            {
                return Result<string>.Fail($"فشل النسخ الاحتياطي: {ex.Message}");
            }
        }

        /// <summary>
        /// خدمة SQL Server غالبًا لا تملك صلاحية الكتابة في مجلدات المستخدم.
        /// نقرأ حساب خدمة SQL من قاعدة البيانات ونمنحه صلاحية التعديل على مجلد النسخ.
        /// </summary>
        private async Task EnsureSqlServiceCanWriteAsync(string folder)
        {
            string serviceAccount = "Authenticated Users";
            try
            {
                var accounts = await _dbContext.Database
                    .SqlQueryRaw<string>("SELECT service_account AS [Value] FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'")
                    .ToListAsync();
                if (accounts.Count > 0)
                    serviceAccount = accounts[0];
            }
            catch
            {
                // نستخدم القيمة الافتراضية
            }

            RunIcaclsGrant(folder, serviceAccount);
            RunIcaclsGrant(folder, "Authenticated Users");
        }

        private static void RunIcaclsGrant(string folder, string account)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = $"\"{folder}\" /grant \"{account}\":(OI)(CI)M",
                    CreateNoWindow = true,
                    UseShellExecute = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch
            {
                // أفضل جهد — إن فشل المنح ستظهر رسالة الخطأ من BACKUP نفسه
            }
        }

        /// <summary>المسار الافتراضي للنسخ الاحتياطي.</summary>
        public static string DefaultBackupFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SaylBackups");

        private static Task CleanupOldBackupsAsync(string backupFolder, int keepLast)
        {
            return Task.Run(() =>
            {
                try
                {
                    var files = Directory.GetFiles(backupFolder, "*.bak")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.CreationTime)
                        .ToList();

                    for (int i = keepLast; i < files.Count; i++)
                    {
                        try { files[i].Delete(); } catch { /* ملف مقفول — نتجاهل */ }
                    }
                }
                catch
                {
                    // فشل التنظيف لا يفشل النسخة نفسها
                }
            });
        }
    }
}
