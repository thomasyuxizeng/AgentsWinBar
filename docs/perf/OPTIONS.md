# Cutting CodexBarWin's CPU and RAM: options and recommendation

Status: revision 2, 25 Nov 2026, after the council review (`docs/perf/COUNCIL.md`) and the owner's decisions on it.
Evidence: `docs/perf/MEASUREMENTS.md` (measured on the owner's laptop) and the two spike reports,
`SPIKE-native-usage.md` (branch `perf/native-usage`) and `SPIKE-native-costscan.md` (branch `perf/native-costscan`),
merged on `perf/native-reads` (2,103 tests passing on the merged tree).

## The owner's requirements

1. As little CPU and RAM as possible. Disk space is free and may be spent to save either.
2. Full feature parity: usage for Claude, Codex and Gemini (Antigravity), including extra windows; API credits and
   Gemini API spend; costs from the Claude and Codex logs; provider status; alerts and early-reset detection.
3. Easy setup and upkeep of signed-in accounts: no sign-ins beyond the ones already used on Windows.

The owner does not use WSL or Docker for anything else, so once the app stops needing WSL, nothing does.

## Where the cost is today (measured)

Every refresh (every 5 minutes on this laptop) costs, on the cold path the app always takes:

| Part | RAM | CPU | Source |
|---|---|---|---|
| WSL VM boot | ~315 MB inside the guest | ~1.5 s (one boot) | guest meminfo with only the probe running; busy ticks at first sample |
| Loading the programs below from the VM's disk (page cache) | ~650 MB | (in the next row) | page cache 90 -> 743 MB at the first refresh |
| Programs: agy 2 to 5 launches (5 when its updater runs; largest 241 MB resident), Codex app server (Node + native, ~160 MB), CodexBarCLI x3 (~50 MB each), python3 | up to 206 MB of process memory (AnonPages) at once | ~6.3 s (one refresh) | `/proc` samples, `results/vm-attribution-run2.txt` |
| Host-side VM overhead not attributed in the guest | ~180 MB | not visible from Windows | host minus guest at idle boot |
| Host total for the VM (vmmemWSL) | **1,326-1,657 MB for 33-47 s** | not visible from Windows | 4 refreshes (2 with saved summaries) |
| App process work per refresh | (resident) | **~2.5 s** | 2 refreshes |

So about **10 CPU-s and a 1.3-1.7 GB spike per refresh**, about 120 CPU-s per hour; the three CPU parts come from
three runs and the total has not been measured end to end. Between refreshes the app idles at ~16 CPU-s per hour and
holds 176-196 MB private bytes (133-157 MB working set). Of that, the .NET heap holds only 7.4 MB live in 55 MB
committed (`dotnet-gcdump` plus `Get-MemoryMap.ps1`). The installed build is a local one with ReadyToRun off, so all
of its code is JIT-compiled at run time.

Hypotheses, not yet shown by measurement:
- **What the other ~104 MB of private memory is.** `Get-MemoryMap.ps1` is a new, unvalidated script; its private
  kinds sum to about 160 of the 179 MB Windows reports. It shows `igc64.dll` (Intel's shader compiler, 83 MB
  mapped) and Direct2D/D3D loaded in the process, which suggests GPU-rendered XAML is a large part, but the split
  between XAML, the GPU driver and other native heaps is not measured. A VMMap or WPA heap trace would settle it.
- **Where the app's ~2.5 CPU-s per refresh goes.** `dotnet-trace` on Windows samples thread time, which counts
  blocked threads too. Its refresh-minus-idle difference points at recreating `ProviderCard` and `UsageMeter`
  controls, rebuilding the taskbar strip, TLS handshakes and the history write, but none of these is a CPU
  measurement. A before/after CPU measurement of each fix is the test.
- **What the app does with 16 CPU-s per hour while idle.** Not diagnosed. Candidates: periodic timers (countdown
  labels, position polling of the strip and overlay), animations running on hidden windows. An ETW trace of an idle
  hour with the dashboard closed is the test. After options 4 and 5 this is the largest remaining CPU term.
- **Guest CPU** (about 6.3 s per refresh) and **VM boot CPU** (about 1.5 s) are single samples.

Already broken today: the app's cost ledger has no Claude day after 20 November, so the WSL Claude cost scan has not
completed since then. Cause not investigated; an earlier stall with the same symptom was fixed on 31 Oct (`d991dfb`,
the scan memo outgrew the 4 MiB write cap), so this may be a recurrence. A native scanner is also a fix, not only a
speed-up.

Two different budgets, then: the **VM episode** (about 90% of the RAM and three quarters of the CPU) and the
**resident app**. A UI rewrite touches only the second.

## Options

| # | Option | VM episode | App per refresh | Resident app | Parity risk | Setup | Effort | Upkeep |
|---|---|---|---|---|---|---|---|---|
| 1 | Tune WSL: switch Codex and Gemini to the CLI's OAuth routes, keep VM settings | ~600 MB and ~2-3 CPU-s left (VM boot + CodexBarCLI x3); estimate | unchanged | unchanged | low | worse: Gemini OAuth needs a separate CodexBar sign-in in WSL; the Codex bridge refuses while `/usr/bin/codex` exists | hours | upstream's |
| 2 | Convert the distro to WSL1 (no VM) | no VM; the same programs still run (~500 MB transient, same CPU); untested | unchanged | unchanged | medium: agy and Codex on WSL1 untested | unchanged | 1 day to test | upstream's |
| 3 | Build the CodexBar CLI for Windows | no VM, but the CLI still launches agy and Codex's app server (Windows builds, similar size) | unchanged | unchanged | high: upstream targets macOS/Linux; untested | better | unknown | upstream's plus a port |
| 3b | Call the Windows builds of the official tools directly (`agy -p /usage`, `codex app-server`), no CodexBar CLI | no VM; agy and the Codex app server still start per read (sizes unmeasured on Windows) | unmeasured | unchanged | low: official clients; they renew their own tokens | good | an hour to test | low |
| **4** | **Native C# reads in the app: HTTPS usage for all three providers, incremental cost scanner with an on-disk index** | **gone: no VM, no child programs** | usage 0.19 s CPU (one sample); cost update ~0.1 s CPU | +few MB (scanner peaks 32 MB private while a full scan runs, once) | usage: matched live; cost: tokens match on recent days only, pricing and log roots open | **best: reuses Windows sign-ins; no WSL** | usage ~1-2 weeks; cost parity and pricing data ~2-3 weeks | high: five undocumented endpoints, three sign-in formats, two log formats, a price list |
| **5** | **Slim the app in place: update UI instead of rebuilding it, destroy windows when closed instead of hiding them, find and remove idle wake-ups, append-only history, GC and ReadyToRun settings** | none | **target ~2.5 s -> under 0.1 s** | maybe -30 to -50 MB (GC commit, JIT; estimate) | low | none | days | none |
| 6 | Split the app: a small always-on core (tray, taskbar strip, overlay, polling) without XAML or the GPU, built with NativeAOT, and the WinUI dashboard as a separate process started on demand | none | low | **core ~15-30 MB (estimate, no prototype); dashboard only while open** | medium: strip and overlay redrawn without XAML, reset animations redone, two UI stacks | none | weeks | medium: two UI stacks and the link between them |
| 7 | Full native rewrite (C++/Rust/Win32) | none on its own | low | ~15-25 MB | high: whole UI redone | none | many weeks | high |

Levers that apply to every option, each cheap:
- **Poll when something can have changed:** refresh on log or sign-in file activity, a visible window, or a known
  reset time; back off to 15-30 minutes otherwise; stop while the screen is locked or off.
- **Provider status on its own cadence** (for example every 15 minutes, with `ETag`/`If-Modified-Since`), not tied to
  the usage read.
- **Cache Antigravity's project lookup** (`loadCodeAssist`) for a day: one TLS handshake fewer per refresh.
- **Cost updates from change notifications** instead of listing 2,340 files every 30 minutes.

### Spike results (option 4)

Usage reads (`SPIKE-native-usage.md`, branch `perf/native-usage`, 24 files):

| One refresh, all three providers in parallel, fresh probe process | Wall | CPU | Allocated |
|---|---|---|---|
| Cold (first call, includes JIT and TLS setup) | 3.5 s | 0.91 s | 0.9 MB |
| Warm (130 s later, new TLS connections, as the app's 5-minute cadence would see) | 2.6 s | 0.19 s | 0.35 MB |
| In-process cost only, replayed responses, after JIT | 1-4 ms | 1-4 ms | 28-51 KB |

Each row is one run of a console probe (timer resolution 15.6 ms), not the packaged app. The whole probe process
peaked at 57 MB working set and 18 MB private, of which ~27 MB / 8 MB is the bare .NET runtime; inside the app, where
the runtime and `HttpClient` are already loaded, the increase should be a few MB (inferred). Wall time is network:
Antigravity's quota summary on `daily-cloudcode-pa` takes about 2 s.

Parity, live, against the app's CLI readings:

| Provider | Result |
|---|---|
| Codex | session 11% vs 10%, weekly 88% vs 88%, resets within 1 s |
| Gemini | 5-hour 29.9% vs 30.8%, weekly 7.5% vs 8.0%, both "Claude and GPT" extras match; resets equal to the second |
| Claude | one pair: 18% vs 16% (the app's reading was 11 minutes older, during active use), weekly 68% vs 68%, Fable-only 8% vs 8%, resets equal. The spike's own comparison read the Wave seat's sign-in because the seat's launcher sets `CLAUDE_CONFIG_DIR`; the pair above is a rerun from a normal environment at 07:35Z, recorded in `SPIKE-native-usage.md`. |

Endpoints: Claude `api.anthropic.com/api/oauth/usage`; Codex `chatgpt.com/backend-api/wham/usage`; Antigravity
`cloudcode-pa.googleapis.com/v1internal:loadCodeAssist` then `daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`.
None is a public API. The usage code imports only `CredReadW` and `CredFree` from Credential Manager, never writes a
sign-in file, and has a host allow list and no redirects.

Cost scanner (`SPIKE-native-costscan.md`, branch `perf/native-costscan`, 13 files; SQLite index through the Windows
inbox library):

| Phase | Wall | CPU | Peak managed | Peak private | Read |
|---|---|---|---|---|---|
| Full scan, empty index, 1 worker, cold file cache | 332 s | 28.6 s | 4.3 MB | 22.5 MB | 10.65 GB, 2,340 files |
| Full scan, empty index, 2 workers, warm file cache (2 runs, straight after the run above) | 11.4-14.7 s | 15.7-22.2 s | 8.8-9.3 MB | 32-33 MB | same |
| Update, nothing changed | 108 ms | 109 ms | | | 0 files |
| Update after 100 new lines | 109-232 ms | 78-156 ms | | | 1 file |

The gap between the first two rows is mostly the file cache, not the worker count: the two-worker runs read files
that the one-worker run had just pulled into memory. A cold two-worker scan is unmeasured, and it is the case the
first real scan will meet. Index on disk: 8.4 MB. After the first scan only appended bytes are read. The two update
targets missed narrowly (109 and 156 ms CPU against 100 ms); the no-change pass opens no files, so its cost is
listing 2,339 files and the SQLite commit, which change notifications would remove.

Parity with the CLI's saved daily totals (26 Oct to 24 Nov):

- Claude: native token counts matched exactly on 20 of 26 days; 6 days differ (largest +336.8M tokens on 20 Nov,
  unexplained). 99.93% of the matched tokens are unpriced: the spike bundled only some rates, and the current models
  (Claude Opus 5.5, Sonnet 5.5, Haiku 5.5) are missing. The reference ledger has no Claude day after 20 Nov, so the
  last 30 days cannot be compared until the WSL scan's stall is understood or a fresh reference is taken.
- Codex: 2 days matched exactly; 4 days have identical tokens and match exactly once the long-context threshold is
  200K instead of 272K; on the 20 days through 16 Nov the current session folder produced no rows at all. Whether
  those logs were archived, deleted or live elsewhere is unverified. 24 Nov differs because the saved snapshot was
  taken before the day ended (verified to the token).
- Not ported: Codex fork baselines, archived sessions, alternative homes and several rarer record shapes; Claude's
  Vertex detection and lexical tie-breaks. 3,523 log lines over 512 KiB were skipped. Effect on cost unverified.

So the engine is validated on tokens for recent days only. Cost parity needs the pricing data, the missing Codex
days explained, the 6 Claude days explained, and the remaining record shapes.

Spike code fixes from the council (in progress, 25 Nov): expired sign-ins shown as stale readings rather than
sign-outs, last good readings for all three providers, redacted credential records, a retry for half-written sign-in
files, default profiles only, and the opt-in Google token refresh removed (branch `perf/native-fixes-usage`); the
cost index re-buckets on a time-zone change instead of failing, reads reports read-only, stops double counting
rewritten files, skips unreadable files, commits in batches and scans at background priority (branch
`perf/native-fixes-cost`).

## Recommendation

Do **4 and 5 together** first. They remove the VM episode entirely (the 1.3-1.7 GB and about 7.8 of the ~10 CPU-s per
refresh), cut the app's own refresh work from ~2.5 s toward 0.1 s, and make setup a single MSIX with no WSL.
Staging, because cost parity is not there yet:

1. Usage reads go native first, per provider, behind a setting that defaults to native (the owner accepts a few days
   of breakage, 25 Nov). WSL is then needed only for cost scans.
2. Native costs replace WSL once each provider reaches 1% daily parity on the logs on disk (the app's own ledger keeps
   older days), after the missing Codex days are found. Claude costs gain the most: the WSL scan has recorded no
   Claude day since 20 November.
3. After that the installer stops requiring WSL.

Option 6 waits (owner, 25 Nov). After stage 1, a day of cheap memory measurements decides it: windows destroyed
instead of hidden, ReadyToRun on, GC conserve-memory, a blank WinUI window as the floor, and a VMMap trace. If the app
is still over the mark, a one-day prototype of a NativeAOT core without XAML comes next. Option 7 buys nothing over 6
and redoes the dashboard. Options 1-3 keep WSL or the child programs and do not meet requirement 3; option 3b is worth
its hour of testing as the renewal path and as a fallback that needs no WSL.

Targets against what the owner approved:

- **CPU per refresh: 0.25 CPU-s or less, everything included.** It is a budget, not yet a measurement:

  | Part | Measured | Budget |
  |---|---|---|
  | Usage, three providers, new TLS connections | 0.19 s (one sample, console probe) | 0.15 |
  | Provider status, three fetches | not measured | 0.04 (on its own cadence, not every refresh) |
  | Cost update | 0.11 s when it runs (every 30 min) | 0.01 averaged (0 with change notifications) |
  | UI update and history write | part of today's ~2.5 s, not measured alone | 0.05 |

  Measured inside the packaged app over 10 or more refreshes, each bounded by in-app start and end markers. Connection
  reuse across refreshes is not assumed: servers close idle connections and `NativeHttp` closes pooled ones after 90 s.
- **Idle CPU: 2 CPU-s per hour or less** (now 16). Needs the idle trace first; no mechanism is shown yet.
- **Resident memory: 90 MB (stretch 60) in Task Manager's memory column (private working set), dashboard closed**
  (owner, 25 Nov), with private bytes reported beside it. The baseline has to be remeasured in that counter: today's
  figures are 142.8 MB working set and 183.7 MB private bytes. Options 4 and 5 alone are estimated at roughly 120-150
  MB private bytes, so they will probably not pass; whether option 6 is needed is what the measurements above decide.

## Sign-in policy (owner, 25 Nov 2026)

Today's WSL route renews tokens as a side effect: every Gemini read runs agy, which renews its own token, and the
shim's Claude route runs the Claude CLI when the token has expired. Native reads do not. So:

- Never write any credential store. Never refresh an Anthropic, OpenAI or Google token from this app: Anthropic and
  OpenAI rotate refresh tokens, so a refresh here would sign the owner out of Claude Code or Codex, and minting Google
  tokens with agy's OAuth client is client impersonation. The opt-in Antigravity refresh built in the spike is removed;
  it is kept on branch `perf/antigravity-refresh-optin`.
- Read the default profiles only: `%USERPROFILE%\.claude\.credentials.json`, `%USERPROFILE%\.codex\auth.json` and the
  `gemini:antigravity` entry in Credential Manager. `CLAUDE_CONFIG_DIR` and `CODEX_HOME` are ignored unless an app
  setting names another path. The path in use shows on the detail page.
- An expired token is not a sign-out. The row keeps its last reading, marked stale with its age; a window whose known
  reset has passed rolls to 0% with a "not read since" note. The sign-in prompt is only for a missing or rejected
  sign-in, naming the exact step.
- Renewal: when the owner opens the flyout or dashboard and a token has expired, run the official tool's cheapest
  renewing command (`claude.exe`, `agy.exe`) at most every 30 minutes, if a one-hour test on Windows shows it is cheap
  in CPU, RAM, seconds and quota. Otherwise stay stale until the owner next uses the tool.
- Codex (10-day tokens, renewed by the Codex CLI): read-only, with a one-line notice if it ever expires.
- Gemini quota also moves when the Antigravity IDE (installed here) or STUDIOPC1 uses it, so a stale Gemini row can
  be behind; the stale label says so.

## Parity matrix

| Feature | Today | Native path |
|---|---|---|
| Claude usage (session, weekly, scoped extras) | CLI in WSL, OAuth endpoint | same endpoint, Windows sign-in |
| Codex usage | CLI in WSL, Codex app server | endpoint from upstream, Windows `auth.json` |
| Gemini usage (5-hour, weekly) and Claude/GPT-on-Antigravity extras | CLI in WSL runs `agy -p /usage`, shim reshapes | Cloud Code quota summary, Credential Manager sign-in, same reshaping |
| Token renewal and stale readings | renewed as a side effect of every read (agy, Claude CLI route) | not renewed by the app; stale last reading with its age; renewal through the official tool on window open if the test passes |
| Codex credits field | in the model, empty for all three providers today | not ported; port if the CLI ever fills it |
| Provider status and incidents | the app's native incident fetcher (`CodexBarService.StartProviderStatusPrefetches`) gets the whole WSL call (7-30 s) plus 500 ms; the CLI's `--status` is a second opinion | the native fetcher alone, on its own timeout and cadence (with the current code it would get about 0.5 s, so this needs the change) |
| API credits | already native (`ApiBillingService`) | unchanged |
| Gemini API spend | already native (`GeminiSpendService`) | unchanged |
| Cost from logs | CLI in WSL over `/mnt/c` every 30 min | native incremental scanner with on-disk index over the Windows log folders (the owner runs no tools inside WSL) |
| Alerts, early resets, history, burndown | app logic on the readings | unchanged |

## Risks

- **Undocumented endpoints and client pinning** (Cloud Code pins an Antigravity client identity; requests carry
  `claude-code/2.1.0` and `antigravity/hub/2.9.1 linux/amd64` user agents). Mitigation: per-provider source setting
  so one broken endpoint falls back alone; contract tests on recorded response shapes; a visible "source failed" state
  rather than silent zeros; client versions read from the installed binaries rather than pinned.
- **A provider moves its sign-in storage** (for example Codex keeping it in the OS keyring instead of `auth.json`).
  Mitigation: a clear "can't find your sign-in" state naming the file looked for.
- **Rate limits on Claude's usage endpoint:** keep the shim's 10-minute cooldown after a 429, and persist it across
  restarts. Never run both routes against Claude at the same time: any side-by-side reading of both sources doubles
  the calls to that endpoint, even inside one app (an earlier revision of this memo said otherwise). Parity is checked
  against readings the WSL route already saved, not by polling both.
- **Pricing upkeep:** prices live in a data file the app can update without a release (bundled table plus overlay, or
  a models.dev fetch), and the UI shows the share of tokens left unpriced rather than counting them as $0.
- **The WSL fallback decays:** nobody renews the WSL sign-ins once the default is native, so the fallback is good for
  one release at most. After that the fallback for a broken endpoint is option 3b (the official Windows tools).
- **Token handling mistakes:** tests that the code never opens credential stores for writing; a Process Monitor check
  on the credential locations during the first week of native readings.

## Verification (pass marks)

| Check | Tool | Pass |
|---|---|---|
| No VM per refresh | `Measure-Refresh.ps1`, 12 refreshes | 0 vmmemWSL episodes |
| CPU per refresh, everything included | in-app refresh markers, 10+ refreshes, packaged app | 0.25 CPU-s or less |
| Idle CPU, dashboard closed | same, 1 h; ETW trace for wake-ups | 2 CPU-s per hour or less |
| Resident memory, dashboard closed | private working set and private bytes, 1 h median | 90 MB or less private working set (stretch 60) |
| Usage parity | first native readings against the last WSL readings saved in `cache.json` just before the switch (no double polling), then 7 days of native readings checked for weekly resets and early-reset detection | within 3 points (allowing for use between the two readings), resets within 60 s |
| Cost parity | completed days on the logs on disk | each provider's daily total within 1%, unpriced share 0% |
| Cost scan | probe, after a reboot (cold file cache) and warm | first scan 60 s or less cold with 2 workers, or resumable and invisible at background priority; 64 MB heap or less; update 100 ms CPU or less |
| Sign-ins | static check on the credential code paths; Process Monitor on `.claude`, `.codex` and Credential Manager, plus entry hashes, over the first week of native readings | zero writes to those locations, hashes unchanged, agy still works |
| Setup | fresh install in Windows Sandbox | installs and runs without WSL, and shows a clean "not signed in" state |
| Battery | `powercfg /srumutil`, rows for the app, vmmemWSL and the WSL service, a fresh baseline taken before the switch | 50% lower energy per day or better |

## Rollout

Revised 25 Nov after the owner accepted a few days of breakage and handed the work over ("take ownership"):

1. Integrate option 4's usage reads behind two settings, usage source per provider (Native or WSL) and cost source
   (Native or WSL), both defaulting to Native. The WSL route stays as a manual fallback setting for one release.
   Land option 5's fixes as separate commits, each with a before-and-after CPU measurement.
2. Before installing, save the WSL route's last `cache.json` readings; compare the first native readings against them
   (no double polling of Claude). Then watch 7 days of native readings for weekly resets and early-reset detection.
3. Costs switch to native in the same build if the cost parity mark passes on the logs on disk; otherwise cost scans
   stay on WSL until it does. The installer stops requiring WSL once both are native.
4. Measure again; decide on option 6 with the memory measurements described above.
