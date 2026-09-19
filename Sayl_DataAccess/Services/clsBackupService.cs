using Common.Results;
using Common.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    /// <summary>
    /// نسخ احتياطي واستعادة لقاعدة البيانات (أساسي لتشغيل أوفلاين: البيانات على جهاز واحد).
    /// نسخ عند الإغلاق + نسخ يومي، مع الاحتفاظ بآخر N نسخة، واستعادة يدوية آمنة من الإعدادات.
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
                string fileName = $"{dbName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
                string fullPath = Path.Combine(backupFolder, fileName);

                string sql = "BACKUP DATABASE [" + dbName.Replace("]", "]]") + "] TO DISK = @path WITH INIT, NAME = @name";
                var parameters = new[]
                {
                    new SqlParameter("@path", fullPath),
                    new SqlParameter("@name", $"Sayl automatic backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}")
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
        /// VULN-24: كان يُمنح «Authenticated Users»Modify أيضًا — أي حساب
        /// ويندوز عادي على جهاز المحل المشترك يستطيع حذف/استبدال كل النسخ.
        /// الآن: حساب خدمة SQL فقط (وإن تعذّر قراءته نوقف بلا منح واسع —
        /// فشل النسخ اللاحق أوضح من نسخ قابلة للتلاعب بصمت).
        /// </summary>
        private async Task EnsureSqlServiceCanWriteAsync(string folder)
        {
            string? serviceAccount = null;
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
                // لا نمنح شيئًا — رسالة فشل BACKUP ستوجه المدير يدويًا
            }

            if (!string.IsNullOrWhiteSpace(serviceAccount))
                RunIcaclsGrant(folder, serviceAccount);
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

        // ─────────────────────────────────────────────────
        // استعادة نسخة احتياطية: بأمان عالٍ — ترفض عند وجود جلسات مفتوحة،
        // تضع القاعدة في وضع أحادي المستخدم أثناء الاستعادة وتعيدها دائمًا.
        // VULN-23: نسخة احتياطية إلزامية قبل الاستعادة — كانت «توصية نصية»
        // في حوار التأكيد فقط؛ أي استعادة فاشلة كانت تترك المحل بلا قاعدة
        // وبلا رجوع. الآن: نسخة وقائية تُنشأ وتنجح قبل لمس القاعدة، وإلا
        // ترفض العملية (النسخة تُميَّز بلاحقة pre-restore لعدم عبور cleanup).
        // ─────────────────────────────────────────────────
        public async Task<Result<string>> RestoreAsync(string backupFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
                    return Result<string>.Fail("ملف النسخة الاحتياطية غير موجود.");

                bool hasOpenSessions = await _dbContext.PosSessions
                    .AnyAsync(s => s.Status == enPosSessionStatusType.open);
                if (hasOpenSessions)
                    return Result<string>.Fail("توجد جلسات بيع مفتوحة. أغلق جميع الجلسات ثم حاول الاستعادة.");

                // VULN-23: نسخة إلزامية قبل أي استعادة — بلاها كانت الاستعادة
                // الفاشلة كارثة غير قابلة للتراجع.
                string preRestoreFolder = Path.GetDirectoryName(backupFilePath)
                                          ?? DefaultBackupFolder;
                var preBackup = await BackupToFolderAsync(preRestoreFolder, fileNameSuffix: "pre-restore");
                if (!preBackup.IsSuccess || string.IsNullOrWhiteSpace(preBackup.Data))
                    return Result<string>.Fail($"يجب نجاح نسخة احتياطية قبل الاستعادة (فشلت: {preBackup.Message}). لم تُلمس قاعدة البيانات.");

                string dbName = _dbContext.Database.GetDbConnection().Database;
                string safeName = dbName.Replace("]", "]]");
                string masterConn = new SqlConnectionStringBuilder(
                    _dbContext.Database.GetConnectionString())
                { InitialCatalog = "master" }.ConnectionString;

                await using var conn = new SqlConnection(masterConn);
                await conn.OpenAsync();

                // المسارات الحالية لملفات البيانات والسجل — نستعاد إليها بـ MOVE
                // حتى لو جاءت النسخة من جهاز آخر بمسارات مختلفة.
                string dataLogical = "", dataPath = "", logLogical = "", logPath = "";
                const string filesSql =
                    "SELECT mf.name AS [Name], mf.physical_name AS [Physical], mf.type_desc AS [Type] " +
                    "FROM sys.master_files mf WHERE mf.database_id = DB_ID(@db)";
                await using (var cmd = new SqlCommand(filesSql, conn) { CommandTimeout = 600 })
                {
                    cmd.Parameters.AddWithValue("@db", dbName);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string type = reader.GetString(2);
                        if (type == "ROWS") { dataLogical = reader.GetString(0); dataPath = reader.GetString(1); }
                        else if (type == "LOG") { logLogical = reader.GetString(0); logPath = reader.GetString(1); }
                    }
                }

                if (dataLogical.Length == 0 || logLogical.Length == 0)
                    return Result<string>.Fail($"قاعدة البيانات '{dbName}' غير موجودة على هذا الخادم. أنشئها أولًا بسكربتات التثبيت.");

                bool singleUser = false;
                try
                {
                    await ExecAsync(conn, $"ALTER DATABASE [{safeName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                    singleUser = true;

                    string q = "'";
                    string move =
                        $" , MOVE N'{dataLogical.Replace(q, "''")}' TO N'{dataPath.Replace(q, "''")}'" +
                        $" , MOVE N'{logLogical.Replace(q, "''")}' TO N'{logPath.Replace(q, "''")}'";
                    string restoreSql =
                        $"RESTORE DATABASE [{safeName}] FROM DISK = @path WITH REPLACE, RECOVERY{move}";

                    await using (var cmd = new SqlCommand(restoreSql, conn) { CommandTimeout = 600 })
                    {
                        cmd.Parameters.AddWithValue("@path", backupFilePath);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    return Result<string>.Ok(backupFilePath, "تمت استعادة النسخة الاحتياطية بنجاح.");
                }
                catch
                {
                    singleUser = false;
                    try { await ExecAsync(conn, $"ALTER DATABASE [{safeName}] SET MULTI_USER"); } catch { /* أفضل جهد */ }
                    throw;
                }
                finally
                {
                    if (singleUser)
                    {
                        try { await ExecAsync(conn, $"ALTER DATABASE [{safeName}] SET MULTI_USER"); } catch { /* أفضل جهد */ }
                    }
                }
            }
            catch (Exception ex)
            {
                return Result<string>.Fail($"فشلت الاستعادة: {ex.Message}");
            }
        }

        private static async Task ExecAsync(SqlConnection conn, string sql)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>VULN-23: نسخة وقائية قبل الاستعادة — بلا تنظيف تلقائي
        /// (لا تُحسب في keepLast فلا تُمسح بموجة تنظيف لاحقة)، وباسم مميز
        /// يسهل تمييزها عند الحاجة للتراجع.</summary>
        private async Task<Result<string>> BackupToFolderAsync(string folder, string fileNameSuffix)
        {
            try
            {
                Directory.CreateDirectory(folder);
                await EnsureSqlServiceCanWriteAsync(folder);

                string dbName = _dbContext.Database.GetDbConnection().Database;
                string fileName = $"{dbName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{fileNameSuffix}.bak";
                string fullPath = Path.Combine(folder, fileName);

                string sql = "BACKUP DATABASE [" + dbName.Replace("]", "]]") + "] TO DISK = @path WITH INIT, NAME = @name";
                var parameters = new[]
                {
                    new SqlParameter("@path", fullPath),
                    new SqlParameter("@name", $"Sayl {fileNameSuffix} backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}")
                };

                await _dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
                return Result<string>.Ok(fullPath, "تم إنشاء النسخة الوقائية.");
            }
            catch (Exception ex)
            {
                return Result<string>.Fail(ex.Message);
            }
        }

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
                        if (files[i].Name.Contains("_pre_restore_", StringComparison.OrdinalIgnoreCase))
                            continue; // نسخة ما قبل الاستعادة محمية دائمًا (VULN-23)
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
