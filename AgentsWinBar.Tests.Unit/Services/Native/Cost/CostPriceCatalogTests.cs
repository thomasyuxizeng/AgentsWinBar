using System.Text;
using AwesomeAssertions;
using AgentsWinBar.Services.Native.Cost;
using static AgentsWinBar.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class CostPriceCatalogTests
{
    private const string Flat = "\"input\":4,\"output\":20,\"cache_read\":0.2,\"cache_write\":5";

    [TestMethod]
    public void Parse_WhenSnapshotHasProvidersObject_ReadsRatesAndStamp()
    {
        var catalog = Catalog(CatalogJson(Model("claude-test-1", Flat), Model("gpt-test", "\"input\":1,\"output\":2")));

        catalog.Stamp.Should().Be(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        catalog.Find(CostPriceCatalog.Anthropic, "claude-test-1").Should().Be(new ModelPrice(4, 20, .2m, 5));
        catalog.Find(CostPriceCatalog.OpenAI, "gpt-test").Should().Be(new ModelPrice(1, 2, null, null));
        catalog.IsPlausible.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_WhenCatalogIsTheFullProviderMap_KeepsOnlyThePricedProviders()
    {
        var json = "{\"anthropic\":{\"models\":{" + Model("claude-test-1", Flat) + "}}," +
                   "\"OpenAI\":{\"models\":{" + Model("gpt-test", Flat) + "}}," +
                   "\"reseller\":{\"models\":{" + Model("claude-test-1", "\"input\":99,\"output\":99") + "}}}";

        var catalog = Catalog(json);

        catalog.Count(CostPriceCatalog.Anthropic).Should().Be(1);
        catalog.Count(CostPriceCatalog.OpenAI).Should().Be(1);
        catalog.Find("reseller", "claude-test-1").Should().BeNull();
        catalog.Find(CostPriceCatalog.Anthropic, "claude-test-1")!.Input.Should().Be(4);
    }

    [TestMethod]
    public void Parse_WhenModelLacksInputOrOutputOrHasBadNumbers_SkipsOrIgnoresThem()
    {
        var models = Model("claude-no-output", "\"input\":4") + "," +
                     Model("claude-text-rate", "\"input\":\"4\",\"output\":20") + "," +
                     Model("claude-negative-cache", "\"input\":4,\"output\":20,\"cache_read\":-1") + "," +
                     "\"claude-no-cost\":{\"id\":\"claude-no-cost\"}";

        var catalog = Catalog(CatalogJson(models, ""));

        catalog.Find(CostPriceCatalog.Anthropic, "claude-no-output").Should().BeNull();
        catalog.Find(CostPriceCatalog.Anthropic, "claude-text-rate").Should().BeNull();
        catalog.Find(CostPriceCatalog.Anthropic, "claude-no-cost").Should().BeNull();
        catalog.Find(CostPriceCatalog.Anthropic, "claude-negative-cache").Should().Be(new ModelPrice(4, 20, null, null));
        catalog.IsPlausible.Should().BeFalse();
    }

    [TestMethod]
    public void Parse_WhenLaneRatesMatchAContextTier_RecordsThatTierSize()
    {
        var lane = "\"context_over_200k\":{\"input\":8,\"output\":30}";
        var tiers = "\"tiers\":[{\"input\":9,\"output\":30,\"tier\":{\"type\":\"context\",\"size\":100000}}," +
                    "{\"input\":8,\"output\":30,\"tier\":{\"type\":\"other\",\"size\":150000}}," +
                    "{\"input\":8,\"output\":30,\"tier\":{\"type\":\"context\",\"size\":272000}}]";

        var catalog = Catalog(CatalogJson("", Model("gpt-tiered", $"{Flat},{lane},{tiers}") + "," + Model("gpt-lane", $"{Flat},{lane}")));

        catalog.Find(CostPriceCatalog.OpenAI, "gpt-tiered")!.Long.Should().Be(new LongContextPrice(272_000, 8, 30, null, null));
        catalog.Find(CostPriceCatalog.OpenAI, "gpt-lane")!.Long.Should().Be(new LongContextPrice(null, 8, 30, null, null));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[1,2,3]")]
    [DataRow("{\"providers\":")]
    public void Parse_WhenTextIsNotACatalog_ReturnsNull(string text)
    {
        CostPriceCatalog.Parse(Encoding.UTF8.GetBytes(text)).Should().BeNull();
    }

    [TestMethod]
    public void MergeMissingFrom_WhenNewCatalogDroppedAModel_KeepsTheOldRateAndPrefersNewOnes()
    {
        var older = Catalog(CatalogJson(Model("claude-old", Flat) + "," + Model("claude-both", Flat), Model("gpt-old", Flat)));
        var newer = Catalog(CatalogJson(Model("claude-both", "\"input\":1,\"output\":2"), ""));

        var merged = newer.MergeMissingFrom(older);

        merged.Find(CostPriceCatalog.Anthropic, "claude-old")!.Input.Should().Be(4);
        merged.Find(CostPriceCatalog.Anthropic, "claude-both")!.Input.Should().Be(1);
        merged.Find(CostPriceCatalog.OpenAI, "gpt-old")!.Input.Should().Be(4);
        merged.Stamp.Should().Be(newer.Stamp);
    }

    [TestMethod]
    public void Serialize_WhenReadBack_PreservesRatesTiersAndStamp()
    {
        var lane = "\"context_over_200k\":{\"input\":8,\"output\":30,\"cache_read\":0.4}";
        var tier = "\"tiers\":[{\"input\":8,\"output\":30,\"cache_read\":0.4,\"tier\":{\"type\":\"context\",\"size\":272000}}]";
        var original = Catalog(CatalogJson(Model("claude-test-1", Flat), Model("gpt-test", $"{Flat},{lane},{tier}")));

        var copy = CostPriceCatalog.Parse(original.Serialize())!;

        copy.Stamp.Should().Be(original.Stamp);
        copy.Find(CostPriceCatalog.Anthropic, "claude-test-1").Should().Be(original.Find(CostPriceCatalog.Anthropic, "claude-test-1"));
        copy.Find(CostPriceCatalog.OpenAI, "gpt-test").Should().Be(original.Find(CostPriceCatalog.OpenAI, "gpt-test"));
        copy.Find(CostPriceCatalog.OpenAI, "gpt-test")!.Long!.TierSize.Should().Be(272_000);
    }

    [TestMethod]
    public async Task ParseLiveAsync_WhenFullCatalogStreams_KeepsAnthropicAndOpenAiWithTheGivenStamp()
    {
        var json = "{\"anthropic\":{\"id\":\"anthropic\",\"name\":\"A\",\"models\":{" + Model("claude-test-1", Flat) + "}}," +
                   "\"openai\":{\"models\":{" + Model("gpt-test", Flat) + ",\"gpt-odd\":7}}," +
                   "\"other\":{\"models\":{" + Model("x", Flat) + "}}}";
        var stamp = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = await CostPriceCatalog.ParseLiveAsync(stream, stamp, CancellationToken.None);

        catalog!.Stamp.Should().Be(stamp);
        catalog.IsPlausible.Should().BeTrue();
        catalog.Find(CostPriceCatalog.OpenAI, "gpt-test").Should().Be(new ModelPrice(4, 20, .2m, 5));
        catalog.Find(CostPriceCatalog.OpenAI, "gpt-odd").Should().BeNull();
        catalog.Find("other", "x").Should().BeNull();
    }

    // Review finding 16: providers other than Anthropic and OpenAI are skipped unread, whatever shape they have.
    [TestMethod]
    public async Task ParseLiveAsync_WhenOtherProvidersContainInvalidOrHugePayloads_SkipsThem()
    {
        var json = "{\"meta\":[1,2,3],\"version\":7,\"other\":{\"models\":[\"not\",\"an\",\"object\"]}," +
                   "\"anthropic\":{\"models\":{" + Model("claude-test-1", Flat) + "}}," +
                   "\"big\":{\"models\":{\"x\":{\"cost\":{\"input\":1,\"output\":2},\"blob\":\"" + new string('x', 200_000) + "\"}}}," +
                   "\"openai\":{\"models\":{" + Model("gpt-test", Flat) + "}}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = await CostPriceCatalog.ParseLiveAsync(stream, DateTimeOffset.UnixEpoch, CancellationToken.None);

        catalog.Should().NotBeNull();
        catalog!.IsPlausible.Should().BeTrue();
        catalog.Find(CostPriceCatalog.Anthropic, "claude-test-1").Should().NotBeNull();
        catalog.Find("big", "x").Should().BeNull();
    }

    // Review finding 20.5: among several dated snapshots of a model, the latest one prices it.
    [TestMethod]
    public void Find_WhenSeveralDatedSnapshotsMatch_PicksTheLatestDeterministically()
    {
        var catalog = Catalog(CatalogJson(
            Model("claude-test-20250101", "\"input\":1,\"output\":2") + "," +
            Model("claude-test-20260101", "\"input\":3,\"output\":4") + "," +
            Model("claude-test-20251231", "\"input\":5,\"output\":6"),
            Model("gpt-x", "\"input\":1,\"output\":2")));

        catalog.Find(CostPriceCatalog.Anthropic, "claude-test")!.Input.Should().Be(3);
    }

    [TestMethod]
    public async Task ParseLiveAsync_WhenCatalogListsGoogle_KeepsItsModelsForAntigravity()
    {
        var json = "{\"anthropic\":{\"models\":{" + Model("claude-test-1", Flat) + "}}," +
                   "\"google\":{\"models\":{" + Model("gemini-test", "\"input\":3,\"output\":15,\"cache_read\":0.15") + "}}," +
                   "\"google-vertex\":{\"models\":{" + Model("gemini-test", "\"input\":99,\"output\":99") + "}}," +
                   "\"openai\":{\"models\":{" + Model("gpt-test", Flat) + "}}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = await CostPriceCatalog.ParseLiveAsync(stream, DateTimeOffset.UnixEpoch, CancellationToken.None);

        catalog!.Find(CostPriceCatalog.Google, "gemini-test").Should().Be(new ModelPrice(3, 15, .15m, null));
        catalog.Find("google-vertex", "gemini-test").Should().BeNull();
    }

    [TestMethod]
    public void Parse_WhenSnapshotListsGoogle_KeepsItAndSerializesItBack()
    {
        var catalog = Catalog(CatalogJson(Model("claude-test-1", Flat), Model("gpt-test", Flat),
            googleModels: Model("gemini-test", "\"input\":3,\"output\":15")));

        var copy = CostPriceCatalog.Parse(catalog.Serialize())!;

        copy.Count(CostPriceCatalog.Google).Should().Be(1);
        copy.Find(CostPriceCatalog.Google, "gemini-test")!.Output.Should().Be(15);
    }

    [TestMethod]
    public void Bundled_WhenLoadedFromTheAssembly_HasGooglePricesAndTheGeminiOverlay()
    {
        CostPriceCatalog.Bundled.Find(CostPriceCatalog.Google, "gemini-4-neon").Should().NotBeNull();
        CostPriceCatalog.Bundled.Find(CostPriceCatalog.Google, "gemini-3.1-pro-preview").Should().NotBeNull();
        CostPriceOverlay.Bundled.Gemini.Should().ContainKey("gpt-oss-120b-medium");
    }

    [TestMethod]
    public async Task ParseLiveAsync_WhenBodyIsNotJson_ReturnsNull()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<html>offline</html>"));

        (await CostPriceCatalog.ParseLiveAsync(stream, DateTimeOffset.UnixEpoch, CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public void Bundled_WhenLoadedFromTheAssembly_HasBothProvidersAndUpstreamTables()
    {
        CostPriceCatalog.Bundled.IsPlausible.Should().BeTrue();
        CostPriceCatalog.Bundled.Stamp.Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        CostPriceOverlay.Bundled.Codex.Should().ContainKey("gpt-6.2-sol");
        CostPriceOverlay.Bundled.Claude.Should().ContainKey("claude-mythos-5-2");
        CostPriceOverlay.Bundled.CodexHistorical.Should().ContainKey("gpt-5.6-sol");
        CostPriceOverlay.Bundled.ClaudeHistorical.Should().ContainKey("claude-opus-4-6");
        CostPriceOverlay.Bundled.CodexLongContextThresholds.Values.Should().OnlyContain(tokens => tokens == 200_000 || tokens == 272_000);
    }

    [TestMethod]
    public void OverlayParse_WhenThresholdsAreListed_KeepsPositiveIntegersOnly()
    {
        var overlay = Overlay("{\"codexLongContextThresholds\":{\"gpt-a\":200000,\"gpt-b\":0,\"gpt-c\":\"x\"},\"codex\":{\"gpt-a\":{" + Flat + "}}}");

        overlay.CodexLongContextThresholds.Should().BeEquivalentTo(new Dictionary<string, int> { ["gpt-a"] = 200_000 });
        overlay.Codex["gpt-a"].Should().Be(new ModelPrice(4, 20, .2m, 5));
    }

    [TestMethod]
    public void CustomParse_WhenEntriesUseEitherSpelling_ReadsRatesAndDropsInvalidOnes()
    {
        var custom = Custom("{\" My-Model \":{\"input\":1,\"cache_read\":0.1,\"cacheCreation\":2}," +
                            "\"bad\":{\"input\":-1,\"output\":\"x\"},\"text\":\"free\"}");

        custom.Find("anthropic", "my-model").Should().Be(new CostCustomPricing.Rates(1, null, .1m, 2));
        custom.Find("anthropic", "bad").Should().BeNull();
        custom.Find("anthropic", "text").Should().BeNull();
        CostCustomPricing.Parse(Encoding.UTF8.GetBytes("not json")).IsEmpty.Should().BeTrue();
    }
}
