using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Parity mode: indexes the real log folders into a temporary index and compares each provider's daily totals with
/// the installed app's saved ledger. Only per-day aggregates leave the process: no log line, prompt or path.
/// </summary>
internal static class Parity
{
    private sealed record LedgerDay(decimal Cost, long Tokens, DateTimeOffset? Updated);
    private sealed record NativeDay(decimal Cost, long Tokens, long UnpricedTokens);

    internal static int Run(string[] args, CancellationToken token)
    {
        var inv = CultureInfo.InvariantCulture;
        var skipArchived = args.Contains("--skip-archived");
        args = [.. args.Where(arg => arg != "--skip-archived")];
        if (args.Length is < 3 or > 4 ||
            !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", inv, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", inv, DateTimeStyles.None, out var to) || to < from)
        {
            Console.Error.WriteLine("Usage: CostProbe parity <from yyyy-MM-dd> <to yyyy-MM-dd> [index-directory] [--skip-archived]");
            return 2;
        }

        var temporary = args.Length == 4
            ? Path.GetFullPath(args[3])
            : Path.Combine(Path.GetTempPath(), "CostProbe-parity-" + Guid.NewGuid().ToString("N"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ledgerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "AgentsWinBar_w05y65rx3mcz6", "LocalCache", "Local", "AgentsWinBar", "cost-ledger.json");
        CostLogRoot[] roots =
        [
            new(CostProvider.Claude, Path.Combine(profile, ".claude", "projects")),
            new(CostProvider.Codex, Path.Combine(profile, ".codex", "sessions")),
            new(CostProvider.Codex, Path.Combine(profile, ".codex", "archived_sessions"))
        ];
        if (skipArchived) roots = roots[..2];

        try
        {
            using var process = Process.GetCurrentProcess();
            var zone = TimeZoneInfo.Local;
            var scanner = new NativeCostScanner(temporary, NullLogger<NativeCostScanner>.Instance);
            Console.WriteLine($"Calendar: {zone.Id}. Range {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.");
            Console.WriteLine();
            Console.WriteLine("| Update | Wall ms | CPU ms | Files checked | Files opened | Bytes read | Oversized lines | Unavailable files |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            Measure("Scan, full listing, 2 workers", () => scanner.Update(roots, 2, token));
            Measure("No change, full listing", () => scanner.Update(roots, 1, token));
            Measure("No change, no notifications", () => scanner.Update(roots, 1, token, Array.Empty<string>()));

            void Measure(string name, Func<CostScanResult> update)
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                var result = update();
                watch.Stop();
                process.Refresh();
                Console.WriteLine(string.Create(inv,
                    $"| {name} | {watch.Elapsed.TotalMilliseconds:F1} | {(process.TotalProcessorTime - cpu).TotalMilliseconds:F1} | {result.DiscoveredFiles} | {result.FilesRead} | {result.BytesRead} | {result.OversizedLines} | {result.UnavailableFiles} |"));
            }

            var missed = new List<(CostProvider Provider, string Name, DateOnly Day)>();
            var now = DateTimeOffset.UtcNow;
            var today = CostIndex.LocalDay(now, zone);
            var history = Math.Max(30, today.DayNumber - from.DayNumber + 1);
            var ledger = ReadLedger(ledgerPath);
            var bundled = new CostPricing();
            var tierSized = new CostPricing(CostPriceCatalog.Bundled, WithoutThresholdOverride());
            var indexPath = Path.Combine(temporary, "index.sqlite");

            Console.WriteLine();
            Console.WriteLine($"Prices: bundled models.dev snapshot of {bundled.CatalogStamp:yyyy-MM-dd}. Difference is native minus CLI, relative to the CLI.");
            foreach (var (provider, name) in new[] { (CostProvider.Claude, "claude"), (CostProvider.Codex, "codex") })
            {
                token.ThrowIfCancellationRequested();
                var days = ReadNative(scanner, provider, bundled, now, history, token);
                var alternative = provider == CostProvider.Codex
                    ? ReadNative(scanner, provider, tierSized, now, history, token) : null;
                ledger.TryGetValue(name, out var saved);
                long tokens = 0, unpriced = 0;
                int compared = 0, tokenMatches = 0, costMatches = 0;
                Console.WriteLine();
                Console.WriteLine($"### {name}");
                Console.WriteLine();
                Console.WriteLine("| Day | CLI tokens | CLI USD | Native tokens | Native USD | Tokens diff % | USD diff % | Native tokens at ledger time | Native USD at ledger time | USD diff % at ledger time | Note |");
                Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    days.TryGetValue(day, out var native);
                    LedgerDay? cli = null;
                    saved?.TryGetValue(day, out cli);
                    native ??= new(0, 0, 0);
                    tokens += native.Tokens;
                    unpriced += native.UnpricedTokens;
                    var note = new List<string>();
                    string asOfTokens = "", asOfCost = "", asOfDiff = "";
                    if (cli is null) note.Add("no ledger day");
                    else
                    {
                        compared++;
                        var tokensOk = Within(native.Tokens, cli.Tokens);
                        var costOk = Within(native.Cost, cli.Cost);
                        // A ledger entry written before the local day ended saw only part of the day.
                        var dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue),
                            zone.GetUtcOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));
                        if (cli.Updated is { } updated && updated < dayEnd)
                        {
                            var partial = ReadAsOf(indexPath, provider, bundled, day, updated, token);
                            asOfTokens = partial.Tokens.ToString(inv);
                            asOfCost = partial.Cost.ToString("F2", inv);
                            asOfDiff = Percent(partial.Cost, cli.Cost);
                            note.Add($"ledger written {TimeZoneInfo.ConvertTime(updated, zone):MM-dd HH:mm}, before the day ended");
                            if (Reached(indexPath, provider, day, cli.Tokens, token) is { } reached)
                                note.Add($"native count reaches the CLI's tokens at {TimeZoneInfo.ConvertTime(reached, zone):HH:mm}");
                            tokensOk |= Within(partial.Tokens, cli.Tokens);
                            costOk |= Within(partial.Cost, cli.Cost);
                        }
                        if (tokensOk) tokenMatches++;
                        if (!tokensOk || !costOk) missed.Add((provider, name, day));
                        if (costOk) costMatches++;
                        else if (alternative is not null && alternative.TryGetValue(day, out var other) &&
                                 Within(other.Cost, cli.Cost))
                            note.Add("within 1% if every model used its 272K tier");
                    }
                    if (alternative is not null && alternative.TryGetValue(day, out var tier) && tier.Cost != native.Cost)
                        note.Add($"{tier.Cost.ToString("F2", inv)} USD if every model used its 272K tier");
                    if (native.UnpricedTokens > 0) note.Add($"{native.UnpricedTokens} unpriced tokens");
                    Console.WriteLine(string.Create(inv,
                        $"| {day:yyyy-MM-dd} | {(cli is null ? "" : cli.Tokens.ToString(inv))} | {(cli is null ? "" : cli.Cost.ToString("F2", inv))} | {native.Tokens} | {native.Cost:F2} | {(cli is null ? "" : Percent(native.Tokens, cli.Tokens))} | {(cli is null ? "" : Percent(native.Cost, cli.Cost))} | {asOfTokens} | {asOfCost} | {asOfDiff} | {string.Join("; ", note)} |"));
                }
                Console.WriteLine();
                Console.WriteLine(string.Create(inv,
                    $"{name}: {compared} days with a ledger entry; tokens within 1% on {tokenMatches}, cost within 1% on {costMatches} (full day or at ledger time). Unpriced share of native tokens in range: {(tokens == 0 ? 0 : 100m * unpriced / tokens):F4}% ({unpriced} of {tokens})."));
                var models = UnpricedModels(indexPath, provider, bundled, from, to, token);
                if (models.Count > 0)
                    Console.WriteLine("Unpriced models: " + string.Join(", ", models.Select(m => $"{m.Key} ({m.Value} tokens)")));
            }

            // Which long-context threshold the CLI applied, judged per model on days whose tokens agree.
            Console.WriteLine();
            Console.WriteLine("### Codex long-context threshold evidence, by model");
            Console.WriteLine();
            Console.WriteLine("| Model | Days compared | Same cost at both thresholds | CLI cost matches 200K only | CLI cost matches 272K only | Matches neither |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|");
            var allModels = ReadLedgerModels(ledgerPath);
            var at200 = new CostPricing(CostPriceCatalog.Bundled, WithThreshold(200_000));
            var low = scanner.ReadReport(CostProvider.Codex, at200, now, history, token).Daily ?? [];
            var high = scanner.ReadReport(CostProvider.Codex, tierSized, now, history, token).Daily ?? [];
            var evidence = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (!allModels.TryGetValue(("codex", day), out var cliModels)) continue;
                var lowDay = low.FirstOrDefault(d => d.Date == day)?.ModelBreakdowns ?? [];
                var highDay = high.FirstOrDefault(d => d.Date == day)?.ModelBreakdowns ?? [];
                foreach (var (model, cli) in cliModels)
                {
                    var a = lowDay.FirstOrDefault(m => m.ModelName == model);
                    var b = highDay.FirstOrDefault(m => m.ModelName == model);
                    if (a is null || b is null || a.TotalTokens != cli.Tokens || cli.Cost is not { } cost) continue;
                    if (!evidence.TryGetValue(model, out var counts)) evidence[model] = counts = new int[5];
                    counts[0]++;
                    var matchLow = Close(a.Cost ?? -1, cost);
                    var matchHigh = Close(b.Cost ?? -1, cost);
                    if (a.Cost == b.Cost) counts[1] += matchLow ? 1 : 0;
                    else if (matchLow && !matchHigh) counts[2]++;
                    else if (matchHigh && !matchLow) counts[3]++;
                    if (!matchLow && !matchHigh) counts[4]++;
                }
            }
            foreach (var (model, counts) in evidence)
                Console.WriteLine($"| {model} | {counts[0]} | {counts[1]} | {counts[2]} | {counts[3]} | {counts[4]} |");

            if (missed.Count > 0)
            {
                // Model ids and totals only, as the app's own ledger stores them.
                Console.WriteLine();
                Console.WriteLine("### Days outside 1%, by model");
                Console.WriteLine();
                Console.WriteLine("| Provider | Day | Model | CLI tokens | CLI USD | Native tokens | Native USD | Native USD, 272K tier |");
                Console.WriteLine("|---|---|---|---:|---:|---:|---:|---:|");
                var ledgerModels = ReadLedgerModels(ledgerPath);
                foreach (var group in missed.GroupBy(miss => miss.Provider))
                {
                    var report = scanner.ReadReport(group.Key, bundled, now, history, token).Daily ?? [];
                    var tier = scanner.ReadReport(group.Key, tierSized, now, history, token).Daily ?? [];
                    foreach (var (_, name, day) in group)
                    {
                        var native = report.FirstOrDefault(d => d.Date == day)?.ModelBreakdowns ?? [];
                        var other = tier.FirstOrDefault(d => d.Date == day)?.ModelBreakdowns ?? [];
                        ledgerModels.TryGetValue((name, day), out var cli);
                        cli ??= [];
                        foreach (var model in native.Select(m => m.ModelName).Union(cli.Keys).Order(StringComparer.Ordinal))
                        {
                            var mine = native.FirstOrDefault(m => m.ModelName == model);
                            var alt = other.FirstOrDefault(m => m.ModelName == model);
                            var has = cli.TryGetValue(model, out var theirs);
                            Console.WriteLine(string.Create(inv,
                                $"| {name} | {day:yyyy-MM-dd} | {model} | {(has ? theirs.Tokens.ToString(inv) : "")} | {(has ? Money(theirs.Cost) : "")} | {mine?.TotalTokens.ToString(inv) ?? ""} | {(mine is null ? "" : Money(mine.Cost))} | {(alt is null ? "" : Money(alt.Cost))} |"));
                        }
                    }
                }
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Messages can carry paths; the type is enough.
            Console.Error.WriteLine($"Parity failed: {ex.GetType().Name}");
            return 1;
        }
        finally
        {
            try
            {
                // A directory the caller named is kept for another run; the default one is removed.
                if (args.Length < 4 && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The temporary index could not be deleted.");
            }
        }
    }

    /// <summary>Equal to the cent, allowing for the CLI's floating-point sums.</summary>
    private static bool Close(decimal native, decimal cli) => Math.Abs(native - cli) <= 0.005m;

    private static CostPriceOverlay WithThreshold(int tokens)
    {
        var overlay = CostPriceOverlay.Bundled;
        return new()
        {
            Claude = overlay.Claude, ClaudeHistorical = overlay.ClaudeHistorical, Codex = overlay.Codex,
            CodexHistorical = overlay.CodexHistorical,
            CodexLongContextThresholds = overlay.Codex.Keys.ToDictionary(model => model, _ => tokens, StringComparer.Ordinal)
        };
    }

    private static string Money(decimal? value) =>
        value is { } known ? known.ToString("F2", CultureInfo.InvariantCulture) : "unpriced";

    private static Dictionary<(string Provider, DateOnly Day), Dictionary<string, (long Tokens, decimal? Cost)>>
        ReadLedgerModels(string path)
    {
        var result = new Dictionary<(string, DateOnly), Dictionary<string, (long, decimal?)>>();
        if (!File.Exists(path)) return result;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!TryGet(document.RootElement, "providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var provider in providers.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var day in provider.Value.EnumerateObject())
            {
                if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out var date) || day.Value.ValueKind != JsonValueKind.Object ||
                    !TryGet(day.Value, "models", out var models) || models.ValueKind != JsonValueKind.Array) continue;
                var byModel = new Dictionary<string, (long, decimal?)>(StringComparer.Ordinal);
                foreach (var model in models.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object || !TryGet(model, "modelName", out var id) ||
                        id.ValueKind != JsonValueKind.String) continue;
                    var tokens = TryGet(model, "totalTokens", out var t) && t.TryGetInt64(out var count) ? count : 0;
                    decimal? cost = TryGet(model, "costUSD", out var c) && c.ValueKind == JsonValueKind.Number &&
                        c.TryGetDecimal(out var usd) ? usd : null;
                    byModel[id.GetString() ?? ""] = (tokens, cost);
                }
                result[(provider.Name.ToLowerInvariant(), date)] = byModel;
            }
        }
        return result;
    }

    private static bool Within(decimal native, decimal cli) =>
        cli == 0 ? native == 0 : Math.Abs(native - cli) / Math.Abs(cli) <= 0.01m;

    private static string Percent(decimal native, decimal cli) =>
        cli == 0 ? (native == 0 ? "0.00%" : "n/a")
            : ((native - cli) / cli * 100m).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";

    private static CostPriceOverlay WithoutThresholdOverride()
    {
        var overlay = CostPriceOverlay.Bundled;
        return new()
        {
            Claude = overlay.Claude, ClaudeHistorical = overlay.ClaudeHistorical, Codex = overlay.Codex,
            CodexHistorical = overlay.CodexHistorical, CodexLongContextThresholds = []
        };
    }

    private static Dictionary<DateOnly, NativeDay> ReadNative(NativeCostScanner scanner, CostProvider provider,
        CostPricing pricing, DateTimeOffset now, int history, CancellationToken token) =>
        (scanner.ReadReport(provider, pricing, now, history, token).Daily ?? [])
        .ToDictionary(day => day.Date, day => new NativeDay(day.TotalCost, day.TotalTokens, day.UnpricedTokens));

    private static Dictionary<string, Dictionary<DateOnly, LedgerDay>> ReadLedger(string path)
    {
        var result = new Dictionary<string, Dictionary<DateOnly, LedgerDay>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!TryGet(document.RootElement, "providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var provider in providers.EnumerateObject())
        {
            var days = new Dictionary<DateOnly, LedgerDay>();
            if (provider.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var day in provider.Value.EnumerateObject())
            {
                if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                        out var date) || day.Value.ValueKind != JsonValueKind.Object) continue;
                var cost = TryGet(day.Value, "costUSD", out var c) && c.TryGetDecimal(out var usd) ? usd : 0;
                var tokens = TryGet(day.Value, "totalTokens", out var t) && t.TryGetInt64(out var count) ? count : 0;
                DateTimeOffset? updated = TryGet(day.Value, "lastUpdatedUtc", out var u) &&
                    u.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(u.GetString(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var stamp) ? stamp : null;
                days[date] = new(cost, tokens, updated);
            }
            result[provider.Name] = days;
        }
        return result;
    }

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>The native totals of one day counting only usage up to an instant.</summary>
    private static NativeDay ReadAsOf(string indexPath, CostProvider provider, CostPricing pricing, DateOnly day,
        DateTimeOffset until, CancellationToken token)
    {
        using var database = new CostIndexDatabase(indexPath, readOnly: true);
        using var row = database.Prepare("""
            SELECT model,band,SUM(input),SUM(output),SUM(read),SUM(written),SUM(one_hour)
            FROM usage WHERE provider=? AND day=? AND incomplete=0 AND utc_seconds<=? GROUP BY model,band
            """);
        row.Bind(1, (int)provider);
        row.Bind(2, day.DayNumber);
        row.Bind(3, until.ToUnixTimeSeconds());
        decimal cost = 0;
        long tokens = 0, unpriced = 0;
        while (row.Step())
        {
            token.ThrowIfCancellationRequested();
            var band = (int)row.Number(1);
            var counts = new CostTokens(row.Number(2), row.Number(3), row.Number(4), row.Number(5), row.Number(6));
            tokens += counts.Total;
            if (pricing.PriceTokens(provider, row.Text(0), (band & 1) != 0, (band & 2) != 0, (band & 4) != 0,
                    band >> 3, counts) is { } price) cost += price;
            else unpriced += counts.Total;
        }
        return new(cost, tokens, unpriced);
    }

    /// <summary>The instant at which the native running total of a day first reaches a token count.</summary>
    private static DateTimeOffset? Reached(string indexPath, CostProvider provider, DateOnly day, long target,
        CancellationToken token)
    {
        using var database = new CostIndexDatabase(indexPath, readOnly: true);
        using var row = database.Prepare("""
            SELECT utc_seconds,SUM(input+output+read+written) FROM usage
            WHERE provider=? AND day=? AND incomplete=0 GROUP BY utc_seconds ORDER BY utc_seconds
            """);
        row.Bind(1, (int)provider);
        row.Bind(2, day.DayNumber);
        long running = 0;
        while (row.Step())
        {
            token.ThrowIfCancellationRequested();
            running += row.Number(1);
            if (running >= target) return DateTimeOffset.FromUnixTimeSeconds(row.Number(0));
        }
        return null;
    }

    private static Dictionary<string, long> UnpricedModels(string indexPath, CostProvider provider,
        CostPricing pricing, DateOnly from, DateOnly to, CancellationToken token)
    {
        using var database = new CostIndexDatabase(indexPath, readOnly: true);
        using var index = new CostIndex(database);
        var models = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in index.ReadDays(provider, from.DayNumber, to.DayNumber, token))
        {
            if (pricing.PriceTokens(provider, row.Model, (row.Band & 1) != 0, (row.Band & 2) != 0,
                    (row.Band & 4) != 0, row.Band >> 3, row.Tokens) is not null) continue;
            models[row.Model] = models.GetValueOrDefault(row.Model) + row.Tokens.Total;
        }
        return models;
    }
}
