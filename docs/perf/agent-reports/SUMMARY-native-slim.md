# native-slim: cutting the app's own CPU and RAM

Branch `perf/native-slim`, all changes uncommitted (the brief allowed no commits). The look of every window is unchanged
(strip checked pixel by pixel against the baseline; see "Visual checks").

## Results

All runs: unpackaged Release build from `bin\` (built with `-p:WindowsPackageType=None`, because the normal `bin\` build
is MSIX-only and crashes unpackaged with `REGDB_E_CLASSNOTREG`). Same data as the installed app (its `LocalCache` files
copied to `%LOCALAPPDATA%\CodexBarWin`), taskbar strip on, overlay off, flyout and dashboard closed, started minimized,
refresh every 300 s (adaptive). Command for every row:

```
pwsh -NoProfile -File tools/perf/Measure-Refresh.ps1 -Minutes <9|11> -AppProcess CodexBarWin `
  -CacheFile "$env:LOCALAPPDATA\CodexBarWin\cache.json" -Out <csv>
```

started 45 s after launch. "Idle (script)" is the script's `idle_cpu_s_per_hour`. It only leaves out 30 s around each
`cache.json` write, so bursts at other times (cost scans, JIT warm-up) count as idle. "Idle (robust)" is the mean of the
10-second CPU slices without the busiest 20%, from the same CSV. Windows charges process CPU in 15.6 ms clock ticks, so
at these levels single slices are coarse.

| Build (cumulative) | Run | Refreshes | CPU-s per refresh | Idle CPU-s/h (script) | Idle CPU-s/h (robust) | Private WS MB (median) | Private bytes MB (median) | Working set MB (median) |
|---|---|---|---|---|---|---|---|---|
| Baseline (HEAD `ff9d551`) | 11 min | 2 | 4.36 | 20.44 | 8.68 | 115.7 | 160.4 | 245.7 |
| + 1 idle wake-ups | 11 min | 2 | 2.09 | 17.06 | 4.18 | 107.0 | 147.3 | 235.0 |
| + 3 update in place | 11 min | 3 | 1.40 | 10.96 | 3.12 | 105.9 | 145.5 | 232.7 |
| + 4 append-only history (and the spinner fix, see 3) | 9 min | 1 | 1.49 | 12.58 | 4.83 | 96.4 | 137.8 | 225.8 |
| + 2 flyout hidden at a minimized start | 9 min | 1 | 1.19 | 11.80 | 3.49 | 95.3 | 136.6 | 223.6 |
| + 5 tiered compilation off (final) | 11 min | 2 | **0.38** | **3.19** | **1.08** | **92.6** | **134.8** | 216.5 |

Targets (owner, 25 Nov): per refresh under 0.1 CPU-s; idle 2 CPU-s/h or less; private working set 90 MB or less (stretch 60).
Against the targets: refresh 4.36 -> 0.38 CPU-s (0.17 in the 9 min run of the same setting): about 11x lower, not yet
under 0.1. Idle 20.4 -> 3.2 CPU-s/h by the script (bursts included), 1.1 with bursts left out: the typical idle hour now
meets the 2 CPU-s target, but hours with cost scans or JIT of rarely used paths do not. Private working set 115.7 ->
92.6 MB: 2.6 MB short of 90 MB. Private bytes 160.4 -> 134.8 MB, working set 245.7 -> 216.5 MB.
Not reached, and what is left: the refresh itself (HTTPS reads, JSON, cost work: about 0.2-0.4 CPU-s, not UI), the 2 s
strip tick (about 2 CPU-s/h), and memory that is not the app's own objects: the GC heap commits about 44 MB for about
7 MB live (the GC settings tried did not shrink it), and about 76 MB of native heaps (XAML, Direct2D/D3D, the driver).

The "+ 1" row uses a build with change 1 only: change 4 was reverted and change 3 disabled by restoring the old
recreate-on-every-change behavior (`Items.Clear()`/`Add`, a new card for every replaced provider, new brushes). The
"+ 3" row reverts change 4 only. Refresh CPU varies run to run with network timing (two baseline-build runs gave 4.36
and 4.42), so read the refresh column as a trend.

## Changes

### 1. Idle wake-ups

Inventory of every timer and periodic loop:

| Timer / loop | Where | Period | Runs while hidden/closed? | What it does | Action |
|---|---|---|---|---|---|
| Strip keep-on-top tick (`DispatcherTimer`) | `TaskbarWindow` | 2 s; 5 s hidden for a fullscreen app; 30 s locked | Stops when hidden on purpose or closed | Lock check, fullscreen check, every 15th tick a rebuild (time layer), reposition, re-raise above the taskbar | Kept at 2 s (no Windows event for z-order changes or for a fullscreen app starting without a foreground change), each tick made cheaper, see below |
| Overlay topmost tick | `OverlayWindow` | 2/5/30 s | Same | Same, plus size/clamp | Same treatment (the overlay is off on the owner's laptop) |
| Strip/overlay rebuild hold and reset release | `TaskbarWindow`, `OverlayWindow` | One-shot | No | Wait for a reset animation / reset gate | Unchanged |
| Foreground watcher (`SetWinEventHook`, `EVENT_SYSTEM_FOREGROUND`) | `WindowZOrder` | Event | While shown | Re-checks fullscreen, position and z-order on a foreground change | Kept (already an event); re-raise only when covered |
| Flyout slide animation (`DispatcherTimer`) | `MainWindow` | 10 ms x 15 steps | Only while sliding | Open/close animation | Unchanged |
| Dashboard redraw debounce (`DispatcherQueueTimer`) | `DashboardAppWindow` | One-shot 250 ms | Stops when closed | Redraw after new data | Unchanged |
| Dashboard clock (`DispatcherQueueTimer`) | `DashboardAppWindow` | 30 s | Stopped when minimized/hidden, released on close | Relative times ("x min ago") | Unchanged: only runs while visible; 30 s keeps minute labels within 30 s of correct, and a slower clock would change what is shown |
| Refresh schedule (`Task.Delay`) | `MainViewModel.RunPeriodicRefreshAsync` | Refresh interval (300 s here, adaptive) | Yes (it is the data refresh) | Usage refresh | Unchanged |
| Unlock poll (`Task.Delay` loop) | `MainViewModel.WatchForUnlockAsync` | 30 s | Only while locked | Notices an unlock | Unchanged |
| Working-set trim (`Task.Delay`) | `MainViewModel.TrimWhenIdleAsync` | One-shot 10 s, at most once a minute | Yes | Trims the working set | Unchanged (tested: not the source of the CPU spikes) |
| "Updated x ago" (flyout), "Resets in" texts | `MainViewModel`, `UsageWindow` | None: set when data arrives | n/a | Labels | No timer exists; nothing to change |
| `ProgressRing` in each provider card | `ProviderCard.xaml` | Composition animation (per frame) | Kept animating when collapsed once the card is updated in place | Loading spinner | Now active only while shown (see change 3) |

What the strip's tick cost and what changed (measured, not assumed):
- With the tick disabled, idle fell from 20.4 to 3.9 CPU-s/h (6 min run), so the tick was most of the idle cost.
- The Win32 queries are cheap (console benchmark, CPU per call): `SHQueryUserNotificationState` 8-23 us,
  `WTSQuerySessionInformation` 23-31 us, `SHAppBarMessage(ABM_GETTASKBARPOS)` 23-86 us, `GetWindowRect` and
  `GetDpiForWindow` under 1 us, the z-order walk above the strip 3 us. The cost was in what they set off: an
  unconditional `SetWindowPos(HWND_TOPMOST)` every 2 s, WinRT `AppWindow.Position/Size` calls, and the rebuild every
  30 s that recreated every bar.
- Re-raise only when it matters: `WindowZOrder.AssertTopmostIfCovered` walks the windows above the strip and calls
  `SetWindowPos` only if the strip lost its topmost style or a visible, uncloaked window above it overlaps it (the only
  cases where the raise changes what is on screen). On this laptop 11 windows were above the strip, none visible and
  overlapping, so the raise was skipped. Pure decision logic in `Helpers/ZOrderCoverage.cs` (tested).
- Taskbar position from events: `Helpers/TaskbarChangeWatcher.cs` subclasses the strip window and marks the cached
  taskbar bounds stale on `WM_SETTINGCHANGE`, `WM_DISPLAYCHANGE`, `WM_DPICHANGED` and `TaskbarCreated` (Explorer
  restart). The bounds are also re-read on every foreground change (as before), when the strip comes back from a lock or
  a fullscreen app, and every 15 ticks (30 s) as a fallback; every tick if the subclass could not be installed.
- The steady-state position check uses `GetWindowRect` instead of WinRT `AppWindow.Position`/`Size` (strip and overlay).
- Lock state is queried every 5th tick (10 s) instead of every tick while unlocked. The lock screen covers the strip, so
  noticing a lock up to 8 s later changes nothing visible; the `Locked` event and the foreground watcher still react at
  once, every locked-state tick still checks, and the reset gate asks for the lock state itself.

Files: `TaskbarWindow.xaml.cs`, `OverlayWindow.xaml.cs`, `Helpers/WindowZOrder.cs`, `Helpers/ZOrderCoverage.cs` (new),
`Helpers/TaskbarChangeWatcher.cs` (new). Tests: `CodexBarWin.Tests.Unit/Helpers/ZOrderCoverageTests.cs` (12 cases).

### 2. Closed windows

- The dashboard and settings windows were already closed and released when dismissed, and recreated on demand
  (`OnDashboardRequested`/`OnSettingsRequested` look them up in `App.Windows`). Their `Closed` handlers unhook every
  singleton event (view model, settings, page events, accelerators, timers, backdrop). No static or view-model
  references hold them. Nothing to change there.
- Found and fixed: with "Start minimized" on (the owner's setting) the flyout was never hidden. `App` activates
  `MainWindow` at launch, but `MainWindow.OnContentLoaded` called `HideWindow()`, which returns early while
  `_isWindowVisible` is false. So the flyout window stayed shown off-screen (`IsWindowVisible` true) with its acrylic
  backdrop and cards until the first open and close, rendering on every refresh. It is now hidden at a minimized start
  with the same call the close animation ends with (`WindowExtensions.Hide(this)`). Nothing visible changes, because it
  was off-screen, not in Alt+Tab and not on the taskbar.
- Not done: destroying the flyout itself. `MainWindow` owns the tray icon, the strip and overlay lifetimes and the
  single-instance mutex, and closing it exits the app. Recreating its page on every open would also delay the slide
  animation's first frame. Moving the tray icon and app lifetime into a separate host is a larger change, left for later.

Files: `MainWindow.xaml.cs`. No unit tests (UI layer, exempt).

### 3. Update the UI in place

- `Helpers/InPlaceRow.cs` (new): a row base class with `UpdateFrom` (copies changed public properties, raises
  `PropertyChanged` for each one, `[UpdateLast]` properties last so a bar's reset stamp follows its window, and
  `[KeepOnUpdate]` properties untouched) and `Apply`, which updates the shown rows when the keys match in order and
  replaces the list otherwise.
- Taskbar strip and overlay: `TaskbarItem`/`OverlayRow` derive from it; their templates use `x:DefaultBindMode="OneWay"`;
  `Rebuild` builds the new rows and calls `InPlaceRow.Apply`, so a refresh or the 30 s time-layer update changes the
  existing `UsageMeter`s instead of recreating them. The icon geometry is reused, and the strip's text brushes and the
  status brushes (`ProviderStatusVisuals`) are created once instead of on every rebuild. Reset animations keep their
  behavior: the stamp is applied after the window, and the window-level `ResetHost` memory still prevents replays.
- Flyout: `MainPage` binds its cards to its own `ProviderSlot` list, synced from `MainViewModel.Providers` with
  `InPlaceRow.Apply`. Before, every `Providers[i] = ...` replacement (3 to 9 per refresh) recreated that
  `ProviderCard`, even with the flyout hidden. `ProviderCard` now parses its icon once and keeps its extra-window rows
  while they are unchanged (`CardExtraRow` is a record, compared by value).
- Regression found while measuring and fixed: the card's loading `ProgressRing` had `IsActive="True"` and only toggled
  `Visibility`. With cards kept across refreshes, the ring kept animating after it was collapsed, so the compositor drew
  frames nonstop (about 90 CPU-s/h in the first run of this build). `IsActive` now follows the loading state. The ring
  looks the same.
- Why it mattered beyond control creation: in the baseline trace, the finalizer thread spent about 2.3 s (thread time)
  releasing WinRT objects (`IObjectReference.Finalize`) in the 30 s around a refresh, which is the churn from recreated
  controls and brushes.

Files: `Helpers/InPlaceRow.cs` (new), `TaskbarWindow.xaml(.cs)`, `OverlayWindow.xaml(.cs)`, `Views/MainPage.xaml(.cs)`,
`Controls/ProviderCard.xaml(.cs)`, `Controls/ProviderStatusVisuals.cs`. Tests:
`CodexBarWin.Tests.Unit/Helpers/InPlaceRowTests.cs` (9 cases).

### 4. Append-only history

- Before: every refresh serialized the whole history (3.1 MB indented JSON, about 15,000 samples) and replaced the file.
- Now the file stays a JSON array, so the current loader (and any older build) reads it, written one sample per line.
  A save appends only the new samples, overwriting from just after the last sample: `,\n{...}\n]`.
  `UsageHistoryStore` keeps the samples added since the last save (`TakeUnsaved`) and a `Generation` that changes when
  the history is replaced.
- Compaction: a full atomic rewrite (temp file plus move, as before) at most once a day (`CompactionInterval`), after a
  load that found more than 10% (and at least 50) dead samples, after a damaged tail, after a failed append, and when the
  in-memory history was replaced. Expired samples otherwise stay in the file for up to a day; the loader prunes them.
- No migration is needed. The existing indented file is appended to as is and compacted to the one-line-per-sample form
  at the first daily rewrite. An append cut off by power loss leaves a tail that `Parse` recovers (every complete sample
  is kept, then the file is rewritten); before, such a file read as empty.

Files: `Services/UsageHistoryStore.cs`, `Services/UsageHistoryPersistence.cs`. Tests (15 new cases in
`UsageHistoryPersistenceTests.cs`): append keeps existing bytes, append after reload, daily compaction drops expired
samples, append within the compaction interval, cut-off tail recovery and rewrite, legacy indented file read and appended
in array-loader format (migration), non-array tail triggers a rewrite, restored store triggers a rewrite, `Format`
round trip with one sample per line, `Parse` of non-JSON, `ShouldCompact` thresholds. The 5 existing persistence tests
pass unchanged.

### 5. Runtime settings

Measured on one binary with environment variables (same code as the final build without this change, JIT), 9 min each:

| Setting | CPU-s per refresh | Idle CPU-s/h (script) | Idle (robust) | Private WS MB | Private bytes MB | CPU-s in the first 45 s |
|---|---|---|---|---|---|---|
| Defaults | 1.19 | 11.80 | 3.49 | 95.3 | 136.6 | 2.7 |
| `DOTNET_TieredPGO=0` | 0.88 | 7.72 | 2.61 | 95.0 | 136.7 | 2.6 |
| `DOTNET_TieredCompilation=0` | **0.17** | **3.20** | **1.46** | **93.0** | **134.7** | 3.7 |
| `DOTNET_gcConcurrent=0` | 1.42 | 9.48 | n/a | 102.0 | 140.5 | n/a |
| `DOTNET_GCConserveMemory=5` | 1.13 | 13.02 | n/a | 96.3 | 136.0 | n/a |
| ReadyToRun | 1.55 | 13.90 | 5.16 | 96.7 | 143.5 | 2.5 |
| ReadyToRun + `TieredCompilation=0` | 0.34 (3 refreshes) | 3.26 | 2.82 | 92.2 | 138.5 | 4.3 |
| ReadyToRun + `TieredPGO=0` | 1.02 | 4.19 | 1.73 | 94.8 | 141.4 | 5.2 |

- Kept: `<TieredCompilation>false</TieredCompilation>` for non-Debug builds in `CodexBarWin.csproj` (it reaches
  `runtimeconfig.json`, also inside the MSIX). The cause it removes: with tiering, methods start unoptimized and are
  recompiled in the background as they get warm, and tiered PGO adds an instrumented step. Code that runs every 2 s,
  30 s or 5 min kept getting recompiled for minutes to hours after launch. A native sampler (per-thread cycle counters
  and instruction pointers) put the 30 s CPU spikes on runtime threads with samples in `clrjit.dll`, and with tiering
  off they were gone. Cost: about 1 CPU-s more once at startup, paid back in about 6 minutes.
- Not kept: `ConcurrentGarbageCollection=false` and `GCConserveMemory=5` (no lower memory in these runs) and
  `TieredPGO` (moot with tiering off). Workstation GC is unchanged.
- ReadyToRun: not extended to the MSIX. The MSIX tooling does support it: a test package built with the README recipe
  but `-p:PublishReadyToRun=true` (`GenerateAppxPackageOnBuild=true`, into `%TEMP%`, not installed) contained the R2R
  images (`CodexBarWin.dll` 4.6 MB vs 1.9 MB) and `TieredCompilation: false`. But R2R measured no gain on top of tiering
  off, cost 4-7 MB more private bytes and 47 MB more disk, so the README and `release.yml` keep
  `-p:PublishReadyToRun=false`. The csproj's existing `PublishReadyToRun` for `dotnet publish` is unchanged (no
  workflow uses it). Note: an unpackaged `dotnet publish` output lacks the `.xbf` files and `Assets` and crashes in
  `Microsoft.UI.Xaml.dll`. That is pre-existing and unrelated to R2R; the R2R runs used the R2R DLLs over the normal
  build output.

### 6. GPU memory while nothing is shown

- What holds GPU memory: `igc64.dll` (Intel shader compiler, 82.7 MB committed image), `igd10um64xe.dll` (12.4 MB) and
  `d2d1.dll` (6.5 MB) are loaded because XAML renders with Direct3D. These are image pages, mostly shared and file-backed,
  not private memory. They stay loaded as long as any XAML window is shown, and the taskbar strip always is.
- `IDXGIDevice3::Trim` is not reachable: WinUI 3 does not expose the D3D device its compositor uses (a
  `CompositionGraphicsDevice` wraps a device the app creates, not XAML's), so the app cannot trim it.
- What was achievable: no window renders while nothing changes on screen. The dashboard and settings windows are
  destroyed on close (already the case), and the flyout no longer stays shown off-screen after a minimized start (change
  2). The collapsed spinners no longer drive the compositor (change 3). Measured effect of the flyout fix alone: private
  working set 96.4 -> 95.3 MB and private bytes 137.8 -> 136.6 MB (9 min runs; within run-to-run noise). The larger
  memory drop came from changes 3 and 4 (no control churn, no 3 MB serialization per refresh).
- Memory map of the final build after the 11 min run (`tools/perf/Get-MemoryMap.ps1`): GC heap 44.2 MB committed,
  native heap and other private 75.6 MB (103.8 MB in `docs/perf/MEASUREMENTS.md` section 4), stacks 0.9 MB, JIT code
  0.0 MB in private executable regions, images 348.1 MB, mapped 181.3 MB.
- Further steps, not done: an unsupported GC knob (`DOTNET_GCgen0size`) or a heap hard limit could cap the 44 MB GC
  commit; they are environment-only or risk out-of-memory failures, so they are left for the owner to decide. Option 6
  in `OPTIONS.md` (strip and overlay without XAML) is what would unload the GPU stack.

## Tests and build

- `dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -nodeReuse:false`: Build succeeded, 0 errors, 1 warning (`NETSDK1198`, missing `win-x64.pubxml`, already present before).
- `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64 -nodeReuse:false`: Passed, 2,280 of 2,280 (0 failed, 0 skipped).
- New test cases: 36 (`ZOrderCoverageTests` 12, `InPlaceRowTests` 9, `UsageHistoryPersistenceTests` +15; data rows counted).
  Changes 1, 3 and 4 add Helpers/Services code, all covered; the window code (UI layer) is exempt per `.claude/rules/testing.md`.

## Visual checks

- Taskbar strip, baseline build vs the changes (screenshots of the bottom-left 30% of the screen, 30 s after launch):
  46 of 60,930 pixels differed, all on the Codex bars' time marker and time layer, which had moved about 1 px because
  time passed between the two launches. Layout, colors and text identical.
- Flyout, baseline vs final (strip clicked to open it, screenshot of the bottom-right corner, clicked again to close):
  identical except "Resets in 20h 49m" vs "20h 48m" (a minute passed). The slide-in animation ran normally; cards show
  their data after being updated in place.

## Notes and follow-ups

- The remaining idle cost is mostly the strip's 2 s tick itself (about 1 ms of CPU per tick in cycle counts, that is
  about 2 CPU-s/h), plus the strip redraw when its signature changes (incident tooltip ages and pace texts change about
  once a minute and re-apply the bars' time values). Updating only the tooltips in that case would skip a redraw but
  would also hold the bars' time layer at a slightly older value until the next 1% step. That changes what is shown by
  a fraction of a pixel, so it was not done.
- Option for later: a slower tick (5-10 s) would cut the remaining tick cost, but the strip would then take longer to
  come back above the taskbar after a click on it, which is a visible change. Not done.
- The overlay creates a new icon geometry per rebuild that the in-place update then discards (small garbage); it is off
  on the owner's laptop.
- Measurement environment: the installed packaged app was stopped and left stopped. The original
  `%LOCALAPPDATA%\CodexBarWin` (an old developer-mode settings file and history) was backed up and restored after the
  runs; the copies of the installed app's data used for the runs were removed.
