# Native usage by default: summary

Branch `perf/native-app`, uncommitted. Usage is now read natively by default. The WSL route is kept as a per-provider
fallback set in settings.json.

## Results
- Release build: `dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false -nodeReuse:false`
  succeeded with 0 errors and 1 warning (NETSDK1198, the publish profile warning that was already there).
- Unit suite: `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64 -nodeReuse:false`
  **Passed 2232, Failed 0, Skipped 0, Total 2232** (1 m 50 s).
- New test cases: 74 in total. That is 71 cases from 59 methods in six new test files (data rows counted separately), plus 3 new `SettingsServiceTests`.

## Changes

### 1. Setting: usage source per provider
- `Models/UsageSource.cs` (new): the `native` and `wsl` constants, `Normalize` (anything other than `wsl` becomes
  `native`) and `Defaults()`.
- `Models/AppSettings.cs`: `UsageSources` (a dictionary keyed by provider id; it defaults to `native` for all three
  providers), `GetUsageSource`, `UsesNativeUsage` and `AllEnabledUsageNative`. `Normalize()` rebuilds the dictionary:
  invalid provider keys are dropped, missing providers and unknown values become `native`, and `WSL` in any case
  becomes `wsl`. Settings files written before this change have no `usageSources` key, so they load as `native`.
- The JSON looks like this: `"usageSources": { "claude": "native", "codex": "wsl", "gemini": "native" }`. There is no
  UI for it in this job.
- I used a dictionary on `AppSettings` rather than a field on `ProviderConfig`, so the settings UI cannot drop the
  value when it rebuilds provider records.
- Tests: `Models/UsageSourceTests.cs` (15 cases) and `Services/SettingsServiceTests.cs` (3 new cases: loading an old
  file as native, normalizing unknown values, and saving then reloading `wsl`).

### 2. NativeUsageService
- `Services/Native/NativeUsageService.cs` (new) contains `INativeUsageService` and `NativeUsageService`.
- The service owns one `HttpClient` from `NativeHttp.CreateClient()` and the Claude, Codex and Antigravity clients.
  Clients are keyed by provider id through `ProviderConstants.ValidateAndNormalize`, so `gemini` uses the Antigravity
  client.
- `FetchUsageJsonAsync` reads one provider. `FetchAllUsageJsonAsync` reads several in parallel.
- Each provider's JSON is `NativeUsageDtoMapper.ToDto(result)` serialized as a one-item list with
  `AppJsonSerializerContext.Default.ListUsageDataDto`, which is the CLI's array shape. An empty error is written as
  `"error": null`, because a default `JsonElement` cannot be serialized; this reads back the same as a missing error.
- If a client throws unexpectedly, the service returns the CLI's error object rather than throwing. Cancellation is
  still passed through.
- `App.xaml.cs` registers it as a singleton through a factory, so DI uses the production constructor and not the test
  one.
- Tests: `Services/Native/NativeUsageServiceTests.cs` (13 cases), covering the array shape, gemini going to the
  Antigravity client, normalization, invalid ids, a throwing client, cancellation, a provider with no client, and
  parallel reads.

### 3. One interpretation path
- `Services/CodexBarService.cs`: a new optional constructor parameter `INativeUsageService? nativeUsage`.
- A provider takes the native route when its source is `native` and a native service is injected. The resulting JSON
  is wrapped as `WslResult { Success = true, Output = json, ExitCode = 0 }` and goes through the existing
  `InterpretProviderResult`.
- `FetchAllProvidersCoreAsync` splits providers into two groups:
  - Native providers are read in parallel. Each has the same usage deadline the CLI command uses.
  - `wsl` providers go through the unchanged batch, retry and fallback logic, with indexes mapped back to the original
    provider list.
  - The WSL batch only starts when at least one provider is set to `wsl`.
- `GetUsageAsync` routes the same way. Developer mode still uses sample data.
- Without a native service, as in the existing tests that build the service without one, every provider uses WSL.
- Tests: `Services/CodexBarServiceNativeRoutingTests.cs` (17 methods):
  - A strict `IWslService` mock sees no calls when every provider is native, for `GetAllUsageAsync`,
    `GetAllUsageStreamAsync` and `GetUsageAsync`.
  - With mixed sources, only the `wsl` provider goes to the batch or its retry.
  - Developer mode touches neither route.
  - A native read that passes its deadline reports "Request timed out".
  - Parity: native JSON and the equivalent CLI JSON produce the same `UsageData` for a full Claude reading, a missing
    sign-in, and a cached Gemini reading. Members computed from the wall clock (`TimeUntilReset`, `ElapsedPercent`,
    `Pace`) are left out of the comparison.

### 4. Provider status gets its own deadline
- `CodexBarService`: `StatusBudget` (500 ms after the usage call) is replaced by `StatusDeadline` = 4 s, counted from
  when each status prefetch started. The start time is recorded before the prefetch begins.
- If the deadline has already passed when usage returns, a finished status is used and an unfinished one is not
  waited for. This applies to both routes.
- Tests: `Services/CodexBarServiceStatusDeadlineTests.cs` (6 methods):
  - A 1.5 s status is attached on the native route.
  - A status that never finishes is dropped at about 4 s.
  - A WSL read slower than the deadline adds no extra wait.
  - A status that finished during a slow WSL read is attached.
  - A 3 s status after a 2 s WSL read is attached.
- 4 existing tests had bounds that assumed 500 ms. Their limits are now based on `StatusDeadline`:
  `CodexBarServiceIncidentMergeTests` (2), `CodexBarServiceReviewRegressionTests` (1), `CodexBarServiceTests` (1).
  `ReleaseHardeningTests` (1, 2 data rows) was changed the same way.

### 5. Claude cache lifetime
- `NativeUsageService.ClaudeCacheLifetime` is 9.5 minutes. `CreateClaudeOptions()` is used to construct
  `ClaudeNativeClient`.
- Tests (in `NativeUsageServiceTests`): the option's value, and a reading at 300 s served from cache with a call at
  600 s.

### 6. Claude 429 cooldown survives a restart
- `Services/Native/ClaudeCooldownStore.cs` (new): `IClaudeCooldownStore` and `ClaudeCooldownFile`. It stores
  `%LOCALAPPDATA%\CodexBarWin\claude-cooldown.json` (beside `cache.json`) as `{"blockedUntil": "..."}`, written
  atomically. A damaged file reads as no cooldown.
- `Services/Native/ClaudeNativeClient.cs`: an optional `cooldownStore` parameter. The constructor loads the saved
  time, capped at now + 24 h so a damaged file cannot block forever. The client saves the time after each 429.
- Tests: `Services/Native/ClaudeCooldownStoreTests.cs` (12 cases):
  - Saving and loading round-trips the time; damaged files read as no cooldown.
  - A 429 saves the time; a success does not.
  - A saved cooldown that is still running blocks calls; one that has passed allows them.
  - The 24 h cap applies.
  - End to end: after a 429 and a "restart" over the same file, the new client makes no call.

### 7. Setup and version checks do not require WSL for native usage
- `Services/SetupChecker.cs`: an optional `ISettingsService`. When every enabled provider is native, it returns
  `IsReady` with `UsageIsNative` and does not call WSL or the setup memo. With any provider on `wsl`, the existing
  probe runs.
- `Models/SetupStatus.cs`: `UsageIsNative`. When it is set, `CurrentStep` is `Ready`.
- Startup in `App.xaml.cs` needed no change. The memo check only reads a file, and `CheckAsync` now returns ready
  without WSL, so the main window starts.
- `CodexBarService.GetVersionAsync` returns `"native"` (`NativeVersion`) and `IsAvailableAsync` returns `true` without
  WSL unless an enabled provider is set to `wsl`. `DashboardService` needed no change.
- Text that names the data source:
  - `ViewModels/SetupViewModel.cs`: "WSL not needed for usage" and "Usage read natively on Windows", with Windows
    sign-in steps.
  - `ViewModels/MainViewModel.cs`: the first-load error for native providers says "Check your network connection"
    instead of "Check WSL status".
- Cost scans still use WSL. If WSL is missing they fail as before and only the costs show as unavailable.
- Tests: `Services/SetupCheckerNativeUsageTests.cs` (8 methods):
  - Ready without WSL, and the setup memo is left alone.
  - A disabled provider set to `wsl` is ignored.
  - With a `wsl` provider and WSL or the CLI missing, the right setup step is still reported.
  - Cancellation; `CurrentStep` is `Ready`.
  - `DashboardService` diagnostics report "native" and available with a strict WSL mock.

## Other files touched
- `Services/ICodexBarService.cs`: doc comments only.
- `CodexBarWin.Tests.Unit/CodexBarWin.Tests.Unit.csproj`: links `Models/UsageSource.cs`. `Services/Native/*.cs` was
  already linked by wildcard.

## Notes
- No real credential files, Credential Manager entries or provider endpoints were touched. Tests use fake clients,
  fake HTTP handlers, synthetic tokens and temp files.
- With `wsl` selected, the WSL path behaves as before, apart from the status deadline in change 4.
