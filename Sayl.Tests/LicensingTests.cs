using Microsoft.Win32;
using System;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PosLicensing;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اختبارات سلوك الترخيص (ActivationManager) — تعمل على مسار ريجستري
/// مؤقت خاص بالاختبار (RegistryPathOverride) وبمفتاح RSA عابر، فلا تلمس
/// رخصة الجهاز الحقيقية ولا أي سرّ إنتاجي.
/// يغطي: تفعيل صالح، رخصة منتهية، بيانات ناقصة/تالفة، رجوع ساعة مؤكد،
/// حذف المرساة، فشل وصول مؤقت للريجستري، وإعادة التحقق وقت التشغيل.
/// </summary>
[Collection("licensing")] // تسلسل إلزامي: الجميع يتبادل مسار الريجستري ومفتاح الاختبار
public class LicensingTests : IDisposable
{
    private readonly string _scratchPath;
    private readonly string _privateKeyXml;
    private readonly string _savedPublicKey;
    private readonly string _savedOverride;

    public LicensingTests()
    {
        _scratchPath = @"SOFTWARE\Sayl\License_Test_" + Guid.NewGuid().ToString("N");
        _savedOverride = ActivationManager.RegistryPathOverride;
        _savedPublicKey = ActivationManager.PublicKeyXml;

        using RSA rsa = RSA.Create(2048);
        _privateKeyXml = rsa.ToXmlString(true);
        ActivationManager.PublicKeyXml = rsa.ToXmlString(false);
        ActivationManager.RegistryPathOverride = _scratchPath;
    }

    public void Dispose()
    {
        ActivationManager.RegistryPathOverride = _savedOverride;
        ActivationManager.PublicKeyXml = _savedPublicKey;
        try { Registry.CurrentUser.DeleteSubKeyTree(_scratchPath, false); } catch { }
    }

    private string SignLicense(long expiryTicksUtc, string? hwid = null, string customer = "Test")
    {
        return LicenseCrypto.Sign(
            new LicensePayload
            {
                Hwid = hwid ?? MachineFingerprint.Normalize(MachineFingerprint.GetDeviceCode()),
                ExpiryTicksUtc = expiryTicksUtc,
                Customer = customer
            },
            _privateKeyXml);
    }

    private static long YearAheadTicks => DateTime.UtcNow.AddDays(365).Ticks;
    private static long YearAgoTicks => DateTime.UtcNow.AddDays(-365).Ticks;

    private string? ReadAnchor() =>
        Registry.CurrentUser.OpenSubKey(_scratchPath)?.GetValue("LastSeenUtc") as string;
    private void WriteAnchor(long ticks)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_scratchPath);
        key.SetValue("LastSeenUtc", ticks.ToString());
    }

    // ── تفعيل صالح: كامل الدورة تفعيل → تحقق → إلغاء → إعادة تفعيل ──

    [Fact]
    public void ValidActivation_FullCycle_Works()
    {
        string license = SignLicense(YearAheadTicks);

        Assert.True(ActivationManager.Activate(license, out string error), error);
        Assert.Equal(LicenseCheckResult.Activated, ActivationManager.ValidateActivation());
        Assert.True(ActivationManager.IsActivated());
        Assert.NotNull(ReadAnchor()); // المرساة تُكتب عند التفعيل

        // إعادة التحقق وقت التشغيل (نفس ما يفعله LicenseGate قبل البيع)
        ActivationManager.Deactivate();
        Assert.Equal(LicenseCheckResult.NotActivated, ActivationManager.ValidateActivation());

        Assert.True(ActivationManager.TryActivate(license, out ActivationFailure f2));
        Assert.Equal(ActivationFailure.None, f2);
        Assert.Equal(LicenseCheckResult.Activated, ActivationManager.ValidateActivation());
    }

    [Fact]
    public void ValidActivation_TryGetLicenseInfo_ReturnsPayload()
    {
        Assert.True(ActivationManager.Activate(SignLicense(YearAheadTicks), out _));
        Assert.True(ActivationManager.TryGetLicenseInfo(out string customer, out DateTime? expiry));
        Assert.Equal("Test", customer);
        Assert.NotNull(expiry);
    }

    // ── رخصة منتهية ──

    [Fact]
    public void ExpiredLicense_IsRefused_AtActivationAndValidation()
    {
        string license = SignLicense(YearAgoTicks);

        Assert.False(ActivationManager.Activate(license, out string error));
        Assert.Contains("انتهت", error);
        Assert.False(ActivationManager.TryActivate(license, out ActivationFailure f));
        Assert.Equal(ActivationFailure.Expired, f);

        // رخصة منتهية مخزَّنة يدويًا (كانت سارية ثم فاتت) — لا تُحيى
        ActivationManager.Deactivate();
        byte[] enc = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(license), null,
            System.Security.Cryptography.DataProtectionScope.LocalMachine);
        using (var key = Registry.CurrentUser.CreateSubKey(_scratchPath))
            key.SetValue("Data", Convert.ToBase64String(enc));
        WriteAnchor(DateTime.UtcNow.Ticks);

        Assert.Equal(LicenseCheckResult.Expired, ActivationManager.ValidateActivation());
        Assert.False(ActivationManager.IsActivated());
    }

    // ── بيانات ناقصة أو تالفة ──

    [Fact]
    public void MissingActivationData_ReturnsNotActivated()
    {
        Assert.Equal(LicenseCheckResult.NotActivated, ActivationManager.ValidateActivation());
        Assert.False(ActivationManager.IsActivated());
    }

    [Fact]
    public void CorruptedStoredData_ReturnsNotActivated()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_scratchPath))
            key.SetValue("Data", "bm90LWEtbGljZW5zZQ=="); // DPAPI سيفشل في فكّه
        Assert.Equal(LicenseCheckResult.NotActivated, ActivationManager.ValidateActivation());
    }

    [Fact]
    public void InvalidLicenseText_IsRefused()
    {
        Assert.False(ActivationManager.TryActivate("garbage.text", out ActivationFailure f));
        Assert.Equal(ActivationFailure.InvalidLicense, f);
    }

    [Fact]
    public void LicenseForAnotherDevice_IsRefused()
    {
        Assert.False(ActivationManager.Activate(
            SignLicense(YearAheadTicks, hwid: "AAAA1111BBBB2222"), out string error));
        Assert.Contains("جهاز آخر", error);
    }

    // ── رجوع الساعة (VULN-27) ──

    [Fact]
    public void ConfirmedClockRollback_RejectsLicense_AndBlocksReactivationBypass()
    {
        string license = SignLicense(YearAheadTicks);
        Assert.True(ActivationManager.Activate(license, out _));

        // محاكاة: آخر رؤية «مستقبلًا» بأيام (المستخدم رجّع الساعة الآن)
        WriteAnchor(DateTime.UtcNow.AddDays(3).Ticks);
        Assert.Equal(LicenseCheckResult.ClockRolledBack, ActivationManager.ValidateActivation());
        Assert.False(ActivationManager.IsActivated());

        // التحايل: إعادة إدخال نفس الرخصة بعد الرجوع — تُرفض أيضًا
        Assert.False(ActivationManager.TryActivate(license, out ActivationFailure f));
        Assert.Equal(ActivationFailure.ClockRolledBack, f);

        // التعافي: ضبط الساعة (إعادة المرساة للوقت الصحيح) → يعمل بلا قفل دائم
        WriteAnchor(DateTime.UtcNow.Ticks);
        Assert.Equal(LicenseCheckResult.Activated, ActivationManager.ValidateActivation());
    }

    [Fact]
    public void DeletedAnchorOnActivatedMachine_IsRejectedNotSafe()
    {
        Assert.True(ActivationManager.Activate(SignLicense(YearAheadTicks), out _));
        using (var key = Registry.CurrentUser.OpenSubKey(_scratchPath, true))
        {
            Assert.NotNull(key);
            key.DeleteValue("LastSeenUtc", false);
        }

        // حذف المرساة لإحياء رخصة = رفض (لا يُعامل كحالة آمنة)
        Assert.Equal(LicenseCheckResult.ClockRolledBack, ActivationManager.ValidateActivation());
    }

    // ── فشل وصول مؤقت للريجستري ≠ حالة آمنة ≠ تلاعب مؤكد ──

    [Fact]
    public void RegistryWriteFailure_ReturnsValidationUnavailable_ThenRecovers()
    {
        Assert.True(ActivationManager.Activate(SignLicense(YearAheadTicks), out _));

        // نمنع الكتابة (المرساة تحتاج فتحًا للكتابة) مع بقاء القراءة ممكنة
        var currentUser = WindowsIdentity.GetCurrent().User!;
        using (var key = Registry.CurrentUser.OpenSubKey(_scratchPath, true))
        {
            Assert.NotNull(key);
            var sec = key.GetAccessControl();
            sec.AddAccessRule(new RegistryAccessRule(
                currentUser,
                RegistryRights.WriteKey,
                AccessControlType.Deny));
            key.SetAccessControl(sec);
        }

        try
        {
            // لا نجاح صامت ولا اتهام بالتلاعب — حالة «تعذّر التحقق» مميَّزة تفشل بالإغلاق
            Assert.Equal(LicenseCheckResult.ValidationUnavailable, ActivationManager.ValidateActivation());
            Assert.False(ActivationManager.IsActivated());
        }
        finally
        {
            using (var key = Registry.CurrentUser.OpenSubKey(_scratchPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
            {
                Assert.NotNull(key);
                var sec = key.GetAccessControl();
                sec.RemoveAccessRule(new RegistryAccessRule(
                    currentUser,
                    RegistryRights.WriteKey,
                    AccessControlType.Deny));
                key.SetAccessControl(sec);
            }
        }

        // زال العائق المؤقت → التحقق ينجح من جديد بلا إعادة تفعيل
        Assert.Equal(LicenseCheckResult.Activated, ActivationManager.ValidateActivation());
    }

    // ── منطق المرساة الصرف (بلا ريجستري) ──

    [Fact]
    public void AnchorCore_NullValue_IsMissing()
    {
        Assert.Equal(ActivationManager.ClockAnchorStatus.Missing,
            ActivationManager.EvaluateClockAnchorCore(null, DateTime.UtcNow.Ticks));
    }

    [Theory]
    [InlineData("not-a-number")] // مرساة تالفة
    [InlineData("")]             // قيمة فارغة
    public void AnchorCore_MissingOrCorruptedValue_IsMissing(string raw)
    {
        Assert.Equal(ActivationManager.ClockAnchorStatus.Missing,
            ActivationManager.EvaluateClockAnchorCore(raw, DateTime.UtcNow.Ticks));
    }

    [Theory]
    [InlineData(0)]     // تقدم طبيعي
    [InlineData(-12)]   // رجوع بضع ساعات — ضمن سماح اليوم الواحد
    public void AnchorCore_NormalOrMinorAdjustment_IsOk(int offsetHours)
    {
        long lastSeen = DateTime.UtcNow.AddHours(-offsetHours).Ticks; // 0: now، -12: رجوع 12 ساعة
        Assert.Equal(ActivationManager.ClockAnchorStatus.Ok,
            ActivationManager.EvaluateClockAnchorCore(lastSeen.ToString(), DateTime.UtcNow.Ticks));
    }

    [Fact]
    public void AnchorCore_RollbackBeyondTolerance_IsRolledBack()
    {
        long lastSeen = DateTime.UtcNow.AddDays(2).Ticks;
        Assert.Equal(ActivationManager.ClockAnchorStatus.RolledBack,
            ActivationManager.EvaluateClockAnchorCore(lastSeen.ToString(), DateTime.UtcNow.Ticks));
    }
}
