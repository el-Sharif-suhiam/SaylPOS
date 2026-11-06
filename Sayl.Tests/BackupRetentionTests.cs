using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Automatic retention cleanup (CleanupOldBackupsAsync) — the pre-restore
/// safety copy (VULN-23) is the ONLY recovery point taken immediately before
/// a destructive restore. If cleanup deleted it, restoring a wrong/corrupt
/// backup would become irreversible.
///
/// Regressed behavior fixed here: the cleanup filter used to check
/// `_pre_restore_` (underscores) while the generated file name is
/// `{db}_{yyyyMMdd_HHmmss}_pre-restore.bak` (hyphen) — the mismatch meant
/// pre-restore backups were matched by NOTHING and could be deleted, and
/// they also consumed keepLast slots they were never meant to use.
///
/// Policy after the fix:
///   - pre-restore backups are NEVER deleted by cleanup, regardless of age
///   - keepLast applies to ordinary backups only (pre-restore excluded
///     from the count entirely)
///   - ordinary backups beyond keepLast are still cleaned up
///
/// ⚠️ SAFETY: these tests run against a temp folder with fake .bak files —
/// no SQL, no database, no real backup files are touched.
/// </summary>
public class BackupRetentionTests
{
    /// <summary>مجلد مؤقت معزول لكل اختبار — يُحذف كاملًا في النهاية.</summary>
    private static string CreateTempBackupFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sayl-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>ملف .bak وهمي بوقت إنشاء محدد — التنظيف يرتّب بـ CreationTime.</summary>
    private static string CreateFakeBackup(string folder, string fileName, DateTime createdAt)
    {
        string path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, new byte[] { 0x53, 0x41, 0x59, 0x4C }); // "SAYL"
        File.SetCreationTime(path, createdAt);
        return path;
    }

    private static string RealName(DateTime ts) => $"SaylDB_{ts:yyyyMMdd_HHmmss}.bak";
    private static string RealPreRestoreName(DateTime ts) => $"SaylDB_{ts:yyyyMMdd_HHmmss}_pre-restore.bak";

    // ══ 1) النسخ العادية التي تجاوزت keepLast تُمحى ══
    [Fact]
    public async Task Cleanup_OrdinaryBackupsBeyondKeepLast_AreDeleted()
    {
        string folder = CreateTempBackupFolder();
        try
        {
            DateTime baseTime = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            var paths = new List<string>();
            for (int i = 0; i < 5; i++)
                paths.Add(CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-i)), baseTime.AddMinutes(-i)));

            await clsBackupService.CleanupOldBackupsAsync(folder, keepLast: 2);

            // الأحدث اثنتان باقيتان، الثلاث الأقدم محياة
            Assert.True(File.Exists(paths[0]));
            Assert.True(File.Exists(paths[1]));
            Assert.False(File.Exists(paths[2]));
            Assert.False(File.Exists(paths[3]));
            Assert.False(File.Exists(paths[4]));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ══ 2) نسخ ما قبل الاستعادة باقية دائمًا مهما قدم عمرها (بغض النظر عن keepLast) ══
    [Fact]
    public async Task Cleanup_PreRestoreBackups_RemainRegardlessOfAge()
    {
        string folder = CreateTempBackupFolder();
        try
        {
            DateTime baseTime = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            // نسخة pre-restore قديمة جدًا (خارج أي حد) + نسخة حديثة
            string oldest = CreateFakeBackup(folder, RealPreRestoreName(baseTime.AddDays(-30)), baseTime.AddDays(-30));
            string recent = CreateFakeBackup(folder, RealPreRestoreName(baseTime), baseTime);
            // نسخ عادية لتنشيط التنظيف فعليًا
            string ord1 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-1)), baseTime.AddMinutes(-1));
            string ord2 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-2)), baseTime.AddMinutes(-2));
            string ord3 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-3)), baseTime.AddMinutes(-3));

            await clsBackupService.CleanupOldBackupsAsync(folder, keepLast: 1);

            // النسختان الوقائيتان باقيتان بالاسم المُولَّد فعليًا (شرطة، لا شرطة سفلية)
            Assert.True(File.Exists(oldest), "نسخة pre-restore القديمة يجب ألا تُمحى أبدًا");
            Assert.True(File.Exists(recent));
            // النسخ العادية خضعت للحد keepLast = 1
            Assert.True(File.Exists(ord1));
            Assert.False(File.Exists(ord2));
            Assert.False(File.Exists(ord3));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ══ 3) نسخ ما قبل الاستعادة لا تُحسب في keepLast — لا تُزاح النسخ العادية بسببها ══
    [Fact]
    public async Task Cleanup_PreRestoreBackups_DoNotCountTowardKeepLast()
    {
        string folder = CreateTempBackupFolder();
        try
        {
            DateTime baseTime = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            // نسختان وقائيتان أحدث من كل النسخ العادية — لو حُسبتا في keepLast = 2
            // لُمحت كل النسخ العادية الثلاث (سلوك قديم خاطئ).
            string pre1 = CreateFakeBackup(folder, RealPreRestoreName(baseTime), baseTime);
            string pre2 = CreateFakeBackup(folder, RealPreRestoreName(baseTime.AddMinutes(-1)), baseTime.AddMinutes(-1));
            string ord1 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-2)), baseTime.AddMinutes(-2));
            string ord2 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-3)), baseTime.AddMinutes(-3));
            string ord3 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-4)), baseTime.AddMinutes(-4));

            await clsBackupService.CleanupOldBackupsAsync(folder, keepLast: 2);

            // الحد طُبق على النسخ العادية وحدها: بقيت اثنتان، مُحيت الأقدم
            Assert.True(File.Exists(pre1));
            Assert.True(File.Exists(pre2));
            Assert.True(File.Exists(ord1), "keepLast يُطبق على النسخ العادية فقط — لا تُزاح بسبب النسخ الوقائية");
            Assert.True(File.Exists(ord2));
            Assert.False(File.Exists(ord3));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ══ 4) التعرف على أنماط أسماء الملفات المُولَّدة فعليًا — أمان في المطابقة ══
    [Theory]
    [InlineData("SaylDB_20261106_120000_pre-restore.bak", true)]    // النمط المُولَّد فعليًا (شرطة)
    [InlineData("SaylDB_20261106_120000_pre-restore.BAK", true)]    // امتداد بحروف كبيرة
    [InlineData("sayldb_20261106_120000_PRE-RESTORE.bak", true)]    // غير حساس لحالة الأحرف
    [InlineData("SaylDB_20261106_120000_pre_restore.bak", true)]    // النمط القديم الموثّق — تحفّظ
    [InlineData("SaylDB_20261106_120000.bak", false)]               // نسخة عادية
    [InlineData("SaylDB_20261106_120000_backup.bak", false)]        // لاحقة أخرى — ليست محمية
    [InlineData("pre-restore.bak", false)]                          // بلا فاصل _ قبل اللاحقة — ليست من الخدمة
    [InlineData("SaylDB_20261106_120000_pre-restore.bak.old", false)] // امتداد غير .bak
    [InlineData("", false)]
    public void IsPreRestoreBackupFile_RealNamePatterns_AreClassifiedCorrectly(string fileName, bool expected)
    {
        Assert.Equal(expected, clsBackupService.IsPreRestoreBackupFile(fileName));
    }

    // ══ 5) الدمج: مجلد واقعي بمزيج الأنماط — التنظيف يمحي العادي فقط ══
    [Fact]
    public async Task Cleanup_MixedRealPatterns_OnlyOrdinaryBeyondLimitDeleted()
    {
        string folder = CreateTempBackupFolder();
        try
        {
            DateTime baseTime = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            string preOld = CreateFakeBackup(folder, RealPreRestoreName(baseTime.AddDays(-7)), baseTime.AddDays(-7));
            string ordKeep1 = CreateFakeBackup(folder, RealName(baseTime), baseTime);
            string ordKeep2 = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-1)), baseTime.AddMinutes(-1));
            string ordDel = CreateFakeBackup(folder, RealName(baseTime.AddMinutes(-2)), baseTime.AddMinutes(-2));

            await clsBackupService.CleanupOldBackupsAsync(folder, keepLast: 2);

            Assert.True(File.Exists(preOld), "نسخة pre-restore الأقدم يجب أن تبقى رغم تجاوزها keepLast");
            Assert.True(File.Exists(ordKeep1));
            Assert.True(File.Exists(ordKeep2));
            Assert.False(File.Exists(ordDel));

            // لا شيء آخر في المجلد — لم يُمحَ ملف محمي بحجم خطأ في المطابقة
            Assert.Equal(3, Directory.GetFiles(folder, "*.bak").Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
