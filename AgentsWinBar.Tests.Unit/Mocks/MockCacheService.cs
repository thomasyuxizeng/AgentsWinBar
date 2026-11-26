using System.Collections.Concurrent;
using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Mocks;

/// <summary>
/// Mock implementation of ICacheService for testing. Thread-safe like the real CacheService: native providers finish
/// in parallel and each one stores its reading, so a plain Dictionary here made routing tests fail intermittently.
/// </summary>
public class MockCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, UsageData> _cache = new();
    private int _getCallCount;
    private int _setCallCount;
    private int _clearCallCount;

    public int GetCallCount => Volatile.Read(ref _getCallCount);
    public int SetCallCount => Volatile.Read(ref _setCallCount);
    public int ClearCallCount => Volatile.Read(ref _clearCallCount);

    public UsageData? Get(string provider)
    {
        Interlocked.Increment(ref _getCallCount);
        return _cache.TryGetValue(provider, out var data) ? data : null;
    }

    public void Set(string provider, UsageData data)
    {
        Interlocked.Increment(ref _setCallCount);
        _cache[provider] = data;
    }

    public void Clear()
    {
        Interlocked.Increment(ref _clearCallCount);
        _cache.Clear();
    }

    public IReadOnlyDictionary<string, UsageData> GetAll()
    {
        return _cache;
    }

    public Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public Task LoadAsync()
    {
        return Task.CompletedTask;
    }

    // Test helpers
    public void PreloadCache(string provider, UsageData data)
    {
        _cache[provider] = data;
    }
}
