# Handoff: cost totals, provider status badges, burndown chart

Paste the prompt below into the coding agent, with the repository root (`CodexBarWin`, branch
`overlay-and-cli-fixes`) as its working directory. The original research and spec are preserved below.
Provider status badges and cost totals are implemented on `codex/provider-status-badges`; history remains pending.

### Feature 1 verification (2026-10-04)

- Baseline: 249 unit tests passed. Status support adds 41 cases (290 passing in total), including
  source-generated JSON parsing, cache round trip, service commands, palette/severity, missing Gemini status,
  unknown indicators and safe status-page URLs.
- Re-ran the installed shim in WSL with `usage --provider <id> --format json --source cli --status`.
  Claude returned `none` / "All Systems Operational" in about 1 second; Codex returned the same in about
  4 seconds; Gemini returned usage without `status` in about 10 seconds. All three returned usable usage
  data without errors. Timings vary with WSL startup and provider/cache state.
- Incident notifications are optional and were not added. Missing status stays grey on cards and has no
  banner or compact incident dot; unknown reported indicators are grey with a detail banner.
- Release MSIX build passed (two existing environment warnings: missing `win-x64.pubxml` and symbol
  packaging tool `mspdbcmf.exe`). Signed with the existing trusted certificate, verified the signature,
  installed local validation version `0.9.5.1`, and launched the packaged app. The installed process
  remained responsive with no new crash log. Interactive visual checks of the card tooltip, incident
  banner/link and compact incident dots were not possible with the available desktop controls; live
  providers were operational or had no status. These visual states remain unverified.
- Local packaging pitfall: `dotnet build ... -p:AppxVersion=...` alone left the package at the manifest's
  `0.0.0.0`. Temporarily set the source manifest's Identity Version to `0.9.5.1` for validation, then
  restored its original version and encoding. No release version, tag or remote push was made.

---

### Feature 2 research and verification (2026-10-04)

- **Verified `CODEX_HOME`**: without an override a refreshed Codex scan returned zero. With the Windows
  `.codex` directory it returned real log totals. A full scan took about 14 seconds, longer than the
  original 0.3–5 second estimate; a later incremental scan took 2.5 seconds.
- **Verified session means today's calendar day**: Codex's session cost/tokens exactly matched its
  `2026-10-04` daily entry. Claude's session totals likewise matched its `2026-10-04` entry. Antigravity
  had only a `2026-09-29` entry and returned zero session totals, confirming that session is not "latest
  daily entry" or "most recent coding session". WSL timezone was +09:00, matching Windows for this check.
- **Shim fix required**: Claude usage caching matched cost commands too, ignoring log-path environment
  changes and swallowing `--refresh`. Cost now passes directly to the real CLI before usage routing and
  caching. Reinstalled using `tools/wsl/install-wsl-shim.ps1`; refreshed scans through the shim returned
  the real Windows totals (Claude 0.54 seconds, Codex 2.5 seconds).
- 342 unit tests passed, covering JSON and empty/token-only totals, Windows paths and shell rejection,
  formatting, calendar series, model aggregation, scan cadence, manual refresh, failure backoff,
  cancellation, provider mapping and the persisted Settings toggle. Release MSIX build passed with the
  same two environment warnings as feature 1. Signed, signature-verified, and installed local validation
  package `0.9.5.2`; interactive visual checks are pending the computer-use gate described below.
- The computer-use API was tried against both launch and a flyout opened by the user. Both returned
  `Computer Use was not approved to use CodexBarWin`. Visual checks remain outstanding until that
  tool-level gate permits access; full filesystem access did not resolve it.

---

## Prompt

> Cost research was verified on 2026-10-04: `CODEX_HOME` works and session cost/token totals mean today's
> calendar day in the CLI's local timezone. Cost scans must bypass the shim's usage cache. See the
> feature 2 verification notes below for measurements and the cache bug.

You are working in CodexBarWin, a WinUI 3 / .NET 10 Windows 11 tray app (MVVM with CommunityToolkit) that
shows AI usage limits for Claude, Codex and Gemini. It runs the CodexBar CLI inside WSL
(`wsl -d Ubuntu-24.04 -- bash -ic "codexbar ..."`) and draws the result. Read `CLAUDE.md`, `.claude/rules/*.md`
and `README.md` first and follow them (English only, `ILogger` not `Debug.WriteLine`, `SafeFireAndForget()`,
no bare `async void` except handlers, `ProviderConstants.ValidateAndNormalize()` before any WSL command that
contains a provider name, no GPL dependencies, no hardcoded secrets, tests for `Services/`, `Models/` and
`Helpers/`).

Implement three features, in this order, as separate commits. Update `CHANGELOG.md` under `[Unreleased]` and
the README feature list for each.

### 1. Provider status badges (smallest, do first)

The CLI reports whether the provider has an incident when `--status` is added to `codexbar usage`:

```json
"status": { "url": "https://status.claude.com/", "description": "All Systems Operational",
            "indicator": "none", "updatedAt": "2026-10-04T09:45:21Z" }
```

`indicator` is a Statuspage indicator: `none`, `minor`, `major`, `critical`, and (Statuspage) `maintenance`;
treat anything unknown as unknown, not as an error.

- Add `--status` to the usage command in `Services/CodexBarService.cs` (`FetchProviderAsync`) and parse the
  top-level `status` object in `Models/AppJsonSerializerContext.cs` (`UsageDataDto`). Add a `ProviderStatus`
  record and indicator enum to `Models/UsageData.cs`. Note `UsageData.Status` already exists and actually
  holds the CLI *source* string; do not reuse it. Name the new property something like `ServiceStatus`.
- Measured through the shim: Claude 0.6 s and Codex 1.8 s with `--status`, both returning a status.
  **Gemini returns no `status` field** (it is served from Antigravity by the shim); that must render as "no
  status", never as an error or a red badge.
- UI: the provider card header already has a green dot (`Controls/ProviderCard.xaml`). Make it reflect status
  (green operational, amber minor or maintenance, red major or critical, grey unknown) with a tooltip of the
  description. On the detail page (`Views/ProviderDetailPage`) show a banner with a link button to `url`
  whenever the indicator is not `none`. On the overlay and taskbar strip (`OverlayWindow`, `TaskbarWindow`) put a
  small coloured dot on the provider logo only when there is an incident (no dot when all is well, to keep them
  minimal).
- Optional: a Windows notification when a provider newly goes to major or critical, once per incident, behind
  the existing "Usage alerts" setting (`UsageAlertEvaluator`).
- Tests: parsing (all indicators, missing `status`, unknown indicator) and the indicator to colour/severity
  mapping, as pure helpers in `Helpers/`.

### 2. Cost and token totals

`codexbar cost --provider <claude|codex|antigravity> --format json` reads local session logs and prints:

```json
[{"provider":"claude","last30DaysCostUSD":71.85,"last30DaysTokens":226356190,
  "sessionCostUSD":20.75,"sessionTokens":73233194,"currencyCode":"USD","historyDays":30,
  "provenance":"listPriceEstimate","coverage":{"priced":8,"unpriced":0,"estimated":0,"unmetered":0},
  "daily":[{"date":"2026-09-23","totalCost":7.19,"totalTokens":15308958,"inputTokens":218,
            "outputTokens":93687,"cacheReadTokens":14922923,"cacheCreationTokens":292130,
            "modelsUsed":["claude-opus-5-5"],
            "modelBreakdowns":[{"modelName":"claude-opus-5-5","totalTokens":15308958,"cost":7.19}]}],
  "projects":[],"totals":{"totalTokens":226356190,"totalCost":71.85}}]
```

Important findings:

- **The CLI reads logs from inside WSL, and the user's real Claude Code and Codex logs are on Windows.** Plain
  `codexbar cost` in WSL returns zeros. It returned real data when pointed at the Windows folders:
  `CLAUDE_CONFIG_DIR=/mnt/c/Users/<user>/.claude codexbar cost --provider claude --format json --refresh`
  (about 5 s). `/mnt/c/Users/<user>/.codex` also exists; **`CODEX_HOME` was verified** for Codex on 2026-10-04.
  Build the command in the app: take `Environment.SpecialFolder.UserProfile`,
  convert `C:\Users\x` to `/mnt/c/Users/x`, and only set a variable when that directory exists on Windows.
  Reject any path containing a quote or newline and single-quote the value (it goes into a shell command).
  Fall back to the WSL home when the Windows folder is missing. Apply these variables to the `cost` command
  only, never to `usage` (it would change authentication).
- `sessionCostUSD` / `sessionTokens` were **verified as today's calendar-day totals** in the CLI's local
  timezone (Claude and Codex matched today's daily entry; Antigravity with only older data returned zero).
- `antigravity` cost works from WSL logs (small numbers). Map the app's `gemini` provider to `antigravity` for
  cost the same way the shim maps it for usage. Claude and Codex are the main use.
- A scan takes about 0.3 to 15 s here, much heavier than usage. Do **not** run it on every refresh: fetch on a slow
  cadence (for example every 15 minutes, on first load, and on manual refresh), cache the last result, and keep
  failures silent (hide the section, log at debug).
- `provenance: "listPriceEstimate"` means list-price estimates, not billing. Say "estimated" in the UI.
- UI: a one-line summary on the flyout card ("Today $x, 15K tokens · 30 days $y, 218M tokens", with compact
  token formatting) and a Cost section on the detail page: last-30-days total, a 7 to 30 day daily bar chart,
  and the top models by cost. Add a Settings toggle "Show cost and token totals" (on by default). No cost on the
  overlay or strip.
- Add `ICodexBarService.GetCostAsync`, a `CostData` model, DTOs in the JSON context (the project uses
  source-generated System.Text.Json, so every new type needs `[JsonSerializable]`), and pure helpers for
  formatting and the daily series. Tests for parsing (including empty `daily`), formatting, WSL path conversion
  and the quoting/rejection rule.

### 3. Burndown / history chart

Record usage snapshots and draw a per-window burndown on the detail page.

- New `UsageHistoryStore` service: persists samples `{ timestamp, provider, windowKey, usedPercent, resetsAt }`
  for the session, weekly and extra windows to a JSON file next to `settings.json` (see `SettingsService` for
  the packaged path). Keep it pure and testable: an in-memory class with add, dedupe and prune logic, and a
  thin persistence layer. Dedupe: store a sample when usage changed by 0.5 points or more (match
  `UsageChangeTracker`), or 10 minutes have passed, or the window reset. Prune to the last 14 days. Write
  atomically and throttled. Record from `MainViewModel.RefreshAsync` after each provider result.
- A pure `BurndownBuilder` in `Helpers/`: for the **current** window only (from `resetsAt` and `windowMinutes`),
  return points as (fraction of the window elapsed, used %), an even-use guide from (0, 0) to (1, 100), and a
  projection from the latest point at the current rate to the reset. Handle: fewer than 2 samples (show
  "collecting data"), a reset mid-series, unknown window length, clock skew.
- A `BurndownChart` control built from `Canvas` and `Polyline`/`Path` (do **not** add a charting package
  unless its licence is MIT or Apache-2.0 and CI's licence check passes). Show it under each main window card on
  the detail page, with the capture time and sample count. Use the existing colours: blue usage, lavender time,
  amber and red when 80 and 95 percent, provider accents for extra windows.
- Tests for the store (dedupe, prune, persistence round trip) and `BurndownBuilder`.

### Definition of done

- `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64` passes (249 tests at the start; keep adding).
- New pure files are added to the test project, which links source files individually
  (`<Compile Include=... Link="Linked\...">` in `CodexBarWin.Tests.Unit.csproj`).
- No change to the existing look of the overlay and strip beyond the optional incident dot.
- Everything degrades quietly: no WSL, a provider without status, empty cost, no history yet.

### Environment notes (read before running anything)

- **Unsigned builds will not run** on this machine (Application Control blocks `bin\` output). Verify by
  building a signed MSIX and installing it: see "From source" in `README.md`. Settings and history for the
  installed app live under `%LOCALAPPDATA%\Packages\CodexBarWin_w05y65rx3mcz6\LocalCache\Local\CodexBarWin\`.
- The `Samples\` folder (developer mode) is read from the install directory, which is read-only for the
  packaged app, so mock data cannot be injected into the installed build.
- WSL quirks: the distro shuts down when idle (no background processes survive), PowerShell piping into WSL
  adds CRLF (send scripts base64-encoded: `wsl -d Ubuntu-24.04 -e bash -c "echo <b64> | base64 -d | bash"`),
  and the first command after idle can take 5 to 10 s.
- The shim `tools/wsl/codexbar-shim.sh` fronts the CLI. It maps `gemini` to `antigravity`, caches Claude for 5
  minutes and passes other arguments through. After editing it, reinstall with
  `tools\wsl\install-wsl-shim.ps1 -Distro Ubuntu-24.04`.
- Never use `Remove-Item` on scratch paths under `%TEMP%` in this environment (a guard blocks it); use other
  means or leave files.
- To release: bump `AppxVersion`, tag `vX.Y.Z` and push; `release.yml` builds and publishes the MSIX.

---

## Where to point it

- **Repository:** `Wave-OCE/CodexBarWin` (private), branch `overlay-and-cli-fixes` (the default).
  Local clone: `C:\Users\ThomasFramework\Documents\CodexBarWin`.
- **This file:** `docs/HANDOFF-cost-status-burndown.md`.
- Files it will touch first: `Services/CodexBarService.cs`, `Services/ICodexBarService.cs`,
  `Models/UsageData.cs`, `Models/AppJsonSerializerContext.cs`, `ViewModels/MainViewModel.cs`,
  `Controls/ProviderCard.xaml(.cs)`, `Views/ProviderDetailPage.xaml(.cs)`, `OverlayWindow.xaml(.cs)`,
  `TaskbarWindow.xaml(.cs)`, `Views/SettingsPage.xaml(.cs)`, `ViewModels/SettingsViewModel.cs`,
  `CodexBarWin.Tests.Unit/CodexBarWin.Tests.Unit.csproj`.
