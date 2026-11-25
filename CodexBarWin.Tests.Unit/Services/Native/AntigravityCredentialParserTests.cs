using System.Text;
using AwesomeAssertions;
using CodexBarWin.Services.Native;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class AntigravityCredentialParserTests
{
    // agy's Windows shape (Go oauth2.Token under "token"), synthetic values.
    private const string AgyBlob = """
        {"token":{"access_token":"synthetic-access","token_type":"Bearer","refresh_token":"synthetic-refresh",
         "expiry":"2026-11-25T17:53:59.6863562+11:00","expires_in":3599},
         "auth_method":"consumer","id_token_email":"someone@gmail.com","writer_id":"synthetic-writer"}
        """;

    [TestMethod]
    public void Parse_WhenAgyShape_ReadsTokenAndExpiry()
    {
        var credentials = AntigravityCredentialParser.Parse(Encoding.UTF8.GetBytes(AgyBlob))!;

        credentials.AccessToken.Should().Be("synthetic-access");
        credentials.Expiry.Should().Be(new DateTimeOffset(2026, 11, 25, 6, 53, 59, TimeSpan.Zero).AddTicks(6863562));
        credentials.AuthMethod.Should().Be("consumer");
        credentials.Email.Should().Be("someone@gmail.com");
    }

    [TestMethod]
    public void Parse_WhenRefreshFieldsNotRequested_LeavesRefreshTokenUnread()
    {
        var credentials = AntigravityCredentialParser.Parse(Encoding.UTF8.GetBytes(AgyBlob))!;

        credentials.RefreshToken.Should().BeNull();
        credentials.ToString().Should().NotContain("synthetic-access");
    }

    [TestMethod]
    public void Parse_WhenRefreshFieldsRequested_ReadsRefreshToken() =>
        AntigravityCredentialParser.Parse(Encoding.UTF8.GetBytes(AgyBlob), includeRefreshFields: true)!.RefreshToken.Should().Be("synthetic-refresh");

    [TestMethod]
    public void Parse_WhenUpstreamFlatShape_ReadsMillisecondExpiryAndProject()
    {
        var json = """{"access_token":"synthetic","expiry_date":1764050000000,"project_id":"synthetic-project","client_id":"x","client_secret":"y"}""";

        var credentials = AntigravityCredentialParser.Parse(Encoding.UTF8.GetBytes(json))!;

        credentials.Expiry.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1764050000000));
        credentials.ProjectId.Should().Be("synthetic-project");
        credentials.ClientSecret.Should().BeNull("secrets are read only for the refresh path");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("""{"token":{"token_type":"Bearer"}}""")]
    [DataRow("""["synthetic"]""")]
    public void Parse_WhenNoAccessToken_ReturnsNull(string json) =>
        AntigravityCredentialParser.Parse(Encoding.UTF8.GetBytes(json)).Should().BeNull();

    [TestMethod]
    public void Parse_WhenGoZeroExpiry_HasNoExpiry() =>
        AntigravityCredentialParser.Parse("""{"token":{"access_token":"s","expiry":"0001-01-01T00:00:00Z"}}"""u8)!.Expiry.Should().BeNull();
}
