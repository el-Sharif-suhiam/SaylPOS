using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Safety guards on the test fixture's destructive operations (DROP DATABASE).
///
/// Regression coverage for the prompt-1 audit Issue 8: the fixture used a FIXED
/// database name and dropped it unconditionally on init AND dispose — two
/// concurrent runs destroyed each other's database. Now the name is randomized
/// per run and every destructive operation is gated by IsSafeTestDbName:
/// any name outside the strict test-only pattern (SaylCogsTest / SaylCogsTest_{32hex})
/// must be refused BEFORE any SQL is sent.
/// </summary>
public class DatabaseFixtureSafetyTests
{
    // ══ 1) النمط الحصري يقبل أسماء الفيكسر فقط ══

    [Theory]
    [InlineData("SaylCogsTest")] // legacy fixed name (pre-randomization fixture)
    [InlineData("SaylCogsTest_0123456789abcdef0123456789abcdef")] // randomized per-run name
    public void IsSafeTestDbName_AcceptsFixtureOwnedNames(string name)
        => Assert.True(DatabaseFixture.IsSafeTestDbName(name), $"'{name}' must be accepted");

    [Theory]
    [InlineData("SaylDB")]                      // production/developer database
    [InlineData("master")]                      // system database
    [InlineData("tempdb")]
    [InlineData("model")]
    [InlineData("msdb")]
    [InlineData("")]                            // empty
    [InlineData("SaylCogs")]                    // prefix must not be shortened
    [InlineData("SaylCogsTestX")]               // prefix extension
    [InlineData("SaylCogsTest_")]               // random part missing
    [InlineData("SaylCogsTest_abc")]            // random part too short
    [InlineData("SaylCogsTest_ZZ0123456789abcdef0123456789abcdef")] // non-hex random part
    [InlineData("SaylCogsTest_0123456789abcdef0123456789abcdefX")]  // extra suffix
    [InlineData("SaylCogsTest_0123456789abcdef0123456789abcdef_extra")] // suffix beyond hex
    [InlineData("SaylCogsTest_' DROP DATABASE master --")]          // injection-shaped
    public void IsSafeTestDbName_RejectsAnythingOutsideTheTestPattern(string name)
        => Assert.False(DatabaseFixture.IsSafeTestDbName(name), $"'{name}' must be refused");

    [Fact]
    public void IsSafeTestDbName_NullName_Refused()
        => Assert.False(DatabaseFixture.IsSafeTestDbName(null));

    // ══ 2) اسم الرَن الحالي نفسه ملتزم بالنمط ══

    [Fact]
    public void FixtureDbName_MatchesTheExclusiveTestPattern()
        => Assert.True(DatabaseFixture.IsSafeTestDbName(DatabaseFixture.DbName),
            "the fixture's own per-run name must pass its own drop gate");

    [Fact]
    public void FixtureDbName_IsNeverALegacyOrProductionName()
    {
        Assert.NotEqual("SaylDB", DatabaseFixture.DbName);
        Assert.NotEqual(DatabaseFixture.LegacyDbName, DatabaseFixture.DbName);
        Assert.NotEqual("master", DatabaseFixture.DbName);
    }
}

/// <summary>
/// كناري حي داخل كل رَن: القاعدة المتصلة معزولة فعليًا. يثبت داخل الرَن نفسه
/// (لا فحص شكلي) أن الاختبارات لم تُوصل بقاعدة تطوير/إنتاج، وأن الاسم عشوائي لا ثابت.
/// </summary>
[Collection("database")]
public class FixtureIdentityCanaryTests
{
    private readonly DatabaseFixture _fx;
    public FixtureIdentityCanaryTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task ConnectedDatabase_IsTheRandomizedIsolatedTestDatabase()
    {
        await using var db = _fx.CreateContext();
        string actual = db.Database.GetDbConnection().Database;

        Assert.Equal(DatabaseFixture.DbName, actual);            // هوية الرَن بالضبط
        Assert.True(DatabaseFixture.IsSafeTestDbName(actual));    // داخل النمط الحصري
        Assert.StartsWith(DatabaseFixture.TestDbPrefix, actual);  // البادئة الاختبارية
    }
}
