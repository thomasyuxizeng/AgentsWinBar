using AwesomeAssertions;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class NativeCredentialHelpersTests
{
    [TestMethod]
    [DataRow("2026-11-25T17:53:59.123456789+11:00", "2026-11-25T06:53:59.1234567+00:00")]
    [DataRow("2026-11-25T06:53:59Z", "2026-11-25T06:53:59+00:00")]
    [DataRow("2026-11-25T06:53:59.5Z", "2026-11-25T06:53:59.5+00:00")]
    public void ParseTimestamp_WhenGoOrIsoFormat_ReturnsUtc(string input, string expected) =>
        NativeParse.ParseTimestamp(input).Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0001-01-01T00:00:00Z")]
    [DataRow("yesterday")]
    public void ParseTimestamp_WhenMissingOrZero_ReturnsNull(string? input) => NativeParse.ParseTimestamp(input).Should().BeNull();

    [TestMethod]
    public void ReadExpiry_WhenJwtHasExp_ReturnsIt() =>
        NativeJwt.ReadExpiry(Jwt("""{"sub":"synthetic","exp":1764050000,"nested":{"exp":1}}""")).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1764050000));

    [TestMethod]
    [DataRow("not-a-jwt")]
    [DataRow("a.b")]
    [DataRow("a.!!!.c")]
    public void ReadExpiry_WhenNotJwt_ReturnsNull(string token) => NativeJwt.ReadExpiry(token).Should().BeNull();

    [TestMethod]
    public void ReadExpiry_WhenDuplicateExp_ReturnsNull() =>
        NativeJwt.ReadExpiry(Jwt("""{"exp":1764050000,"exp":1764060000}""")).Should().BeNull();

    [TestMethod]
    public void ReadChatGptAccountId_WhenNestedClaim_ReturnsIt() =>
        NativeJwt.ReadChatGptAccountId(Jwt("""{"https://api.openai.com/auth":{"user_id":"x","chatgpt_account_id":"synthetic-acct"}}""")).Should().Be("synthetic-acct");

    [TestMethod]
    public async Task ReadAsync_WhenFileOpenForWritingByOwner_StillReads()
    {
        var path = TempFile("""{"synthetic":true}""");
        try
        {
            await using var owner = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None);

            System.Text.Encoding.UTF8.GetString(buffer!.Span).Should().Be("""{"synthetic":true}""");
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenMissing_ReturnsNull() =>
        (await NativeCredentialFile.ReadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.json"), CancellationToken.None)).Should().BeNull();

    [TestMethod]
    public async Task ReadAsync_WhenFileEmptyThenRewritten_RetriesOnceAndReadsNewContent()
    {
        var path = TempFile(string.Empty);
        try
        {
            var retries = 0;

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ =>
            {
                retries++;
                File.WriteAllText(path, """{"synthetic":true}""");
                return Task.CompletedTask;
            });

            retries.Should().Be(1);
            System.Text.Encoding.UTF8.GetString(buffer!.Span).Should().Be("""{"synthetic":true}""");
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenFileTruncatedMidWriteThenCompleted_RetriesOnceAndReadsNewContent()
    {
        var path = TempFile("""{"claudeAiOauth":{"accessToken":"synth""");
        try
        {
            var retries = 0;

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ =>
            {
                retries++;
                File.WriteAllText(path, """{"claudeAiOauth":{"accessToken":"synthetic"}}""");
                return Task.CompletedTask;
            });

            retries.Should().Be(1);
            System.Text.Encoding.UTF8.GetString(buffer!.Span).Should().EndWith("""synthetic"}}""");
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenStillInvalidAfterRetry_RetriesOnlyOnceAndReturnsSecondRead()
    {
        var path = TempFile("not json");
        try
        {
            var retries = 0;

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ => { retries++; return Task.CompletedTask; });

            retries.Should().Be(1);
            System.Text.Encoding.UTF8.GetString(buffer!.Span).Should().Be("not json");
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenValidJson_DoesNotRetry()
    {
        var path = TempFile("""{"synthetic":true}""");
        try
        {
            var retries = 0;

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ => { retries++; return Task.CompletedTask; });

            retries.Should().Be(0);
            buffer.Should().NotBeNull();
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenJsonHasByteOrderMark_DoesNotRetry()
    {
        var path = TempFile("\uFEFF" + """{"synthetic":true}""");
        try
        {
            var retries = 0;

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ => { retries++; return Task.CompletedTask; });

            retries.Should().Be(0);
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public async Task ReadAsync_WhenMissing_DoesNotRetry()
    {
        var retries = 0;

        var buffer = await NativeCredentialFile.ReadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.json"), CancellationToken.None,
            _ => { retries++; return Task.CompletedTask; });

        buffer.Should().BeNull();
        retries.Should().Be(0);
    }

    [TestMethod]
    public async Task ReadAsync_WhenInvalidAndNoDelayOverride_WaitsAboutRetryDelay()
    {
        var path = TempFile(string.Empty);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None);

            NativeCredentialFile.RetryDelay.Should().Be(TimeSpan.FromMilliseconds(75));
            watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50));
            buffer!.Length.Should().Be(0);
        }
        finally { DeleteTemp(path); }
    }

    [TestMethod]
    public void AllowedHosts_WhenInspected_HasNoGoogleTokenEndpoint()
    {
        NativeHttp.AllowedHosts.Should().NotContain("oauth2.googleapis.com");
        NativeHttp.AllowedHosts.Should().BeEquivalentTo(
            "api.anthropic.com", "chatgpt.com", "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com");
    }

    [TestMethod]
    public async Task HostAllowListHandler_WhenGoogleTokenHost_Throws()
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.PostAsync("https://oauth2.googleapis.com/token", new StringContent(string.Empty));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    [DataRow(-5.0, "less than a minute")]
    [DataRow(1.0, "1 minute")]
    [DataRow(42.0, "42 minutes")]
    [DataRow(180.0, "3 hours")]
    [DataRow(3000.0, "2 days")]
    public void Ago_WhenGivenMinutes_ReadsNaturally(double minutes, string expected) =>
        NativeParse.Ago(TimeSpan.FromMinutes(minutes)).Should().Be(expected);

    [TestMethod]
    public async Task HostAllowListHandler_WhenHostNotAllowed_Throws()
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.GetAsync("https://example.com/");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task HostAllowListHandler_WhenPlainHttp_Throws()
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.GetAsync("http://api.anthropic.com/");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
