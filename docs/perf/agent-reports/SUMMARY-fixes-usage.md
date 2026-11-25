# Native usage readers: fixes-usage

Branch `perf/native-fixes-usage` (nothing committed). All paths are relative to the repo root. Tests live in `CodexBarWin.Tests.Unit/Services/Native/`.

## Verification (run in this worktree)

| Command | Result |
|---|---|
| `dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false` | Build succeeded, 0 errors, 1 warning (NETSDK1198, publish profile, already present) |
| `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64` | **Passed! Failed: 0, Passed: 2158, Skipped: 0, Total: 2158** |
| `dotnet build tools/NativeProbe/NativeProbe.csproj -c Release` | Build succeeded, 0 warnings, 0 errors |

## Changes

### 1. An expired token is not a sign-out
- Files: `Services/Native/NativeUsageDtoMapper.cs`
- Only `SignInMissing` and `Unauthorized` set `AuthRequired`/`AuthError` and the `"auth"` error kind.
- `SignInExpired` with a reading: cached row (`Cached = true`, `CacheUpdatedAt` = the reading's `UpdatedAt`), no error, no `AuthRequired`.
- `SignInExpired` without a reading: error with kind `"provider"`, no `code`, message `Sign-in expired; waiting for the provider's own app to renew it` (public const `SignInExpiredMessage`). It does not trip `AuthFailureDetector.IsAuthFailure`.
- Tests (`NativeUsageDtoMapperTests`):
  - `ToDto_WhenSignInExpiredWithoutReading_ReturnsQuietNonAuthError`
  - `ToDto_WhenSignInExpiredWithoutReading_DoesNotTripAuthFailureDetector` (claude, codex, gemini)
  - `ToDto_WhenSignInExpiredWithReading_EmitsCachedRowWithoutErrorOrAuth`
  - `ToDto_WhenSignInExpiredWithReadingNotMarkedCached_StillEmitsCachedRowAtReadingTime`
  - `ToDto_WhenSignedOutOrRejectedWithoutReading_FlagsAuthRequired` (SignInMissing, Unauthorized)
  - `ToDto_WhenSignedOutOrRejectedWithCachedReading_KeepsReadingAndFlagsAuthRequired`
  - The old `ToDto_WhenSignInExpiredWithoutReading_FlagsAuthRequired` was replaced.

### 2. Codex and Antigravity keep their last good reading
- Files: new `Services/Native/NativeLastGood.cs` (`NativeLastGood`, `NativeLastGoodStore`), `CodexNativeClient.cs`, `AntigravityNativeClient.cs`, `ClaudeNativeClient.cs`.
- `HasPastReset` moved out of Claude into `NativeLastGood`. `FromCacheOr` is now shared: it serves the reading cached, and any window with `ResetsAt <= now` becomes 0% used with `ResetsAt` (and the stale reset text) cleared.
- `FetchAsync` in Codex and Antigravity now wraps the old logic (`FetchCoreAsync`) and passes the result through `NativeLastGoodStore.Apply`.
- The last good reading is served for `SignInExpired`, `Failed` (network, timeout, 5xx, unreadable response) and `RateLimited`.
- It is not served for `SignInMissing` or `Unauthorized`; these still report the auth problem with no reading.
- Claude keeps serving it for every failure, as before, and now also clears past-reset windows.
- Tests:
  - `NativeLastGoodTests`: `ClearPastResets_WhenWindowsHavePassed_ZeroesOnlyThoseWindows`, `ClearPastResets_WhenResetIsExactlyNow_ZeroesTheWindow`, `ClearPastResets_WhenNothingHasReset_ReturnsSameReading`, `FromCacheOr_WhenNoLastGood_ReturnsFailureUnchanged`, `FromCacheOr_WhenLastGood_ServesItCachedWithFailureFields`, `ServesLastGood_WhenStatusGiven_ServesOnlyExpiredAndTransient`, `Apply_WhenFreshReadingThenFailure_ServesRememberedReading`, `Apply_WhenFailureBeforeAnyReading_ReturnsFailure`, `Apply_WhenSignedOutOrRejected_DoesNotServeRememberedReading`, `Apply_WhenOkWithoutUsage_DoesNotReplaceRememberedReading`
  - `CodexNativeClientTests`: `FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached`, `FetchAsync_WhenTransientHttpFailureAfterGoodReading_ServesLastGoodReadingAsCached` (500, 502, 429), `FetchAsync_WhenNetworkErrorAfterGoodReading_...`, `FetchAsync_WhenTimeoutAfterGoodReading_...`, `FetchAsync_WhenUnauthorizedAfterGoodReading_DoesNotServeLastGoodReading`, `FetchAsync_WhenFailureBeforeAnyGoodReading_ReturnsFailureWithoutCachedRow`, `FetchAsync_WhenServedLastGoodHasPastReset_ReportsThatWindowAsUnused`
  - `AntigravityNativeClientTests`: the same set, plus `FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached`
  - `ClaudeNativeClientTests`: `FetchAsync_WhenTokenExpiredAfterGoodReading_ServesLastGoodReadingAsCached`, `FetchAsync_WhenServedLastGoodHasPastReset_ReportsThatWindowAsUnused`, `FetchAsync_WhenTokenExpiredWithoutGoodReading_ReturnsSignInExpiredOnly`

### 3. Records never print tokens
- Files: `ClaudeNativeClient.cs` (`ClaudeCredentials.ToString`), `CodexNativeClient.cs` (`CodexCredentials.ToString`).
- Claude prints `ExpiresAt` and `SubscriptionType`. Codex prints `ExpiresAt` and `LastRefresh`. The access token is never printed. Codex also leaves out `AccountId`.
- Tests: `ClaudeNativeClientTests.ToString_WhenCredentialsHaveToken_OmitsTokenValue`, `ClaudeNativeClientTests.ToString_WhenParsedFromFile_OmitsEveryTokenInTheFile`, `CodexNativeClientTests.ToString_WhenCredentialsHaveTokens_OmitsEveryTokenValue`.

### 4. One retry for a credential file caught mid-rewrite
- Files: `Services/Native/NativeCredentialHelpers.cs`.
- `NativeCredentialFile.ReadAsync` now checks the first read for one complete JSON document (BOM-tolerant, trailing commas and comments allowed, as in the parsers).
- If the content is empty or invalid, it disposes the buffer (which zeroes it), waits `RetryDelay` (75 ms) and reads once more. The second read is returned either way.
- A new optional `delayBeforeRetry` parameter lets tests replace the wait.
- Tests (`NativeCredentialHelpersTests`):
  - `ReadAsync_WhenFileEmptyThenRewritten_RetriesOnceAndReadsNewContent`
  - `ReadAsync_WhenFileTruncatedMidWriteThenCompleted_RetriesOnceAndReadsNewContent`
  - `ReadAsync_WhenStillInvalidAfterRetry_RetriesOnlyOnceAndReturnsSecondRead`
  - `ReadAsync_WhenValidJson_DoesNotRetry`
  - `ReadAsync_WhenJsonHasByteOrderMark_DoesNotRetry`
  - `ReadAsync_WhenMissing_DoesNotRetry`
  - `ReadAsync_WhenInvalidAndNoDelayOverride_WaitsAboutRetryDelay`

### 5. Default profiles only
- Files: `ClaudeNativeClient.cs` (`ClaudeNativeOptions`), `CodexNativeClient.cs` (`CodexNativeOptions`).
- The `CLAUDE_CONFIG_DIR` and `CODEX_HOME` lookups are gone.
- Defaults are `%USERPROFILE%\.claude\.credentials.json` and `%USERPROFILE%\.codex\auth.json`. An explicit options path still wins. Doc comments updated.
- Tests: `ClaudeNativeClientTests.ResolveCredentialsPath_WhenClaudeConfigDirSet_IgnoresIt`, `ClaudeNativeClientTests.ResolveCredentialsPath_WhenExplicitPathGiven_UsesIt`, `CodexNativeClientTests.ResolveAuthPath_WhenCodexHomeSet_IgnoresIt`, `CodexNativeClientTests.ResolveAuthPath_WhenExplicitPathGiven_UsesIt`. They set the env var to a fake temp path and restore it afterwards.

### 6. Opt-in Google token refresh removed
- Deleted: `Services/Native/AntigravityTokenRefresher.cs`, `Services/Native/AntigravityOAuthClientDiscovery.cs`, `CodexBarWin.Tests.Unit/Services/Native/AntigravityOAuthClientDiscoveryTests.cs`.
- Removed `AntigravityNativeOptions.AllowInMemoryRefresh`, the `_refresher`/`_refreshed` fields and logic, and the constructor's `refresher` parameter.
- Removed `GoogleTokenResponse` and its `[JsonSerializable]` entry from `NativeJsonContext.cs`.
- Removed `oauth2.googleapis.com` from `NativeHttp.AllowedHosts`.
- An expired Antigravity token now returns `SignInExpired`, with the last good reading per change 2.
- `tools/NativeProbe/Program.cs`: removed the `discover` mode and `Discover()`, and updated the header and usage text.
- Removed the two refresh tests from `AntigravityNativeClientTests` and renamed the expired-token test to `FetchAsync_WhenTokenExpired_ReturnsSignInExpiredWithoutCalling`.
- Tests added:
  - `AntigravityNativeClientTests.Options_WhenInspected_HasNoRefreshSwitch`
  - `NativeCredentialHelpersTests.AllowedHosts_WhenInspected_HasNoGoogleTokenEndpoint`
  - `NativeCredentialHelpersTests.HostAllowListHandler_WhenGoogleTokenHost_Throws`

## Decisions and open points
- **Last-good scope.** Codex and Antigravity serve the last good reading for `SignInExpired` and for `Failed` and `RateLimited` only, as the task listed. `SignInMissing` and `Unauthorized` do not serve it. Claude's existing behavior is unchanged: it serves for every failure.
- **Still-invalid file after the retry.** The second read is returned as is, so the parsers decide. A truncated or garbage file still ends as "could not be read" (`Failed`, which keeps the last good reading) and not as `SignInMissing`. I chose this so a long rewrite does not flip a provider to signed out.
- **Past-reset windows.** The reset text (`ResetDescription`) is cleared along with `ResetsAt`, so the old "Resets in …" text does not stay on a window that has reset.
- **Left in place.** `AntigravityCredentialParser.Parse(includeRefreshFields: true)` and its test remain. The task did not list them, and nothing calls them now, so they could go in a follow-up.
- **Untouched.** `Services/Native/Cost/`, `docs/`, `SPIKE-*.md` and the `perf/antigravity-refresh-optin` branch. The WSL notes in `tools/wsl/README.md` and `INSTALL.md` mention `CODEXBAR_CODEX_HOME` for the WSL shim and have nothing to do with these readers.
- **Process.** Nothing was committed or pushed, and no real credential files were read. Commands ran in the foreground, apart from the long builds and test runs the tool moved to the background and I waited on. No stray processes were left (`-nodeReuse:false`).
- **Visual work.** None.
