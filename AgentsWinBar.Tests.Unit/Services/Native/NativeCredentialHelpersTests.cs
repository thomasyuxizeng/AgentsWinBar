using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

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

    // Review finding 1: Retry-After never throws and never asks for more than a day.
    [TestMethod]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("NaN")]
    [DataRow("1e20")]
    [DataRow("999999999999")]
    [DataRow("2147483647")]
    [DataRow("31536000")]
    [DataRow("Fri, 31 Dec 9999 23:59:59 GMT")]
    public void RetryAfter_WhenHeaderIsInfinityOrOverflowsDateTimeOffset_DoesNotThrow(string value)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);

        DateTimeOffset? result = null;
        var act = () => { result = NativeHttp.RetryAfter(response, Now); };

        act.Should().NotThrow();
        if (result is { } until) until.Should().BeOnOrBefore(Now + NativeHttp.MaxRetryAfter);
    }

    [TestMethod]
    [DataRow("120", 120)]
    [DataRow("1.5", 1.5)]
    public void RetryAfter_WhenHeaderIsSeconds_ReturnsThatDelay(string value, double seconds)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);

        NativeHttp.RetryAfter(response, Now).Should().Be(Now.AddSeconds(seconds));
    }

    // Review finding 7: a known claim holding an array must not hide the nested account id.
    [TestMethod]
    public void ReadChatGptAccountId_WhenTopLevelClaimIsArray_StillReadsNestedClaim() =>
        NativeJwt.ReadChatGptAccountId(Jwt("""{"chatgpt_account_id":[],"https://api.openai.com/auth":{"chatgpt_account_id":"synthetic-acct"}}"""))
            .Should().Be("synthetic-acct");

    // Review finding 8: whitespace during a rewrite is not a complete document.
    [TestMethod]
    public async Task ReadAsync_WhenFileIsWhitespaceOnlyThenRewritten_RetriesOnceAndReadsNewContent()
    {
        var path = TempFile("   \n");
        try
        {
            var retries = 0;
            using var buffer = await NativeCredentialFile.ReadAsync(path, CancellationToken.None, _ =>
            {
                retries++;
                File.WriteAllText(path, """{"synthetic":2}""");
                return Task.CompletedTask;
            });

            retries.Should().Be(1);
            System.Text.Encoding.UTF8.GetString(buffer!.Span).Should().Be("""{"synthetic":2}""");
        }
        finally { DeleteTemp(path); }
    }

    // Review finding 8: a file that grows past the slack between Length and EOF is still read.
    [TestMethod]
    public async Task ReadAsync_WhenFileGrowsBeyondInitialSlackDuringRead_RetriesOrGrowsUpToMaxBytes()
    {
        var json = "{\"synthetic\":\"" + new string('a', 40 * 1024) + "\"}";
        await using var growing = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        using var buffer = await NativeCredentialFile.ReadToEndAsync(growing, length: 0, CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(buffer.Span).Should().Be(json);
    }

    [TestMethod]
    public async Task ReadToEndAsync_WhenStreamExceedsMaxBytes_Throws()
    {
        await using var huge = new PaddedJsonStream("\"", NativeCredentialFile.MaxBytes + 10, "\"");

        var act = () => NativeCredentialFile.ReadToEndAsync(huge, length: 0, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        huge.Served.Should().BeLessThanOrEqualTo(NativeCredentialFile.MaxBytes + 64 * 1024);
    }

    // Review finding 17: the allow list also guards synchronous sends, ports and user info.
    [TestMethod]
    public void HostAllowListHandler_WhenSynchronousSend_Throws()
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.Send(new HttpRequestMessage(HttpMethod.Get, "https://example.com/"));

        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    [DataRow("https://api.anthropic.com:8443/api/oauth/usage")]
    [DataRow("https://user:pass@api.anthropic.com/api/oauth/usage")]
    [DataRow("http://127.0.0.1:8080/api/oauth/usage")]
    public async Task HostAllowListHandler_WhenNonDefaultPortOrUserInfoOrLoopback_Throws(string url)
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.GetAsync(url);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task HostAllowListHandler_WhenAllowedHostOnDefaultPort_Sends()
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(inner));

        using var response = await client.GetAsync("https://api.anthropic.com/api/oauth/usage");

        inner.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task HostAllowListHandler_WhenPlainHttp_Throws()
    {
        using var client = new HttpClient(new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}"))));

        var act = () => client.GetAsync("http://api.anthropic.com/");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
