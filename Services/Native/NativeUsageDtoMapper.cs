using System.Buffers;
using System.Text.Json;
using CodexBarWin.Models;

namespace CodexBarWin.Services.Native;

/// <summary>
/// Maps a <see cref="NativeUsageResult"/> to the DTO the app already parses from CLI JSON, with the same meaning the
/// WSL shim gives it: cached rows carry cached/cacheUpdatedAt; a sign-in failure with a last good reading keeps the
/// reading and adds authRequired/authError; failures without a reading become the CLI's error object.
/// </summary>
public static class NativeUsageDtoMapper
{
    public static UsageDataDto ToDto(NativeUsageResult result)
    {
        var provider = ProviderConstants.ValidateAndNormalize(result.Provider);
        var authFailure = result.Status is NativeUsageStatus.SignInMissing or NativeUsageStatus.SignInExpired or NativeUsageStatus.Unauthorized;
        var hasUsage = result.HasUsage;
        return new UsageDataDto
        {
            Provider = provider,
            Source = result.Source,
            Cached = result.Cached,
            CacheUpdatedAt = result.Cached ? result.CacheUpdatedAt?.UtcDateTime : null,
            Usage = hasUsage ? new UsageDto
            {
                LoginMethod = result.LoginMethod,
                Primary = Window(result.Primary),
                Secondary = Window(result.Secondary),
                Tertiary = Window(result.Tertiary),
                ExtraRateWindows = result.ExtraWindows.Select(e => new ExtraRateWindowDto { Id = e.Id, Title = e.Title, Window = Window(e.Window) }).ToList(),
                UpdatedAt = result.UpdatedAt?.UtcDateTime
            } : null,
            Error = result.Status == NativeUsageStatus.Ok || hasUsage ? default : ErrorElement(result, authFailure),
            AuthRequired = authFailure,
            AuthError = authFailure ? result.Message : null
        };
    }

    private static UsageWindowDto? Window(NativeUsageWindow? window) => window == null ? null : new UsageWindowDto
    {
        UsedPercent = window.UsedPercent,
        WindowMinutes = window.WindowMinutes,
        ResetsAt = window.ResetsAt?.UtcDateTime,
        ResetDescription = window.ResetDescription
    };

    /// <summary>{"kind": ..., "code": ..., "message": ...} like the CLI's error object.</summary>
    private static JsonElement ErrorElement(NativeUsageResult result, bool authFailure)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", authFailure ? "auth" : "provider");
            var code = result.HttpStatus ?? (authFailure ? 401 : result.Status == NativeUsageStatus.RateLimited ? 429 : (int?)null);
            if (code is { } c) writer.WriteNumber("code", c);
            writer.WriteString("message", result.Message ?? "Provider returned an error");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
