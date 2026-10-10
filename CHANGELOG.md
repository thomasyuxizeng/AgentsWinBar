# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Provider incident fetcher (`ProviderIncidentFetcher`) and status text sanitizer (`IncidentTextSanitizer`) for provider outage and degradation details across Claude, Codex, and Gemini feeds.
- Service status incident detail integration in `CodexBarService`, dashboard snapshot models (`DashboardProvider`), and caption presentation helpers (`IncidentCaption`).
- Incident detail (title, latest update, update age) is shown in the service status banner on the dashboard and the provider detail page, and in the status dot tooltips.
- Permanent cost ledger (`Services/CostLedger.cs`, `ICostLedger`) persisting daily cost and token series to `cost-ledger.json` across local CLI log rotations, with atomic writes, rolling `.bak` backup, resilient corrupt file rotation, and automatic first-run backfill from `cost-cache.json`.
- Query API on `ICostLedger` for pure lifetime totals (`GetLifetime`), daily date ranges (`GetRange`), and 30 calendar day series (`GetLast30Days`) with distinct 'no data' vs recorded zero indication.
- Standalone read-only PowerShell report script `tools/cost-ledger-report.ps1` to query lifetime and last-30-days totals per provider or custom date ranges.

### Changed
- Detail page: the stale service status line now sits with the status instead of under the Source row.
- Detail page: "Last fetched" keeps the absolute fetch time when data is stale and appends the stale caption.
- The failed-fetch banner says "Showing last-known data from ..." when the usage shown is the last good reading.
- Cost data is now retained indefinitely in cache and memory, exempt from the 7-day retention limit that continues to apply to usage meters and service status.
- Stale dashboard cost displays and trailing 30-day totals now recompute reliably from the permanent cost ledger when a fresh CLI scan is unavailable or covers fewer than 30 days.

### Fixed
- The cost ledger can no longer hang the app after developer mode is turned off; initial queries load saved costs in the background.
- Claude service status no longer flips to stale on alternate refreshes when a fresh incident status is available alongside replayed usage.
- Slow provider status requests no longer block the UI batch callback or delay starting an individual WSL usage fetch.
- Held usage replays remain valid when the WSL observation clock is more than five minutes ahead.
- Cost-cache backfill without daily entries uses the original reading's local calendar day instead of the ledger load day.
- The cost ledger report accepts a provider as its first positional argument while keeping the ledger path named.
- Claude no longer shows 'Fetch failed' on every other refresh when the WSL shim replays a cached reading within its 10-minute TTL.
- Gemini and incident status feeds no longer re-download and abort on each refresh; the 500 ms refresh budget bounds waiting rather than canceling HTTP requests, slow fetches populate the cache in the background, and failures are cached for 60 seconds.
- History no longer feeds the usage fallback or launders synthetic records into cache; `CacheService` (retained up to 7 days) serves as the sole last-known usage source.
- Untrusted provider incident descriptions and titles are sanitized (control characters stripped, whitespace collapsed, length capped to 200 characters with word-boundary truncation).
- Cache last-known recovery consistently tolerates up to 5 minutes of clock skew.
- A status-only refresh success no longer advances the usage supersession generation, preventing concurrent in-flight usage successes from being discarded.
- Inferred replay detection for legacy WSL shims missing the `cached` property, and fallback to `cat` in the shim when Python 3 is absent.

## [1.1.0] - 2026-10-11

### Changed
- Stale service status now says when it was last checked.
- Usage colours and limit labels now turn amber at 75% and red at 90% across all displays, using shared thresholds.
- Provider cards and the detail page show a quiet "Stale · last updated 12m ago" caption, including for cost and tokens, when the dashboard is showing last-known-good data.
- Added a 7-day expiry on all last-known-good cache data across usage and cost caches, in-memory cost tracking, and history restore fallback; expired entries are purged on load/save and treated as absent.

### Fixed
- Gemini status fallback is asynchronous and adds at most 500 ms to a usage refresh. Feed reads are capped at 2 MB, and open incidents with no update for more than seven days are ignored.
- Cached service status expires independently after seven days, including on disk load and startup restore; fresh usage cannot prolong an old incident.
- Failed, empty, offline, and timed-out provider refreshes retain the last successful usage windows, status, cost and token totals across restarts. Expired readings remain available as stale fallbacks within 7 days, usage history recovers missing windows, and successful zero readings replace older values. Dashboard models now expose staleness and last-success timestamps per data kind.
- Over-quota readings exceeding 100% (e.g. 105%) are accepted as valid readings rather than treated as failed fetches, showing honest percentages in the UI while clamping visual bar fills to 100%.
- Clock skew tolerance of up to 5 minutes is shared and clamped across cache loading instead of discarding entire cache entries.
- Cache loading now treats explicitly null `extraWindows` JSON fields as empty instead of skipping the provider.
- `CostCacheStore` and `UsageData.IsStale` normalize timestamps to UTC, preventing false future/stale classifications with Local or Unspecified DateTimeKind.
- Readings the WSL shim replays from its own cache keep their original time and show as stale, instead of renewing their own 7-day expiry or being recorded in history as new.
- Cost totals already on screen and per-window usage history now expire after 7 days on their own.
- Two refreshes of the same provider can no longer let an older result overwrite a newer one.
- Over-quota readings above 100% are kept in usage history and the startup fallback.

### Added
- Gemini service status fallback (`GeminiStatusFallback`): queries the Google Workspace Status incident feed directly with in-memory 5-minute caching when the CodexBar CLI returns no status object for Gemini.

## [1.0.2] - 2026-10-08

### Fixed
- A manual refresh, and the periodic refresh while the dashboard is open, no longer shows "Refreshing…" for up to a few minutes while cost totals are scanned. Usage updates first; cost totals follow in the background.
- Cost scans that fall back to one provider at a time (or a single cost lookup) were still cut off after 60 seconds. They now get the same 5 minutes as the combined scan.
- The app package now includes the .NET runtime. Before, it needed the .NET 10 Desktop Runtime, which `install.ps1` never installed, so the app couldn't start on a PC without it.
- `install.ps1` failed with "WSL package installation failed (exit 2)" when it was checked out with Windows line endings (a normal `git clone` on Windows). The scripts it runs inside WSL now always get Linux line endings.
- `install.ps1` could report success when the provider output after sign-in still had extra text the app can't read, including text printed after the JSON. It also now asks for a reboot when the Windows App Runtime installer requires one, and reads its own state files correctly when the Windows user name has non-ASCII characters.
- After midnight, cost totals could keep showing yesterday's "today" figures for up to 30 minutes.
- Cancelling a cost scan (for example by changing settings) could bring back older totals for providers that had already finished.
- If saving `cost-cache.json` failed once, it was not retried, so finished totals could be lost on restart.
- Clicking Refresh while an automatic refresh was running didn't rescan costs.
- A cached reading with an incomplete extra usage window could crash the provider details view.
- The WSL helper reported success with empty output when reshaping Gemini usage failed. When Claude's sign-in route was cooling down, it also hid the real exit code of the Claude CLI.
- The WSL installer checks the downloaded CLI's checksum against its exact file name, stops if copying any file fails, replaces the helper script atomically, and checks the version of the CLI it just installed rather than whatever comes first on `PATH`.
- Releases now validate the tag (`vX.Y.Z`) before using the signing secrets, no longer insert the tag directly into scripts, and refuse to publish if a package, certificate or checksum is missing or doesn't match its build.

### Removed
- Releases no longer publish `CodexBarWin.appinstaller`. It pointed at the upstream project and wasn't meant to be used.

## [1.0.1] - 2026-10-08

### Fixed
- Cost totals that finished before a scan was cancelled (for example by closing the dashboard) are now saved too, and kept totals older than 7 days are no longer shown.
- `install.ps1` no longer mistakes WSL's own error messages for shell noise, and a rerun before the reboot that enabling WSL needs no longer asks for admin rights again.
- Claude usage could stay hours out of date while the dashboard said "Fetched 2m ago". An expired WSL sign-in token was treated as a rate limit, and the route that refreshes it was skipped for 6 hours after one failure. Expired tokens now go straight to that route, it is retried after 30 minutes, the real error is shown instead of "rate limited", and the dashboard shows "Data from <time>" and marks the provider stale when a reading is more than 15 minutes older than its fetch.
- `install.ps1` could report success without the Claude sign-in on a fresh install (its first check left the shim answering "rate limited"); it now checks `claude auth status` and clears the shim's cooldown before each check.
- `install.ps1` reported a needed reboot as an admin failure on PCs without WSL, treated a missing ("Absent") Windows feature as a pending reboot, and accepted command output that the app can't parse (for example text printed by `~/.bashrc` before the JSON). The admin step now also reports its actual error.
- `install.ps1` checks the Windows Codex sign-in bridge right after enabling it and falls back to a Linux sign-in if it returns no usage. It reads the required Windows App Runtime version from the app package instead of a fixed value.
- Releases fail clearly when the signing secrets are missing, instead of signing with a throwaway certificate (which would have needed a new admin prompt on every machine).
- Cost totals could show only Gemini (for example "$0.25 · all providers"): Claude and Codex cost scans were cut off after 60 seconds, but reading large Windows logs through WSL can take longer (about 90 seconds for Codex on a real machine), and a failed scan replaced the last totals with nothing for 30 minutes. Cost scans now get up to 5 minutes, the last complete result per provider is kept in `cost-cache.json` and shown at startup, and a failed or timed-out scan keeps showing it until a newer complete scan replaces it.
- `install.ps1` refused every first-time install: before the certificate is trusted, Windows reports the release package's signature as `UnknownError` (untrusted root), which the signer check didn't accept. It now accepts that status only when an untrusted self-signed root is the certificate chain's sole problem.
- `install.ps1` could fail to install the app with `0x80073CF0` ("Package could not be opened"), because Windows' deployment service couldn't open packages in a new folder directly under `%LOCALAPPDATA%` on some machines. The verified package is now installed from a fresh copy in `%TEMP%`.

## [1.0.0] - 2026-10-08

### Added
- `install.ps1`: one-command setup that an AI coding agent (or you) can run from a clone. It checks the machine, downloads the latest release and verifies its checksum and signer, sets up WSL and the CodexBar CLI, installs any missing provider CLIs, installs the app and checks that each provider returns live usage. Admin actions (certificate trust, enabling or updating WSL, the Windows App Runtime) are batched into at most one UAC prompt, and any sign-ins happen in one window. It is safe to rerun and has a `-DryRun` preview.
- `INSTALL.md`: instructions written for an AI agent, covering exit codes, what the human has to do, and safety rules.
- Codex can reuse your Windows Codex sign-in read-only, so no second Codex login inside WSL is needed. It is used only when WSL Codex isn't signed in; turn it off with `install.ps1 -NoWindowsSignIns`.

### Changed
- Releases sign both architectures with one certificate (a stable one when the `SIGNING_PFX_BASE64` and `SIGNING_PFX_PASSWORD` repository secrets are set), valid for 10 years, and timestamp the signature. Each build checks that its package signer matches the published certificate. Releases also publish `CodexBarWin-Certificate.cer` and `CodexBarWin-checksums.sha256` under stable names.
- The WSL CLI installer validates the version, uses private temporary files, adds download timeouts and retries, and fails if the installed CLI doesn't run.
- The shim keeps its Claude cache and cooldown files per Linux user, so files left by another user (for example root) can't block refreshes.

### Fixed
- The ARM64 package was signed with a different certificate from the one published with the release, so it couldn't be installed by trusting that certificate.

## [0.9.9] - 2026-10-06

### Changed
- Lock state is also read directly from Windows, detecting locked sessions without lock events (for example when the app starts while locked).
- Refreshes slow down while Windows is locked, using `lockedRefreshIntervalMinutes` (30 minutes by default; 0 disables it), and check for stale usage after unlock or resume. Resume also restarts the refresh timer.
- Startup skips WSL checks when setup succeeded within the last 24 hours and restores a fresh usage cache until the next refresh is due.
- Cost scans share one WSL call and remain cached for 30 minutes. Opening the flyout or dashboard scans only providers without a cost value; existing estimates update during the next refresh.

### Fixed
- A locked session is polled every 30 seconds for an unlock, because Windows unlock events never reached the app: unlocking now restores the normal refresh interval (and refreshes stale data) within seconds instead of waiting out the 30-minute locked interval.
- The single-process WSL batch could fail and silently fall back to one WSL call per provider: the shell script now runs with `wsl --exec` (so Windows' default shell no longer expands `$VARIABLES` and `$(...)` in it) and never contains carriage returns, whatever line endings the source file was checked out with.
- A corrupt entry in the saved usage cache no longer discards the valid providers beside it, and developer mode never reads or writes the real usage cache or setup memo.
- When the single-process batch has to fall back to one WSL call per provider, the fallback is recorded in the dashboard's refresh log instead of only a log file.
- A manual Refresh now cancels an in-progress background cost scan and waits briefly for it to stop, so a late background result cannot overwrite the fresh totals.

## [0.9.8] - 2026-10-05

### Changed
- Refreshes back off while usage is unchanged (up to 3x the interval, capped at 15 minutes; turn off with `adaptiveRefreshEnabled` in settings.json) and return to normal when usage moves or a window opens on data older than the interval. Cost scans run only while the flyout or dashboard is open. The app trims its working set when idle (about 175 MB down to 10-50 MB)
- A suggested `.wslconfig` (4 GB cap, gradual memory reclaim) in the README keeps the WSL VM small between refreshes
- Usage for all providers is now fetched through a single WSL process per refresh.

### Fixed
- A provider fetch that hits its timeout now says "Request timed out" instead of "A task was canceled."
- The dashboard no longer shows an "F11" tooltip when hovering over its contents

## [0.9.7] - 2026-10-05

### Added
- Dashboard: a full window (resizable, maximizable, full screen with F11, optional keep-on-top) opened from the new button in the flyout footer. It shows everything the app knows at once: headline tiles (provider health, incidents, limits, most-used window, next reset, cost today and 30 days), a panel per provider with every usage window (pace, reset times, burndown), service status, cost and token mix, top models and history, plus a diagnostics column (CLI and app versions, effective settings, history store, cost scans, refresh log and alerts this session)
- In-memory activity log of refresh results and alerts, for the dashboard
- Dashboard data snapshots with usage and cost summaries, history, cached CLI diagnostics and session refresh/alert activity.
- Per-window usage history charts with an even-use guide, observed-rate projection, capture time and sample count. Snapshots persist atomically beside Settings for 14 days, deduplicated at half-point changes, ten-minute intervals and window resets. New installations show a collecting-data state until enough samples are available
- Estimated cost and token totals from local Windows Claude/Codex logs and WSL Antigravity logs, with a 15-minute scan cadence, manual refresh, daily cost bars and top models on the detail page, and a Settings toggle. Cost commands bypass the shim's usage cache so log-path changes cannot reuse a zero-cost result
- Provider service status badges: green operational, amber minor incidents or maintenance, red major or critical incidents, and grey when status is unavailable. Detail pages show the provider's report and a status-page link; compact displays show a small logo dot only during incidents

### Changed
- Dashboard overview: every usage bar at once, right under the headline tiles. One compact card per provider with a one-line bar for each window (name, bar with its pace layer, percent used, time to reset), so nothing needs scrolling to see; the detailed panels follow below
- Dashboard performance: refreshes no longer rebuild the whole window. Unchanged providers, tiles and diagnostics are skipped, the 30-second clock only updates relative times, and the charts and bars redraw only when their data changes, so scrolling stays smooth while a refresh runs
- Dashboard layout: diagnostics always sit below the providers (also in full screen), and Gemini takes a full-width row with its own models and the External pool side by side
- Shorter labels for the extra usage windows so the flyout bars get more room: "Fable only" is now "Fable", Antigravity's "Claude and GPT 5-hour" and "Claude and GPT weekly" are "External 5h" and "External 7d" (everywhere: cards, compact displays, detail pages, alerts and the dashboard)

### Fixed
- Dashboard hardening: no re-entrant redraws, handlers released on close, a minimized dashboard is restored when reopened, keep-on-top survives full screen, duplicate or odd usage windows (non-finite percentages, local or unspecified timestamps, negative costs, huge durations) no longer break the display, usage history is ordered by its UTC time, and today-only or unpriced-model usage now counts as cost data
- Settings saves are serialized and replace the complete file atomically; failed saves restore uncommitted values, keep Settings open and scroll to a visible error. Descriptions clear their switches at 150% scaling. Malformed provider lists, timer intervals, timeouts and animation settings recover safely
- Settings changes cancel obsolete refreshes, including late cost results; canceled cost scans preserve prior totals and allow immediate retries instead of creating a 15-minute empty-cache backoff. Developer samples no longer overwrite real usage caches or leak into history. Quit stops refreshes and flushes history before exiting
- Setup completes once and creates the main window before closing the wizard, preventing an early exit. Setup guidance now uses the fork's WSL installer, and its footer fits at 150% scaling
- Start minimized now controls startup visibility, Settings reuses its existing window, and Settings and flyout action controls have explicit accessibility names. The tray menu uses the native popup path to avoid the secondary-window XamlRoot crash
- Detail-page "Last fetched" timestamps now use local time instead of UTC. History captions use "1 sample", the even-use guide has stronger light/dark contrast, and collecting-data text no longer overlaps chart lines. Card cost estimates show each period on a separate wrapping line instead of cutting off the 30-day totals

## [0.9.6] - 2026-10-04

### Added
- Usage alerts also cover the extra windows (Claude and GPT quota, Fable only, ...) with the same three levels
- Extra bars are coloured by provider: Anthropic orange (#D97757) for Claude windows (e.g. Fable only), OpenAI green (#10A37F) for OpenAI, and an orange to teal gradient for the shared Claude and GPT quota (routed through gold and green so the middle stays clean instead of turning brown); the time-passed layer behind these bars takes the same colour, softened (so Fable looks orange even at 0% used); amber and red still apply
- Extra usage windows now appear on the main flyout cards too: Claude's "Fable only" bar, and Antigravity's Claude and GPT quota on Gemini's card. They are coloured differently from the main session and weekly bars
- The floating overlay and the taskbar strip now show each provider's most relevant two bars instead of always session and weekly: the window that changed most recently, or one running ahead of schedule, takes a slot; windows at or near their limit rank last, so you see the resources you still have (at most one extra window per row; hover a bar to see which window it is). The app remembers when each bar last moved to do this
- Gemini's detail page also shows Antigravity's separate Claude and GPT quota (5-hour and weekly) with the usual bars, pace and reset times. Any extra windows the CodexBar CLI reports are shown the same way
- Projected run-out time in every usage bar's hover tooltip, e.g. "At this pace you'll hit the limit in 1h 20m, 3h 10m before the reset" (or the usage you're on track for by the reset)
- Usage alerts: a Windows notification when usage is far ahead of pace (25+ points and the limit would be hit well before the reset), nearly out (90%+), or at the limit; once per usage window. Settings toggle (on by default) and a "Send test notification" button
- The floating overlay and the taskbar strip hide while a fullscreen game, video or presentation is running, and come back afterwards
- `tools/wsl`: installer for the CodexBar CLI and the shim that bridges Gemini (via Antigravity) and Claude (CLI route, cached) inside WSL, so a fresh machine or reinstalled distro can be set up in one step
- Pace layer under every usage bar: a soft violet layer shows how much of the usage window's time has passed (full at the reset); when it shows past the end of the blue you're using usage slowly. When usage is outpacing the schedule, a thin marker line appears at the time position instead. Hover a bar for the exact numbers
- Overlay and taskbar strip number columns size to the actual numbers (two digits as the typical case) so values sit snugly next to their bars and the "%" signs line up
- The taskbar strip sits flush with the taskbar: full height (starting just below the taskbar's top line), square left end, rounded right end, no border
- Taskbar display: a compact usage strip on the taskbar, bottom-left corner, with its own Settings toggle (independent of the floating overlay)
- Provider logos replace the text labels in the overlay and taskbar strip to save space
- Always-on overlay: a minimal, draggable usage readout that stays above other windows without taking focus (tray menu and Settings toggle; position is remembered)
- Crash log at `%LOCALAPPDATA%\CodexBarWin\crash.log`
- Developer mode for testing without CodexBar CLI
- Sample JSON data files for Claude, Gemini, and Codex providers
- Settings toggle to enable/disable developer mode

### Changed
- README rewritten for the fork: quick start (WSL, sign-ins, install), how to read the bars, what the fork adds, troubleshooting; new screenshots of the flyout, detail page, floating overlay, taskbar strip and Settings

### Fixed
- Claude "Command timed out after 30 seconds": the app had a hidden 30-second cap on every WSL command that fired before the provider's own 45 to 60 second timeout, and Claude's CLI route had become slow (about 18 seconds, then failing). Claude now uses the fast OAuth route first (about 1 second), keeps the CLI as a fallback that is skipped for 6 hours after it fails, and backs off for 10 minutes if Anthropic rate limits; the WSL cap is never shorter than the provider timeouts
- Enabling, disabling or reordering providers in Settings now takes effect immediately; previously the list was only built at startup, so a newly enabled provider didn't appear until the app was restarted
- Reset times read consistently as "Resets in 4h 59m" on the cards and detail page, and the detail page no longer squeezes or garbles the usage text
- Flyout not appearing over other programs: it now takes the foreground reliably and stays above windows that become topmost
- "Command failed (exit code 3)" for Claude: results are cached for 5 minutes and the last good result is shown if Anthropic rate limits
- Gemini windows are labelled "5-hour" and "Weekly" instead of "Pro" and "Flash"

## [0.1.0] - TBD

### Added
- System tray integration with Windows 11 native UI
- Multi-provider support (Claude, Codex, Gemini)
- Dark mode support with Mica backdrop
- Auto refresh functionality
- Settings page for provider configuration
- Window slide-in animation from system tray
- Setup wizard for first-time configuration

[Unreleased]: https://github.com/Wave-OCE/CodexBarWin/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Wave-OCE/CodexBarWin/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/Wave-OCE/CodexBarWin/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Wave-OCE/CodexBarWin/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Wave-OCE/CodexBarWin/compare/v0.9.9...v1.0.0
[0.9.9]: https://github.com/Wave-OCE/CodexBarWin/compare/v0.9.8...v0.9.9
[0.9.8]: https://github.com/Wave-OCE/CodexBarWin/compare/v0.9.7...v0.9.8
[0.9.7]: https://github.com/Wave-OCE/CodexBarWin/compare/v0.9.6...v0.9.7
[0.9.6]: https://github.com/Wave-OCE/CodexBarWin/releases/tag/v0.9.6
[0.1.0]: https://github.com/nek0der/CodexBarWin/releases/tag/v0.1.0
