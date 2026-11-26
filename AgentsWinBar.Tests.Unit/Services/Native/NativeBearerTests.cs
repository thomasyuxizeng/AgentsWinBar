using System.Text;
using System.Text.Json;
using AgentsWinBar.Services.Native;
using AwesomeAssertions;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

[TestClass]
public class NativeBearerTests
{
    [TestMethod]
    [DataRow("claude", " ")]
    [DataRow("codex", "\n")]
    [DataRow("gemini", "\u007f")]
    [DataRow("claude", "\u00e9")]
    [DataRow("codex", "\t")]
    [DataRow("gemini", "\r")]
    public async Task FetchAsync_WhenStoredBearerIsMalformed_KeepsLastGoodWithoutSending(string provider, string badSuffix)
    {
        const string synthetic = "synthetic-bearer";
        var clock = new TestClock(Now);
        var source = new MutableCredentialSource();
        var file = TempFile(string.Empty);
        var claudeLogger = new ListLogger<ClaudeNativeClient>();
        var codexLogger = new ListLogger<CodexNativeClient>();
        var geminiLogger = new ListLogger<AntigravityNativeClient>();
        var handler = new RecordingHandler(request => Json(request.RequestUri!.AbsolutePath switch
        {
            "/api/oauth/usage" => """{"five_hour":{"utilization":37}}""",
            "/backend-api/wham/usage" => """{"rate_limit":{"primary_window":{"used_percent":37,"limit_window_seconds":18000}}}""",
            "/v1internal:loadCodeAssist" => """{"cloudaicompanionProject":"synthetic-project"}""",
            _ => """{"groups":[{"displayName":"Gemini models","buckets":[{"bucketId":"gemini-5h","window":"5h","remainingFraction":0.63}]}]}"""
        }));
        using var http = handler.Client();
        INativeUsageClient client = provider switch
        {
            "claude" => new ClaudeNativeClient(http, claudeLogger, new() { CredentialsPath = file, UseFreshCache = false }, clock),
            "codex" => new CodexNativeClient(http, codexLogger, new() { AuthPath = file }, clock),
            _ => new AntigravityNativeClient(http, geminiLogger, source, clock: clock)
        };

        try
        {
            WriteToken(synthetic);
            var good = await client.FetchAsync();
            good.Status.Should().Be(NativeUsageStatus.Ok);
            handler.Requests.Clear();

            WriteToken(synthetic + badSuffix);
            var bad = await client.FetchAsync();
            bad.Status.Should().Be(NativeUsageStatus.Failed);
            bad.Cached.Should().BeTrue();
            bad.Primary.Should().Be(good.Primary);
            bad.Message.Should().Contain("could not be read").And.NotContain(synthetic);
            handler.Requests.Should().BeEmpty();
            var messages = claudeLogger.Messages.Concat(codexLogger.Messages).Concat(geminiLogger.Messages);
            messages.Should().Contain(m => m.Contains("could not be read")).And.NotContain(m => m.Contains(synthetic));
        }
        finally { DeleteTemp(file); }

        void WriteToken(string token)
        {
            var value = JsonSerializer.Serialize(token);
            switch (provider)
            {
                case "claude":
                    File.WriteAllText(file, $$$"""{"claudeAiOauth":{"accessToken":{{{value}}}}}""");
                    break;
                case "codex":
                    File.WriteAllText(file, $$$"""{"tokens":{"access_token":{{{value}}}}}""");
                    break;
                default:
                    source.Json = $$"""{"access_token":{{value}}}""";
                    break;
            }
        }
    }

    private sealed class MutableCredentialSource : IAntigravityCredentialSource
    {
        public string Json { get; set; } = string.Empty;
        public SensitiveBuffer ReadBlob() => SensitiveBuffer.From(Encoding.UTF8.GetBytes(Json));
    }
}
