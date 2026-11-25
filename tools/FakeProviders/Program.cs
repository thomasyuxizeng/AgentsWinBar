using System.Globalization;
using System.Net;
using System.Text;

// Fake Claude, Codex and Antigravity (Gemini) usage endpoints on http://127.0.0.1:<port>/ for a Debug build of
// CodexBarWin running on its CODEXBARWIN_TEST_* hooks. See README.md. Request headers are never printed: the app
// sends its (fake) bearer token in Authorization.

if (FakeOptions.Parse(args) is not { } options)
{
    Console.Error.WriteLine(FakeOptions.Usage);
    return 2;
}

var state = new ScenarioState(options.Scenario, options.PerProvider);
using var stop = new CancellationTokenSource();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{options.Port}/");
try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.Error.WriteLine($"Cannot listen on http://127.0.0.1:{options.Port}/: {ex.Message}");
    return 1;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
if (options.ExitAfter is { } exitAfter) stop.CancelAfter(exitAfter);
using var stopListening = stop.Token.Register(listener.Stop);

Console.WriteLine($"Fake providers on http://127.0.0.1:{options.Port}/ ({state.Describe()}).");
Console.WriteLine(options.ExitAfter is { } limit
    ? $"Stops by itself after {limit.TotalSeconds:0} s, on Ctrl+C or on GET /__stop."
    : "Stops on Ctrl+C or on GET /__stop.");

while (!stop.IsCancellationRequested)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
    {
        break;
    }
    // Each request on its own task, so a slow answer does not hold up the others.
    _ = Task.Run(() => FakeServer.HandleAsync(context, state, stop));
}

Console.WriteLine("Fake providers stopped.");
return 0;

internal sealed record FakeOptions(int Port, string Scenario, IReadOnlyDictionary<string, string> PerProvider, TimeSpan? ExitAfter)
{
    public const string Usage = """
        Usage: FakeProviders [--port 18080] [--scenario normal] [--claude <s>] [--codex <s>] [--antigravity <s>]
                             [--exit-after <seconds>]
        Scenarios: normal, 401, 403, 429, 429-seconds, 429-date, 429-absurd, 500, slow, malformed, empty, huge
        Switch while running: curl.exe "http://127.0.0.1:<port>/__scenario?name=<s>[&provider=claude|codex|antigravity]"
        Show: curl.exe http://127.0.0.1:<port>/__scenario    Stop: curl.exe http://127.0.0.1:<port>/__stop
        """;

    public static FakeOptions? Parse(string[] args)
    {
        var port = 18080;
        var scenario = "normal";
        var perProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        TimeSpan? exitAfter = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) return null;
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536:
                    port = p;
                    break;
                case "--scenario" when Scenarios.IsKnown(value):
                    scenario = value;
                    break;
                case "--claude" or "--codex" or "--antigravity" when Scenarios.IsKnown(value):
                    perProvider[args[i - 1][2..]] = value;
                    break;
                case "--exit-after" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0:
                    exitAfter = TimeSpan.FromSeconds(s);
                    break;
                default:
                    return null;
            }
        }
        return new FakeOptions(port, scenario, perProvider, exitAfter);
    }
}

internal static class Scenarios
{
    public static readonly string[] All =
        ["normal", "401", "403", "429", "429-seconds", "429-date", "429-absurd", "500", "slow", "malformed", "empty", "huge"];

    public static readonly string[] Providers = ["claude", "codex", "antigravity"];

    public static bool IsKnown(string name) => Array.IndexOf(All, name) >= 0;
}

/// <summary>The scenario each provider answers with; switchable while the server runs.</summary>
internal sealed class ScenarioState(string scenario, IReadOnlyDictionary<string, string> perProvider)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _perProvider = new(perProvider, StringComparer.Ordinal);
    private string _default = scenario;

    public string For(string provider)
    {
        lock (_gate) return _perProvider.TryGetValue(provider, out var named) ? named : _default;
    }

    /// <summary>Sets one provider's scenario, or every provider's when <paramref name="provider"/> is null.</summary>
    public void Set(string name, string? provider)
    {
        lock (_gate)
        {
            if (provider is null)
            {
                _default = name;
                _perProvider.Clear();
            }
            else _perProvider[provider] = name;
        }
    }

    public string Describe() => string.Join(", ", Scenarios.Providers.Select(p => $"{p}: {For(p)}"));
}

internal static class FakeServer
{
    private const long HugeBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan SlowDelay = TimeSpan.FromSeconds(30);

    public static async Task HandleAsync(HttpListenerContext context, ScenarioState state, CancellationTokenSource stop)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";
        try
        {
            // Control routes take GET (a POST needs a body or Content-Length: 0, or http.sys answers 411).
            switch (path)
            {
                case "/__scenario" when request.QueryString["name"] is null:
                    await WriteAsync(response, 200, state.Describe() + "\n", "text/plain");
                    return;
                case "/__scenario":
                    var name = request.QueryString["name"]!;
                    var provider = request.QueryString["provider"];
                    if (!Scenarios.IsKnown(name) || provider is not null && Array.IndexOf(Scenarios.Providers, provider) < 0)
                    {
                        await WriteAsync(response, 400, FakeOptions.Usage + "\n", "text/plain");
                        return;
                    }
                    state.Set(name, provider);
                    await WriteAsync(response, 200, state.Describe() + "\n", "text/plain");
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} scenario -> {state.Describe()}");
                    return;
                case "/__stop":
                    await WriteAsync(response, 200, "stopping\n", "text/plain");
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} stop requested");
                    stop.Cancel();
                    return;
            }

            if (Route(request.HttpMethod, path) is not { } route)
            {
                await WriteAsync(response, 404, """{"error":"not found"}""");
                Log(request, path, "404");
                return;
            }
            var scenario = state.For(route.Provider);
            Log(request, path, scenario);
            await AnswerAsync(response, scenario, route.Body, stop.Token);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The app gave up (a timeout or the size cap) or the server is stopping.
            try { response.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    private static (string Provider, Func<DateTimeOffset, string> Body)? Route(string method, string path) => (method, path) switch
    {
        ("GET", "/api/oauth/usage") => ("claude", ClaudeUsage),
        ("GET", "/backend-api/wham/usage") => ("codex", CodexUsage),
        ("POST", "/v1internal:loadCodeAssist") => ("antigravity", _ => LoadCodeAssist),
        ("POST", "/v1internal:retrieveUserQuotaSummary") => ("antigravity", QuotaSummary),
        ("POST", "/v1internal:fetchAvailableModels") => ("antigravity", AvailableModels),
        _ => null
    };

    private static async Task AnswerAsync(HttpListenerResponse response, string scenario, Func<DateTimeOffset, string> body,
        CancellationToken stopping)
    {
        var now = DateTimeOffset.UtcNow;
        switch (scenario)
        {
            case "normal":
                await WriteAsync(response, 200, body(now));
                break;
            case "401":
                await WriteAsync(response, 401, """{"error":{"type":"authentication_error","message":"fake: token rejected"}}""");
                break;
            case "403":
                await WriteAsync(response, 403, """{"error":{"type":"permission_error","message":"fake: forbidden"}}""");
                break;
            case "429":
                await WriteAsync(response, 429, """{"error":{"type":"rate_limit_error","message":"fake: slow down"}}""");
                break;
            case "429-seconds":
                response.AddHeader("Retry-After", "120");
                await WriteAsync(response, 429, """{"error":{"type":"rate_limit_error"}}""");
                break;
            case "429-date":
                response.AddHeader("Retry-After", now.AddMinutes(10).ToString("R", CultureInfo.InvariantCulture));
                await WriteAsync(response, 429, """{"error":{"type":"rate_limit_error"}}""");
                break;
            case "429-absurd":
                response.AddHeader("Retry-After", "999999999999");
                await WriteAsync(response, 429, """{"error":{"type":"rate_limit_error"}}""");
                break;
            case "500":
                await WriteAsync(response, 500, """{"error":{"type":"api_error","message":"fake: internal error"}}""");
                break;
            case "slow":
                await Task.Delay(SlowDelay, stopping);
                await WriteAsync(response, 200, body(DateTimeOffset.UtcNow));
                break;
            case "malformed":
                await WriteAsync(response, 200, """{"five_hour": {"utilization": 37.0, "resets_at": "2026-""");
                break;
            case "empty":
                await WriteAsync(response, 200, "");
                break;
            case "huge":
                await WriteHugeAsync(response, stopping);
                break;
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string body,
        string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>An 8 MiB JSON body streamed in chunks (never held in memory), with no Content-Length.</summary>
    private static async Task WriteHugeAsync(HttpListenerResponse response, CancellationToken stopping)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.SendChunked = true;
        var output = response.OutputStream;
        await output.WriteAsync("{\"padding\": \""u8.ToArray(), stopping);
        var chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)'x');
        for (long sent = 0; sent < HugeBytes; sent += chunk.Length)
            await output.WriteAsync(chunk, stopping);
        await output.WriteAsync("\"}"u8.ToArray(), stopping);
        response.Close();
    }

    private static void Log(HttpListenerRequest request, string path, string answer) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {request.HttpMethod} {path} -> {answer}");

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);

    // Raw strings with $$$: interpolation is {{{...}}}, and JSON's own braces are never three in a row.
    private static string ClaudeUsage(DateTimeOffset now) => $$$"""
        {"five_hour": {"utilization": 37.0, "resets_at": "{{{Iso(now.AddHours(2))}}}"},
         "seven_day": {"utilization": 12.5, "resets_at": "{{{Iso(now.AddDays(3))}}}"},
         "seven_day_opus": null,
         "seven_day_sonnet": {"utilization": 4.0, "resets_at": "{{{Iso(now.AddDays(3))}}}"}}
        """;

    private static string CodexUsage(DateTimeOffset now) => $$$"""
        {"plan_type": "plus",
         "rate_limit": {"allowed": true, "limit_reached": false,
           "primary_window": {"used_percent": 41, "limit_window_seconds": 18000, "reset_at": {{{now.AddHours(2).ToUnixTimeSeconds()}}}},
           "secondary_window": {"used_percent": 9, "limit_window_seconds": 604800, "reset_at": {{{now.AddDays(4).ToUnixTimeSeconds()}}}}},
         "credits": {"has_credits": false, "unlimited": false, "balance": "0"}}
        """;

    private const string LoadCodeAssist = """
        {"currentTier": {"id": "free-tier", "name": "Antigravity Free"}, "cloudaicompanionProject": "fake-project-123"}
        """;

    private static string QuotaSummary(DateTimeOffset now) => $$$"""
        {"groups": [
          {"displayName": "Claude and GPT models", "buckets": [
            {"bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 0.75, "resetTime": "{{{Iso(now.AddDays(4))}}}"},
            {"bucketId": "3p-5h", "window": "5h", "remainingFraction": 0.9, "resetTime": "{{{Iso(now.AddHours(3))}}}"}]},
          {"displayName": "Gemini models", "buckets": [
            {"bucketId": "gemini-weekly", "window": "weekly", "remainingFraction": 0.61, "resetTime": "{{{Iso(now.AddDays(4))}}}"},
            {"bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.2, "resetTime": "{{{Iso(now.AddHours(3))}}}"}]}]}
        """;

    private static string AvailableModels(DateTimeOffset now) => $$$"""
        {"models": {"gemini-3-pro": {"displayName": "Gemini 3 Pro",
          "quotaInfo": {"remainingFraction": 0.5, "resetTime": "{{{Iso(now.AddHours(3))}}}"} } }}
        """;
}
