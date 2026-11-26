# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.6.1] - 2026-11-26

Both fixes apply only if you open the dashboard. Install 1.6.1 over 1.6.0; settings and history are kept.

### Fixed
- Opening and closing the dashboard no longer leaks about 30 GDI objects (Windows drawing resources) and 40 handles per cycle from WinUI 3 creating and destroying a custom title bar window. In 1.6.0, roughly 300 opens without a restart would hit Windows' default 10,000 GDI object limit and stop the app from drawing. The window is now kept hidden after its first open, builds fresh content each time, and returns as a normal window if closed in full screen or kept on top. Keeping it hidden costs no extra memory (about 22 MB idle private memory after one open and close, versus about 30 MB in 1.6.0).
- Closing the dashboard no longer fails partway through cleanup. In 1.6.0, that error happened on every close, skipping the rest of cleanup and logging "Dashboard page cleanup failed" to `app.log`. Any future cleanup failure now logs the error type and stack frames to `crash.log` (never messages or paths).

## [1.6.0] - 2026-11-26

The first release as AgentsWinBar, and the first that reads everything natively on Windows.

### Changed
- Renamed to AgentsWinBar. The package, executable, data folder (`%LOCALAPPDATA%\AgentsWinBar`) and release asset names change with it. On its first start AgentsWinBar copies the settings, caches, history, cost ledger and cost index of an installed CodexBarWin (the old files stay in place), and setup prints the command that removes the old app.
- Usage is read natively on Windows by default, with no WSL virtual machine during a refresh: Claude from Claude Code's sign-in, Codex from the Codex CLI or app sign-in, and Gemini from the Antigravity (agy) sign-in in Windows Credential Manager. Every sign-in is only read, never written. The WSL route stays available as an opt-in.
- Costs come from Claude Code and Codex session logs on Windows, including compressed `.jsonl.zst` Codex archives, priced per model (bundled prices, refreshed from models.dev at most once a day, overridable with `custom-pricing.json`). A local index makes later scans incremental, and scans run only while the flyout or a dashboard is open or after Refresh.
- Setup installs without WSL by default; `-WithWsl` keeps the old route.
- Much lighter: a median working set of about 37 MB at idle (1.5.4: 133 to 157 MB, plus a 1.3 to 1.7 GB WSL virtual machine during every refresh), about 17 MB in Task Manager's Memory column, and about 0.2 CPU seconds per refresh (was about 10 across Windows and the virtual machine). [PERFORMANCE.md](PERFORMANCE.md) says how this was measured.
- The dashboard's graph switches are check boxes.

### Added
- A Sign in button for a signed-out provider, on the dashboard, on the provider page and in Settings > Sign-ins, opening that provider's official sign-in.
- An expired sign-in is renewed by the provider's own tool: `claude auth status --json` for Claude Code and `agy models` for Antigravity, hidden, with its output discarded, at most every 30 minutes and backing off to every 2 and then 6 hours while renewal keeps failing. `RenewClaudeSignIn` and `RenewGeminiSignIn` in settings.json turn them off.
- Release builds keep two small local logs in the data folder: `app.log` for warnings and `crash.log` for unexpected errors, each capped at 1 MB with one older copy. They record exception types and stack frames, never exception messages, file paths or process output.
- The Gemini provider card on the dashboard shows this month's Gemini API spend, estimated at list prices from token counts in a local spend log, with cached input at the cache rate when the log records it (hidden when no log exists). The spend log path can be overridden through the `CODEXBAR_GEMINI_SPEND_LOG` environment variable.
- **Settings > Anthropic API credits** sets up the dashboard's API credits panel without editing environment variables by hand: a link to create an Admin key, a box to paste it, and **Save key**, which checks the key with one read-only request and saves it to the `ANTHROPIC_ADMIN_KEY` user environment variable, in use at once with no restart. It shows whether the key works (with only its last 4 characters) and can replace or remove it. The dashboard's Claude panel links to it with **Open Settings**.
- The taskbar strip supports UI Automation's Invoke pattern, so assistive tools can open the flyout.
- The taskbar strip no longer covers Start and search: when Start is at the taskbar's left end (Windows 10, or a left-aligned Windows 11 taskbar) it sits just left of the system tray and follows the tray as it grows or shrinks. **Settings > Taskbar display > Position** chooses Automatic, Left end, or Right end.

### Security
- Setup trusts and installs only packages signed by the release certificate (thumbprint `4E280F44AE28900533BBC497F28F62505990F3BD`) and stops when no certificate is pinned; before, any certificate named `CN=CodexBarWin OSS` was accepted.
- Releases are signed in a separate job that checks out and builds nothing; the signing key is selected by thumbprint so its password never reaches a command line, every workflow action is pinned to a commit SHA, and the release fails when the signing secret differs from the installer's pin.
- The Sign in buttons start each provider's tool by its full path from the system folder, with Command Prompt's AutoRun and current-folder search off, so a same-named file in the home folder cannot run instead.
- Setup reads the repository from git only when the script sits in that checkout, and reads package manifests with DTDs prohibited and a size cap.
- A malformed token in a provider's sign-in is treated as unreadable instead of reaching an HTTP header.

### Fixed
- Windows notifications now appear: usage alerts, early-reset alerts and the Settings test notification were refused by Windows, because the package did not declare the notification handler a packaged app needs. The test button now says whether the notification was sent, is turned off in Windows Settings, or was refused.
- Usage alerts are remembered across restarts (`alerts-sent.json`), so each fires once per usage window instead of again every time the app starts.
- A click on a flyout button no longer leaves the app drawing frames in the background (15-30 ms of CPU every second until it restarted).
- Gemini renewal no longer runs up to a minute early and then waits 30 minutes; a token that has not expired yet is still used.
- Codex and Gemini respect HTTP 429 and its Retry-After, as Claude already did; signing in again is checked right away instead of waiting out the old limit.
- A fresh, unused Gemini account shows its limits instead of an error.
- Cost totals: a damaged or outdated cost index is rebuilt from every log (it could be refilled only from changed files and show totals far too low), one log line with invalid UTF-8 no longer stops cost scans, and the full index check runs once per start instead of on every update. Daily totals follow a change of Windows time zone after a restart, and a Claude model the price catalog lists without cache rates uses the bundled cache rates (or stays unpriced) instead of the full input rate.
- The log folder watchers stop 15 minutes after the last cost view closes.
- The one-time import from CodexBarWin copies the cost index as a consistent snapshot and retries files that failed.
- A Claude Code installed through npm (claude.cmd) gets a message to open Claude Code, since background renewal needs claude.exe.
- A failed save of an early-reset record no longer breaks later refreshes, and a sign-in that writes its file more than once is still picked up.
- A clean install with `-WithWsl` now points the app at the WSL route it set up.
- A closed dashboard is fully released (page, backdrop and animation listeners), and its refresh spinner stops when it closes mid-refresh.
- The app starts even when the Windows taskbar is not ready yet.
- An expired sign-in shows the last reading as stale instead of signed out.
- Failures in the strip's and overlay's background checks are recorded in crash.log instead of being ignored.
- Resizing the dashboard window no longer crashes the app. The Needs attention list used a WinUI grid layout whose measure could fail with a division by zero while the window changed size; it now uses the app's own column layout.
- The tray flyout grows to show every provider card when the screen has room, instead of one and a half cards, and every card's bars end at the same place. After a restart within one refresh interval (every update), its first open no longer shows an empty list.
- Usage graphs: a projection above 100% stops on the limit line with a dot instead of running off the chart, lines at 0% and 100% are drawn whole, and the chart legend is readable in both themes.
- Plan names reported in lowercase (Codex "team") show as "Team", and the dashboard header wraps in a narrow window instead of being cut off.

## [1.5.4] - 2026-11-03

### Added
- The taskbar strip's right-click menu has an "Open dashboard" item, next to "Open details".

### Changed
- When a window's longer partner is used up (the weekly at 100% while the session is not, or "External 7d" at 100% while "External 5h" is not), the strip and the overlay now show the longer one in place of the short window that cannot be used. An Antigravity row that was showing two empty bars now shows its red limit bar.

### Fixed
- Fix: Claude cost scans no longer stall on large logs; capture limits preserve CLI memo writes, scan deadlines adapt, scans finish after windows close, and cached failures are marked stale.
- Signed-out providers are flagged on the dashboard with sign-in instructions, while their last known usage remains visible as stale data.

## [1.5.3] - 2026-10-16

### Fixed
- A reset animation on the taskbar strip or the overlay no longer plays a second time when another provider's numbers arrive a few seconds later. The strip and the overlay kept their memory of what they had already played in a lookup keyed by the window's XamlRoot, a Windows object that is recreated after a garbage collection, so the memory was lost and the next rebuild replayed the reset. Each display now owns that memory and hands it to its bars directly, which also keeps the wait for a running animation intact.

## [1.5.2] - 2026-10-16

### Fixed
- A reset that happens while the display is off, or while a fullscreen app hides the taskbar strip or the overlay, is no longer missed. The strip and the overlay hold the animation (as they already did for a locked PC) and play it once, one second after they are back on screen so the start is not cut off. A reset that happens while they are on screen still plays at once.
- A reset animation on the taskbar strip and the overlay now plays once and to the end. Those displays recreate their bars whenever their content changes, which cut the animation short and made each new bar play the same reset again (2 to 4 times after an unlock). Each display now remembers what it played per bar and waits for a running animation to finish before it rebuilds.
- Switching Windows battery saver on or off no longer restarts the wait for the pending refresh, so a laptop that is plugged in and out repeatedly can no longer keep pushing refreshes back. The refresh interval shown stays whole.
- The taskbar strip and the overlay no longer stay frozen after the PC has been locked. Windows' unlock notification never reached them, so they stopped keeping themselves on top, checking for fullscreen apps and refreshing their time; they now check for the unlock every 30 seconds while locked.
- The strip and the overlay come back when a fullscreen app ends without another window taking focus (they stayed hidden).
- The overlay is moved back on screen after a monitor change even when its usage data is unchanged.
- The dashboard no longer redraws every 30 seconds while the PC is locked.
- The API credits poll no longer runs while the PC is locked, even with the dashboard window left open. It resumes on unlock.
- The API credits breakdown no longer shows old per-person amounts next to a freshly fetched total, and it is left out when negative adjustments mean the contributors cannot be reconciled with the total.
- API credits from a finished billing cycle are no longer shown as the new cycle's spend after the renewal day passes or the budget settings change, and the days left and daily pace follow UTC midnight.
- A slow or older API credits request can no longer overwrite a newer result, and a request still running is cancelled when settings change or the app exits.
- A provider's usage now appears as soon as it is ready instead of waiting for the slowest provider's status check.
- Cost scans that finish while the app is closing are saved to the cost history before it exits.
- The output of WSL commands is capped (4 MiB per command, 32 MiB per batch). A runaway command now fails with a clear message instead of growing memory without limit.
- The cost history keeps days it already recorded when the CLI's logs rotate, and lowers a past day's cost only when that day's token counts are unchanged.
- Cost history dates no longer break, or count a day twice, when Windows uses a non-Gregorian calendar such as the Thai one.
- A cost history file with missing or null entries is recovered from its backup instead of failing later.
- Last-30-days cost totals now include days the CLI's logs no longer hold after fresh scans as well as stale ones, without counting a day twice.
- `tools\cost-ledger-report.ps1` reads the installed app's cost ledger by default; `-Path` still selects another file.
- The floating overlay and the taskbar strip no longer change size when a number gains or loses a digit. They used to grow a few pixels when a bar reached 100% and shrink again afterwards, which made the panel move. Every percentage now has room for three digits, and the gap between a bar and its number is a little tighter (6 instead of 8) so the panel stays evenly padded.
- Settings: the Reset animation box now shows the saved mode as soon as Settings opens (it stayed blank until you picked a value), and the App theme box is no longer cut off to "Use system setti". Both dropdowns sit under their labels, at the same width.
- Dashboard: a NEEDS ATTENTION entry that lists many windows now ends in an ellipsis instead of being cut off mid-word; the full text is its tooltip.

### Changed
- A release tag now runs the unit tests and the license check before anything is built, and the license check fails on a GPL-family license, a package without license information or a missing report. Licenses that are a file or a URL instead of an SPDX identifier (Microsoft's Windows App SDK and WebView2 packages) are listed as warnings, so there is no approval list to maintain. The installer passes its administrator step in one encoded command, copies the Windows App Runtime installer into a protected folder before running it, and can pin the release signing certificate once its thumbprint is set.

## [1.5.1] - 2026-10-15

### Fixed
- A usage bar's reset animation no longer plays on a locked screen, where it was missed. A reset detected while the PC is locked now plays once, on each bar, when you unlock (or resume from sleep to the desktop).

## [1.5.0] - 2026-10-15

### Added
- An API credits tracker on the dashboard shows organization API spend against a monthly budget: spent, remaining, percent used, days left, pace per day, and who contributed when a breakdown is available. Spend is read with an Anthropic Admin key from the `ANTHROPIC_ADMIN_KEY` environment variable and is never saved to disk. It refreshes only while the dashboard is open. Settings has a Monthly API budget (USD, default 200) and a Credit renewal day (UTC).
- A NEEDS ATTENTION list at the top of the dashboard names windows that are at their limit, will run out before they reset, or are near their limit, and says "All clear" when nothing is wrong. Providers showing old or last-known data are marked stale, and windows that reset together are grouped.
- A GRAPHS section below the provider panels shows each provider's burndown charts and daily cost chart. Settings has a Show graphs option for each provider.

### Changed
- The dashboard is rebuilt around one panel per provider; the old usage overview, mini bars and usage cards are gone. The diagnostics now sit in a collapsible section, and the even-use guide line on burndown charts is fainter so the usage line reads first.
- Usage refreshes back off properly when nothing is changing. Idle rolling windows (for example Gemini 5 hours, Codex session) report a reset time that drifts on every refresh, which used to count as a change and kept the adaptive refresh from ever slowing down; a reset time must now move by more than an hour to count.
- Battery saver doubles the refresh delay, up to 30 minutes, and applies the change to the pending refresh straight away.
- Less background work while the PC is locked or the window is hidden: no cost scans or billing polls while locked, the dashboard clock stops when it is minimized, and the taskbar strip and overlay stop their keep-on-top timers on lock and skip redraws when nothing visible changed.
- Cost scans for several providers now write the cost ledger once instead of once per provider, and the usage cache is no longer saved twice after a refresh.

### Fixed
- Usage alerts ignore stale readings and windows that are already past their reset time.
- A damaged usage history file is skipped instead of failing the load, and history is never saved before it has finished loading.
- The WSL shim no longer replays a cached reading that contains a reset time already in the past.

### Known limitations
A refresh that rebuilds the bars within 15 seconds of a detected reset can replay the reset animation once.

## [1.4.0] - 2026-10-14

### Added
- Usage refreshes two seconds after the next window reset (with one retry 15 seconds later if the provider has not rolled over yet), so cleared limits appear sooner. Each reset refresh is logged as a new or still-old window.
- A usage bar plays a short green animation when its own window resets: a ring draws around the bar, the previous fill turns green as the ring fades, the green collapses to the left and hands over to a glow that sweeps across. A session reset animates only the session bar, a weekly reset only the weekly bar.
- A Reset animation setting (Settings) with Always on, Always off, or Off in battery saver (the default). The animation is also skipped when Windows animations are off.

### Known limitations
A refresh that rebuilds the bars within 15 seconds of a detected reset can replay the animation once.

## [1.3.0] - 2026-10-11

### Added
- A RESET EARLY chip, recorded usage drop, and optional toast identify a fresh drop of 10+ points before a known scheduled reset, with a reset-time shift of over 10 minutes or usage at 2% or below. Fresh baselines survive replays and restarts; plan, limit, or window-length changes start a new baseline.

### Fixed
- Early-reset details retain the recorded destination as usage rises. Notifications are saved before display, and pending writes are flushed on exit.
- A rate-limited Claude refresh serves cache immediately. The CLI fallback has a 25-second timeout with a 3-second kill grace, is skipped when timeout is unavailable, and uses a 90-second in-flight stamp while running. Its 30-minute failure cooldown starts only after a failed attempt returns.

### Known limitations
Early-reset detection cannot tell an account switch from a reset; if saving early-resets.json fails, a toast may repeat after a restart.

## [1.2.1] - 2026-10-11

### Fixed
- A cached reading replayed by the WSL shim inside its 10-minute window now counts as fetched now, so Claude no longer shows "Stale data" on every other refresh. Older replays keep their original time and stay stale.

## [1.2.0] - 2026-10-11

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

[Unreleased]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.6.1...HEAD
[1.6.1]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.6.0...v1.6.1
[1.6.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.5.4...v1.6.0
[1.5.4]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.5.3...v1.5.4
[1.5.3]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.5.2...v1.5.3
[1.5.2]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.5.1...v1.5.2
[1.5.1]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v0.9.9...v1.0.0
[0.9.9]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v0.9.8...v0.9.9
[0.9.8]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v0.9.7...v0.9.8
[0.9.7]: https://github.com/thomasyuxizeng/AgentsWinBar/compare/v0.9.6...v0.9.7
[0.9.6]: https://github.com/thomasyuxizeng/AgentsWinBar/releases/tag/v0.9.6
[0.1.0]: https://github.com/nek0der/CodexBarWin/releases/tag/v0.1.0
