using System.Text;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class GeminiSpendServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, exception), exception));
    }

    private readonly Clock _clock = new();
    private readonly CapturingLogger<GeminiSpendService> _logger = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly AppSettings _appSettings = new();
    private readonly List<EnvironmentVariableTarget> _envReads = [];
    private string? _processEnv;
    private string? _userEnv;
    private Func<string, Stream>? _fileOpener;
    private Func<string, (bool Exists, long Length, DateTime LastWriteUtc)>? _fileInfoReader;

    [TestInitialize]
    public void Initialize()
    {
        _settings.SetupGet(s => s.Settings).Returns(_appSettings);
    }

    private GeminiSpendService CreateService(TimeZoneInfo? zone = null)
    {
        return new GeminiSpendService(
            _settings.Object,
            _logger,
            _clock,
            environmentReader: (key, target) =>
            {
                _envReads.Add(target);
                return target == EnvironmentVariableTarget.Process ? _processEnv : _userEnv;
            },
            fileOpener: _fileOpener,
            timeZone: zone ?? TimeZoneInfo.Utc,
            fileInfoReader: _fileInfoReader);
    }

    [TestMethod]
    public async Task GetAsync_MissingFile_ReturnsNoLogStateWithoutLogging()
    {
        _fileInfoReader = _ => (false, 0, DateTime.MinValue);
        using var service = CreateService();

        var result = await service.GetAsync(default);

        result.State.Should().Be(GeminiApiSpendState.NoLog);
        result.TotalUsd.Should().Be(0m);
        result.MonthStart.Should().Be(new DateOnly(2026, 11, 1));
        _logger.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetAsync_UnreadableFile_ReturnsUnavailableStateAndLogsFixedWarningWithoutExceptionDetails()
    {
        _fileInfoReader = _ => (true, 100, _clock.Now.UtcDateTime);
        _fileOpener = _ => throw new UnauthorizedAccessException("C:\\private\\secret\\path access denied");
        using var service = CreateService();

        var result = await service.GetAsync(default);

        result.State.Should().Be(GeminiApiSpendState.Unavailable);
        result.Message.Should().Be("Gemini spend log could not be read");
        _logger.Entries.Should().ContainSingle();
        var entry = _logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Text.Should().Be("Gemini spend log could not be read");
        entry.Text.Should().NotContain("secret");
        entry.Text.Should().NotContain("private");
        entry.Exception.Should().BeNull();
    }

    [TestMethod]
    public async Task GetAsync_DeveloperModeEnabled_ReturnsFixedSampleWithoutReadingFiles()
    {
        _appSettings.DeveloperModeEnabled = true;
        var openerCalled = false;
        _fileOpener = _ => { openerCalled = true; throw new InvalidOperationException(); };
        using var service = CreateService();

        var result = await service.GetAsync(default);

        openerCalled.Should().BeFalse();
        result.State.Should().Be(GeminiApiSpendState.Ok);
        result.VertexUsd.Should().BeGreaterThan(0m);
        result.AiStudioUsd.Should().BeGreaterThan(0m);
        result.UnrecordedRouteUsd.Should().BeGreaterThan(0m);
        result.TotalUsd.Should().Be(result.VertexUsd + result.AiStudioUsd + result.UnrecordedRouteUsd);
        result.Models.Should().Contain(m => m.Model.Contains("neon", StringComparison.OrdinalIgnoreCase));
        result.Models.Should().Contain(m => m.Model.Contains("carbon", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GetAsync_UnchangedFile_CachesResultAndRecomputesWhenMonthChanges()
    {
        const string log = """
            {"time": "2026-11-10T12:00:00", "model": "gemini-4-neon", "usd": 5.0}
            """;
        var bytes = Encoding.UTF8.GetBytes(log);
        var writeTime = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        _fileInfoReader = _ => (true, bytes.Length, writeTime);

        var openCount = 0;
        _fileOpener = _ =>
        {
            openCount++;
            return new MemoryStream(bytes);
        };

        using var service = CreateService();

        // First read: parses and caches
        var first = await service.GetAsync(default);
        openCount.Should().Be(1);
        first.TotalUsd.Should().Be(5.0m);
        first.MonthStart.Should().Be(new DateOnly(2026, 11, 1));
        first.DaysLeft.Should().Be(11);

        // Advance 2 days within same month: cache hit, file not re-opened
        _clock.Now = _clock.Now.AddDays(2);
        var second = await service.GetAsync(default);
        openCount.Should().Be(1);
        second.TotalUsd.Should().Be(5.0m);
        second.DaysLeft.Should().Be(9);

        // Advance into next month (December 2026): cache invalidated because month rolled over
        _clock.Now = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
        var third = await service.GetAsync(default);
        openCount.Should().Be(2);
        third.MonthStart.Should().Be(new DateOnly(2026, 12, 1));
        third.TotalUsd.Should().Be(0.0m); // November row no longer counts in December
    }

    [TestMethod]
    public async Task GetAsync_EnvironmentOverride_ReadsProcessFirstThenUserScope()
    {
        _processEnv = null;
        _userEnv = "C:\\custom\\spend.jsonl";
        string? openedPath = null;
        _fileInfoReader = path => (true, 10, _clock.Now.UtcDateTime);
        _fileOpener = path =>
        {
            openedPath = path;
            return new MemoryStream(Encoding.UTF8.GetBytes(""));
        };

        using var service = CreateService();
        await service.GetAsync(default);

        _envReads.Should().Equal(EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User);
        openedPath.Should().Be("C:\\custom\\spend.jsonl");
    }

    [TestMethod]
    public async Task GetAsync_OversizeFile_KeepsNewestLinesAndDiscardsPartialFirstLine()
    {
        // Construct a stream that exceeds 8 MiB
        const int eightMb = 8 * 1024 * 1024;
        const string oldDataPadding = "OLD_DATA_PADDING_LINE\n";
        var paddingBytes = Encoding.UTF8.GetBytes(oldDataPadding);

        var stream = new MemoryStream();
        while (stream.Length < eightMb + 500)
        {
            stream.Write(paddingBytes);
        }

        // At the boundary, write a line that will be partially cut off, followed by a valid recent line
        var boundaryText = "PARTIAL_TRUNCATED_JSON_HEADER_{\"time\": \"2026-11-19T01:00:00\", \"model\": \"gemini-4-neon\", \"usd\": 99.0}\n"
            + "{\"time\": \"2026-11-20T10:00:00\", \"model\": \"gemini-4-neon\", \"usd\": 12.34}\n";
        var boundaryBytes = Encoding.UTF8.GetBytes(boundaryText);
        stream.Write(boundaryBytes);
        stream.Position = 0;

        _fileInfoReader = _ => (true, stream.Length, _clock.Now.UtcDateTime);
        _fileOpener = _ =>
        {
            var ms = new MemoryStream(stream.ToArray());
            return ms;
        };

        using var service = CreateService();
        var result = await service.GetAsync(default);

        result.State.Should().Be(GeminiApiSpendState.Ok);
        result.Calls.Should().Be(1);
        result.TotalUsd.Should().Be(12.34m);
        result.Models.Single().Usd.Should().Be(12.34m);
    }

    [TestMethod]
    public async Task GetAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var service = CreateService();
        var act = async () => await service.GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
