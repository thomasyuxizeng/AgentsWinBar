# Security and Edge-Case Review: Native Backend

Repository: `C:\Users\ThomasFramework\worktrees\CodexBarWin\native-app`
Review scope: All 31 native backend files in `files.txt`, supporting integration paths (`CodexBarService`, `CacheService`, `CacheRetention`, `CostLedger`, `App.xaml.cs`, `MainWindow.xaml.cs`, `MainViewModel`), and unit tests under `CodexBarWin.Tests.Unit/Services/`.

Findings are ordered from most severe to least severe. No critical token-exfiltration or arbitrary-code-execution vulnerabilities were found; the most severe issues are denial-of-service / permanent lockout bugs, wrong displayed numbers from integration and state bugs, and a calling-convention crash on x86 builds.

---

## 1. Unhandled `ArgumentOutOfRangeException` on `Retry-After` and uncapped live 429 cooldown in `ClaudeNativeClient`

- **Severity:** high
- **Location:** `Services/Native/NativeHttp.cs:46-60`, `Services/Native/ClaudeNativeClient.cs:152-163, 178`, `Services/Native/CodexNativeClient.cs:116-117, 126`
- **Concrete scenario:**
  1. Anthropic or ChatGPT (or a proxy) responds with HTTP 429 and `Retry-After: Infinity`, `Retry-After: 1e20`, or a large delta-seconds value whose sum with `now` exceeds `DateTimeOffset.MaxValue`. `NativeHttp.RetryAfter` throws `ArgumentOutOfRangeException`. Neither `ClaudeNativeClient.FetchAsync` nor `CodexNativeClient.FetchCoreAsync` catches `ArgumentOutOfRangeException`, so the exception escapes to `NativeUsageService`, discarding the client's last-good reading (returning a generic failure instead of cached usage) and skipping Claude's cooldown update and persistence (so the next refresh immediately hits the rate-limited endpoint again).
  2. Anthropic (or a proxy) responds with HTTP 429 and a valid but huge `Retry-After` such as `31536000` (1 year) or a far-future HTTP date. `ClaudeNativeClient` stores that uncapped date in `_blockedUntil` and writes it to `claude-cooldown.json`. Because the app runs for weeks without restarting, Claude usage stops updating for the rest of the process lifetime (and still stays blocked for 24 hours even after a restart).
- **Why it happens:**
  In `Services/Native/NativeHttp.cs:49-56`, `RetryAfter` neither checks `double.IsFinite(seconds)` nor guards addition against `DateTimeOffset.MaxValue`:
  ```csharp
  if (header?.Delta is { } delta && delta >= TimeSpan.Zero) return now + delta;
  ...
  if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
      return now.AddSeconds(seconds);
  ```
  In `Services/Native/ClaudeNativeClient.cs:155-158`, `MaxPersistedCooldown` (24 hours) is only applied in the constructor when loading from disk (lines 78-79), never when handling a live 429:
  ```csharp
  var candidate = callNow + _options.RateLimitCooldown;
  var retryAfter = NativeHttp.RetryAfter(response, callNow);
  if (retryAfter > candidate) candidate = retryAfter.Value;
  lock (_gate) { if (candidate > _blockedUntil) _blockedUntil = candidate; blockedUntil = _blockedUntil; }
  ```
  And both `Services/Native/ClaudeNativeClient.cs:178` and `Services/Native/CodexNativeClient.cs:126` only catch `HttpRequestException or JsonException or IOException`.
- **Suggested fix:**
  In `NativeHttp.RetryAfter`, require `double.IsFinite(seconds)`, bound `delta` and `seconds` so `now + delta` / `now.AddSeconds(seconds)` cannot exceed `DateTimeOffset.MaxValue` (for example, clamp or reject values above a sane maximum such as `TimeSpan.FromDays(365)` or return `null` on overflow). In `ClaudeNativeClient.FetchAsync`, clamp `candidate` to `callNow + MaxPersistedCooldown` before updating `_blockedUntil` and saving to `_cooldownStore`.
- **Unit test:**
  1. `RetryAfter_WhenHeaderIsInfinityOrOverflowsDateTimeOffset_DoesNotThrow` (in `NativeCredentialHelpersTests`): pass 429 responses with `Retry-After: Infinity`, `Retry-After: 1e20`, and `Retry-After: 999999999999` and assert `NativeHttp.RetryAfter` returns `null` (or a capped timestamp) without throwing.
  2. `FetchAsync_When429HasHugeRetryAfter_CapsLiveCooldownToMaxPersistedCooldown` (in `ClaudeNativeClientTests`): return HTTP 429 with `Retry-After: 31536000` and assert `result.Status == NativeUsageStatus.RateLimited`, `result.RetryAfter == Now + ClaudeNativeClient.MaxPersistedCooldown`, and `client.BlockedUntil == Now + ClaudeNativeClient.MaxPersistedCooldown`.

---

## 2. `CodexBarService.ResolveUsageCore` discards `NativeLastGood.ClearPastResets`, keeping stale non-zero usage for already-reset windows

- **Severity:** high
- **Location:** `Services/CodexBarService.cs:1164-1191`, `Services/Native/NativeLastGood.cs:26-56`, `Services/CacheRetention.cs:48-62`
- **Concrete scenario:**
  At 08:25, any native client (Claude, Codex, or Gemini) fetches a fresh reading where the 5-hour window is at 80% used and resets at 08:30 (`UpdatedAt = 08:25`). `CodexBarService` saves that reading in `_cacheService`. At 08:40 (10 minutes after the 5-hour window reset), the provider sign-in expires (`SignInExpired`), returns HTTP 429 (`RateLimited`), or fails offline (`Failed`). The native client calls `NativeLastGood.FromCacheOr`, which runs `ClearPastResets` and returns Primary = 0% (`ResetsAt = null`, `Cached = true`, `UpdatedAt = 08:25`, `CacheUpdatedAt = 08:25`). When `CodexBarService.ResolveUsageCore` processes the mapped DTO, it throws away the cleared 0% window and restores `previous!.Session` (80% used, `ResetAt = 08:30`) from `_cacheService`. All three providers show stale non-zero percentages for windows that have already reset, defeating `NativeLastGood.ClearPastResets` in production.
- **Why it happens:**
  `NativeLastGood.FromCacheOr` preserves `UpdatedAt = lastGood.UpdatedAt` (08:25). In `Services/CodexBarService.cs:1164-1165`, `obsAtUtc` (08:25) equals `prevObsAt` (08:25), making `isReplayOfHeld` true:
  ```csharp
  var isReplayOfHeld = !usageSuperseded && isCached && data.HasUsage && obsAtUtc.HasValue &&
      (previous != null && prevObsAt != null && obsAtUtc!.Value <= prevObsAt.Value);
  ```
  Then `Services/CodexBarService.cs:1185-1188` unconditionally prefer `previous!` over `data`:
  ```csharp
  Session = acceptsUsage ? (isReplayOfHeld ? previous!.Session : data.Session) : previous?.Session,
  Weekly = acceptsUsage ? (isReplayOfHeld ? previous!.Weekly : data.Weekly) : previous?.Weekly,
  Tertiary = acceptsUsage ? (isReplayOfHeld ? previous!.Tertiary : data.Tertiary) : previous?.Tertiary,
  ExtraWindows = acceptsUsage ? (isReplayOfHeld ? previous!.ExtraWindows : data.ExtraWindows) : previous?.ExtraWindows ?? [],
  ```
  `CacheRetention.PruneExpired` (`Services/CacheRetention.cs:48-62`) only expires readings older than 7 days and never zeroes windows whose `ResetAt <= now`. Existing unit tests only test `ClearPastResets` directly on the native clients, never through `CodexBarService`.
- **Suggested fix:**
  Apply past-reset window clearing in `CodexBarService.ResolveUsageCore` (or in a shared helper invoked on `previous` / non-fresh `resolved` windows whenever `window.ResetAt is { } r && DashboardValues.Utc(r) <= now`), zeroing `Used`, `ReportedPercent`, `ResetAt`, and `ResetIn` on stale/replayed windows whose reset instant has passed.
- **Unit test:**
  `GetAllUsageAsync_WhenNativeReturnsClearedPastResetReplay_PublishesZeroedWindow` (in `CodexBarServiceNativeRoutingTests`): fetch an initial reading at 08:25 with Primary = 80% resetting at 08:30, advance the clock to 08:40, return a cached `SignInExpired` / `RateLimited` reading with Primary cleared to 0% (`ResetsAt = null`, `UpdatedAt = 08:25`), and assert the returned `UsageData.Session!.Percent == 0` and `UsageData.Session.ResetAt == null`.

---

## 3. Skipped locked or mid-compression log files are dropped by `CostLogWatcher` for up to 6 hours

- **Severity:** high
- **Location:** `Services/Native/NativeCostService.cs:90-103`, `Services/Native/Cost/NativeCostScanner.cs:271-277`
- **Concrete scenario:**
  A Claude or Codex log file is modified (or Codex creates a new `.jsonl.zst` archive) and `CostLogWatcher` queues its path. On the next cost scan, `TakePlan()` hands out `ChangedPaths` and clears `_changed`. While `NativeCostScanner.Update` reads that file, antivirus/backup holds an exclusive lock (`IOException`/`UnauthorizedAccessException`) or Codex has not finished writing the zstd frame (`ZstdException`). `NativeCostScanner.Read` catches the exception, marks `input.Skipped = true`, increments `unavailable`, and returns normally. `NativeCostService.Scan` ignores `result.UnavailableFiles` and calls `_watcher.Complete(plan, succeeded: true)`. The skipped file is lost from `_changed`; if no further writes happen to it, its entire session cost is missing until `CostLogWatcher.FullListingInterval` (6 hours) expires.
- **Why it happens:**
  In `Services/Native/Cost/NativeCostScanner.cs:271-277`, per-file read failures are swallowed into a counter:
  ```csharp
  catch (Exception ex) when (ex is UnauthorizedAccessException or ZstdSharp.ZstdException ||
      ex is IOException and not CostIndexDatabase.SqliteException)
  {
      input.Skipped = true;
      Interlocked.Increment(ref unavailable);
      if (modified) Interlocked.Exchange(ref retry, 1);
  }
  ```
  And in `Services/Native/NativeCostService.cs:94-95`, `result.UnavailableFiles` is never checked before completing the watcher plan:
  ```csharp
  var result = _scanner.Update(_roots, plan.FullListing ? 2 : 1, ct, plan.FullListing ? null : plan.ChangedPaths);
  _watcher.Complete(plan, succeeded: true);
  ```
  `NativeCostScannerTests.Update_WhenChangedFileIsLocked_ScansOtherFilesAndRetriesFromPreviousOffset` only tests `NativeCostScanner.Update` directly with full listings, not `NativeCostService` with `CostLogWatcher`.
- **Suggested fix:**
  Record skipped file paths in `CostScanResult` (e.g., `IReadOnlyList<string> SkippedPaths`). In `NativeCostService.Scan`, after `Update` completes, re-queue any `result.SkippedPaths` into `_watcher` (or if `plan.FullListing` and `result.UnavailableFiles > 0`, keep `_fullListingNeeded = true` or re-queue the skipped paths) so the next scan retries them as soon as the lock or compression finishes.
- **Unit test:**
  `FetchCostJsonAsync_WhenWatchedFileIsTemporarilyLocked_RetriesItOnNextScan` (in `NativeCostServiceTests`): settle the initial scan, append to a log file, trigger a watcher notification while holding a `FileShare.None` lock on the file, run `FetchCostJsonAsync` (verifying the scan succeeds without the locked bytes), release the lock without writing again, run `FetchCostJsonAsync` again (before `FullListingInterval`), and assert the previously locked tokens are now included.

---

## 4. Corrupt or deleted `index.sqlite` is neither detected nor rebuilt when `changedPaths` is empty

- **Severity:** high
- **Location:** `Services/Native/Cost/NativeCostScanner.cs:48, 399-407`, `Services/Native/NativeCostService.cs:90-124`
- **Concrete scenario:**
  After startup, no log files change (`plan.FullListing == false`, `plan.ChangedPaths` is empty).
  1. If `index.sqlite` is deleted (e.g., user clears cache folder or cleanup tool removes it), `NativeCostScanner.Update` returns immediately without checking `_indexPath`. `ReadReport` -> `ReadDays` sees `!File.Exists(_indexPath)` and returns an empty list, so `NativeCostService` reports a valid `HasReading = true` $0.00 / 0-token cost report for up to 6 hours (even across manual force refreshes).
  2. If `index.sqlite` is corrupted on disk, `NativeCostScanner.Update` returns immediately at line 48, `NativeCostService` marks `_watcher.Complete(plan, succeeded: true)` at line 95, and then `ReadReport` at line 119 throws `CostIndexDatabase.SqliteException`. Because `_watcher.NotifyLost()` is never called, every subsequent scan (including manual `forceRefresh: true`) receives `ChangedPaths = []` again and keeps failing in `ReadReport` for up to 6 hours instead of triggering `OpenForUpdate`'s rebuild logic.
- **Why it happens:**
  `Services/Native/Cost/NativeCostScanner.cs:48` short-circuits before opening or validating the database:
  ```csharp
  if (changedPaths is { Count: 0 }) return new(0, 0, 0, 0, 0, 0, 0);
  ```
  `Services/Native/NativeCostService.cs:94-95, 119` marks the watcher plan succeeded before calling `ReadReport`:
  ```csharp
  var result = _scanner.Update(_roots, plan.FullListing ? 2 : 1, ct, plan.FullListing ? null : plan.ChangedPaths);
  _watcher.Complete(plan, succeeded: true);
  ...
  var report = source is { } known
      ? _scanner.ReadReport(known, pricing, asOf, HistoryDays, ct)
  ```
  And `Services/Native/Cost/NativeCostScanner.cs:401` silently returns empty on a missing file:
  ```csharp
  if (!File.Exists(_indexPath)) return [];
  ```
  `NativeCostScannerTests.Update_WhenIndexIsInvalid_RebuildsFromLogs` only calls full-listing `Update()`, never `NativeCostService` with an empty `changedPaths`.
- **Suggested fix:**
  In `NativeCostService.Scan`, if `!File.Exists(indexPath)` when `plan.FullListing == false`, upgrade the scan to a full listing (`changedPaths: null`) so a deleted index rebuilds immediately. Wrap the `ReadReport` loop in a `catch (CostIndexDatabase.SqliteException ex) when (ex.InvalidIndex)` that calls `_watcher.NotifyLost()` and performs one immediate full-listing rebuild-and-read retry.
- **Unit test:**
  1. `FetchCostJsonAsync_WhenIndexIsDeletedBetweenScans_RebuildsImmediately` (in `NativeCostServiceTests`): run an initial scan, delete `index.sqlite`, run `FetchCostJsonAsync` with no log changes and before `FullListingInterval`, and assert the original token totals are returned.
  2. `FetchCostJsonAsync_WhenIndexIsCorruptedBetweenScans_RebuildsImmediately` (in `NativeCostServiceTests`): run an initial scan, overwrite `index.sqlite` with `"not sqlite"`, run `FetchCostJsonAsync` with no log changes, and assert it rebuilds and returns the original token totals instead of throwing.

---

## 5. `ClaudeNativeClient` serves cached last-good usage on `SignInMissing` and `Unauthorized`, contradicting `NativeLastGood.ServesLastGood`

- **Severity:** high
- **Location:** `Services/Native/ClaudeNativeClient.cs:118-120, 164-169, 328-329`, `Services/Native/NativeLastGood.cs:14-15`, `Services/Native/NativeUsageDtoMapper.cs:46`
- **Concrete scenario:**
  After Claude has fetched a valid reading, the user signs out of Claude Code (`.credentials.json` is deleted or `claudeAiOauth` is removed) or Anthropic rejects the token with HTTP 401/403. After the 9.5-minute fresh cache expires, `ClaudeNativeClient.FetchAsync` returns `Status = SignInMissing` or `Unauthorized` together with `Cached = true` and the old `Primary`/`Secondary` windows (`HasUsage = true`). Downstream, `NativeUsageDtoMapper.ToDto` omits `Error` whenever `hasUsage` is true, `UsageDataDto.ToUsageData` sets `UsageFetchOutcome = FetchOutcome.Success` and `Error = null`, and `CodexBarService.ResolveUsageCore` sets `acceptsUsage = true` and writes the old reading back into `_cacheService`. By contrast, `CodexNativeClient` and `AntigravityNativeClient` both use `NativeLastGoodStore.Apply`, returning `HasUsage = false` and `Cached = false` on `SignInMissing` and `Unauthorized`.
- **Why it happens:**
  `Services/Native/NativeLastGood.cs:14-15` defines the contract:
  ```csharp
  public static bool ServesLastGood(NativeUsageStatus status) =>
      status is NativeUsageStatus.SignInExpired or NativeUsageStatus.Failed or NativeUsageStatus.RateLimited;
  ```
  However, `Services/Native/ClaudeNativeClient.cs:118-120, 164-169, 328-329` calls `FromCacheOr` unconditionally on `SignInMissing` and `Unauthorized` without checking `NativeLastGood.ServesLastGood`:
  ```csharp
  if (credentials == null)
      return FromCacheOr(lastGood, Fail(NativeUsageStatus.SignInMissing, null,
          "Not signed in to Claude on Windows. Sign in to Claude Code on Windows."));
  ...
  if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
  {
      _logger.LogWarning("Claude usage endpoint answered {Status}", status);
      return FromCacheOr(lastGood, Fail(NativeUsageStatus.Unauthorized, status,
          $"Claude rejected the Windows sign-in (HTTP {status}). Sign in to Claude Code on Windows again.") with { NetworkCalls = 1 });
  }
  ```
  Unlike `CodexNativeClientTests` (line 180) and `AntigravityNativeClientTests` (line 217), `ClaudeNativeClientTests` has no test for `401` or `SignInMissing` after a prior good reading.
- **Suggested fix:**
  Return `Fail(...)` directly (without `FromCacheOr`) for `SignInMissing` and `Unauthorized`, or guard `FromCacheOr` with `if (!NativeLastGood.ServesLastGood(failure.Status)) return failure;`, and clear `_lastGood = null` under `_gate` on `SignInMissing` / `Unauthorized`.
- **Unit test:**
  `FetchAsync_WhenUnauthorizedOrSignInMissingAfterGoodReading_DoesNotServeLastGoodReading` (in `ClaudeNativeClientTests`): fetch a valid reading, advance clock past `CacheLifetime`, then either return HTTP 401 or delete `_path`; assert `result.Status` is `Unauthorized` / `SignInMissing`, `result.Cached` is `false`, and `result.HasUsage` is `false`.

---

## 6. Unbounded HTTP response reads with `AutomaticDecompression` in all three native usage clients

- **Severity:** medium
- **Location:** `Services/Native/NativeHttp.cs:33`, `Services/Native/ClaudeNativeClient.cs:143-144`, `Services/Native/CodexNativeClient.cs:104-105`, `Services/Native/AntigravityNativeClient.cs:166-167`
- **Concrete scenario:**
  A misbehaving upstream endpoint, captive portal, or corporate proxy returns HTTP 200 with a huge JSON payload or a small gzip/deflate/brotli body that expands to hundreds of megabytes. All three native usage clients decompress and deserialize the full body into memory with no byte limit, spiking RAM or causing an out-of-memory crash on a RAM-constrained laptop (whereas `CostPriceStore.DownloadAsync` enforces `MaxDownloadBytes` with `LimitedStream` and `NativeCredentialFile` enforces `MaxBytes = 4 MB`).
- **Why it happens:**
  `Services/Native/NativeHttp.cs:33` enables automatic decompression:
  ```csharp
  AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
  ```
  And all three clients deserialize straight from `response.Content.ReadAsStreamAsync(deadline.Token)` with no `ContentLength` check and no stream byte limit, for example `Services/Native/ClaudeNativeClient.cs:143-144`:
  ```csharp
  await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
      body = await JsonSerializer.DeserializeAsync(stream, NativeJsonContext.Default.ClaudeUsageResponse, deadline.Token).ConfigureAwait(false);
  ```
- **Suggested fix:**
  Add a helper in `NativeHttp` (e.g., `ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct, long maxBytes = 2 * 1024 * 1024)`) that rejects `ContentLength > maxBytes` and wraps the response stream in a bounded `Stream` (throwing `InvalidDataException` or `IOException` once `maxBytes` decompressed bytes are exceeded, which all three clients already catch and map to `Failed`).
- **Unit test:**
  `FetchAsync_WhenResponseExceedsSizeLimit_FailsWithoutReadingWholeBody` (in `ClaudeNativeClientTests`, `CodexNativeClientTests`, and `AntigravityNativeClientTests`): return a 200 OK response whose stream yields more than `MaxResponseBytes` of JSON padding (or a large `Content-Length`), assert `result.Status == NativeUsageStatus.Failed` and that the stream stopped reading shortly after the limit.

---

## 7. Credential and JWT `Utf8JsonReader` loops omit `reader.Skip()` on non-string values, causing structural desync and false `SignInMissing`

- **Severity:** medium
- **Location:** `Services/Native/ClaudeNativeClient.cs:206-218`, `Services/Native/CodexNativeClient.cs:156-172`, `Services/Native/AntigravityCredentials.cs:138-161`, `Services/Native/NativeCredentialHelpers.cs:170-177`
- **Concrete scenario:**
  If a provider changes a recognized credential field to a JSON object or array (or a tool writes an unexpected container type) before `accessToken`/`access_token`, the parser desynchronizes and misses the valid token:
  - In Codex `auth.json`: `{"OPENAI_API_KEY": {"env": "OPENAI_API_KEY"}, "tokens": {"access_token": "valid-jwt"}}` causes the reader to step into `{"env": ...}`, stop at its `EndObject`, never reach `"tokens"`, and return `(CodexCredentialKind.Missing, null)`.
  - In Claude `.credentials.json`: `"claudeAiOauth": {"subscriptionType": ["pro"], "accessToken": "valid"}` stops the loop on the array element `"pro"` and returns `null` (`SignInMissing`).
  - In Antigravity Credential Manager blob: `{"expiry": {"seconds": 1764050000}, "access_token": "valid"}` steps into `"expiry"`, stops at its `EndObject`, and returns `null` (`SignInMissing`).
  - In `NativeJwt.ReadChatGptAccountId`: `{"chatgpt_account_id": [], "https://api.openai.com/auth": {"chatgpt_account_id": "acct"}}` exits the loop on `EndArray` and returns `null`.
- **Why it happens:**
  Each parser calls `reader.Read()` on a known property name and checks `TokenType == JsonTokenType.String` (or `Number`), but never calls `reader.Skip()` when the value is `StartObject` or `StartArray`. For example, `Services/Native/ClaudeNativeClient.cs:208`:
  ```csharp
  if (reader.ValueTextEquals("accessToken"u8)) { reader.Read(); token = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
  ```
  `Services/Native/CodexNativeClient.cs:165-170`:
  ```csharp
  else if (reader.ValueTextEquals("OPENAI_API_KEY"u8))
  {
      reader.Read();
      // Only note presence; the key itself is never materialized.
      hasApiKey = reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0;
  }
  ```
  `Services/Native/AntigravityCredentials.cs:148`:
  ```csharp
  else if (reader.ValueTextEquals("expiry"u8)) { reader.Read(); f.Expiry = NativeParse.ParseTimestamp(Str(ref reader)) ?? f.Expiry; }
  ```
  `Services/Native/NativeCredentialHelpers.cs:172-177`:
  ```csharp
  if (reader.ValueTextEquals("chatgpt_account_id"u8))
  {
      reader.Read();
      if (reader.TokenType == JsonTokenType.String) found = NonEmpty(reader.GetString());
      continue;
  }
  ```
- **Suggested fix:**
  Call `reader.Skip()` after reading the value in every branch (or whenever `TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray`), matching the pattern already used in `CostLogParser.cs:83`.
- **Unit test:**
  `ParseCredentials_WhenKnownFieldPrecedingTokenIsObjectOrArray_StillReadsAccessToken` (in `ClaudeNativeClientTests`, `CodexNativeClientTests`, `AntigravityCredentialParserTests`, and `NativeCredentialHelpersTests`): place a known field with an object value `{"nested": 1}` and an array value `["x"]` before `accessToken`/`tokens`/`chatgpt_account_id` and assert the valid token/account ID is still extracted.

---

## 8. `NativeCredentialFile.ReadOnceAsync` throws unretried `InvalidDataException` when a credential file grows mid-rewrite, and `IsCompleteJson` treats whitespace-only as complete

- **Severity:** medium
- **Location:** `Services/Native/NativeCredentialHelpers.cs:56-82, 103-118`
- **Concrete scenario:**
  1. Claude Code rewrites `%USERPROFILE%\.claude\.credentials.json` (~36 KB) in-place by truncating it to 0 bytes and writing the new 36 KB JSON. `ReadOnceAsync` opens the file while `stream.Length == 0`, rents a 4096-byte `SensitiveBuffer`, and then reads 4096 bytes as Claude Code writes the 36 KB. Line 112 throws `InvalidDataException("Credential file grew while being read.")`. Because `ReadAsync` only retries when `IsCompleteJson(first.Span)` returns `false`, the growth exception bypasses the 75 ms retry and fails the read immediately.
  2. If a credential file contains only whitespace (e.g., `"   \n"`) during a rewrite, `IsCompleteJson` returns `true`, skipping the 75 ms retry and causing `ParseCredentials` to report `SignInMissing`.
- **Why it happens:**
  In `Services/Native/NativeCredentialHelpers.cs:58-64`:
  ```csharp
  var first = await ReadOnceAsync(path, ct).ConfigureAwait(false);
  if (first == null) return null;
  if (IsCompleteJson(first.Span)) return first;
  ```
  In `Services/Native/NativeCredentialHelpers.cs:106, 112`:
  ```csharp
  var buffer = new SensitiveBuffer((int)length + 4096);
  ...
  if (total == buffer.Array.Length) throw new InvalidDataException("Credential file grew while being read.");
  ```
  And in `Services/Native/NativeCredentialHelpers.cs:74-76`, `IsCompleteJson` does not check that at least one JSON token was read:
  ```csharp
  var reader = new Utf8JsonReader(content, ReaderOptions);
  while (reader.Read()) { }
  return true;
  ```
- **Suggested fix:**
  In `ReadOnceAsync`, when `total == buffer.Array.Length`, either grow the `SensitiveBuffer` up to `MaxBytes` (zeroing and disposing the old buffer after copying) or catch the growth `InvalidDataException` in `ReadAsync` to trigger the single `RetryDelay` retry. In `IsCompleteJson`, track `var sawToken = false; while (reader.Read()) sawToken = true; return sawToken;`.
- **Unit test:**
  1. `ReadAsync_WhenFileIsWhitespaceOnlyThenRewritten_RetriesOnceAndReadsNewContent` (in `NativeCredentialHelpersTests`): start with `"   \n"`, overwrite with valid JSON in `delayBeforeRetry`, and assert `retries == 1` and the new JSON is returned.
  2. `ReadAsync_WhenFileGrowsBeyondInitialSlackDuringRead_RetriesOrGrowsUpToMaxBytes` (in `NativeCredentialHelpersTests`): simulate a file that grows by >4 KB between `Length` and EOF (or test a retry hook after growth) and assert the complete JSON is returned without throwing.

---

## 9. Backward system clock jump keeps `ClaudeNativeClient` cache/cooldown and `CodexBarService` cost cache stuck

- **Severity:** medium
- **Location:** `Services/Native/ClaudeNativeClient.cs:98-105`, `Services/CodexBarService.cs:212-213`
- **Concrete scenario:**
  If the system clock steps backward by 2 hours (e.g., NTP correction after sleep/resume or manual clock adjustment):
  1. In `ClaudeNativeClient.FetchAsync`, `now - updated` is `-2h`, which satisfies `< _options.CacheLifetime` (9.5 min). Claude serves its old cached reading as fresh for 2 hours and 9.5 minutes instead of 9.5 minutes.
  2. In `ClaudeNativeClient.FetchAsync`, if a 10-minute 429 cooldown is active when the clock steps backward by 2 hours, `blockedUntil > now` stays true for 2 hours and 10 minutes.
  3. In `CodexBarService.FetchCostsAsync`, `now - cached.AttemptedAt` is `-2h < CostCacheTtl` (30 min), skipping non-forced cost scans for 2 hours and 30 minutes.
  (By contrast, `CacheService.cs:195`, `CostLogWatcher.cs:53`, and `CostPriceStore.cs:121` all explicitly guard against backward clock jumps.)
- **Why it happens:**
  `Services/Native/ClaudeNativeClient.cs:98-103`:
  ```csharp
  if (_options.UseFreshCache && lastGood?.UpdatedAt is { } updated && now - updated < _options.CacheLifetime &&
      !NativeLastGood.HasPastReset(lastGood, now))
      return lastGood with { Cached = true, CacheUpdatedAt = updated, NetworkCalls = 0 };

  if (blockedUntil > now)
  ```
  `Services/CodexBarService.cs:212-213`:
  ```csharp
  if (!forceRefresh && _costCache.TryGetValue(provider, out var cached) &&
      now - cached.AttemptedAt < (_costTimedOut.Contains(provider) ? CostTimeoutBackoff : CostCacheTtl))
  ```
- **Suggested fix:**
  Require `now >= updated` in `ClaudeNativeClient` (and clamp `blockedUntil - now` to `MaxPersistedCooldown` or track in-memory cooldown duration with `_clock.GetTimestamp()`), and require `now >= cached.AttemptedAt` in `CodexBarService.FetchCostsAsync`.
- **Unit test:**
  1. `FetchAsync_WhenClockMovesBackward_DoesNotServeStaleFreshCache` (in `ClaudeNativeClientTests`): fetch at `Now`, move `clock.Now = Now.AddHours(-2)`, fetch again, and assert a second HTTP request is made.
  2. `GetCostAsync_WhenClockMovesBackward_RescansInsteadOfServingCache` (in `CodexBarServiceNativeCostTests`): scan at `Now`, move `_clock.Now = Now.AddHours(-2)`, call `GetCostAsync`, and assert the native cost service is called again.

---

## 10. Extreme-year timestamps in a log line crash `NativeCostScanner` with an uncaught `ArgumentOutOfRangeException`

- **Severity:** medium
- **Location:** `Services/Native/Cost/NativeCostScanner.cs:206, 271-277`, `Services/Native/Cost/CostIndex.cs:99, 121-122`, `Services/Native/Cost/CostLogParser.cs:69-70, 98, 123`
- **Concrete scenario:**
  A single malformed or corrupted JSONL line contains `"timestamp": "9999-12-31T20:00:00Z"` on a machine in a positive UTC offset (e.g., UTC+9) or `"timestamp": "0001-01-01T01:00:00Z"` on a machine in a negative UTC offset (e.g., UTC-8). `CostLogParser` accepts it because `record.Time != default`. When `NativeCostScanner` converts it via `CostIndex.LocalDay(evt.Time, zone)`, `TimeZoneInfo.ConvertTime` overflows `DateTimeOffset.MaxValue`/`MinValue` and throws `ArgumentOutOfRangeException`. Because `NativeCostScanner.Read` only catches `UnauthorizedAccessException`, `ZstdException`, and `IOException`, the entire cost scan aborts (and keeps failing on every subsequent scan while that file remains).
- **Why it happens:**
  `Services/Native/Cost/CostLogParser.cs:69-70` accepts any `DateTimeOffset` other than `default`:
  ```csharp
  else if (name.SequenceEqual("timestamp"u8) && reader.TokenType == JsonTokenType.String)
      reader.TryGetDateTimeOffset(out record.Time);
  ```
  `Services/Native/Cost/CostIndex.cs:121-122` converts it to local time without bounds checking:
  ```csharp
  internal static DateOnly LocalDay(DateTimeOffset time, TimeZoneInfo zone) =>
      DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);
  ```
  And `Services/Native/Cost/NativeCostScanner.cs:271-272` does not catch `ArgumentOutOfRangeException`.
- **Suggested fix:**
  Reject out-of-range timestamps in `CostLogParser` (e.g., require `record.Time.Year is >= 2000 and <= 9998`, or at minimum `> 1 and < 9999` so any +/-14-hour UTC offset conversion is always in range), incrementing `BadLines` or returning `null`.
- **Unit test:**
  `Update_WhenLogContainsExtremeYearTimestamp_SkipsLineWithoutCrashing` (in `NativeCostScannerTests`): configure a UTC+9 (and UTC-8) scanner, write a Claude log with `"timestamp":"9999-12-31T20:00:00Z"` (and `"0001-01-01T01:00:00Z"`) alongside a valid line, call `Update`, and assert it completes without throwing and indexes the valid line.

---

## 11. OS time zone changes are not reflected in cost reports when no log files have changed

- **Severity:** medium
- **Location:** `Services/Native/Cost/NativeCostScanner.cs:48, 67, 341`, `Services/Native/NativeCostService.cs:94, 119`
- **Concrete scenario:**
  The app has been running in one time zone (e.g., UTC+9) and the user changes Windows time zone (e.g., travels to UTC-8) before any new Claude/Codex log lines are written. On the next cost refresh, `CostLogWatcher` returns `FullListing = false, ChangedPaths = []`. `NativeCostScanner.Update` returns early at line 48, skipping both `TimeZoneInfo.ClearCachedData()` and `index.SetZone(zone)`. `ReadReport` then reads `TimeZoneInfo.Local` (still cached as the old zone) and queries `days` (still bucketed by the old zone). Daily buckets and "today" stay in the old time zone for up to 6 hours.
- **Why it happens:**
  In `Services/Native/Cost/NativeCostScanner.cs:48, 67`:
  ```csharp
  if (changedPaths is { Count: 0 }) return new(0, 0, 0, 0, 0, 0, 0);
  ...
  if (_zone is null) TimeZoneInfo.ClearCachedData();
  ```
  And `Services/Native/Cost/NativeCostScanner.cs:341` calls `TimeZoneInfo.Local` without clearing cached time zone data or triggering re-bucketing:
  ```csharp
  var today = CostIndex.LocalDay(asOf ?? DateTimeOffset.UtcNow, _zone ?? TimeZoneInfo.Local);
  ```
- **Suggested fix:**
  When `_zone is null`, call `TimeZoneInfo.ClearCachedData()` at the start of `Update` (before the `changedPaths.Count == 0` early return) and compare `TimeZoneInfo.Local.Id` against the last zone ID applied (stored on the scanner or read cheaply from `settings`). Only take the empty-`changedPaths` early return when the zone ID has not changed (and the index file exists). Also refresh `TimeZoneInfo.ClearCachedData()` in `ReadReport` when `_zone is null`.
- **Unit test:**
  `Update_WhenChangedPathsIsEmptyAndZoneChanged_RebucketsStoredUsage` (in `NativeCostScannerArchiveTests` or `NativeCostScannerTests`): index usage in UTC+9, create a scanner for UTC+10 over the same index, call `Update(Roots, changedPaths: [])`, and assert `ReadReport` reflects the new time zone's day boundaries.

---

## 12. Incremental `.jsonl.zst` handling ignores coexisting plain logs, skips file identity checks on archive replacement, and lacks a decompressed size cap

- **Severity:** medium
- **Location:** `Services/Native/Cost/NativeCostScanner.cs:102-126, 214-244`
- **Concrete scenario:**
  1. Plain `rollout-a.jsonl` is indexed (10,000 bytes). While it still exists on disk, `rollout-a.jsonl.zst` is created (or updated) and `CostLogWatcher` sends `changedPaths = [".../rollout-a.jsonl.zst"]`. The `changedPaths` loop does not check whether the plain `.jsonl` exists, so it violates the "plain file wins" rule, opens `.jsonl.zst`, and overwrites the `files` entry with the archive's `Length`/`Created`/`FileId` (and if the archive is an older shorter snapshot, line 237 deletes the plain log's unkeyed usage). On the next full listing, the plain file wins again and re-reads from offset 0.
  2. If `rollout-a.jsonl.zst` is replaced by a different archive at the same path whose decompressed length is `>= old.Offset`, lines 214-244 skip `old.Offset` bytes without checking `Created` or `FileId`, landing mid-line in the new archive with a stale `CodexCursor` (wrong model, tier, and cumulative token baseline).
  3. `ReadLines(plain, indexed, long.MaxValue, ...)` imposes no decompressed byte limit on `ZstdSharp.DecompressionStream`, so a corrupt or high-ratio `.jsonl.zst` archive can burn CPU and hold `_costGate` and the cross-process `Mutex` until the 5-minute timeout.
- **Why it happens:**
  In `Services/Native/Cost/NativeCostScanner.cs:110-115`, `IsLog(path, provider)` offers the `.zst` directly without checking whether `path[..^CompressedSuffix.Length]` exists.
  In `Services/Native/Cost/NativeCostScanner.cs:214-234`:
  ```csharp
  if (input.Entry.Compressed)
  {
      generation = old?.Generation ?? 0;
      var indexed = old?.Offset ?? 0;
      var cursor = old?.Cursor ?? default;
      ...
      result = ReadLines(plain, indexed, long.MaxValue, buffer, parser,
          (evt, offset) => Accept(evt, offset, current), cancellationToken);
  ```
  Unlike the plain-file branch (lines 247-248), the compressed branch never inspects `old.Created` or `old.FileId` and passes `long.MaxValue` as the length bound.
- **Suggested fix:**
  In the `changedPaths` branch, if `IsCompressed(path)` and `File.Exists(path[..^CompressedSuffix.Length])`, skip the `.zst` (or offer the plain file instead). Track whether the stored `files` row came from a compressed file (or check `FileId`/`Created` when the previous row was already compressed) so replaced archives reset from offset 0. Cap decompressed bytes per archive (e.g., 512 MB or a configurable limit) and skip oversized archives with a warning.
- **Unit test:**
  `Update_WhenChangedPathsNamesArchiveWhilePlainLogExists_PrefersPlainLog` (in `NativeCostScannerArchiveTests`): index `rollout-a.jsonl` (100 tokens), create `rollout-a.jsonl.zst` (40 tokens) while keeping `rollout-a.jsonl`, call `Update(Roots, changedPaths: [plain + ".zst"])`, and assert total tokens remain 100 and a subsequent full listing reads 0 files.

---

## 13. `CostIndexDatabase` uses `CallingConvention.Cdecl` for Windows inbox `winsqlite3.dll`, which is `__stdcall` on x86 (unverified on x86 runtime)

- **Severity:** medium
- **Location:** `Services/Native/Cost/CostIndexDatabase.cs:71-99`, `CodexBarWin.csproj:8-9`
- **Concrete scenario:**
  `CodexBarWin.csproj` targets `x86`, `x64`, and `ARM64` (`<Platforms>x86;x64;ARM64</Platforms>`). On Windows x86, the inbox `winsqlite3.dll` exports `sqlite3_*` functions using `__stdcall` (`SQLITE_STDCALL` in the Windows SDK `winsqlite3.h`), not `__cdecl`. Calling those exports with `CallingConvention.Cdecl` on x86 corrupts the stack pointer on the first SQLite call and crashes the app. Because unit tests run on `-p:Platform=x64` (where `Cdecl` and `StdCall` use the same x64 calling convention), this bug is hidden in CI. (Marked unverified at runtime because this review is read-only and no x86 binary was built or executed.)
- **Why it happens:**
  `Services/Native/Cost/CostIndexDatabase.cs:75-98` declares every `winsqlite3.dll` import with `CallingConvention.Cdecl`, for example line 75:
  ```csharp
  [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
  internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
  ```
- **Suggested fix:**
  Change `CallingConvention.Cdecl` to `CallingConvention.StdCall` on all 12 P/Invoke methods in `CostIndexDatabase.Native` (on x64 and ARM64, `StdCall` uses the standard platform ABI identically to `Cdecl`).
- **Unit test:**
  `CostIndexDatabase_NativeImports_UseStdCallForWinSqlite3` (in `NativeCostScannerTests`): use reflection on `CostIndexDatabase`'s nested `Native` class (or verify `DllImportAttribute.CallingConvention`) to assert every `winsqlite3.dll` import uses `CallingConvention.StdCall` (and run the scanner tests on x86 in CI if x86 remains a supported platform).

---

## 14. Transient `IOException` in `CostPriceStore.GetPricing()` poisons `_loadedFrom`, permanently ignoring custom or refreshed prices

- **Severity:** medium
- **Location:** `Services/Native/Cost/CostPriceStore.cs:51-66, 151-169`
- **Concrete scenario:**
  The user edits and saves `custom-pricing.json` (or `RefreshIfDueAsync` moves `model-prices.json` into place), changing the file's `(LastWriteTimeUtc.Ticks ^ Length)` stamp to `S2`. `GetPricing()` computes `stamps` with `S2` on line 53, then calls `ReadFile` while the editor or antivirus briefly holds an exclusive lock. `ReadFile` catches `IOException` and returns `null`, so `GetPricing()` falls back to `CostCustomPricing.Empty` (or the bundled catalog) and stores `_loadedFrom = stamps` (with `S2`). Every future call to `GetPricing()` sees `_loadedFrom == stamps` and returns the cached fallback indefinitely until the file is edited again.
- **Why it happens:**
  `Services/Native/Cost/CostPriceStore.cs:53-65`:
  ```csharp
  var stamps = (Stamp(CatalogPath), Stamp(CustomPath));
  lock (_stateGate)
  {
      if (_pricing is not null && _loadedFrom == stamps) return _pricing;
      ...
      var custom = ReadFile(CustomPath) is { } overlay ? CostCustomPricing.Parse(overlay) : CostCustomPricing.Empty;
      _loadedFrom = stamps;
      return _pricing = new CostPricing(catalog, CostPriceOverlay.Bundled, custom);
  }
  ```
  And `Services/Native/Cost/CostPriceStore.cs:161-169` collapses transient `IOException` into the same `null` return value as a missing file:
  ```csharp
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
  ```
- **Suggested fix:**
  Have `ReadFile` indicate whether a read failed due to `IOException`/`UnauthorizedAccessException` (for example, `TryReadFile(path, out byte[]? bytes, out bool transientError)`). If either file had a transient read error, do not record its stamp in `_loadedFrom` (store a sentinel such as `long.MinValue` or skip updating `_loadedFrom`) so the next `GetPricing()` call retries once the lock clears.
- **Unit test:**
  `GetPricing_WhenCustomPricingFileIsTemporarilyLocked_RetriesOnNextCall` (in `CostPriceStoreTests`): write `custom-pricing.json`, open it with `FileShare.None`, call `GetPricing()` (assert fallback pricing), close the lock, call `GetPricing()` again without modifying the file, and assert the custom rates are now loaded.

---

## 15. Concurrent `ClaudeNativeClient.FetchAsync` calls lack deduplication and can race on 429 cooldown persistence

- **Severity:** medium
- **Location:** `Services/Native/ClaudeNativeClient.cs:90-183`
- **Concrete scenario:**
  Two callers invoke `ClaudeNativeClient.FetchAsync` concurrently while the cache is cold or expired (for example, `GetUsageAsync("claude")` overlapping with `GetAllUsageStreamAsync()`). Both pass the cache and `_blockedUntil` checks on lines 95-105 and both send live GET requests to `https://api.anthropic.com/api/oauth/usage`, wasting Anthropic's tight rate limit. If both receive HTTP 429 with different `Retry-After` headers (e.g., 10 min and 30 min), `_cooldownStore.SaveAsync(blockedUntil)` runs outside `_gate` (line 160), so the 10-minute call can complete its write after the 30-minute call and overwrite `claude-cooldown.json` with the shorter cooldown (or a 429 call can persist a cooldown after a concurrent 200 OK cleared `_blockedUntil`).
- **Why it happens:**
  `Services/Native/ClaudeNativeClient.cs:95-106` reads `_lastGood` and `_blockedUntil` under `_gate`, releases `_gate`, and performs the HTTP call and `_cooldownStore.SaveAsync` unlocked (line 160):
  ```csharp
  lock (_gate) { if (candidate > _blockedUntil) _blockedUntil = candidate; blockedUntil = _blockedUntil; }
  _logger.LogWarning("Claude usage endpoint answered 429; cooling down until {Until:o}", blockedUntil);
  if (_cooldownStore != null) await _cooldownStore.SaveAsync(blockedUntil, CancellationToken.None).ConfigureAwait(false);
  ```
- **Suggested fix:**
  Guard the live fetch path with a `SemaphoreSlim(1, 1)` (with fast-path cache/cooldown check before acquiring and re-check after acquiring), so concurrent refreshes share the cache/cooldown result and cooldown persistence is serialized with `_blockedUntil` updates.
- **Unit test:**
  `FetchAsync_WhenCalledConcurrentlyOnColdCache_MakesOnlyOneHttpRequest` (in `ClaudeNativeClientTests`): block the first HTTP response on a `TaskCompletionSource`, start two `FetchAsync()` calls concurrently, release the response, and assert `handler.Requests.Count == 1` and both callers receive `NativeUsageStatus.Ok`.

---

## 16. `CostPriceCatalog.ParseLiveAsync` materializes all ~80+ providers in memory before filtering to Anthropic and OpenAI

- **Severity:** low
- **Location:** `Services/Native/Cost/CostPriceCatalog.cs:119-141, 385-392`
- **Concrete scenario:**
  During the daily refresh of `https://models.dev/api.json`, `ParseLiveAsync` deserializes the entire response into a `Dictionary<string, LiveProvider>` where every provider's `Models` dictionary holds cloned `JsonElement` trees for every model across all ~80+ providers on models.dev, and only discards non-Anthropic/OpenAI providers afterwards. On a RAM-constrained laptop (especially with the 64 MB `MaxDownloadBytes` cap), this creates an avoidable managed-heap spike, contradicting the method's doc comment ("Reads the full models.dev catalog from a stream without holding it in memory").
- **Why it happens:**
  `Services/Native/Cost/CostPriceCatalog.cs:128-136`:
  ```csharp
  var live = await JsonSerializer.DeserializeAsync(stream,
      CostPriceJsonContext.Default.DictionaryStringLiveProvider, token).ConfigureAwait(false);
  ...
  foreach (var (name, provider) in live)
  {
      var id = name.Trim().ToLowerInvariant();
      if (Array.IndexOf(KeptProviders, id) < 0 || provider?.Models is null) continue;
  ```
- **Suggested fix:**
  Implement a small custom `JsonConverter` (or chunked `Utf8JsonReader` pass) over the top-level object that immediately calls `reader.Skip()` on any provider key other than `"anthropic"` and `"openai"`, only parsing the `models` objects of those two providers.
- **Unit test:**
  `ParseLiveAsync_WhenOtherProvidersContainInvalidOrHugePayloads_SkipsThemWithoutAllocating` (in `CostPriceCatalogTests`): include unrelated providers with non-object values (e.g., `"other": [1, 2, 3]`, which currently causes `DeserializeAsync<Dictionary<string, LiveProvider>>` to fail the entire catalog with `JsonException`!) alongside valid `anthropic` and `openai` providers, and assert `ParseLiveAsync` succeeds and returns both kept providers. (Note: this also catches a live resilience bug where a non-object top-level metadata key in `models.dev/api.json` would fail deserialization of the whole dictionary!)

---

## 17. `NativeHttp.HostAllowListHandler` does not override synchronous `Send` and does not restrict port or userinfo

- **Severity:** low
- **Location:** `Services/Native/NativeHttp.cs:77-86`
- **Concrete scenario:**
  `HostAllowListHandler` is a `public` security boundary class, but:
  1. Calling synchronous `httpClient.Send(request)` invokes `DelegatingHandler.Send`, which forwards straight to `InnerHandler.Send` and completely bypasses `SendAsync` and the host allowlist.
  2. Calling `https://user:pass@api.anthropic.com:8443/api/oauth/usage` passes `uri.Scheme == "https"` and `AllowedHosts.Contains(uri.Host)` even though the port is non-default (8443) and credentials appear in `UserInfo`.
- **Why it happens:**
  `Services/Native/NativeHttp.cs:79-85`:
  ```csharp
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
      var uri = request.RequestUri;
      if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
          throw new InvalidOperationException($"Native usage request to a host outside the allow list: {uri?.Host}");
      return base.SendAsync(request, cancellationToken);
  }
  ```
  `Send(HttpRequestMessage, CancellationToken)` is not overridden.
- **Suggested fix:**
  Add a `Validate(HttpRequestMessage request)` method that checks `uri != null && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && AllowedHosts.Contains(uri.Host)` and call it from both `Send` and `SendAsync`.
- **Unit test:**
  `HostAllowListHandler_WhenSynchronousSendOrNonDefaultPort_Throws` (in `NativeCredentialHelpersTests`): assert `client.Send(new HttpRequestMessage(HttpMethod.Get, "https://example.com/"))` and `await client.GetAsync("https://api.anthropic.com:8443/")` both throw `InvalidOperationException`.

---

## 18. `CostIndexDatabase.Statement.Dispose()` can double-finalize native handles, and `App.Services` never disposes native singletons on exit

- **Severity:** low
- **Location:** `Services/Native/Cost/CostIndexDatabase.cs:49-69`, `Services/Native/Cost/CostIndex.cs:251-256`, `Services/Native/NativeCostService.cs:133`, `App.xaml.cs:25, 148-152, 268-274`
- **Concrete scenario:**
  Calling `Statement.Dispose()` or `CostIndex.Dispose()` more than once invokes `Native.sqlite3_finalize(handle)` twice on the same native `sqlite3_stmt*` pointer, causing a native double-free / heap corruption crash. Separately, `App.xaml.cs` never disposes `App.Services` when exiting (leaving `NativeCostService`'s `FileSystemWatcher` instances and `NativeUsageService`'s `HttpClient` undisposed until OS process teardown), and `NativeCostService.Dispose()` does not dispose `_gate`.
- **Why it happens:**
  `Services/Native/Cost/CostIndexDatabase.cs:68`:
  ```csharp
  public void Dispose() => Native.sqlite3_finalize(handle);
  ```
  `Services/Native/Cost/CostIndex.cs:251-256`:
  ```csharp
  public void Dispose()
  {
      _save?.Dispose();
      _add?.Dispose();
      _remove?.Dispose();
  }
  ```
- **Suggested fix:**
  Store `private IntPtr _handle = handle;` in `Statement` and call `var h = Interlocked.Exchange(ref _handle, IntPtr.Zero); if (h != IntPtr.Zero) Native.sqlite3_finalize(h);` in `Dispose()` (or use `SafeHandle`), and null `_save`/`_add`/`_remove` in `CostIndex.Dispose()`. Dispose `(Services as IDisposable)?.Dispose()` during `App` shutdown.
- **Unit test:**
  `CostIndex_DisposeTwice_DoesNotCrash` (in `NativeCostScannerTests`): create a `CostIndexDatabase` and `CostIndex`, prepare/execute a statement, call `index.Dispose()` twice, and assert no exception or crash occurs.

---

## 19. `CostLogWatcher` queues non-log files on create and rename, triggering unnecessary SQLite validation and full `files` reads

- **Severity:** low
- **Location:** `Services/Native/Cost/CostLogWatcher.cs:77-85, 132-135`
- **Concrete scenario:**
  Claude Code and Codex create and rename non-log files inside `%USERPROFILE%\.claude\projects` and `%USERPROFILE%\.codex\sessions` (for example, temporary files, memory `.md` files, `.json` metadata, and tool output `.txt` files). `CostLogWatcher` adds every created or renamed path to `_changed` without checking whether it is a directory or a log file. On the next cost scan, `plan.ChangedPaths` is non-empty, defeating the zero-I/O early return in `NativeCostScanner.Update`: the scanner acquires the named mutex, opens `index.sqlite`, runs `PRAGMA quick_check(1)`, loads the entire `files` table into memory, opens a write transaction for `SetZone`, checks the non-log paths, discovers 0 logs, and closes SQLite. During heavy sessions creating >2,048 non-log files, this also forces an unnecessary full directory walk.
- **Why it happens:**
  `Services/Native/Cost/CostLogWatcher.cs:81, 85`:
  ```csharp
  if (NativeCostScanner.IsLog(path, provider) || created) Mark(path);
  ...
  internal void NotifyRenamed(string newPath) => Mark(newPath);
  ```
- **Suggested fix:**
  For `created` and `renamed` events on paths where `!NativeCostScanner.IsLog(path, provider)`, only call `Mark(path)` when the path has no non-log file extension or `Directory.Exists(path)` is true (or filter out known non-log files before calling `OpenForUpdate` in `NativeCostScanner.Update`, i.e., resolve/filter `changedPaths` to existing log files or directories before opening SQLite).
- **Unit test:**
  `Update_WhenChangedPathsContainsOnlyNonLogFiles_DoesNotOpenIndex` (in `NativeCostScannerArchiveTests`): call `Update(Roots, changedPaths: [Path.Combine(Claude, "notes.md"), Path.Combine(Codex, "temp.tmp")])` on a fresh index directory and assert `Directory.Exists(indexDir)` remains `false`.

---

## 20. Remaining minor edge cases and hygiene items

- **Severity:** low
- **Locations & concrete scenarios:**
  1. **Dead refresh-secret extraction & malformed-blob classification (`Services/Native/AntigravityCredentials.cs:102, 116-127, 147, 158-159`)**: Token refresh was removed (`AntigravityNativeClientTests.cs:141`), yet `AntigravityCredentialParser.Parse(..., bool includeRefreshFields = false)` still retains code to materialize `refresh_token`, `client_id`, and `client_secret` into managed strings. In addition, line 124 catches `JsonException` and returns `null`, which `AntigravityNativeClient` maps to `SignInMissing` (dropping last-good and flagging `AuthRequired = true`) rather than `Failed` (unlike `ClaudeNativeClient` and `CodexNativeClient`).
     - *Fix:* Remove `includeRefreshFields` and the unused `RefreshToken`/`ClientId`/`ClientSecret` properties; let `JsonException` propagate (or return a distinct parse-error status) so `AntigravityNativeClient` maps corrupt blobs to `Failed` and preserves last-good.
     - *Unit test:* `FetchAsync_WhenCredentialBlobIsMalformedJson_ReturnsFailedAndPreservesLastGood` (in `AntigravityNativeClientTests`): return a good reading, switch the fake source to `"{broken"`, fetch again, and assert `Status == NativeUsageStatus.Failed` and `Cached == true`.
  2. **Unbounded file read in `ClaudeCooldownFile.Load()` (`Services/Native/ClaudeCooldownStore.cs:40`)**: `File.ReadAllBytes(FilePath)` reads the entire file without a size cap (unlike `NativeCredentialFile.MaxBytes` and `CostPriceStore.MaxLocalFileBytes`). A corrupted/bloated `claude-cooldown.json` could allocate arbitrary RAM on startup.
     - *Fix:* Check `new FileInfo(FilePath).Length <= 64 * 1024` before reading.
     - *Unit test:* `Load_WhenCooldownFileExceedsSizeLimit_ReturnsNullWithoutReading` (in `ClaudeCooldownStoreTests`): write a >64 KB file and assert `Load()` returns `null`.
  3. **Unchecked `int` narrowing of `LimitWindowSeconds` and `UsedPercent` (`Services/Native/CodexNativeClient.cs:223`, `Models/AppJsonSerializerContext.cs:190`)**: `(int)(seconds / 60)` overflows to a negative `WindowMinutes` if `LimitWindowSeconds > int.MaxValue * 60L`, and `(int)UsedPercent!.Value` overflows to `int.MinValue` if `UsedPercent > int.MaxValue`.
     - *Fix:* Clamp `seconds / 60` to `int.MaxValue` and clamp `UsedPercent` before casting to `int`.
     - *Unit test:* `Map_WhenLimitWindowSecondsExceedsIntMaxMinutes_ClampsInsteadOfOverflowing` (in `CodexNativeClientTests`): pass `limit_window_seconds: 9223372036854775807` and assert `WindowMinutes > 0`.
  4. **Unnormalized `changedPaths` traversal check (`Services/Native/Cost/NativeCostScanner.cs:105`)**: `changed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)` does not normalize `changed` with `Path.GetFullPath` first, so a caller passing `root\..\outside.jsonl` bypasses the root-containment check.
     - *Fix:* Normalize `var full = Path.GetFullPath(changed);` before `full.StartsWith(prefix, ...)`.
     - *Unit test:* `Update_WhenChangedPathUsesParentTraversal_IgnoresFileOutsideRoot` (in `NativeCostScannerArchiveTests`): pass `Path.Combine(Codex, "..", "elsewhere.jsonl")` and assert `DiscoveredFiles == 0`.
  5. **Codex date-suffix stripping without digit validation & non-deterministic dated catalog lookup (`Services/Native/Cost/CostPricing.cs:92-93`, `Services/Native/Cost/CostPriceCatalog.cs:62-64`)**: `CostPricing.Normalize` strips any 11-character `-xxxx-xx-xx` suffix on Codex models even when non-digits appear (e.g., `-fast-v1-ab`), and `CostPriceCatalog.Find` returns the first matching dated snapshot in arbitrary `Dictionary` order when multiple dated entries exist for the same model.
     - *Fix:* Reuse digit checks (`IsDateSuffix`) in `CostPricing.Normalize`, and in `CostPriceCatalog.Find` pick the lexicographically greatest (latest) dated ID deterministically.
     - *Unit test:* `Normalize_WhenCodexModelHasNonDateHyphenatedSuffix_DoesNotStripIt` (in `CostPricingTests`): assert `CostPricing.Normalize("gpt-6-fast-v1-ab", CostProvider.Codex) == "gpt-6-fast-v1-ab"`.
  6. **Duplicate bucket IDs in `AntigravityNativeClient.MapQuotaSummary` (`Services/Native/AntigravityNativeClient.cs:239-247`)**: `extras` iterates `known` without deduplicating by `Id` (unlike `ClaudeNativeClient.cs:233` and `CodexNativeClient.cs:233`), so duplicate non-Gemini bucket IDs in a response produce duplicate extra windows. Also `ClaudeNativeClient.cs:145` uses `TimeZoneInfo.Local` without ever refreshing cached time zone data if the user changes time zones while cost scans are disabled.
     - *Fix:* Deduplicate `extras` by `Id` using a `HashSet<string>(StringComparer.Ordinal)`.
     - *Unit test:* `MapQuotaSummary_WhenDuplicateExtraBucketIdsReturned_DeduplicatesExtras` (in `AntigravityNativeClientTests`): include two `3p-5h` buckets and assert `ExtraWindows` contains only one `antigravity-quota-summary-3p-5h` entry.

---

## Checked and Found Sound

- **Read-only credential access:** None of `ClaudeNativeClient`, `CodexNativeClient`, `AntigravityNativeClient`, `WindowsCredentialManagerSource`, or `NativeCredentialFile` ever opens a credential file for writing or imports `CredWriteW` / `CredDeleteW`. `AntigravityNativeClient` never calls `onboardUser` or `oauth2.googleapis.com`, and `NativeHttp.AllowedHosts` excludes `oauth2.googleapis.com`.
- **No tokens in logs, URLs, DTOs, or `ToString()`:** `ClaudeCredentials`, `CodexCredentials`, and `AntigravityCredentials` all override `ToString()` to omit access tokens, refresh tokens, client secrets, account IDs, and emails. All native usage clients and `NativeUsageService` log only `ex.GetType().Name` and HTTP status codes, never exception messages, headers, or file paths. Tokens are sent only in the `Authorization: Bearer` header, never in query strings.
- **Credential buffer zeroing and sharing:** `SensitiveBuffer.Dispose()` calls `CryptographicOperations.ZeroMemory(array)` across the entire rented array before returning it to `ArrayPool<byte>.Shared`, and `NativeJwt.WithPayload` zeroes its rented base64url decode buffer in `finally`. `NativeCredentialFile` opens files with `FileShare.ReadWrite | FileShare.Delete` so owning apps are never locked out.
- **No redirects or cookies:** Both `NativeHttp.CreateClient` and `CostPriceStore`'s `SocketsHttpHandler` explicitly disable `AllowAutoRedirect` and `UseCookies`, preventing bearer token forwarding or unintended cookie persistence.
- **Provider input validation:** All public entry points (`NativeUsageService`, `NativeCostService`, `NativeUsageDtoMapper`, `CodexBarService`, `AppSettings.Normalize`) validate and normalize provider IDs via `ProviderConstants.ValidateAndNormalize` / `IsValidProvider`.
- **SQLite injection safety and concurrency:** `CostIndex` uses parameterized bindings (`sqlite3_bind_int64`, `sqlite3_bind_text`, `sqlite3_bind_null`) for all dynamic values (no string interpolation in SQL). `NativeCostScanner` serializes cross-process index updates with a session-scoped named `Mutex`, handles `AbandonedMutexException`, uses `BEGIN IMMEDIATE` / `COMMIT` / `ROLLBACK` around batches, and serializes parallel worker writes to `CostIndex` via `lock (index)`.
- **Partial JSONL lines and UTF-8 BOM:** `NativeCostScanner.ReadLines` only advances `CompleteOffset` on `\n`, strips UTF-8 BOM only at offset 0, and skips oversized lines (>512 KB) cleanly across buffer boundaries without retaining unbounded memory.
- **Symlink/reparse-point skipping in directory walks:** `NativeCostScanner.EnumerateLogs` sets `AttributesToSkip = FileAttributes.ReparsePoint`, and the `changedPaths` branch skips files and directories carrying `FileAttributes.ReparsePoint`.
- **Event storm memory bounding:** `CostLogWatcher.Mark` caps `_changed` at `MaxChangedPaths` (2,048), clears the set, and switches to `_fullListingNeeded = true` so watcher storms cannot grow memory unboundedly.
- **License compliance:** `ZstdSharp.Port` (MIT) and Windows inbox `winsqlite3.dll` (public domain) introduce no GPL dependencies.
- **P/Invoke struct layouts:** `WindowsCredentialManagerSource.Credential` matches Win32 `CREDENTIALW` on both x64 and x86, and `NativeCostScanner.FileInformation` matches `BY_HANDLE_FILE_INFORMATION`.
