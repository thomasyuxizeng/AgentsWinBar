# Cutting CodexBarWin's CPU and RAM: options and recommendation

Status: draft for review, 25 Nov 2026. Evidence: `docs/perf/MEASUREMENTS.md` (measured on the owner's laptop) and
the two spike reports, `SPIKE-native-usage.md` (branch `perf/native-usage`) and `SPIKE-native-costscan.md`
(branch `perf/native-costscan`).

## The owner's requirements

1. As little CPU and RAM as possible. Disk space is free and may be spent to save either.
2. Full feature parity: usage for Claude, Codex and Gemini (Antigravity), including extra windows; API credits and
   Gemini API spend; costs from the Claude and Codex logs; provider status; alerts and early-reset detection.
3. Easy setup and upkeep of signed-in accounts: no sign-ins beyond the ones already used on Windows.

## Where the cost is today (measured)

Every refresh (every 5 minutes) costs, on the cold path the app always takes:

| Part | RAM | CPU | Source |
|---|---|---|---|
| WSL VM boot | ~315 MB | ~1.5 s | guest meminfo with only the probe running; busy ticks at first sample |
| Loading the programs below from the VM's disk (page cache) | ~650 MB | (in the next row) | page cache 90 -> 743 MB at the first refresh |
| Programs: agy x4 (largest 241 MB), Codex app server (Node + native, ~160 MB), CodexBarCLI x3 (~50 MB each), python3 | up to 206 MB at once | ~4 s (lower bound) | `/proc` samples |
| Host total for the VM (vmmemWSL) | **1,326-1,657 MB for 33-47 s** | not visible from Windows | 4 clean refreshes |
| App process work per refresh | (resident) | **~2.5 s** | 2 clean refreshes |

So about **8 CPU-s and a 1.3-1.7 GB spike per refresh**, about 95 CPU-s per hour. Between refreshes the app
idles at ~16 CPU-s per hour and holds 176-196 MB private (133-157 MB working set). Of that, the .NET heap holds
only 7.4 MB live in 55 MB committed; ~104 MB is native (WinUI/XAML, Direct2D, the Intel GPU driver, whose shader
compiler alone is mapped at 83 MB).

Two different budgets, then: the **VM episode** (90% of the RAM and ~70% of the CPU) and the **resident app**.
A UI rewrite touches only the second.

## Options

| # | Option | VM episode | App per refresh | Resident app | Parity risk | Setup | Effort |
|---|---|---|---|---|---|---|---|
| 1 | Tune WSL: switch Codex and Gemini to the CLI's OAuth routes, keep VM settings | ~600 MB and ~2-3 CPU-s left (VM boot + CodexBarCLI x3); estimate | unchanged | unchanged | low | worse: Gemini OAuth needs a separate CodexBar sign-in in WSL; the Codex bridge refuses while `/usr/bin/codex` exists | hours |
| 2 | Convert the distro to WSL1 (no VM) | no VM; the same programs still run (~500 MB transient, same CPU); untested | unchanged | unchanged | medium: agy and Codex on WSL1 untested | unchanged | 1 day to test |
| 3 | Build the CodexBar CLI for Windows | no VM, but the CLI still launches agy and Codex's app server (Windows builds, similar size) | unchanged | unchanged | high: upstream targets macOS/Linux; untested | better | unknown |
| **4** | **Native C# reads in the app: HTTPS usage for all three providers, incremental cost scanner with an on-disk index** | **gone: no VM, no child programs** | usage 0.19 s CPU; cost update ~0.1 s CPU | +few MB (scanner peaks 32 MB private while a full scan runs, once) | usage: matched live; cost: tokens match, pricing port still needed | **best: reuses Windows sign-ins; no WSL** | spikes done; integration plus pricing port ~1-2 weeks |
| **5** | **Slim the app in place: update UI instead of rebuilding it, skip hidden windows, keep HTTPS connections alive, append-only history, GC and ReadyToRun settings** | none | **target ~2.5 s -> under 0.1 s** | maybe -30 to -50 MB (GC commit, JIT) | low | none | days |
| 6 | Split the app: a small always-on core (tray, taskbar strip, overlay, polling) without XAML or the GPU, and the WinUI dashboard as a separate process started on demand | none | low | **core ~15-30 MB; dashboard only while open** | medium: strip and overlay rebuilt, reset animations redone | none | weeks |
| 7 | Full native rewrite (C++/Rust/Win32) | none on its own | low | ~15-25 MB | high: whole UI redone | none | many weeks |

### Spike results (option 4)

Usage reads (`SPIKE-native-usage.md`, branch `perf/native-usage`, 24 files, 2,092 tests passing):

| One refresh, all three providers in parallel, fresh probe process | Wall | CPU | Allocated |
|---|---|---|---|
| Cold (first call, includes JIT and TLS setup) | 3.5 s | 0.91 s | 0.9 MB |
| Warm (130 s later, new TLS connections, as the app's 5-minute cadence would see) | 2.6 s | 0.19 s | 0.35 MB |
| In-process cost only, replayed responses, after JIT | 1-4 ms | 1-4 ms | 28-51 KB |

The whole probe process peaked at 57 MB working set and 18 MB private, of which ~27 MB / 8 MB is the bare .NET
runtime; inside the app, where the runtime and `HttpClient` are already loaded, the increase should be a few MB
(inferred). Wall time is network: Antigravity's quota summary on `daily-cloudcode-pa` takes about 2 s.

Parity, live, against the app's CLI readings:

| Provider | Result |
|---|---|
| Codex | session 11% vs 10%, weekly 88% vs 88%, resets within 1 s |
| Gemini | 5-hour 29.9% vs 30.8%, weekly 7.5% vs 8.0%, both "Claude and GPT" extras match; resets equal to the second |
| Claude | 18% vs 16% (the app's reading was 11 minutes older, during active use), weekly 68% vs 68%, Fable-only 8% vs 8%, resets equal. (The spike's own Claude comparison read the Wave seat's sign-in, because the seat's launcher sets `CLAUDE_CONFIG_DIR`; rerun from a normal environment on 25 Nov 07:35Z.) |

Endpoints: Claude `api.anthropic.com/api/oauth/usage`; Codex `chatgpt.com/backend-api/wham/usage`; Antigravity
`cloudcode-pa.googleapis.com/v1internal:loadCodeAssist` then `daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`.
None is a public API. The code imports only `CredReadW` and `CredFree` from Credential Manager, never writes a sign-in
file, and has a host allow list and no redirects.

Cost scanner (`SPIKE-native-costscan.md`, branch `perf/native-costscan`, 13 files, 2,011 tests passing; SQLite
index through the Windows inbox library):

| Phase | Wall | CPU | Peak managed | Peak private | Read |
|---|---|---|---|---|---|
| Full scan, empty index, 2 workers (2 runs) | 11.4-14.7 s | 15.7-22.2 s | 8.8-9.3 MB | 32-33 MB | 10.65 GB, 2,340 files |
| Full scan, 1 worker | 332 s | 28.6 s | 4.3 MB | 22.5 MB | same |
| Update, nothing changed | 108 ms | 109 ms | | | 0 files |
| Update after 100 new lines | 109-232 ms | 78-156 ms | | | 1 file |

Index on disk: 8.4 MB. A full scan happens once; afterwards only appended bytes are read. Targets: full scan and
heap met; the two update targets missed narrowly (109 and 156 ms CPU against 100 ms), and the no-change pass
opens no files, so its cost is listing 2,339 files and the SQLite commit, which a file-system watcher or a
directory-change check would remove.

Parity with the CLI's saved daily totals failed, for reasons found and bounded:

- Claude: native **token counts matched exactly on 20 of 26 days**, but 99.93% of those tokens are unpriced: the
  spike bundled only some rates, and the current models (Claude Opus 5.5, Sonnet 5.5, Haiku 5.5) are missing.
  Upstream resolves rates from models.dev plus provider rules and overlays. Porting that pricing is the missing piece.
- Codex: 2 days matched exactly; 4 days have identical tokens and match the CLI exactly once the long-context
  threshold is 200K instead of 272K; older days are absent from the current session folder (the app's ledger keeps
  them); 24 Nov differs because the saved snapshot was taken before the day ended (verified to the token).
- Not ported: Codex fork baselines, archived sessions, alternative homes and several rarer record shapes; Claude's
  Vertex detection and lexical tie-breaks. Effect on cost unverified.

So the engine is validated on tokens; cost parity needs the full pricing port and the remaining record shapes.

## Recommendation

Do **4 and 5 together** first. They remove the VM episode entirely (the 1.3-1.7 GB and roughly 5.5 of the 8 CPU-s
per refresh), cut the app's own refresh work from ~2.5 s toward 0.1 s, and make setup a single MSIX with no WSL.
Usage reads can ship first (they matched live); native costs ship once the pricing port reaches 1% daily parity,
and until then costs can keep coming from WSL on a slow, visible-only schedule or simply stay at the last saved
ledger values.
Then measure the resident app again and decide on **6** with numbers: if the always-on app is still above the
owner's comfort, move the tray, strip and overlay out of XAML and keep the dashboard as an on-demand process.
Option 7 buys nothing over 6 and redoes the dashboard. Options 1-3 keep WSL, keep the child programs (or a VM),
and do not meet requirement 3.

## Sign-in policy (decided 25 Nov 2026, owner delegated the balance)

- Never write any credential store. Never refresh an Anthropic or OpenAI token directly: both rotate refresh
  tokens, so a refresh here would sign the owner out of Claude Code or Codex on Windows.
- Claude (8 h tokens, renewed by every running Claude Code session): read-only. If expired, show the last reading
  as stale; when someone opens the flyout or dashboard, renew through the official `claude.exe` (at most every
  30 minutes). The cheapest renewing command is unverified.
- Codex (10-day tokens, renewed by the Codex CLI): read-only, with a one-line notice if it ever expires.
- Antigravity (about 1 h tokens, agy not resident). Default: read-only. Quota only changes when agy runs, and agy
  renews the token whenever it runs, so an expired token means no Gemini use on this machine for an hour; show the
  last reading and roll a window to 0% locally when its known reset time passes. Opt-in: renew in memory with the
  OAuth client found in the installed agy binary (built in the spike, never run live). Open question for review:
  minting tokens with another app's OAuth client is what upstream CodexBar does, but it may be read as client
  impersonation under Google's terms, and Google may rotate or limit agy's refresh token. Use on other machines
  (STUDIOPC1) is the one case the default misses.
- Ask the owner to act only when a sign-in is truly revoked, naming the exact step.

## Parity matrix

| Feature | Today | Native path |
|---|---|---|
| Claude usage (session, weekly, scoped extras) | CLI in WSL, OAuth endpoint | same endpoint, Windows sign-in |
| Codex usage | CLI in WSL, Codex app server | spike: endpoint from upstream, Windows `auth.json` |
| Gemini usage (5-hour, weekly) and Claude/GPT-on-Antigravity extras | CLI in WSL runs `agy -p /usage`, shim reshapes | Cloud Code quota summary, Credential Manager sign-in, same reshaping |
| Provider status and incidents | already native HTTPS (with `--status` also asked of the CLI) | native only |
| API credits | already native (`ApiBillingService`) | unchanged |
| Gemini API spend | already native (`GeminiSpendService`) | unchanged |
| Cost from logs | CLI in WSL over `/mnt/c` every 30 min | native incremental scanner with on-disk index |
| Alerts, early resets, history, burndown | app logic on the readings | unchanged |

## Risks

- Undocumented endpoints and client pinning (Cloud Code pins an Antigravity client identity). Mitigation: keep the
  WSL path as a fallback setting for one release; contract tests on recorded response shapes; a visible "source
  failed" state rather than silent zeros.
- Rate limits on Claude's usage endpoint: keep the shim's 10-minute cooldown after a 429.
- Cost parity drift when upstream changes pricing or de-duplication: a weekly self-check that compares one
  completed day against a CLI run (manual, through WSL, only while WSL is still installed).
- Token handling mistakes: tests that the code never opens credential stores for writing; a Process Monitor check
  during the side-by-side run.

## Verification (pass marks)

| Check | Tool | Pass |
|---|---|---|
| No VM per refresh | `Measure-Refresh.ps1`, 12 refreshes | 0 vmmemWSL episodes |
| CPU per refresh, everything included | same | 0.1 CPU-s or less (stretch 0.05) |
| Idle CPU, dashboard closed | same, 1 h | 2 CPU-s per hour or less |
| Resident memory, dashboard closed | same, 1 h median | private 120 MB or less after 4+5; 60 MB or less if 6 is built |
| Usage parity | side by side, 24 h, 24+ pairs per provider | within 3 points, resets within 60 s |
| Cost parity | completed days, last 30 | each provider's daily total within 1% |
| Cost scan | probe | full rescan 60 s or less and 64 MB heap or less; update 100 ms CPU or less |
| Sign-ins | Process Monitor + entry hashes over 24 h | zero writes, hashes unchanged, agy still works |
| Setup | fresh install in Windows Sandbox | first good reading with no WSL and no extra sign-ins |
| Battery | SRUM energy per day vs the October baseline | 50% lower or better |

## Rollout

1. Integrate option 4 behind a setting (`DataSource = Native | Wsl`), default WSL, plus option 5's app fixes.
2. Run a side-by-side build (separate package identity) for 24 h with Native, logging parity against the
   installed WSL build.
3. Flip the default to Native; keep WSL as a fallback for one release; the installer stops requiring WSL.
4. Measure again; decide on option 6.
