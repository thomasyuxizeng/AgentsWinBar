using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWin.Services.Native.Cost;

/// <summary>USD per million tokens for one model, as models.dev publishes it. Missing fields stay unknown.</summary>
internal sealed record ModelPrice(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite,
    LongContextPrice? Long = null);

/// <summary>
/// The rates of the long-context lane ("context_over_200k"). <see cref="TierSize"/> is the size of the context tier
/// with the same rates, when the catalog lists one; the lane's name is not the threshold.
/// </summary>
internal sealed record LongContextPrice(int? TierSize, decimal? Input, decimal? Output, decimal? CacheRead,
    decimal? CacheWrite);

/// <summary>
/// Model rates in the models.dev shape: either the full catalog (provider id at the top level) or a snapshot with a
/// "providers" object. Only the providers the cost scanner prices are kept.
/// </summary>
internal sealed class CostPriceCatalog
{
    internal const string Anthropic = "anthropic";
    internal const string OpenAI = "openai";
    private static readonly string[] KeptProviders = [Anthropic, OpenAI];
    private static readonly Lazy<CostPriceCatalog> BundledCatalog = new(LoadBundled);

    private readonly Dictionary<string, Dictionary<string, ModelPrice>> _providers;

    internal static CostPriceCatalog Empty { get; } = new([], default);

    /// <summary>The snapshot shipped with the app (tools/update-model-prices.ps1 regenerates it).</summary>
    internal static CostPriceCatalog Bundled => BundledCatalog.Value;

    private static CostPriceCatalog LoadBundled() => Parse(PricingResources.Read("models-dev-prices.json")) ?? Empty;

    /// <summary>When the rates were read from models.dev.</summary>
    internal DateTimeOffset Stamp { get; }

    internal CostPriceCatalog(Dictionary<string, Dictionary<string, ModelPrice>> providers, DateTimeOffset stamp)
    {
        _providers = providers;
        Stamp = stamp;
    }

    internal int Count(string provider) => _providers.TryGetValue(provider, out var models) ? models.Count : 0;

    /// <summary>Upstream's refresh check: both direct pricing sources must list at least one priced model.</summary>
    internal bool IsPlausible => Count(Anthropic) > 0 && Count(OpenAI) > 0;

    /// <summary>
    /// Exact id first, then Claude's "@default" alias, then a catalog id whose date suffix is the only difference
    /// (the index stores model ids without their snapshot date). Among several dated ids the latest date wins, so
    /// the choice does not depend on the catalog's order.
    /// </summary>
    internal ModelPrice? Find(string provider, string model)
    {
        if (!_providers.TryGetValue(provider, out var models)) return null;
        if (models.TryGetValue(model, out var price)) return price;
        if (model.StartsWith("claude-", StringComparison.Ordinal) &&
            models.TryGetValue(model + "@default", out price)) return price;
        ModelPrice? latest = null;
        string? latestId = null;
        var latestDate = -1;
        foreach (var (id, candidate) in models)
        {
            if (id.Length <= model.Length || !id.StartsWith(model, StringComparison.Ordinal) ||
                DateSuffix(id.AsSpan(model.Length)) is not { } date) continue;
            if (date > latestDate || date == latestDate && string.CompareOrdinal(id, latestId) > 0)
                (latest, latestId, latestDate) = (candidate, id, date);
        }
        return latest;
    }

    /// <summary>The date of a "-YYYYMMDD" or "-YYYY-MM-DD" suffix as the number YYYYMMDD, or null for any other suffix.</summary>
    private static int? DateSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.Length is not (9 or 11) || suffix[0] != '-') return null;
        var digits = 0;
        var date = 0;
        foreach (var c in suffix[1..])
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                date = date * 10 + (c - '0');
            }
            else if (c != '-') return null;
        }
        return digits == 8 ? date : null;
    }

    /// <summary>Keeps rates the older catalog had for models the newer one no longer lists.</summary>
    internal CostPriceCatalog MergeMissingFrom(CostPriceCatalog older)
    {
        var merged = _providers.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, ModelPrice>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (provider, models) in older._providers)
        {
            if (!merged.TryGetValue(provider, out var target)) merged[provider] = target = new(StringComparer.Ordinal);
            foreach (var (id, price) in models) target.TryAdd(id, price);
        }
        return new(merged, Stamp);
    }

    internal static CostPriceCatalog? Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var stamp = ReadStamp(root, "fetchedAt") ?? ReadStamp(root, "generatedAt") ?? default;
            var source = root.TryGetProperty("providers", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : root;
            var providers = new Dictionary<string, Dictionary<string, ModelPrice>>(StringComparer.Ordinal);
            foreach (var provider in source.EnumerateObject())
            {
                var id = provider.Name.Trim().ToLowerInvariant();
                if (Array.IndexOf(KeptProviders, id) < 0 || provider.Value.ValueKind != JsonValueKind.Object ||
                    !provider.Value.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Object) continue;
                providers[id] = ParseModels(models.EnumerateObject().Select(m => (m.Name, m.Value)));
            }
            return new(providers, stamp);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Reads the full models.dev catalog from a stream without holding it in memory: only the kept providers are
    /// materialized, and the others are skipped whatever their shape.
    /// </summary>
    internal static async Task<CostPriceCatalog?> ParseLiveAsync(Stream stream, DateTimeOffset stamp,
        CancellationToken token)
    {
        try
        {
            var live = await JsonSerializer.DeserializeAsync(stream,
                CostPriceJsonContext.Default.LiveCatalog, token).ConfigureAwait(false);
            if (live is null) return null;
            var providers = new Dictionary<string, Dictionary<string, ModelPrice>>(StringComparer.Ordinal);
            foreach (var (id, provider) in new[] { (Anthropic, live.Anthropic), (OpenAI, live.OpenAI) })
            {
                if (provider.ValueKind != JsonValueKind.Object || !provider.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Object) continue;
                providers[id] = ParseModels(models.EnumerateObject().Select(m => (m.Name, m.Value)));
            }
            return new(providers, stamp);
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, ModelPrice> ParseModels(IEnumerable<(string Id, JsonElement Model)> models)
    {
        var result = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
        foreach (var (id, model) in models)
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("cost", out var cost)) continue;
            if (ParseCost(cost) is { } price) result[id.Trim()] = price;
        }
        return result;
    }

    /// <summary>A models.dev cost block. Null when it has no input or no output rate.</summary>
    internal static ModelPrice? ParseCost(JsonElement cost)
    {
        if (cost.ValueKind != JsonValueKind.Object) return null;
        if (Rate(cost, "input") is not { } input || Rate(cost, "output") is not { } output) return null;
        LongContextPrice? longContext = null;
        if (cost.TryGetProperty("context_over_200k", out var over) && over.ValueKind == JsonValueKind.Object)
        {
            longContext = new(null, Rate(over, "input"), Rate(over, "output"), Rate(over, "cache_read"),
                Rate(over, "cache_write"));
            if (cost.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
            {
                foreach (var tier in tiers.EnumerateArray())
                {
                    if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("tier", out var kind) ||
                        kind.ValueKind != JsonValueKind.Object ||
                        !kind.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                        !type.ValueEquals("context") ||
                        !kind.TryGetProperty("size", out var size) || !size.TryGetInt32(out var tokens) || tokens <= 0)
                        continue;
                    // Match the lane's rates to an explicit context tier, as upstream does.
                    if (Rate(tier, "input") == longContext.Input && Rate(tier, "output") == longContext.Output &&
                        Rate(tier, "cache_read") == longContext.CacheRead &&
                        Rate(tier, "cache_write") == longContext.CacheWrite)
                    {
                        longContext = longContext with { TierSize = tokens };
                        break;
                    }
                }
            }
        }
        return new(input, output, Rate(cost, "cache_read"), Rate(cost, "cache_write"), longContext);
    }

    /// <summary>A finite rate of zero or more; anything else is unknown.</summary>
    internal static decimal? Rate(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var rate) && rate >= 0 && rate <= 1_000_000 ? rate : null;

    private static DateTimeOffset? ReadStamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp) ? stamp : null;

    /// <summary>Writes the snapshot shape that <see cref="Parse"/> reads back.</summary>
    internal byte[] Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("source", CostPriceStore.CatalogUrl);
            writer.WriteString("fetchedAt", Stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.WriteStartObject("providers");
            foreach (var (provider, models) in _providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(provider);
                writer.WriteString("id", provider);
                writer.WriteStartObject("models");
                foreach (var (id, price) in models.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(id);
                    writer.WriteString("id", id);
                    writer.WriteStartObject("cost");
                    WriteRates(writer, price.Input, price.Output, price.CacheRead, price.CacheWrite);
                    if (price.Long is { } lane)
                    {
                        writer.WriteStartObject("context_over_200k");
                        WriteRates(writer, lane.Input, lane.Output, lane.CacheRead, lane.CacheWrite);
                        writer.WriteEndObject();
                        if (lane.TierSize is { } size)
                        {
                            writer.WriteStartArray("tiers");
                            writer.WriteStartObject();
                            WriteRates(writer, lane.Input, lane.Output, lane.CacheRead, lane.CacheWrite);
                            writer.WriteStartObject("tier");
                            writer.WriteString("type", "context");
                            writer.WriteNumber("size", size);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                            writer.WriteEndArray();
                        }
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteRates(Utf8JsonWriter writer, decimal? input, decimal? output, decimal? read, decimal? write)
    {
        if (input is { } i) writer.WriteNumber("input", i);
        if (output is { } o) writer.WriteNumber("output", o);
        if (read is { } r) writer.WriteNumber("cache_read", r);
        if (write is { } w) writer.WriteNumber("cache_write", w);
    }
}

/// <summary>
/// Upstream's built-in tables as data (Pricing/pricing-overlay.json): rates for models that models.dev does not
/// list, fields it leaves out, and rates from before a published price change.
/// </summary>
internal sealed class CostPriceOverlay
{
    private static readonly Lazy<CostPriceOverlay> BundledOverlay = new(LoadBundled);

    internal static CostPriceOverlay Empty { get; } = new();
    internal static CostPriceOverlay Bundled => BundledOverlay.Value;

    internal Dictionary<string, ModelPrice> Claude { get; init; } = [];
    internal Dictionary<string, ModelPrice> ClaudeHistorical { get; init; } = [];
    internal Dictionary<string, ModelPrice> Codex { get; init; } = [];
    internal Dictionary<string, ModelPrice> CodexHistorical { get; init; } = [];

    private static CostPriceOverlay LoadBundled() => Parse(PricingResources.Read("pricing-overlay.json")) ?? Empty;

    /// <summary>Per Codex model: replaces the context tier size as the long-context threshold.</summary>
    internal Dictionary<string, int> CodexLongContextThresholds { get; init; } = [];

    internal static CostPriceOverlay? Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new()
            {
                Claude = Table(root, "claude"), ClaudeHistorical = Table(root, "claudeHistorical"),
                Codex = Table(root, "codex"), CodexHistorical = Table(root, "codexHistorical"),
                CodexLongContextThresholds = Thresholds(root, "codexLongContextThresholds")
            };
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, int> Thresholds(JsonElement root, string name)
    {
        var thresholds = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var models) || models.ValueKind != JsonValueKind.Object) return thresholds;
        foreach (var model in models.EnumerateObject())
            if (model.Value.ValueKind == JsonValueKind.Number && model.Value.TryGetInt32(out var tokens) && tokens > 0)
                thresholds[model.Name.Trim()] = tokens;
        return thresholds;
    }

    private static Dictionary<string, ModelPrice> Table(JsonElement root, string name)
    {
        var table = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var models) || models.ValueKind != JsonValueKind.Object) return table;
        foreach (var model in models.EnumerateObject())
            if (CostPriceCatalog.ParseCost(model.Value) is { } price) table[model.Name.Trim()] = price;
        return table;
    }
}

/// <summary>
/// The user's own rates (custom-pricing.json in the app's data folder, upstream's format): USD per million tokens,
/// exact model key, 0 means free, and a missing field stays unknown instead of being filled from another source.
/// </summary>
internal sealed class CostCustomPricing
{
    internal const string FileName = "custom-pricing.json";
    internal readonly record struct Rates(decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite);

    private readonly Dictionary<string, Rates> _entries;

    internal static CostCustomPricing Empty { get; } = new([]);

    private CostCustomPricing(Dictionary<string, Rates> entries) => _entries = entries;

    internal bool IsEmpty => _entries.Count == 0;

    internal Rates? Find(string providerId, string model)
    {
        if (_entries.Count == 0) return null;
        var key = model.Trim().ToLowerInvariant();
        if (key.Length == 0) return null;
        if (_entries.TryGetValue(key, out var exact)) return exact;
        return _entries.TryGetValue(providerId + "/" + key, out var routed) ? routed : null;
    }

    internal static CostCustomPricing Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return Empty;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            var entries = new Dictionary<string, Rates>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var key = entry.Name.Trim().ToLowerInvariant();
                if (key.Length == 0 || entry.Value.ValueKind != JsonValueKind.Object) continue;
                var value = entry.Value;
                var rates = new Rates(CostPriceCatalog.Rate(value, "input"), CostPriceCatalog.Rate(value, "output"),
                    CostPriceCatalog.Rate(value, "cacheRead") ?? CostPriceCatalog.Rate(value, "cache_read"),
                    CostPriceCatalog.Rate(value, "cacheWrite") ?? CostPriceCatalog.Rate(value, "cache_write") ??
                    CostPriceCatalog.Rate(value, "cacheCreation") ?? CostPriceCatalog.Rate(value, "cache_creation"));
                if (rates.Input is null && rates.Output is null && rates.CacheRead is null && rates.CacheWrite is null)
                    continue;
                entries[key] = rates;
            }
            return entries.Count == 0 ? Empty : new(entries);
        }
        catch (JsonException) { return Empty; }
    }
}

internal static class PricingResources
{
    /// <summary>Reads a price file embedded in the assembly that compiles this folder.</summary>
    internal static byte[] Read(string fileName)
    {
        using var stream = typeof(PricingResources).Assembly.GetManifestResourceStream("CodexBarWin.Pricing." + fileName);
        if (stream is null) return [];
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>
/// The two providers the scanner prices, read from the full models.dev catalog. Every other provider is skipped
/// by the reader without being kept, whatever its shape.
/// </summary>
internal sealed class LiveCatalog
{
    [JsonPropertyName(CostPriceCatalog.Anthropic)]
    public JsonElement Anthropic { get; set; }

    [JsonPropertyName(CostPriceCatalog.OpenAI)]
    public JsonElement OpenAI { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LiveCatalog))]
internal sealed partial class CostPriceJsonContext : JsonSerializerContext;
