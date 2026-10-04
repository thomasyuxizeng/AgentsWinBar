# Handoff: cost totals, provider status badges, burndown chart

Paste the prompt below into the coding agent, with the repository root (`CodexBarWin`, branch
`overlay-and-cli-fixes`) as its working directory. Nothing for these features is implemented yet:
what exists is research, recorded here so it doesn't have to be redone.

---

## Prompt

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
  (about 5 s). `/mnt/c/Users/<user>/.codex` also exists; `CODEX_HOME` is the expected variable for Codex but was
  **not verified**, so test it. Build the command in the app: take `Environment.SpecialFolder.UserProfile`,
  convert `C:\Users\x` to `/mnt/c/Users/x`, and only set a variable when that directory exists on Windows.
  Reject any path containing a quote or newline and single-quote the value (it goes into a shell command).
  Fall back to the WSL home when the Windows folder is missing. Apply these variables to the `cost` command
  only, never to `usage` (it would change authentication).
- `sessionCostUSD` / `sessionTokens` are probably "today" but this is **unverified**: compare with the latest
  `daily` entry and label accordingly.
- `antigravity` cost works from WSL logs (small numbers). Map the app's `gemini` provider to `antigravity` for
  cost the same way the shim maps it for usage. Claude and Codex are the main use.
- A scan takes 0.3 to 5 s, much heavier than usage. Do **not** run it on every refresh: fetch on a slow
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
