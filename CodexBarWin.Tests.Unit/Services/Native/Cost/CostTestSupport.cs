using System.Net;
using System.Text;
using CodexBarWin.Services.Native.Cost;

namespace CodexBarWin.Tests.Unit.Services.Native.Cost;

/// <summary>Synthetic fixtures shared by the native cost tests. Nothing here reads a real log or price file.</summary>
internal static class CostTestSupport
{
    internal static CostPriceCatalog Catalog(string json) => CostPriceCatalog.Parse(Encoding.UTF8.GetBytes(json))!;

    internal static CostPriceOverlay Overlay(string json) => CostPriceOverlay.Parse(Encoding.UTF8.GetBytes(json))!;

    internal static CostCustomPricing Custom(string json) => CostCustomPricing.Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>A catalog in the snapshot shape with the given model blocks per provider.</summary>
    internal static string CatalogJson(string anthropicModels, string openAiModels, string stamp = "2030-01-01T00:00:00Z") =>
        "{\"fetchedAt\":\"" + stamp + "\",\"providers\":{" +
        "\"anthropic\":{\"id\":\"anthropic\",\"models\":{" + anthropicModels + "}}," +
        "\"openai\":{\"id\":\"openai\",\"models\":{" + openAiModels + "}}}}";

    internal static string Model(string id, string cost) =>
        "\"" + id + "\":{\"id\":\"" + id + "\",\"cost\":{" + cost + "}}";

    internal static string TempDirectory(string prefix)
    {
        var path = Path.Combine(AppContext.BaseDirectory, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static byte[] Compress(string text)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    internal const string CodexSession = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-1\"}}";

    internal static string CodexContext(string model = "gpt-6.2-sol") =>
        "{\"type\":\"turn_context\",\"payload\":{\"model\":\"" + model + "\"}}";

    /// <summary>One cumulative token_count record; <paramref name="total"/> is the running input total.</summary>
    internal static string CodexCount(int total, int last, string timestamp = "2026-11-25T09:00:00Z") =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + timestamp + "\",\"payload\":{\"type\":\"token_count\",\"info\":{" +
        "\"total_token_usage\":{\"input_tokens\":" + total + ",\"cached_input_tokens\":0,\"output_tokens\":0}," +
        "\"last_token_usage\":{\"input_tokens\":" + last + ",\"cached_input_tokens\":0,\"output_tokens\":0}}}}";

    internal static string ClaudeLine(string id, int input, string model = "claude-sonnet-5-5",
        string timestamp = "2026-11-25T09:00:00Z") =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"requestId\":\"request-" + id +
        "\",\"message\":{\"id\":\"message-" + id + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input + "}}}";

    internal static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Answers every request from a delegate and counts the calls.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    internal int Requests { get; private set; }
    internal Uri? LastUri { get; private set; }

    internal static StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        LastUri = request.RequestUri;
        return Task.FromResult(respond(request));
    }
}
