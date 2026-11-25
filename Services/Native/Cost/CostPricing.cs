namespace CodexBarWin.Services.Native.Cost;

public enum CostProvider { Claude, Codex }

public readonly record struct CostRates(decimal Input, decimal Output, decimal CacheRead,
    decimal CacheWrite = 0, decimal CacheWriteOneHour = 0)
{
    internal decimal Price(CostTokens tokens) =>
        (Input * tokens.Input + Output * tokens.Output + CacheRead * tokens.Read +
         CacheWrite * (tokens.Write - tokens.OneHour) + CacheWriteOneHour * tokens.OneHour) / 1_000_000m;
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

/// <summary>Bundled USD rates per million tokens. Aggregates retain context, historical and service tiers.</summary>
public class CostPricing
{
    private static readonly CostRates ClaudeSonnet = Anthropic(3, 15, .3m);
    private static readonly CostRates ClaudeOpus = Anthropic(5, 25, .5m);
    private static readonly CostRates Codex51 = OpenAI(1.25m, 10, .125m);
    private static readonly CostRates Codex52 = OpenAI(1.75m, 14, .175m);

    private static readonly Dictionary<string, CostRates> Claude = new(StringComparer.Ordinal)
    {
        ["claude-haiku-4-5"] = Anthropic(1, 5, .1m),
        ["claude-sonnet-4"] = ClaudeSonnet, ["claude-sonnet-4-5"] = ClaudeSonnet,
        ["claude-sonnet-4-6"] = ClaudeSonnet,
        ["claude-opus-4"] = Anthropic(15, 75, 1.5m), ["claude-opus-4-1"] = Anthropic(15, 75, 1.5m),
        ["claude-opus-4-5"] = ClaudeOpus, ["claude-opus-4-6"] = ClaudeOpus,
        ["claude-opus-4-7"] = ClaudeOpus, ["claude-opus-4-8"] = ClaudeOpus,
        ["claude-fable-5"] = Anthropic(10, 50, 1),
        ["claude-mythos-5"] = Anthropic(10, 50, 1),
        ["claude-mythos-5-1"] = Anthropic(10, 50, .25m),
        ["claude-mythos-5-2"] = Anthropic(8, 40, .2m)
    };

    private static readonly Dictionary<string, CostRates> Codex = new(StringComparer.Ordinal)
    {
        ["gpt-5"] = Codex51, ["gpt-5-codex"] = Codex51,
        ["gpt-5-mini"] = OpenAI(.25m, 2, .025m), ["gpt-5-nano"] = OpenAI(.05m, .4m, .005m),
        ["gpt-5-pro"] = OpenAI(15, 120),
        ["gpt-5.1"] = Codex51, ["gpt-5.1-codex"] = Codex51, ["gpt-5.1-codex-max"] = Codex51,
        ["gpt-5.1-codex-mini"] = OpenAI(.25m, 2, .025m),
        ["gpt-5.2"] = Codex52, ["gpt-5.2-codex"] = Codex52, ["gpt-5.3-codex"] = Codex52,
        ["gpt-5.2-pro"] = OpenAI(21, 168), ["gpt-5.3-codex-spark"] = default,
        ["gpt-5.4"] = OpenAI(2.5m, 15, .25m), ["gpt-5.4-mini"] = OpenAI(.75m, 4.5m, .075m),
        ["gpt-5.4-nano"] = OpenAI(.2m, 1.25m, .02m), ["gpt-5.4-pro"] = OpenAI(30, 180),
        ["gpt-5.5"] = OpenAI(5, 30, .5m), ["gpt-5.5-pro"] = OpenAI(30, 180),
        ["gpt-5.5-cyber"] = OpenAI(12.5m, 75, 1.25m), ["gpt-5.6-cyber"] = OpenAI(12.5m, 75, 1.25m, 15.625m),
        ["gpt-5.6-sol"] = OpenAI(4, 20, .4m, 5), ["gpt-5.6-terra"] = OpenAI(2, 12, .2m, 2.5m),
        ["gpt-5.6-luna"] = OpenAI(.2m, 1.2m, .02m, .25m),
        ["gpt-6-astra"] = OpenAI(10, 50, 1, 12.5m),
        ["gpt-6-sol"] = OpenAI(2, 10, .2m, 2.5m), ["gpt-6-luna"] = OpenAI(.1m, .5m, .01m, .125m),
        ["gpt-6.1-sol"] = OpenAI(2, 10, .1m, 2.5m), ["gpt-6.1-terra"] = OpenAI(1, 5, .05m, 1.25m),
        ["gpt-6.2-astra"] = OpenAI(8, 40, .4m, 10), ["gpt-6.2-sol"] = OpenAI(2, 10, .1m, 2.5m)
    };

    private static CostRates Anthropic(decimal input, decimal output, decimal read) =>
        new(input, output, read, input * 1.25m, input * 2);

    private static CostRates OpenAI(decimal input, decimal output, decimal? read = null, decimal? write = null) =>
        new(input, output, read ?? input, write ?? input);

    internal static string Normalize(string raw, CostProvider provider)
    {
        var model = raw.Trim();
        if (provider == CostProvider.Claude)
        {
            var start = model.LastIndexOf("claude-", StringComparison.Ordinal);
            if (start > 0 && model[start - 1] is '.' or '/') model = model[start..];
            var version = model.LastIndexOf("-v", StringComparison.Ordinal);
            if (version > 0 && model.AsSpan(version).Contains(':')) model = model[..version];
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
        // Retain bounded model identifiers for repricing, including unpriced future models.
        return model.Length is > 0 and <= 80 &&
               (model.StartsWith("claude-", StringComparison.Ordinal) ||
                model.StartsWith("gpt-", StringComparison.Ordinal)) &&
               model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? model : "unknown";
    }

    internal static bool Historical(string model, long unixSeconds) => model switch
    {
        "claude-opus-4-6" or "claude-sonnet-4-6" => unixSeconds < 1_773_360_000,
        "gpt-5.6-terra" or "gpt-5.6-luna" => unixSeconds < 1_785_369_600,
        "gpt-5.6-sol" => unixSeconds < 1_787_270_400,
        "gpt-6.2-astra" or "gpt-6.2-sol" => unixSeconds < 1_803_888_000,
        _ => false
    };

    public virtual CostRates? GetRates(CostProvider provider, string model, bool above200K,
        bool above272K, bool historical, int tier)
    {
        if (!(provider == CostProvider.Claude ? Claude : Codex).TryGetValue(model, out var rate))
            return null;
        if (provider == CostProvider.Claude)
        {
            var longContext = model is "claude-sonnet-4" or "claude-sonnet-4-5" ||
                historical && model is "claude-opus-4-6" or "claude-sonnet-4-6";
            return above200K && longContext
                ? new(rate.Input * 2, rate.Output * 1.5m, rate.CacheRead * 2, rate.CacheWrite * 2, rate.CacheWriteOneHour * 2)
                : rate;
        }

        if (historical)
            rate = model switch
            {
                "gpt-5.6-sol" => OpenAI(5, 30, .5m, 6.25m),
                "gpt-5.6-terra" => OpenAI(2.5m, 15, .25m, 3.125m),
                "gpt-5.6-luna" => OpenAI(1, 6, .1m, 1.25m),
                _ => rate
            };
        var supportsLongContext = model.StartsWith("gpt-6", StringComparison.Ordinal) ||
            model is "gpt-5.4" or "gpt-5.4-pro" or "gpt-5.5" or "gpt-5.5-pro" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna";
        if (above272K && supportsLongContext)
            rate = new(rate.Input * 2, rate.Output * 1.5m, rate.CacheRead * 2, rate.CacheWrite * 2);

        var supportsFast = model is "gpt-6-astra" or "gpt-6.1-sol" or "gpt-6.1-terra" or "gpt-6-luna" or
            "gpt-6.2-astra" or "gpt-6.2-sol" ||
            (!above272K || !supportsLongContext) &&
            model is "gpt-5.4" or "gpt-5.4-mini" or "gpt-5.5" or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna";
        decimal multiplier = (tier, supportsFast, model) switch
        {
            (1, true, "gpt-5.5") => 2.5m,
            (1, true, _) => 2,
            (2, _, "gpt-6-astra" or "gpt-6.1-sol") => 6,
            (2, _, "gpt-6.2-astra" or "gpt-6.2-sol") => historical ? 3 : 6,
            _ => 1
        };
        return new(rate.Input * multiplier, rate.Output * multiplier, rate.CacheRead * multiplier,
            rate.CacheWrite * multiplier);
    }
}
