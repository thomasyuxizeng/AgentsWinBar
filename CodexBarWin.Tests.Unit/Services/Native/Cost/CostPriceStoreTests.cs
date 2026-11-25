using System.Net;
using AwesomeAssertions;
using CodexBarWin.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;
using static CodexBarWin.Tests.Unit.Services.Native.Cost.CostTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native.Cost;

[TestClass]
public sealed class CostPriceStoreTests
{
    private static readonly DateTimeOffset Start = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Opus = "claude-opus-5-5";
    private string _directory = null!;
    private ManualClock _clock = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = TempDirectory("price-store");
        _clock = new ManualClock(Start);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CostPriceStore Store(StubHandler handler) =>
        new(_directory, NullLogger<CostPriceStore>.Instance, _clock, () => handler);

    private static string LiveCatalog(decimal opusInput = 99) =>
        "{\"anthropic\":{\"models\":{" + Model(Opus, $"\"input\":{opusInput},\"output\":199") + "}}," +
        "\"openai\":{\"models\":{" + Model("gpt-live", "\"input\":1,\"output\":2") + "}}}";

    private static decimal? OpusInput(CostPriceStore store) =>
        store.GetPricing().GetRates(CostProvider.Claude, Opus, false, false, false, 0)?.Input;

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenCatalogDownloads_SavesItAndPricesFromIt()
    {
        var handler = StubHandler.Json(LiveCatalog());
        var store = Store(handler);
        var bundled = OpusInput(store);

        var refreshed = await store.RefreshIfDueAsync();

        refreshed.Should().BeTrue();
        handler.Requests.Should().Be(1);
        handler.LastUri.Should().Be(new Uri(CostPriceStore.CatalogUrl));
        File.Exists(store.CatalogPath).Should().BeTrue();
        bundled.Should().NotBe(99);
        OpusInput(store).Should().Be(99);
        // Models the download no longer lists keep their bundled rate.
        store.GetPricing().GetRates(CostProvider.Claude, "claude-sonnet-5-5", false, false, false, 0).Should().NotBeNull();
        store.GetPricing().GetRates(CostProvider.Codex, "gpt-live", false, false, false, 0).Should().Be(new CostRates(1, 2, 1, 1));
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenSavedCatalogExists_IsReadByANewStore()
    {
        await Store(StubHandler.Json(LiveCatalog())).RefreshIfDueAsync();

        OpusInput(Store(StubHandler.Json("{}"))).Should().Be(99);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, "{}")]
    [DataRow(HttpStatusCode.NotFound, "missing")]
    [DataRow(HttpStatusCode.OK, "<html>captive portal</html>")]
    [DataRow(HttpStatusCode.OK, "{\"anthropic\":{\"models\":{}},\"openai\":{\"models\":{}}}")]
    [DataRow(HttpStatusCode.OK, "{\"anthropic\":{\"models\":{\"claude-opus-5-5\":{\"cost\":{\"input\":99,\"output\":1}}}}}")]
    [DataRow(HttpStatusCode.OK, "[]")]
    public async Task RefreshIfDueAsync_WhenDownloadIsUnusable_KeepsBundledPrices(HttpStatusCode status, string body)
    {
        var store = Store(StubHandler.Json(body, status));
        var bundled = OpusInput(store);

        var refreshed = await store.RefreshIfDueAsync();

        refreshed.Should().BeFalse();
        File.Exists(store.CatalogPath).Should().BeFalse();
        OpusInput(store).Should().Be(bundled).And.NotBeNull();
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenRequestThrows_KeepsBundledPricesWithoutThrowing()
    {
        var store = Store(new StubHandler(_ => throw new HttpRequestException("offline")));
        var bundled = OpusInput(store);

        (await store.RefreshIfDueAsync()).Should().BeFalse();

        OpusInput(store).Should().Be(bundled);
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenDownloadFailsAfterAnEarlierSuccess_KeepsTheSavedCatalog()
    {
        await Store(StubHandler.Json(LiveCatalog())).RefreshIfDueAsync();
        _clock.Now = Start.AddDays(2);
        var store = Store(StubHandler.Json("down", HttpStatusCode.BadGateway));

        (await store.RefreshIfDueAsync()).Should().BeFalse();

        OpusInput(store).Should().Be(99);
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenCalledAgainWithinADay_DoesNotDownloadAgain()
    {
        var handler = StubHandler.Json(LiveCatalog());
        var store = Store(handler);

        await store.RefreshIfDueAsync();
        _clock.Now = Start.AddHours(23);
        var second = await store.RefreshIfDueAsync();
        var otherProcess = await Store(handler).RefreshIfDueAsync();

        second.Should().BeFalse();
        otherProcess.Should().BeFalse();
        handler.Requests.Should().Be(1);
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenLastAttemptFailed_StillWaitsADay()
    {
        var handler = StubHandler.Json("down", HttpStatusCode.ServiceUnavailable);
        var store = Store(handler);

        await store.RefreshIfDueAsync();
        _clock.Now = Start.AddHours(1);
        await store.RefreshIfDueAsync();

        handler.Requests.Should().Be(1);
    }

    [TestMethod]
    public async Task RefreshIfDueAsync_WhenADayHasPassed_DownloadsAgain()
    {
        var handler = StubHandler.Json(LiveCatalog());
        var store = Store(handler);

        await store.RefreshIfDueAsync();
        _clock.Now = Start.AddHours(24);
        var second = await store.RefreshIfDueAsync();

        second.Should().BeTrue();
        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public void IsDue_WhenAttemptStampIsInTheFutureOrUnreadable_AllowsARefresh()
    {
        var store = Store(StubHandler.Json("{}"));
        File.WriteAllText(store.AttemptPath, Start.AddDays(3).ToString("O"));
        store.IsDue(Start).Should().BeTrue();

        File.WriteAllText(store.AttemptPath, "yesterday");
        store.IsDue(Start).Should().BeTrue();

        File.WriteAllText(store.AttemptPath, Start.AddHours(-2).ToString("O"));
        store.IsDue(Start).Should().BeFalse();
    }

    [TestMethod]
    public void GetPricing_WhenSavedCatalogIsOlderThanBundled_UsesBundled()
    {
        var store = Store(StubHandler.Json("{}"));
        var bundled = OpusInput(store);
        File.WriteAllText(store.CatalogPath, CatalogJson(Model(Opus, "\"input\":99,\"output\":1"),
            Model("gpt-old", "\"input\":1,\"output\":2"), stamp: "2020-01-01T00:00:00Z"));

        OpusInput(store).Should().Be(bundled);
    }

    [TestMethod]
    [DataRow("{ broken")]
    [DataRow("{\"fetchedAt\":\"2031-01-01T00:00:00Z\",\"providers\":{}}")]
    public void GetPricing_WhenSavedCatalogIsCorruptOrEmpty_UsesBundled(string content)
    {
        var store = Store(StubHandler.Json("{}"));
        var bundled = OpusInput(store);
        File.WriteAllText(store.CatalogPath, content);

        OpusInput(store).Should().Be(bundled).And.NotBeNull();
    }

    [TestMethod]
    public void GetPricing_WhenCustomPricingFileAppearsOrChanges_AppliesIt()
    {
        var store = Store(StubHandler.Json("{}"));
        store.GetPricing().GetRates(CostProvider.Claude, "house-model", false, false, false, 0).Should().BeNull();

        File.WriteAllText(store.CustomPath, "{\"house-model\":{\"input\":1,\"output\":2,\"cacheRead\":3,\"cacheWrite\":4}}");
        store.GetPricing().GetRates(CostProvider.Claude, "house-model", false, false, false, 0)
            .Should().Be(new CostRates(1, 2, 3, 4, 4));

        File.WriteAllText(store.CustomPath, "{\"house-model\":{\"input\":10,\"output\":20,\"cacheRead\":30,\"cacheWrite\":40}}");
        store.GetPricing().GetRates(CostProvider.Claude, "house-model", false, false, false, 0)!.Value.Input.Should().Be(10);
    }

    [TestMethod]
    public void GetPricing_WhenNothingChanged_ReturnsTheSameInstance()
    {
        var store = Store(StubHandler.Json("{}"));

        store.GetPricing().Should().BeSameAs(store.GetPricing());
    }
}
