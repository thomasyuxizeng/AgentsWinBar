using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodexBarWin.Services.Native;
using Microsoft.Extensions.Logging;

// Native usage probe (spike). Prints usage numbers, statuses and costs. Never prints token values, client secrets
// or credential blobs: only a SHA-256 prefix of the Antigravity blob, and only key facts (expiry deltas).
//
//   NativeProbe hash                         SHA-256 prefix and expiry state of the Antigravity credential (no network)
//   NativeProbe discover                     find the Antigravity OAuth client in the installed binaries (no network)
//   NativeProbe single <claude|codex|gemini> [--warm-after <s>]   one provider, cold call (and optional warm call)
//   NativeProbe all [--warm-after <s>]       one refresh = the three providers in parallel, cold (and optional warm)
//   NativeProbe replay [--rounds <n>]        parse/map cost with synthetic responses and sign-ins (no network)

var mode = args.FirstOrDefault() ?? "help";
var warmAfter = Arg("--warm-after") is { } w ? TimeSpan.FromSeconds(double.Parse(w, CultureInfo.InvariantCulture)) : (TimeSpan?)null;
var self = Process.GetCurrentProcess();
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"NativeProbe {mode} | {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} | .NET {Environment.Version} | {RuntimeInfo()}");
PrintMemory("baseline (before any call)");

switch (mode)
{
    case "hash": Hash(); break;
    case "discover": Discover(); break;
    case "single": await Single(args.ElementAtOrDefault(1) ?? throw new ArgumentException("provider required")); break;
    case "all": await All(); break;
    case "replay": await Replay(int.Parse(Arg("--rounds") ?? "5", CultureInfo.InvariantCulture)); break;
    default: Console.WriteLine("modes: hash | discover | single <provider> [--warm-after s] | all [--warm-after s] | replay [--rounds n]"); break;
}
return;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string RuntimeInfo() =>
    $"GC={(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} TieredPGO={AppContext.TryGetSwitch("System.Runtime.TieredPGO", out var pgo) && pgo}";

void PrintMemory(string label)
{
    self.Refresh();
    Console.WriteLine($"  [{label}] working set {self.WorkingSet64 / 1024.0 / 1024:F1} MB, private bytes {self.PrivateMemorySize64 / 1024.0 / 1024:F1} MB, managed heap {GC.GetTotalMemory(false) / 1024.0 / 1024:F1} MB");
}

async Task<T> Measure<T>(string label, Func<Task<T>> action)
{
    self.Refresh();
    var cpu0 = self.TotalProcessorTime;
    var alloc0 = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew();
    var result = await action();
    sw.Stop();
    var alloc1 = GC.GetTotalAllocatedBytes(true);
    self.Refresh();
    var cpu1 = self.TotalProcessorTime;
    Console.WriteLine($"  [{label}] wall {sw.Elapsed.TotalMilliseconds:F0} ms | CPU {(cpu1 - cpu0).TotalMilliseconds:F0} ms | allocated {(alloc1 - alloc0) / 1024.0:F0} KB");
    PrintMemory(label + ", after");
    return result;
}

ILoggerFactory Loggers() => new ConsoleLoggerFactory();

// --cap anthropic=2,chatgpt=2,google=4 : hard per-provider request caps so live budgets cannot be exceeded.
HttpClient CreateHttp()
{
    var caps = (Arg("--cap") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1], CultureInfo.InvariantCulture));
    var inner = new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90), PooledConnectionLifetime = TimeSpan.FromMinutes(15), ConnectTimeout = TimeSpan.FromSeconds(15)
    };
    // Same handler settings as NativeHttp.CreateClient, plus the budget guard behind the host allow list.
    return new HttpClient(new NativeHttp.HostAllowListHandler(new BudgetHandler(caps, inner))) { Timeout = Timeout.InfiniteTimeSpan };
}

(INativeUsageClient Client, HttpClient Http) Build(string provider, HttpClient? shared = null, ILoggerFactory? factory = null)
{
    var http = shared ?? CreateHttp();
    factory ??= Loggers();
    INativeUsageClient client = provider switch
    {
        // The fresh-cache would answer a second call from memory; the probe measures real calls.
        "claude" => new ClaudeNativeClient(http, factory.CreateLogger<ClaudeNativeClient>(), new ClaudeNativeOptions { UseFreshCache = false }),
        "codex" => new CodexNativeClient(http, factory.CreateLogger<CodexNativeClient>()),
        "gemini" or "antigravity" => new AntigravityNativeClient(http, factory.CreateLogger<AntigravityNativeClient>()),
        _ => throw new ArgumentException("provider must be claude, codex or gemini")
    };
    return (client, http);
}

async Task Single(string provider)
{
    var (client, http) = Build(provider);
    using (http)
    {
        var cold = await Measure($"{provider} cold", () => client.FetchAsync());
        Print(cold);
        if (warmAfter is { } wait)
        {
            Console.WriteLine($"  waiting {wait.TotalSeconds:F0} s before the warm call...");
            await Task.Delay(wait);
            var warm = await Measure($"{provider} warm", () => client.FetchAsync());
            Print(warm);
        }
    }
}

async Task All()
{
    using var http = CreateHttp();
    var factory = Loggers();
    var clients = new[] { "claude", "codex", "gemini" }.Select(p => Build(p, http, factory).Client).ToArray();
    async Task<NativeUsageResult[]> Refresh() => await Task.WhenAll(clients.Select(async c =>
    {
        var sw = Stopwatch.StartNew();
        var r = await c.FetchAsync();
        Console.WriteLine($"    {c.ProviderId}: own wall {sw.Elapsed.TotalMilliseconds:F0} ms");
        return r;
    }));

    var cold = await Measure("refresh (3 in parallel) cold", Refresh);
    foreach (var r in cold) Print(r);
    if (warmAfter is { } wait)
    {
        Console.WriteLine($"  waiting {wait.TotalSeconds:F0} s before the warm refresh...");
        await Task.Delay(wait);
        var warm = await Measure("refresh (3 in parallel) warm", Refresh);
        foreach (var r in warm) Print(r);
    }
}

void Print(NativeUsageResult r)
{
    Console.WriteLine($"  -> {r.Provider}: {r.Status}{(r.HttpStatus is { } h ? $" (HTTP {h})" : "")}, network calls {r.NetworkCalls}, source {r.Source}, plan {r.LoginMethod ?? "(none)"}" +
                      $"{(r.Cached ? ", cached" : "")}{(r.ExpiredFor is { } ago ? $", expired {ago.TotalMinutes:F0} min ago" : "")}");
    if (r.Message != null) Console.WriteLine($"     message: {r.Message}");
    void W(string name, NativeUsageWindow? x)
    {
        if (x != null) Console.WriteLine($"     {name,-34} {x.UsedPercent,6:F1}% used | {x.WindowMinutes,5} min | resets {x.ResetsAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "-"} | {x.ResetDescription}");
    }
    W("primary", r.Primary);
    W("secondary", r.Secondary);
    W("tertiary", r.Tertiary);
    foreach (var e in r.ExtraWindows) W($"extra {e.Id} \"{e.Title}\"", e.Window);
}

void Hash()
{
    var source = new WindowsCredentialManagerSource();
    using var blob = source.ReadBlob();
    if (blob == null) { Console.WriteLine("  no Antigravity credential"); return; }
    var hash = SHA256.HashData(blob.Span);
    Console.WriteLine($"  Antigravity credential: {blob.Length} bytes, sha256 prefix {Convert.ToHexStringLower(hash)[..8]}");
    var credentials = AntigravityCredentialParser.Parse(blob.Span);
    if (credentials?.Expiry is { } expiry)
    {
        var delta = expiry - DateTimeOffset.UtcNow;
        Console.WriteLine(delta > TimeSpan.Zero ? $"  access token valid for {delta.TotalMinutes:F0} more min" : $"  access token expired {-delta.TotalMinutes:F0} min ago");
    }
}

void Discover()
{
    foreach (var path in AntigravityOAuthClientDiscovery.CandidatePaths())
    {
        if (!File.Exists(path)) continue;
        var name = Path.GetFileName(path);
        var client = Measure($"discover in {name} ({new FileInfo(path).Length / 1024 / 1024} MB)",
            () => Task.FromResult(AntigravityOAuthClientDiscovery.Discover([path]))).GetAwaiter().GetResult();
        // Only facts, never the values.
        Console.WriteLine($"  -> {name}: client found {client != null}, consumer client {client?.IsConsumerClient == true}, secret shape valid {client?.ClientSecret.Length == 35}");
    }
}

async Task Replay(int rounds)
{
    // Synthetic sign-ins and responses (shapes as recorded upstream), served by an in-memory handler.
    var dir = Path.Combine(AppContext.BaseDirectory, "replay");
    Directory.CreateDirectory(dir);
    var claudePath = Path.Combine(dir, "claude.json");
    var codexPath = Path.Combine(dir, "codex.json");
    File.WriteAllText(claudePath, $$"""{"claudeAiOauth":{"accessToken":"synthetic","expiresAt":{{DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeMilliseconds()}},"subscriptionType":"team"} }""");
    File.WriteAllText(codexPath, """{"tokens":{"access_token":"synthetic","account_id":"synthetic"}}""");
    var agyBlob = $$"""{"token":{"access_token":"synthetic","expiry":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"},"auth_method":"consumer"}""";

    using var http = new HttpClient(new NativeHttp.HostAllowListHandler(new ReplayHandler())) { Timeout = Timeout.InfiniteTimeSpan };
    var factory = new ConsoleLoggerFactory();
    INativeUsageClient[] clients =
    [
        new ClaudeNativeClient(http, factory.CreateLogger<ClaudeNativeClient>(), new ClaudeNativeOptions { CredentialsPath = claudePath, UseFreshCache = false }),
        new CodexNativeClient(http, factory.CreateLogger<CodexNativeClient>(), new CodexNativeOptions { AuthPath = codexPath }),
        new AntigravityNativeClient(http, factory.CreateLogger<AntigravityNativeClient>(), new ReplaySource(agyBlob))
    ];
    foreach (var client in clients)
    {
        for (var i = 0; i < rounds; i++)
        {
            var result = await Measure($"replay {client.ProviderId} #{i + 1}{(i == 0 ? " (cold)" : "")}", () => client.FetchAsync());
            if (i == 0) Console.WriteLine($"  -> {result.Provider}: {result.Status}, windows {(result.Primary != null ? 1 : 0) + (result.Secondary != null ? 1 : 0) + (result.Tertiary != null ? 1 : 0) + result.ExtraWindows.Count}");
        }
    }
    for (var i = 0; i < rounds; i++)
        await Measure($"replay refresh (3 in parallel) #{i + 1}", () => Task.WhenAll(clients.Select(c => c.FetchAsync())));
}

sealed class BudgetHandler(Dictionary<string, int> caps, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly Dictionary<string, int> _used = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var key = host.EndsWith("anthropic.com", StringComparison.Ordinal) ? "anthropic" : host.EndsWith("chatgpt.com", StringComparison.Ordinal) ? "chatgpt" : "google";
        lock (_used)
        {
            var used = _used.GetValueOrDefault(key) + 1;
            if (caps.TryGetValue(key, out var cap) && used > cap)
                throw new HttpRequestException($"probe call budget for {key} exhausted ({cap})");
            _used[key] = used;
        }
        Console.WriteLine($"     request {request.Method} {request.RequestUri.GetLeftPart(UriPartial.Path)}");
        return base.SendAsync(request, cancellationToken);
    }
}

sealed class ReplaySource(string json) : IAntigravityCredentialSource
{
    public SensitiveBuffer? ReadBlob() => SensitiveBuffer.From(Encoding.UTF8.GetBytes(json));
}

sealed class ReplayHandler : HttpMessageHandler
{
    private const string Claude = """{"five_hour":{"utilization":37.0,"resets_at":"2026-11-25T08:29:59.912345+00:00"},"seven_day":{"utilization":12.5,"resets_at":"2026-11-30T03:00:00+00:00"},"seven_day_oauth_apps":null,"seven_day_opus":null,"seven_day_sonnet":{"utilization":4.0,"resets_at":"2026-11-30T03:00:00+00:00"},"extra_usage":{"is_enabled":false},"limits":[{"kind":"weekly_scoped","group":"weekly","percent":18,"resets_at":"2026-11-28T08:00:00Z","scope":{"model":{"id":"fable","display_name":"Fable"}}}]}""";
    private const string Codex = """{"plan_type":"team","rate_limit":{"allowed":true,"limit_reached":false,"primary_window":{"used_percent":23,"limit_window_seconds":18000,"reset_after_seconds":9000,"reset_at":1764060000},"secondary_window":{"used_percent":9,"limit_window_seconds":604800,"reset_after_seconds":400000,"reset_at":1764400000}},"credits":{"has_credits":false,"unlimited":false,"balance":"0"},"additional_rate_limits":null}""";
    private const string CodeAssist = """{"currentTier":{"id":"free-tier","name":"Antigravity Free"},"allowedTiers":[{"id":"free-tier","isDefault":true}],"cloudaicompanionProject":"synthetic-project"}""";
    private const string Summary = """{"groups":[{"displayName":"Gemini models","buckets":[{"bucketId":"gemini-5h","window":"5h","remainingFraction":0.2,"resetTime":"2026-11-25T09:38:00Z"},{"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.61,"resetTime":"2026-11-29T06:00:00Z"}]},{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-5h","window":"5h","remainingFraction":0.9,"resetTime":"2026-11-25T09:00:00Z"},{"bucketId":"3p-weekly","window":"weekly","remainingFraction":0.75,"resetTime":"2026-11-30T00:00:00Z","description":"Shared weekly quota"}]}]}""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.RequestUri!.AbsolutePath switch
        {
            "/api/oauth/usage" => Claude,
            "/backend-api/wham/usage" => Codex,
            "/v1internal:loadCodeAssist" => CodeAssist,
            "/v1internal:retrieveUserQuotaSummary" => Summary,
            _ => null
        };
        return Task.FromResult(body == null ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Minimal console logger (the repo has no console logging package). Messages never contain token values.</summary>
sealed class ConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName[(categoryName.LastIndexOf('.') + 1)..]);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Console.Error.WriteLine($"     log {logLevel} {category}: {formatter(state, exception)}");
        }
    }
}
