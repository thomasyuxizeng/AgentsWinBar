using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native.Cost;

/// <summary>
/// Supplies the prices for cost reports: the bundled snapshot, or a newer copy of the models.dev catalog kept in
/// the app's data folder and refreshed at most once a day. Every failure (offline, bad status, malformed or
/// implausible data, unwritable folder) leaves the previous prices in place.
/// </summary>
public sealed class CostPriceStore
{
    public const string CatalogUrl = "https://models.dev/api.json";
    internal const string FileName = "model-prices.json";
    internal const string AttemptFileName = "model-prices.attempt";
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private const long MaxDownloadBytes = 64L * 1024 * 1024;
    private const long MaxLocalFileBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private readonly string _directory;
    private readonly ILogger<CostPriceStore> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private CostPricing? _pricing;
    /// <summary>The file stamps the prices were read from; null after a failed read, so the next call reads again.</summary>
    private (long CatalogStamp, long CustomStamp)? _loadedFrom;

    public CostPriceStore(string dataDirectory, ILogger<CostPriceStore> logger, TimeProvider? clock = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _directory = Path.GetFullPath(dataDirectory);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });
    }

    internal string CatalogPath => Path.Combine(_directory, FileName);
    internal string AttemptPath => Path.Combine(_directory, AttemptFileName);
    internal string CustomPath => Path.Combine(_directory, CostCustomPricing.FileName);

    /// <summary>The prices to use now. Reloads only when the refreshed catalog or the user's overlay changed.</summary>
    public CostPricing GetPricing()
    {
        var stamps = (Stamp(CatalogPath), Stamp(CustomPath));
        lock (_stateGate)
        {
            if (_pricing is not null && _loadedFrom == stamps) return _pricing;
            var bundled = CostPriceCatalog.Bundled;
            var catalog = bundled;
            var failed = false;
            if (ReadFile(CatalogPath, ref failed) is { } bytes && CostPriceCatalog.Parse(bytes) is { IsPlausible: true } local &&
                local.Stamp > bundled.Stamp)
                catalog = local.MergeMissingFrom(bundled);
            var custom = ReadFile(CustomPath, ref failed) is { } overlay ? CostCustomPricing.Parse(overlay) : CostCustomPricing.Empty;
            // A file that could not be read (locked while it is written, for example) is read again next time
            // even though its stamp has not changed.
            _loadedFrom = failed ? null : stamps;
            return _pricing = new CostPricing(catalog, CostPriceOverlay.Bundled, custom);
        }
    }

    /// <summary>
    /// Downloads the catalog when the last attempt (successful or not) is at least a day old. Returns true when
    /// new prices were saved. Never throws except for the caller's cancellation.
    /// </summary>
    public async Task<bool> RefreshIfDueAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (!IsDue(now)) return false;
            try
            {
                Directory.CreateDirectory(_directory);
                // Recorded first, so a failing download is not repeated on every cost update.
                await File.WriteAllTextAsync(AttemptPath, now.ToString("O", CultureInfo.InvariantCulture), cancellationToken)
                    .ConfigureAwait(false);
                var fetched = await DownloadAsync(now, cancellationToken).ConfigureAwait(false);
                if (fetched is null || !fetched.IsPlausible)
                {
                    _logger.LogInformation("Price refresh returned no usable catalog; keeping the current prices");
                    return false;
                }
                var merged = fetched.MergeMissingFrom(CurrentCatalog());
                var temporary = CatalogPath + ".tmp";
                await File.WriteAllBytesAsync(temporary, merged.Serialize(), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, CatalogPath, overwrite: true);
                _logger.LogInformation("Prices refreshed from models.dev ({Anthropic} Anthropic, {OpenAI} OpenAI and {Google} Google models)",
                    merged.Count(CostPriceCatalog.Anthropic), merged.Count(CostPriceCatalog.OpenAI), merged.Count(CostPriceCatalog.Google));
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or OperationCanceledException or InvalidDataException or NotSupportedException)
            {
                _logger.LogInformation("Price refresh failed ({Type}); keeping the current prices", ex.GetType().Name);
                return false;
            }
        }
        finally { _refreshGate.Release(); }
    }

    internal bool IsDue(DateTimeOffset now)
    {
        var last = DateTimeOffset.MinValue;
        try
        {
            if (File.Exists(AttemptPath) && DateTimeOffset.TryParse(File.ReadAllText(AttemptPath).Trim(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var attempt))
                last = attempt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // A timestamp from the future (clock change) must not block refreshes forever.
        return last > now || now - last >= RefreshInterval;
    }

    private CostPriceCatalog CurrentCatalog()
    {
        var bundled = CostPriceCatalog.Bundled;
        var failed = false;
        return ReadFile(CatalogPath, ref failed) is { } bytes && CostPriceCatalog.Parse(bytes) is { IsPlausible: true } local
            ? local.MergeMissingFrom(bundled) : bundled;
    }

    private async Task<CostPriceCatalog?> DownloadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        // One short-lived client per day keeps no idle connection or handler in memory.
        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "model-price-refresh/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Price catalog returned status {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException("Price catalog is larger than expected.");
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var limited = new LimitedStream(body, MaxDownloadBytes);
        return await CostPriceCatalog.ParseLiveAsync(limited, now, deadline.Token).ConfigureAwait(false);
    }

    private static long Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The file's bytes, or null when it is missing, empty or too large; sets <paramref name="failed"/> when it could not be read.</summary>
    private static byte[]? ReadFile(string path, ref bool failed)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= MaxLocalFileBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = true;
            return null;
        }
    }

    /// <summary>Fails a read once more than the allowed number of bytes came through.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > limit) throw new InvalidDataException("Price catalog is larger than expected.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
