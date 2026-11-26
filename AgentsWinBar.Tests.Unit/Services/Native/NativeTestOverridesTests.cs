using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

/// <summary>
/// The Debug-only test hooks (AGENTSWINBAR_TEST_*). The parser is tested in Debug builds; a Release build (the CI test
/// run) checks that the hooks are stubs there. No test reads the real environment, profile or app data.
/// </summary>
[TestClass]
public sealed class NativeTestOverridesTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;

    [TestMethod]
    public void AllowedHosts_InEveryBuild_AreExactlyTheFourProviderHosts() =>
        NativeHttp.AllowedHosts.Should().BeEquivalentTo(
            "api.anthropic.com", "chatgpt.com", "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com");

    [TestMethod]
    public void Apply_WhenNothingIsOverridden_KeepsProductionEndpointsAndPaths()
    {
        var none = new NativeTestOverrides();

        none.Apply(new ClaudeNativeOptions()).UsageUri.Should().Be(ClaudeNativeClient.UsageUri);
        none.Apply(new CodexNativeOptions()).UsageUri.Should().Be(CodexNativeClient.UsageUri);
        var antigravity = none.Apply(new AntigravityNativeOptions());
        antigravity.LoadCodeAssistUri.Should().Be(AntigravityNativeClient.LoadCodeAssistUri);
        antigravity.QuotaSummaryUri.Should().Be(AntigravityNativeClient.QuotaSummaryUri);
        antigravity.AvailableModelsUri.Should().Be(AntigravityNativeClient.AvailableModelsUri);
        none.Apply(new ClaudeNativeOptions()).CredentialsPath.Should().BeNull();
        none.CooldownFilePath.Should().BeNull();
    }

    [TestMethod]
    public void Apply_WhenProfileAndOriginsAreSet_PointsReadersAtTheFakes()
    {
        var root = Path.Combine(Path.GetTempPath(), "fake-profile");
        var overrides = new NativeTestOverrides
        {
            ProfileRoot = root, AppDataDirectory = Path.Combine(root, "data"),
            ClaudeOrigin = new Uri("http://127.0.0.1:18001/"), CodexOrigin = new Uri("http://127.0.0.1:18002/"),
            AntigravityOrigin = new Uri("http://127.0.0.1:18003/")
        };

        var claude = overrides.Apply(new ClaudeNativeOptions());
        var codex = overrides.Apply(new CodexNativeOptions());
        var antigravity = overrides.Apply(new AntigravityNativeOptions());

        claude.CredentialsPath.Should().Be(Path.Combine(root, ".claude", ".credentials.json"));
        claude.UsageUri.Should().Be(new Uri("http://127.0.0.1:18001/api/oauth/usage"));
        codex.AuthPath.Should().Be(Path.Combine(root, ".codex", "auth.json"));
        codex.UsageUri.Should().Be(new Uri("http://127.0.0.1:18002/backend-api/wham/usage"));
        antigravity.LoadCodeAssistUri.Should().Be(new Uri("http://127.0.0.1:18003/v1internal:loadCodeAssist"));
        antigravity.QuotaSummaryUri.Should().Be(new Uri("http://127.0.0.1:18003/v1internal:retrieveUserQuotaSummary"));
        antigravity.AvailableModelsUri.Should().Be(new Uri("http://127.0.0.1:18003/v1internal:fetchAvailableModels"));
        overrides.CooldownFilePath.Should().Be(Path.Combine(root, "data", ClaudeCooldownFile.FileName));
        overrides.LoopbackOrigins.Should().HaveCount(3);
    }

#if DEBUG
    [TestMethod]
    public void Load_WhenNoVariableIsSet_ReturnsNull()
    {
        NativeTestOverrides.Load(_ => null).Should().BeNull();
        NativeTestOverrides.Load(_ => "  ").Should().BeNull();
    }

    [TestMethod]
    public void Load_WhenEveryVariableIsSet_ReadsEachOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "fake-profile");

        var overrides = NativeTestOverrides.Load(Environment(
            (NativeTestOverrides.ProfileRootVariable, root),
            (NativeTestOverrides.AppDataVariable, Path.Combine(root, "data")),
            (NativeTestOverrides.AntigravityCredentialFileVariable, Path.Combine(root, "antigravity.json")),
            (NativeTestOverrides.ClaudeBaseUrlVariable, "http://127.0.0.1:18001"),
            (NativeTestOverrides.CodexBaseUrlVariable, "http://127.0.0.1:18001/"),
            (NativeTestOverrides.AntigravityBaseUrlVariable, " http://127.0.0.1:65535 ")))!;

        overrides.ProfileRoot.Should().Be(root);
        overrides.AppDataDirectory.Should().Be(Path.Combine(root, "data"));
        overrides.AntigravityCredentialFile.Should().Be(Path.Combine(root, "antigravity.json"));
        overrides.ClaudeOrigin.Should().Be(new Uri("http://127.0.0.1:18001/"));
        overrides.CodexOrigin.Should().Be(new Uri("http://127.0.0.1:18001/"));
        overrides.AntigravityOrigin.Should().Be(new Uri("http://127.0.0.1:65535/"));
        overrides.LoopbackOrigins.Should().HaveCount(2, "two providers share one fake server");
    }

    [TestMethod]
    public void Load_WhenOnlyTheDataFolderIsSet_KeepsTheRealProfileAndHosts()
    {
        var overrides = NativeTestOverrides.Load(Environment((NativeTestOverrides.AppDataVariable, Path.GetTempPath())))!;

        overrides.ProfileRoot.Should().BeNull();
        overrides.LoopbackOrigins.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("https://127.0.0.1:18001")]
    [DataRow("http://localhost:18001")]
    [DataRow("http://127.0.0.2:18001")]
    [DataRow("http://[::1]:18001")]
    [DataRow("http://127.0.0.1")]
    [DataRow("http://127.0.0.1:0")]
    [DataRow("http://127.0.0.1:65536")]
    [DataRow("http://127.0.0.1:18001/api")]
    [DataRow("http://127.0.0.1:18001/?x=1")]
    [DataRow("http://127.0.0.1:18001#x")]
    [DataRow("http://user:pass@127.0.0.1:18001")]
    [DataRow("http://127.0.0.1.example.com:18001")]
    [DataRow("http://api.anthropic.com")]
    public void Load_WhenBaseUrlIsNotALoopbackOriginWithPort_Throws(string value)
    {
        var load = () => NativeTestOverrides.Load(Environment(
            (NativeTestOverrides.ProfileRootVariable, Path.GetTempPath()),
            (NativeTestOverrides.ClaudeBaseUrlVariable, value)));

        load.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.ClaudeBaseUrlVariable}*");
    }

    [TestMethod]
    public void Load_WhenBaseUrlIsSetWithoutTheFakeCredential_Throws()
    {
        // Pointing a provider at the fake server with the real sign-in would send a real token over plain http.
        var data = (NativeTestOverrides.AppDataVariable, Path.GetTempPath());
        var claude = () => NativeTestOverrides.Load(Environment(data, (NativeTestOverrides.ClaudeBaseUrlVariable, "http://127.0.0.1:18001")));
        var codex = () => NativeTestOverrides.Load(Environment(data, (NativeTestOverrides.CodexBaseUrlVariable, "http://127.0.0.1:18001")));
        var antigravity = () => NativeTestOverrides.Load(Environment(data,
            (NativeTestOverrides.ProfileRootVariable, Path.GetTempPath()),
            (NativeTestOverrides.AntigravityBaseUrlVariable, "http://127.0.0.1:18001")));

        claude.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.ProfileRootVariable}*");
        codex.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.ProfileRootVariable}*");
        antigravity.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.AntigravityCredentialFileVariable}*");
    }

    [TestMethod]
    [DataRow(NativeTestOverrides.ProfileRootVariable)]
    [DataRow(NativeTestOverrides.AntigravityCredentialFileVariable)]
    public void Load_WhenAnOverrideIsSetWithoutTheDataFolder_Throws(string variable)
    {
        // Fake readings must not be written into the real settings, caches and cost index.
        var load = () => NativeTestOverrides.Load(Environment((variable, Path.GetTempPath())));

        load.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.AppDataVariable}*");
    }

    [TestMethod]
    [DataRow("relative\\profile")]
    [DataRow("\\rooted-but-no-drive")]
    [DataRow("C:drive-relative")]
    public void Load_WhenPathIsNotAbsolute_Throws(string value)
    {
        var load = () => NativeTestOverrides.Load(Environment((NativeTestOverrides.ProfileRootVariable, value)));

        load.Should().Throw<InvalidOperationException>().WithMessage($"*{NativeTestOverrides.ProfileRootVariable}*");
    }

    [TestMethod]
    public async Task HostAllowListHandler_WithLoopbackOrigin_AllowsExactlyThatOrigin()
    {
        var inner = new RecordingHandler(_ => Json("{}"));
        using var http = new HttpClient(new NativeHttp.HostAllowListHandler(inner, [new Uri("http://127.0.0.1:18001/")]));

        (await http.GetAsync("http://127.0.0.1:18001/api/oauth/usage")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetAsync("https://api.anthropic.com/api/oauth/usage")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var refused in new[] { "http://127.0.0.1:18002/x", "https://127.0.0.1:18001/x", "http://localhost:18001/x",
                     "http://user:pw@127.0.0.1:18001/x", "http://api.anthropic.com/api/oauth/usage" })
        {
            var send = () => http.GetAsync(refused);
            await send.Should().ThrowAsync<InvalidOperationException>(refused);
        }
        inner.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    [DataRow("http://localhost:18001/")]
    [DataRow("https://127.0.0.1:18001/")]
    [DataRow("http://10.0.0.1:18001/")]
    public void HostAllowListHandler_WhenGivenANonLoopbackOrigin_Throws(string origin)
    {
        var create = () => new NativeHttp.HostAllowListHandler(new RecordingHandler(_ => Json("{}")), [new Uri(origin)]);

        create.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void FileAntigravityCredentialSource_WhenFileExistsOrNot_ReadsItOrReportsNoSignIn()
    {
        var path = TempFile("""{"token": {"access_token": "synthetic-antigravity-token"}}""");
        try
        {
            using var blob = new FileAntigravityCredentialSource(path).ReadBlob();
            AntigravityCredentialParser.Parse(blob!.Span)!.AccessToken.Should().Be("synthetic-antigravity-token");
            new FileAntigravityCredentialSource(path + ".missing").ReadBlob().Should().BeNull();
        }
        finally { DeleteTemp(path); }
    }

    /// <summary>
    /// The whole Debug wiring over a real loopback socket: fake profile and credential files, the allow list's loopback
    /// exception and rebased endpoints. The bodies are the "normal" answers of tools/FakeProviders.
    /// </summary>
    [TestMethod]
    public async Task NativeUsageService_WithFakeProfileAndServer_ReadsEveryProviderFromTheFakes()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentswinbar-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        Directory.CreateDirectory(Path.Combine(root, ".codex"));
        var expires = DateTimeOffset.UtcNow.AddHours(5);
        var codexToken = Jwt($$"""{"exp":{{expires.ToUnixTimeSeconds()}}}""");
        File.WriteAllText(Path.Combine(root, ".claude", ".credentials.json"),
            $$$"""{"claudeAiOauth": {"accessToken": "synthetic-claude-token", "expiresAt": {{{expires.ToUnixTimeMilliseconds()}}}}}""");
        File.WriteAllText(Path.Combine(root, ".codex", "auth.json"),
            $$$"""{"tokens": {"access_token": "{{{codexToken}}}", "account_id": "synthetic-account"}}""");
        var antigravityFile = Path.Combine(root, "antigravity.json");
        File.WriteAllText(antigravityFile,
            $$"""{"token": {"access_token": "synthetic-antigravity-token", "expiry": "{{expires:O}}"}, "auth_method": "consumer"}""");
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<(string Path, string? Authorization)>();
        var serve = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                var path = context.Request.Url!.AbsolutePath;
                lock (seen) seen.Add((path, context.Request.Headers["Authorization"]));
                var body = Encoding.UTF8.GetBytes(FakeBody(path));
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        try
        {
            var origin = new Uri($"http://127.0.0.1:{port}/");
            using var service = new NativeUsageService(NullLoggerFactory.Instance, renewClaudeSignIn: null, new NativeTestOverrides
            {
                ProfileRoot = root, AppDataDirectory = Path.Combine(root, "data"), AntigravityCredentialFile = antigravityFile,
                ClaudeOrigin = origin, CodexOrigin = origin, AntigravityOrigin = origin
            });

            var claude = await service.FetchUsageJsonAsync("claude");
            var codex = await service.FetchUsageJsonAsync("codex");
            var gemini = await service.FetchUsageJsonAsync("gemini");

            claude.Should().Contain("\"usedPercent\": 37").And.Contain("\"error\": null");
            codex.Should().Contain("\"usedPercent\": 41").And.Contain("\"error\": null");
            gemini.Should().Contain("\"usedPercent\": 80").And.Contain("\"error\": null");
            seen.Should().BeEquivalentTo([
                ("/api/oauth/usage", "Bearer synthetic-claude-token"),
                ("/backend-api/wham/usage", "Bearer " + codexToken),
                ("/v1internal:loadCodeAssist", "Bearer synthetic-antigravity-token"),
                ("/v1internal:retrieveUserQuotaSummary", "Bearer synthetic-antigravity-token")]);
        }
        finally
        {
            listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FakeBody(string path) => path switch
    {
        "/api/oauth/usage" => """{"five_hour": {"utilization": 37.0, "resets_at": "2099-01-01T00:00:00Z"}, "seven_day": {"utilization": 12.5}}""",
        "/backend-api/wham/usage" =>
            """{"plan_type": "plus", "rate_limit": {"primary_window": {"used_percent": 41, "limit_window_seconds": 18000}}}""",
        "/v1internal:loadCodeAssist" =>
            """{"currentTier": {"id": "free-tier", "name": "Antigravity Free"}, "cloudaicompanionProject": "fake-project-123"}""",
        "/v1internal:retrieveUserQuotaSummary" => """
            {"groups": [{"displayName": "Gemini models", "buckets": [
              {"bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.61, "resetTime": "2099-01-01T00:00:00Z"},
              {"bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.2, "resetTime": "2099-01-01T00:00:00Z"}]}]}
            """,
        _ => "{}"
    };

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
#else
    [TestMethod]
    public void Load_InReleaseBuild_ReturnsNullWithoutReadingTheEnvironment()
    {
        var reads = 0;

        var overrides = NativeTestOverrides.Load(_ =>
        {
            reads++;
            return "http://127.0.0.1:18001";
        });

        overrides.Should().BeNull();
        reads.Should().Be(0);
        NativeTestOverrides.Current.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateHttpClient_InReleaseBuild_StillRefusesLoopback()
    {
        var overrides = new NativeTestOverrides { ProfileRoot = Path.GetTempPath(), ClaudeOrigin = new Uri("http://127.0.0.1:9/") };
        using var http = overrides.CreateHttpClient();

        var send = () => http.GetAsync("http://127.0.0.1:9/api/oauth/usage");

        await send.Should().ThrowAsync<InvalidOperationException>();
        overrides.CreateAntigravitySource().Should().BeNull();
    }

    [TestMethod]
    public void HostAllowListHandler_InReleaseBuild_HasOnlyTheAllowListConstructor() =>
        typeof(NativeHttp.HostAllowListHandler).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().ContainSingle();
#endif
}
