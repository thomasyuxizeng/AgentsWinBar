using AwesomeAssertions;
using CodexBarWin.Helpers;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class StaleCaptionTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Text_Fresh_IsEmpty() =>
        StaleCaption.Text(false, Now.AddHours(-3), Now).Should().BeEmpty();

    [TestMethod]
    public void Text_FreshWithNullLastSuccess_IsEmpty() =>
        StaleCaption.Text(false, null, Now).Should().BeEmpty();

    [TestMethod]
    [DataRow(0, "Stale · last updated just now")]
    [DataRow(30, "Stale · last updated just now")]
    [DataRow(60, "Stale · last updated 1m ago")]
    [DataRow(12 * 60, "Stale · last updated 12m ago")]
    [DataRow(59 * 60 + 59, "Stale · last updated 59m ago")]
    [DataRow(60 * 60, "Stale · last updated 1h ago")]
    [DataRow(5 * 3600 + 1800, "Stale · last updated 5h ago")]
    [DataRow(24 * 3600, "Stale · last updated 1d ago")]
    [DataRow(3 * 86400 + 7200, "Stale · last updated 3d ago")]
    public void Text_Stale_UsesAgeBuckets(int secondsAgo, string expected) =>
        StaleCaption.Text(true, Now.AddSeconds(-secondsAgo), Now).Should().Be(expected);

    [TestMethod]
    public void Text_StaleWithNullLastSuccess_SaysUnknown() =>
        StaleCaption.Text(true, null, Now).Should().Be("Stale · last update unknown");

    [TestMethod]
    public void Text_LastSuccessInFuture_ClampsToJustNow() =>
        StaleCaption.Text(true, Now.AddMinutes(5), Now).Should().Be("Stale · last updated just now");

    [TestMethod]
    public void Text_UnspecifiedKindTreatedAsUtc() =>
        StaleCaption.Text(true, DateTime.SpecifyKind(Now.AddMinutes(-20), DateTimeKind.Unspecified), Now)
            .Should().Be("Stale · last updated 20m ago");

    [TestMethod]
    public void Text_StaleCost_UsesSameCaption() =>
        StaleCaption.Text(true, Now.AddHours(-2), Now).Should().Be("Stale · last updated 2h ago");
}
