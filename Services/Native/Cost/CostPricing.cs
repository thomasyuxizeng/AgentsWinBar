namespace CodexBarWin.Services.Native.Cost;

public enum CostProvider { Claude, Codex }

public readonly record struct CostRates(decimal Input, decimal Output, decimal CacheRead,
    decimal CacheWrite = 0, decimal CacheWriteOneHour = 0)
{
    internal decimal Price(CostTokens tokens) =>
        (Input * tokens.Input + Output * tokens.Output + CacheRead * tokens.Read +
         CacheWrite * (tokens.Write - tokens.OneHour) + CacheWriteOneHour * tokens.OneHour) / 1_000_000m;

    internal CostRates Multiply(decimal factor) => factor == 1 ? this
        : new(Input * factor, Output * factor, CacheRead * factor, CacheWrite * factor, CacheWriteOneHour * factor);
}

internal readonly record struct CostTokens(long Input, long Output, long Read, long Write = 0,
    long OneHour = 0, long Reasoning = 0)
{
    internal long Total => Input + Output + Read + Write;

    internal CostTokens Add(CostTokens other) => new(Input + other.Input, Output + other.Output,
        Read + other.Read, Write + other.Write, OneHour + other.OneHour, Reasoning + other.Reasoning);

    internal CostTokens Subtract(CostTokens other) => new(Math.Max(0, Input - other.Input),
        Math.Max(0, Output - other.Output), Math.Max(0, Read - other.Read),
        Math.Max(0, Write - other.Write), 0, Math.Max(0, Reasoning - other.Reasoning));

    internal bool Contains(CostTokens other) => Input >= other.Input && Output >= other.Output &&
        Read >= other.Read && Write >= other.Write;

    internal CostTokens Uncached() => this with
    {
        Input = Input - Math.Min(Input, Read) - Math.Min(Input - Math.Min(Input, Read), Write),
        Read = Math.Min(Input, Read),
        Write = Math.Min(Input - Math.Min(Input, Read), Write),
        Reasoning = Math.Min(Output, Reasoning)
    };
}

/// <summary>
/// List prices in USD per million tokens, ported from CodexBar's pricing (CostUsagePricing.swift at a26ca57). Rates
/// are data: the user's custom-pricing.json first, then rates from before a published price change, then the
/// models.dev catalog, then upstream's built-in tables (Pricing/pricing-overlay.json). The rules stay in code:
/// model aliases, long-context tiers, Claude cache-write TTLs and the Codex Fast multipliers. A model without a
/// rate stays unpriced (null) so the report can count its tokens.
/// </summary>
public class CostPricing
{
    /// <summary>Codex Fast pricing stops at this many input tokens for models without long-context Fast rates.</summary>
    internal const int CodexPriorityInputTokenLimit = 272_000;
    internal const string UnattributedModel = "unknown";

    private readonly CostPriceCatalog _catalog;
    private readonly CostPriceOverlay _overlay;
    private readonly CostCustomPricing _custom;

    /// <summary>The bundled snapshot and tables, without a refresh and without the user's overlay.</summary>
    public CostPricing() : this(CostPriceCatalog.Bundled) { }

    internal CostPricing(CostPriceCatalog catalog, CostPriceOverlay? overlay = null, CostCustomPricing? custom = null)
    {
        _catalog = catalog;
        _overlay = overlay ?? CostPriceOverlay.Bundled;
        _custom = custom ?? CostCustomPricing.Empty;
    }

    /// <summary>When the catalog behind these prices was read from models.dev.</summary>
    internal DateTimeOffset CatalogStamp => _catalog.Stamp;

    internal static string Normalize(string raw, CostProvider provider)
    {
        var model = raw.Trim();
        if (provider == CostProvider.Claude)
        {
            var start = model.LastIndexOf("claude-", StringComparison.Ordinal);
            if (start > 0 && model[start - 1] is '.' or '/') model = model[start..];
            var version = model.LastIndexOf("-v", StringComparison.Ordinal);
            if (version > 0 && IsVersionSuffix(model.AsSpan(version + 2))) model = model[..version];
            if (model.Length > 9 && model[^9] == '-' && int.TryParse(model.AsSpan(model.Length - 8), out _))
                model = model[..^9];
        }
        else
        {
            if (model.StartsWith("openai/", StringComparison.Ordinal)) model = model[7..];
            model = model switch
            {
                "gpt-5.6" or "gpt-daybreak-blue-latest" => "gpt-5.6-sol",
                "gpt-reserve" => "gpt-5.6-luna",
                "gpt-daybreak-red-latest" => "gpt-5.6-cyber",
                _ => model
            };
            if (model.Length > 11 && model[^11] == '-' && model[^6] == '-' && model[^3] == '-')
                model = model[..^11];
        }
        // Retain bounded model identifiers for repricing, including unpriced future models and other vendors'
        // models used through these tools (the report names them, and custom-pricing.json can price them).
        return model.Length is > 0 and <= 80 && char.IsAsciiLetterOrDigit(model[0]) &&
               model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':' or '@')
            ? model : UnattributedModel;
    }

    /// <summary>The "1:0" of a Bedrock-style "-v1:0" suffix: digits, a colon, digits.</summary>
    private static bool IsVersionSuffix(ReadOnlySpan<char> text)
    {
        var colon = text.IndexOf(':');
        return colon > 0 && colon < text.Length - 1 &&
               text[..colon].IndexOfAnyExceptInRange('0', '9') < 0 && text[(colon + 1)..].IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>True when usage at this time falls before a price change the index has to remember.</summary>
    internal static bool Historical(string model, long unixSeconds) => model switch
    {
        "claude-opus-4-6" or "claude-sonnet-4-6" => unixSeconds < 1_773_360_000,
        "gpt-5.6-terra" or "gpt-5.6-luna" => unixSeconds < 1_785_369_600,
        "gpt-5.6-sol" => unixSeconds < 1_787_270_400,
        "gpt-6.2-astra" or "gpt-6.2-sol" => unixSeconds < 1_803_888_000,
        _ => false
    };

    /// <summary>
    /// The rates for one index bucket, or null when the model has no known price. <paramref name="above200K"/> and
    /// <paramref name="above272K"/> say whether the request's input was over those sizes.
    /// </summary>
    public virtual CostRates? GetRates(CostProvider provider, string model, bool above200K,
        bool above272K, bool historical, int tier)
    {
        if (model == UnattributedModel) return null;
        if (FindCustom(provider, model) is { } custom)
        {
            // A partial entry is priced per token class in PriceTokens; as a full rate card it is unknown.
            if (custom.Input is not { } input || custom.Output is not { } output ||
                custom.CacheRead is not { } read || custom.CacheWrite is not { } write) return null;
            var flat = new CostRates(input, output, read, write, provider == CostProvider.Claude ? write : 0);
            return provider == CostProvider.Codex ? flat.Multiply(ServiceTierMultiplier(model, above272K, historical, tier)) : flat;
        }

        return provider == CostProvider.Claude
            ? ClaudeRates(model, above200K, above272K, historical)
            : CodexRates(model, above200K, above272K, historical)?
                .Multiply(ServiceTierMultiplier(model, above272K, historical, tier));
    }

    /// <summary>The cost of one index bucket in USD, or null when any of its tokens has no price.</summary>
    internal virtual decimal? PriceTokens(CostProvider provider, string model, bool above200K, bool above272K,
        bool historical, int tier, CostTokens tokens)
    {
        if (model != UnattributedModel && FindCustom(provider, model) is { } custom)
        {
            // Upstream prices a custom entry flat and leaves a token class without a rate unknown.
            decimal total = 0;
            if (!Add(tokens.Input, custom.Input) || !Add(tokens.Output, custom.Output) ||
                !Add(tokens.Read, custom.CacheRead) || !Add(tokens.Write, custom.CacheWrite)) return null;
            var factor = provider == CostProvider.Codex ? ServiceTierMultiplier(model, above272K, historical, tier) : 1;
            return total * factor / 1_000_000m;

            bool Add(long count, decimal? rate)
            {
                if (count <= 0) return true;
                if (rate is not { } known) return false;
                total += count * known;
                return true;
            }
        }
        return GetRates(provider, model, above200K, above272K, historical, tier)?.Price(tokens);
    }

    private CostCustomPricing.Rates? FindCustom(CostProvider provider, string model) =>
        _custom.IsEmpty ? null
            : _custom.Find(provider == CostProvider.Claude ? CostPriceCatalog.Anthropic : CostPriceCatalog.OpenAI, model);

    private static bool UsesLongContext(int? threshold, bool above200K, bool above272K) =>
        // The index keeps two bands per request; a tier of 272K or more reads the upper one.
        threshold is { } tokens && (tokens >= 272_000 ? above272K : above200K);

    private CostRates? ClaudeRates(string model, bool above200K, bool above272K, bool historical)
    {
        ModelPrice? price;
        int? threshold;
        if (_overlay.ClaudeHistorical.TryGetValue(model, out var before) &&
            _overlay.Claude.TryGetValue(model, out var current))
        {
            // Known historical rates take precedence over today's catalog.
            price = historical ? before : current;
            threshold = price.Long is { } lane ? lane.TierSize ?? 200_000 : null;
        }
        else
        {
            // Recognizable model families stay with their vendor's catalog.
            var openAi = model.StartsWith("gpt-", StringComparison.Ordinal);
            price = _catalog.Find(openAi ? CostPriceCatalog.OpenAI : CostPriceCatalog.Anthropic, model);
            if (price is not null)
            {
                var bundled = openAi && _overlay.Codex.TryGetValue(model, out var codex) ? codex.Long?.TierSize : null;
                threshold = bundled ?? (price.Long is { } lane ? lane.TierSize ?? 200_000 : null);
            }
            else if (_overlay.Claude.TryGetValue(model, out price))
            {
                threshold = price.Long is { } lane ? lane.TierSize ?? 200_000 : null;
            }
            else return null;
        }

        var longContext = UsesLongContext(threshold, above200K, above272K) ? price.Long : null;
        var input = longContext?.Input ?? price.Input;
        var write = price.CacheWrite ?? price.Input;
        var read = price.CacheRead ?? price.Input;
        // A one-hour cache write costs twice the input rate that applies to the request.
        return new(input, longContext?.Output ?? price.Output, longContext?.CacheRead ?? read,
            longContext?.CacheWrite ?? write, input * 2);
    }

    private CostRates? CodexRates(string model, bool above200K, bool above272K, bool historical)
    {
        decimal input, output;
        decimal? read, write, longInput, longOutput, longRead, longWrite;
        int? threshold;
        _overlay.Codex.TryGetValue(model, out var bundled);
        if (historical && _overlay.CodexHistorical.TryGetValue(model, out var before))
        {
            (input, output, read, write) = (before.Input, before.Output, before.CacheRead, before.CacheWrite);
            threshold = before.Long is { } lane ? lane.TierSize ?? CodexPriorityInputTokenLimit : null;
            (longInput, longOutput, longRead, longWrite) =
                (before.Long?.Input, before.Long?.Output, before.Long?.CacheRead, before.Long?.CacheWrite);
        }
        else if (_catalog.Find(CostPriceCatalog.OpenAI, model) is { } found)
        {
            var bundledThreshold = bundled?.Long is { } lane ? lane.TierSize ?? CodexPriorityInputTokenLimit : (int?)null;
            // The lane's name is not the threshold: use its context tier, then upstream's table, then 200K.
            var foundThreshold = found.Long is { } foundLane
                ? foundLane.TierSize ?? bundledThreshold ?? 200_000 : (int?)null;
            // Without a lane the catalog has no long-context opinion, so the bundled tuple applies whole.
            var bundledLong = foundThreshold is null ? bundled?.Long : null;
            (input, output) = (found.Input, found.Output);
            read = found.CacheRead ?? bundled?.CacheRead;
            write = found.CacheWrite ?? bundled?.CacheWrite;
            threshold = foundThreshold ?? bundledThreshold;
            longInput = found.Long?.Input ?? bundledLong?.Input;
            longOutput = found.Long?.Output ?? bundledLong?.Output;
            longRead = found.Long?.CacheRead ?? (foundThreshold is not null
                ? found.CacheRead ?? found.Long?.Input ?? found.Input : bundledLong?.CacheRead);
            longWrite = found.Long?.CacheWrite ?? (foundThreshold is not null
                ? found.CacheWrite ?? found.Long?.Input ?? found.Input : bundledLong?.CacheWrite);
        }
        else if (bundled is not null)
        {
            (input, output, read, write) = (bundled.Input, bundled.Output, bundled.CacheRead, bundled.CacheWrite);
            threshold = bundled.Long is { } lane ? lane.TierSize ?? CodexPriorityInputTokenLimit : null;
            (longInput, longOutput, longRead, longWrite) =
                (bundled.Long?.Input, bundled.Long?.Output, bundled.Long?.CacheRead, bundled.Long?.CacheWrite);
        }
        else return null;

        if (threshold is not null && _overlay.CodexLongContextThresholds.TryGetValue(model, out var configured))
            threshold = configured;
        if (!UsesLongContext(threshold, above200K, above272K))
            return new(input, output, read ?? input, write ?? input);
        var inputRate = longInput ?? input;
        return new(inputRate, longOutput ?? output, longRead ?? read ?? inputRate, longWrite ?? write ?? inputRate);
    }

    /// <summary>
    /// Public API Fast rates relative to Standard (tier 1 "priority", tier 2 "ultrafast"). Models without
    /// long-context Fast rates fall back to Standard above the priority input limit.
    /// </summary>
    internal static decimal ServiceTierMultiplier(string model, bool above272K, bool historical, int tier)
    {
        var allowsLongContext = model is "gpt-6-astra" or "gpt-6.1-sol" or "gpt-6.1-terra" or "gpt-6-luna" or
            "gpt-6.2-astra" or "gpt-6.2-sol";
        return tier switch
        {
            1 when above272K && !allowsLongContext => 1,
            1 => model switch
            {
                "gpt-5.5" => 2.5m,
                "gpt-5.4" or "gpt-5.4-mini" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" => 2,
                _ when allowsLongContext => 2,
                _ => 1
            },
            2 => model switch
            {
                "gpt-6-astra" or "gpt-6.1-sol" => 6,
                // Promotional rate until midnight Pacific on March 1, 2027; the index marks earlier usage historical.
                "gpt-6.2-astra" or "gpt-6.2-sol" => historical ? 3 : 6,
                _ => 1
            },
            _ => 1
        };
    }
}
