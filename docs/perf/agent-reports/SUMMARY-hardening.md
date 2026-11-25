# Native backend hardening: summary

Branch `perf/hardening` (worktree `C:\Users\ThomasFramework\worktrees\CodexBarWin\hardening`). The work covers the 20 findings in `NEON-FINDINGS.md`, part B (Debug test hooks and a fake provider server) and part C (Claude sign-in renewal). No XAML, view, control, window or view-model file was changed. (Saved by the coordinator from the agent's final message: the harness blocks report files from subagents.)

## Results

Every test run had `CODEXBARWIN_SKIP_WSL_INTEGRATION=1` set.

| Check | Result |
| --- | --- |
| Release app build (`-p:PublishReadyToRun=false -nodeReuse:false`) | Pass: 0 errors, 1 warning (NETSDK1198, `win-x64.pubxml` not found) |
| Debug app build | Pass, same warning |
| `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64` | 2513 total: 2503 passed, **6 failed**, 4 skipped |
| Same suite with `-c Release` (CI's test.yml configuration; compiles the `#if !DEBUG` tests) | 2488 total: 2478 passed, **6 failed**, 4 skipped |
| Release `CodexBarWin.dll` searched for `CODEXBARWIN_TEST_` (UTF-16 at both byte offsets, and ASCII) | Not found. The Debug DLL does contain it, so the search works |

**The 6 failures** are `WslBatchProtocolTests.BuildScript_RealBash*`. Neither that test file nor `WslBatchProtocol.cs` was changed. With the WSL skip variable set, these tests fall back to Git Bash, where the batch script fails with `"/0.out": No such file or directory`. This branch predates 09e29f0, which makes those tests opt-in; after the merge into `perf/native-app` they skip.

**Verdicts (25 items):** 24 fixed and 1 not reproduced (finding 10). Inside fixed items: not reproduced, the whitespace part of 8 and the percentage part of 20.3; won't fix, app-exit disposal in 18 and the cached-zone label in 20.6.

## Findings

Test classes are under `CodexBarWin.Tests.Unit/Services/`. Each test was run red before its fix unless the row says otherwise.

| # | Title | Verdict | Proving test(s) | Files touched |
| --- | --- | --- | --- | --- |
| 1 | Retry-After throws; live 429 cooldown uncapped | Fixed. Retry-After never throws and is capped at 24 h (delta, HTTP date, raw seconds). Claude's live cooldown is capped like the saved one. | `NativeCredentialHelpersTests.RetryAfter_WhenHeaderIsInfinityOrOverflowsDateTimeOffset_DoesNotThrow`, `ClaudeNativeClientTests.FetchAsync_When429HasHugeRetryAfter_CapsLiveCooldownToMaxPersistedCooldown`, `CodexNativeClientTests.FetchAsync_When429HasAbsurdRetryAfter_ServesLastGoodWithBoundedRetryAfter` | `Services/Native/NativeHttp.cs`, `ClaudeNativeClient.cs` |
| 2 | Replayed last-good reading brings back windows that have reset | Fixed. When the reading is not a fresh success, windows whose reset time has passed are zeroed. | `CodexBarServiceNativeRoutingTests.GetAllUsageAsync_WhenNativeReturnsClearedPastResetReplay_PublishesZeroedWindow` | `Services/CodexBarService.cs` |
| 3 | A locked log is skipped and waits up to 6 h | Fixed. The scan result names skipped logs; the service queues them again for up to 3 scans, then leaves them to the periodic listing. | `NativeCostServiceTests.FetchCostJsonAsync_WhenWatchedFileIsTemporarilyLocked_RetriesItOnNextScan`, `..._WhenChangedFileStaysUnreadable_StopsRetryingAfterAFewScans` | `Cost/NativeCostScanner.cs` (`CostScanResult.SkippedPaths`), `Cost/CostLogWatcher.cs` (`Retry`), `NativeCostService.cs` |
| 4 | Deleted or corrupt index not rebuilt between listings | Fixed. A missing index forces a full listing; an invalid-index SQLite error while reading triggers one rebuild and one re-read. | `NativeCostServiceTests.FetchCostJsonAsync_WhenIndexIsDeletedOrCorruptedBetweenScans_RebuildsImmediately` (deleted, corrupt) | `NativeCostService.cs` |
| 5 | Claude serves last good on a missing or rejected sign-in | Fixed in the client and in `NativeLastGoodStore`; both now forget the reading. | `ClaudeNativeClientTests.FetchAsync_WhenUnauthorizedAfterGoodReading_DoesNotServeLastGoodReading`, `..._WhenSignInMissingAfterGoodReading_DoesNotServeLastGoodReading`, `NativeLastGoodTests.Apply_WhenTransientFailureFollowsMissingOrRejectedSignIn_DoesNotServeOlderReading` | `ClaudeNativeClient.cs`, `NativeLastGood.cs` |
| 6 | Unbounded HTTP response reads | Fixed. Bodies go through a 2 MiB cap after decompression; a larger body fails the read. | `FetchAsync_WhenResponseExceedsSizeLimit_FailsWithoutReadingWholeBody` in the Claude, Codex and Antigravity client tests | `NativeHttp.cs` (`ReadJsonAsync`), the three clients |
| 7 | Credential and JWT readers lose their place on object or array values | Fixed: `reader.Skip()` after every value. | `ParseCredentials_WhenKnownFieldPrecedingTokenIsObjectOrArray_StillReadsAccessToken` (Claude, Codex), `AntigravityCredentialParserTests.Parse_WhenKnownFieldPrecedingTokenIsObjectOrArray_StillReadsAccessToken`, `NativeCredentialHelpersTests.ReadChatGptAccountId_WhenTopLevelClaimIsArray_StillReadsNestedClaim` | `ClaudeNativeClient.cs`, `CodexNativeClient.cs`, `AntigravityCredentials.cs`, `NativeCredentialHelpers.cs` |
| 8 | Credential file growing mid-read; whitespace counted as complete | Growth fixed: the buffer grows up to the 4 MiB cap. Whitespace not reproduced: a whitespace-only file already fails JSON parsing and is retried. | `NativeCredentialHelpersTests.ReadAsync_WhenFileGrowsBeyondInitialSlackDuringRead_RetriesOrGrowsUpToMaxBytes`; `ReadAsync_WhenFileIsWhitespaceOnlyThenRewritten_RetriesOnceAndReadsNewContent` (passed before any change) | `NativeCredentialHelpers.cs` |
| 9 | A backward clock jump keeps caches and cooldowns alive | Fixed for Claude's fresh cache, Claude's cooldown (re-based on when it began) and the cost cache. | `ClaudeNativeClientTests.FetchAsync_WhenClockMovesBackward_DoesNotServeStaleFreshCache`, `..._WhenClockMovesBackwardDuringCooldown_BlocksNoLongerThanTheCooldown`, `CodexBarServiceNativeCostTests.GetCostAsync_WhenClockMovesBackward_RescansInsteadOfServingCache` | `ClaudeNativeClient.cs`, `Services/CodexBarService.cs` |
| 10 | Extreme-year timestamps crash the scanner | Not reproduced (`ConvertTime` clamps on .NET 10). The test stays as a guard. | `NativeCostScannerTests.Update_WhenLogContainsExtremeYearTimestamp_SkipsLineWithoutCrashing` | none |
| 11 | A time zone change is ignored when no log changed | Fixed. The scanner remembers the zone it applied and re-buckets days when the system zone changes. | `NativeCostScannerTests.Update_WhenChangedPathsIsEmptyAndZoneChanged_RebucketsStoredUsage` | `Cost/NativeCostScanner.cs` |
| 12 | `.jsonl.zst` handling | Fixed: an archive named while its plain log exists reads the plain log; an archive whose indexed part does not end at a line break is re-indexed from the start; decompression stops at 1 GiB, with a log line. | `NativeCostScannerArchiveTests.Update_WhenChangedPathsNamesArchiveWhilePlainLogExists_PrefersPlainLog`, `..._WhenArchiveIsReplacedByDifferentArchive_IndexesItFromTheStart`, `..._WhenArchiveDecompressesBeyondCap_CountsOnlyLinesWithinTheCap` | `Cost/NativeCostScanner.cs` |
| 13 | winsqlite3 imports use Cdecl, but winsqlite3 is stdcall on x86 | Fixed: all 12 imports are `StdCall` (same convention on x64 and ARM64). Red by inspection. | `NativeCostScannerTests.CostIndexDatabase_NativeImports_UseStdCallForWinSqlite3` | `Cost/CostIndexDatabase.cs` |
| 14 | A transient read error in `GetPricing` is never retried | Fixed. A failed read does not record the file stamps. | `CostPriceStoreTests.GetPricing_WhenCustomPricingFileIsTemporarilyLocked_RetriesOnNextCall` | `Cost/CostPriceStore.cs` |
| 15 | Concurrent Claude fetches race | Fixed. One live call at a time; waiting callers re-check the cache and cooldown. | `ClaudeNativeClientTests.FetchAsync_WhenCalledConcurrentlyOnColdCache_MakesOnlyOneHttpRequest` | `ClaudeNativeClient.cs` |
| 16 | The live price catalog loads every provider | Fixed. Only `anthropic` and `openai` are read. | `CostPriceCatalogTests.ParseLiveAsync_WhenOtherProvidersContainInvalidOrHugePayloads_SkipsThem` | `Cost/CostPriceCatalog.cs` |
| 17 | The allow list skips synchronous `Send`, ports and user info | Fixed. Both send paths are checked: HTTPS, default port, no user info, allowed host. | `NativeCredentialHelpersTests.HostAllowListHandler_WhenSynchronousSend_Throws`, `HostAllowListHandler_WhenNonDefaultPortOrUserInfoOrLoopback_Throws` | `NativeHttp.cs` |
| 18 | Statements finalized twice; `App.Services` not disposed | Statement and index disposal fixed (finalize once); not run red (a double `sqlite3_finalize` could crash the test host). App-exit disposal: won't fix. | `NativeCostScannerTests.CostIndex_DisposeTwice_DoesNotFinalizeTwice` | `Cost/CostIndexDatabase.cs`, `Cost/CostIndex.cs` |
| 19 | The watcher queues files that are not logs | Fixed. Only logs or directories are queued; the scanner drops other paths before it opens the index. | `CostLogWatcherTests.NotifyChanged_WhenNonLogFileIsCreatedOrRenamed_IsIgnored`, `NotifyRenamed_WhenFileIsRenamedToALog_QueuesIt`, `NativeCostScannerArchiveTests.Update_WhenChangedPathsContainsOnlyNonLogFiles_DoesNotOpenIndex` | `Cost/CostLogWatcher.cs`, `Cost/NativeCostScanner.cs` |
| 20.1 | The Antigravity parser keeps refresh secrets; a malformed blob reads as signed out | Fixed. Refresh token and client id/secret fields removed; malformed JSON is a failed read that keeps the last good reading. | `AntigravityCredentialParserTests.Credentials_WhenInspected_HaveNoRefreshSecretFields`, `Parse_WhenBlobIsMalformedJson_Throws`, `AntigravityNativeClientTests.FetchAsync_WhenCredentialBlobIsMalformedJson_ReturnsFailedAndPreservesLastGood` | `AntigravityCredentials.cs`, `AntigravityNativeClient.cs` |
| 20.2 | The cooldown file is read without a size cap | Fixed: 64 KiB cap. | `ClaudeCooldownStoreTests.Load_WhenCooldownFileExceedsSizeLimit_ReturnsNullWithoutReading` | `ClaudeCooldownStore.cs` |
| 20.3 | Unchecked int narrowing | Window length fixed (2^31 minutes gave -2147483648 before the clamp). Percentage not reproduced (.NET 9+ saturates double-to-int). | `CodexNativeClientTests.Map_WhenLimitWindowSecondsExceedsIntMaxMinutes_ClampsInsteadOfOverflowing`, `NativeUsageDtoMapperTests.ToUsageWindow_WhenUsedPercentExceedsIntRange_DoesNotWrapNegative` | `CodexNativeClient.cs` |
| 20.4 | Changed paths not normalized before the root check | Fixed: `Path.GetFullPath` first. | `NativeCostScannerArchiveTests.Update_WhenChangedPathUsesParentTraversal_IgnoresFileOutsideRoot` | `Cost/NativeCostScanner.cs` |
| 20.5 | Date suffix cut without checking for digits; dated lookup depends on order | Fixed. Only all-digit dates are cut; the latest dated id wins. | `CostPricingTests.Normalize_WhenCodexModelHasNonDateHyphenatedSuffix_DoesNotStripIt`, `Normalize_WhenClaudeModelHasSignedNumberSuffix_DoesNotStripIt`, `CostPriceCatalogTests.Find_WhenSeveralDatedSnapshotsMatch_PicksTheLatestDeterministically` | `Cost/CostPricing.cs`, `Cost/CostPriceCatalog.cs` |
| 20.6 | Duplicate Antigravity extra windows; cached local zone | Dedupe fixed. Zone part won't fix (the UI never shows that label for Claude). Cost scans now refresh the process's zone cache. | `AntigravityNativeClientTests.MapQuotaSummary_WhenDuplicateExtraBucketIdsReturned_DeduplicatesExtras` | `AntigravityNativeClient.cs` |

**Known limit (12):** the line-break check catches a replacement archive that is shorter or cut mid-line, not one whose line break happens to fall at the old offset; that would need a content fingerprint in the index schema.

**Existing tests changed:** the Antigravity parser's refresh-field tests were replaced (those fields no longer exist); the "not json" case moved from returns-null to throws (20.1); the watcher's directory test now creates the directories it reports changes for (19).

## Part B: Debug-only test hooks and the fake provider server

A Debug build reads these once, on first use:

| Variable | Stands in for |
| --- | --- |
| `CODEXBARWIN_TEST_PROFILE_ROOT` | `%USERPROFILE%`: the Claude and Codex sign-in files, the three cost log folders, the Gemini spend log |
| `CODEXBARWIN_TEST_APPDATA` | The data folder: settings, cache, cost index and prices, cooldown file, crash.log |
| `CODEXBARWIN_TEST_ANTIGRAVITY_CREDENTIAL_FILE` | Credential Manager: a file holding a fake credential blob |
| `CODEXBARWIN_TEST_CLAUDE_BASE_URL`, `_CODEX_BASE_URL`, `_ANTIGRAVITY_BASE_URL` | The provider hosts |

- The reader lives in `Services/Native/NativeTestOverrides.cs`; `Load(Func<string, string?> read)` is its only entry point. In Release, `Load` returns null without calling `read` and `Current` is a literal `null`; the variable names, parser, file credential source and loopback allow-list exist only inside `#if DEBUG`.
- Fails closed: paths must be absolute; a base URL must match `^http://127\.0\.0\.1:<port>/?$`; a Claude or Codex base URL needs `PROFILE_ROOT`, the Antigravity one needs `ANTIGRAVITY_CREDENTIAL_FILE` (so a real token never reaches the fake server over plain http); every override needs `APPDATA`.
- An override swaps only the origin and keeps the production path. Release allow list is unchanged (a test asserts exactly the four HTTPS hosts).
- With the hooks on: no Claude renewal runner, no admin key for `ApiBillingService`, the Gemini spend log is read under the fake profile, and the single-instance mutex is `CodexBarWin_SingleInstance_Test`.
- Not redirected: the status pages, the daily models.dev price download, and the WSL route.
- **`tools/FakeProviders`:** a net10.0 console app on `HttpListener` (127.0.0.1), no packages, not in the solution. Scenarios: normal, 401, 403, 429, 429-seconds, 429-date, 429-absurd, 500, slow (30 s), malformed, empty, huge (8 MiB, streamed). Switch with `GET /__scenario?name=..&provider=..`; stop with `GET /__stop` or `--exit-after`. It never prints request headers. Its README has the commands.
- **Tests:** `NativeTestOverridesTests.cs`: 31 Debug cases (including one end-to-end read of all three providers over a loopback socket) and 6 Release cases.
- Not done: the Debug app itself was not launched against the fake server; the README's side-by-side package steps are untested.

## Part C: Claude sign-in renewal

When Claude's sign-in is found expired and `RenewClaudeSignIn` (in `AppSettings`, default true, no UI) is on, `ClaudeNativeClient` asks `IClaudeSignInRenewer` to run `claude auth status --json`. `ClaudeAuthStatusRunner` finds `claude.exe` on absolute PATH entries or at `%USERPROFILE%\.local\bin\claude.exe`, starts it hidden with stdin closed, discards stdout and stderr (they contain the account email), kills the process tree after 30 s, and logs only "not found", the timeout, or the exit code and duration. At most one attempt per 30 minutes per app run; afterwards the credentials file is read once more and used when its expiry moved later and is still in the future. The app never writes the credentials file.

Files: `Services/Native/ClaudeSignInRenewal.cs` (new), `ClaudeNativeClient.cs`, `NativeUsageService.cs`, `Models/AppSettings.cs`, `App.xaml.cs`. Tests: `ClaudeSignInRenewalTests.cs`, `Models/RenewClaudeSignInSettingTests.cs`. The real `claude` was never run.

## Notes

- `App.xaml.cs` changed only in wiring (DI factories, crash-log folder, Debug-only mutex name). In Release every override is null.
- User-facing changes for the CHANGELOG: fixes 1 to 6, 9, 11 and 12, and the `RenewClaudeSignIn` setting.
- `tools/NativeProbe` does not build (it links `Services/Native/*.cs`, including `NativeCostService.cs` and its app-only dependencies); inferred to be the case before this branch too.
