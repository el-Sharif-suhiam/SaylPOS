using Common.Results;
using Common.Enums;
using Common.Utils;
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
        /// <summary>لاحقة اسم ملف نسخة ما قبل الاستعادة — المصدر الوحيد للنمط،
        /// يُستخدم عند توليد الاسم وعند فلتر الحماية في التنظيف معًا حتى لا
        /// يتطابقا أبدًا (كان التطابق مكسورًا: فلتر `_pre_restore_` مقابل
        /// اسم مُولَّد `_pre-restore.` فتُمحى نسخة التراجع الوحيدة).</summary>
        public const string PreRestoreSuffix = "pre-restore";

        /// <summary>هل هذا الملف نسخة ما قبل الاستعادة (VULN-23) المحمية من
        /// التنظيف التلقائي؟ يُطابق النمط المُولَّد فعليًا
        /// ({db}_{yyyyMMdd_HHmmss}_pre-restore.bak) ويقبل تحفّظيًا النمط
        /// القديم الموثّق بشرطات سفلية — حماية زائدة لا ضير منها.
        /// المطابقة على نهاية الاسم (قبل الامتداد) بعد توحيد الشرطة/الشرطة
        /// السفلية، فلا خطأ في الإيجاب يحمي ملفًا عاديًا ولا خطأ في السلب
        /// يفقدها.</summary>
        public static bool IsPreRestoreBackupFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string name = Path.GetFileName(fileName);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            string normalized = name.Replace('-', '_');
            return normalized.EndsWith("_pre_restore", StringComparison.OrdinalIgnoreCase);
        }

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
        // VULN-27: بوابة تصريح داخل الخدمة — الاستعادة تُلغي كل ما سُجّل
        // بعد النسخة (مبيعات/فواتير/رواتب)، ولا يجوز أن تُنفَّذ بحجب زر
        // الواجهة وحده أو باستدعاء مباشر للخدمة. النمط المعتمد في بقية
        // الخدمات: هوية موثقة + دور نشط + صلاحية شاشة الإعدادات
        // (AppPermissions.Settings) — نفس الشاشة التي يظهر منها الزر،
        // فالمحاسب (508) والكاشير (0) مرفوضان بنيويًا.
        // ─────────────────────────────────────────────────
        public async Task<Result<string>> RestoreAsync(string backupFilePath, int actorUserId)
        {
            try
            {
                // ── بوابة التصريح (VULN-27): قبل أي فحص آخر وأي أمر SQL —
                // -1 = هوية مفقودة أو مستخدم/دور غير موجود أو غير مفعّل.
                // كل رفض هنا يُدقَّق (تدقيق المحاولات المرفوضة) — خارج أي
                // معاملة فلا خطر ضياع السجل، والكتابة مستقلة أفضل جهد.
                if (actorUserId <= 0)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, null, "RestoreDenied",
                        $"محاولة استعادة مرفوضة: هوية منفّذ الاستعادة غير محددة (الملف '{backupFilePath}').");
                    return Result<string>.Fail("هوية منفّذ الاستعادة غير محددة — العملية مرفوضة.");
                }

                int actorRole = await clsPermissions.GetUserRoleValueAsync(_dbContext, actorUserId);
                if (actorRole < 0)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "RestoreDenied",
                        $"محاولة استعادة مرفوضة: المنفّذ #{actorUserId} غير موجود أو غير مفعّل (الملف '{backupFilePath}').");
                    return Result<string>.Fail("منفّذ الاستعادة غير موجود أو غير مفعّل — العملية مرفوضة.");
                }

                if (!AppPermissions.Has(actorRole, AppPermissions.Settings))
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "RestoreDenied",
                        $"محاولة استعادة مرفوضة: المنفّذ #{actorUserId} (دور {actorRole}) بلا صلاحية شاشة الإعدادات (الملف '{backupFilePath}').");
                    return Result<string>.Fail("استعادة النسخة الاحتياطية تتطلب صلاحية شاشة الإعدادات — العملية مرفوضة.");
                }

                if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "RestoreDenied",
                        $"محاولة استعادة مرفوضة: ملف النسخة الاحتياطية غير موجود ('{backupFilePath}').");
                    return Result<string>.Fail("ملف النسخة الاحتياطية غير موجود.");
                }

                bool hasOpenSessions = await _dbContext.PosSessions
                    .AnyAsync(s => s.Status == enPosSessionStatusType.open);
                if (hasOpenSessions)
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "RestoreDenied",
                        $"محاولة استعادة مرفوضة: توجد جلسات بيع مفتوحة (المنفّذ #{actorUserId}، الملف '{backupFilePath}').");
                    return Result<string>.Fail("توجد جلسات بيع مفتوحة. أغلق جميع الجلسات ثم حاول الاستعادة.");
                }

                // تدقيق: من بدأ الاستعادة ومتى (VULN-27). يُكتب قبل النسخة الوقائية
                // ليدخل ضمنها — الاستعادة نفسها تمحو سجل القاعدة الحية، ونسخة
                // pre-restore هي الأثر الدائم لمن بدأ العملية.
                clsAudit.Log(_dbContext, actorUserId, "RestoreDatabase",
                    $"بدء استعادة قاعدة البيانات من '{Path.GetFileName(backupFilePath)}'");
                await _dbContext.SaveChangesAsync();

                // VULN-23: نسخة إلزامية قبل أي استعادة — بلاها كانت الاستعادة
                // الفاشلة كارثة غير قابلة للتراجع.
                string preRestoreFolder = Path.GetDirectoryName(backupFilePath)
                                          ?? DefaultBackupFolder;
                var preBackup = await BackupToFolderAsync(preRestoreFolder, fileNameSuffix: PreRestoreSuffix);
                if (!preBackup.IsSuccess || string.IsNullOrWhiteSpace(preBackup.Data))
                {
                    await clsAudit.LogRejectedAsync(_dbContext, actorUserId, "RestoreDenied",
                        $"استعادة مرفوضة: فشلت النسخة الوقائية قبل الاستعادة ({preBackup.Message}). لم تُلمس قاعدة البيانات.");
                    return Result<string>.Fail($"يجب نجاح نسخة احتياطية قبل الاستعادة (فشلت: {preBackup.Message}). لم تُلمس قاعدة البيانات.");
                }

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

                    // تدقيق الإكمال (VULN-27): القاعدة المستعارة حديثًا فقدت سجل
                    // البدء — يُكتب أثر الإكمال فيها إن وُجد المستخدم نفسه بها
                    // (قيد FK على SystemLogs.UserId)، بلا إفشال نجاح الاستعادة.
                    try
                    {
                        if (await _dbContext.Users.AnyAsync(u => u.UserId == actorUserId))
                        {
                            clsAudit.Log(_dbContext, actorUserId, "RestoreDatabaseCompleted",
                                $"اكتملت استعادة قاعدة البيانات من '{Path.GetFileName(backupFilePath)}'");
                            await _dbContext.SaveChangesAsync();
                        }
                    }
                    catch { /* أفضل جهد — لا يُفشل نجاح الاستعادة */ }

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

        internal static Task CleanupOldBackupsAsync(string backupFolder, int keepLast)
        {
            return Task.Run(() =>
            {
                try
                {
                    var files = Directory.GetFiles(backupFolder, "*.bak")
                        // نمط الـwildcard على ويندوز يُطابق امتدادات أطول مثل
                        // .backup ("8.3 short-name" behavior) — فحص الامتداد
                        // صراحةً يجعل السلوك حتميًا على أي runtime: النسخة
                        // العادية هي .bak فقط (بأي حالة أحرف)، وما سواه
                        // (.backup / .bak.tmp) ليس نسخة ولا يُمحى ولا يُحسب.
                        .Where(f => string.Equals(
                            Path.GetExtension(f), ".bak", StringComparison.OrdinalIgnoreCase))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.CreationTime)
                        .ToList();

                    // نسخ ما قبل الاستعادة (VULN-23): محمية دائمًا من التنظيف
                    // بغض النظر عن العمر، ولا تُحسب في keepLast — تُستبعد من
                    // القائمة قبل تطبيق الحد حتى لا تُزاح النسخ العادية ولا تُمحى هي.
                    var ordinary = files.Where(f => !IsPreRestoreBackupFile(f.Name)).ToList();

                    // keepLast سالب أو صفر: لا تُبقى نسخة عادية — نقيّد الحد
                    // صراحةً بـ 0 بدل الاعتماد على ابتلاع استثناء فهرس سالب
                    // في try/catch المخصص للملفات المقفولة.
                    int effectiveKeep = Math.Max(0, keepLast);

                    for (int i = effectiveKeep; i < ordinary.Count; i++)
                    {
                        try { ordinary[i].Delete(); } catch { /* ملف مقفول — نتجاهل */ }
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
