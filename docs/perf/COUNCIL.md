# Council review of the CPU and RAM options memo (25 Nov 2026)

Reviewed: `docs/perf/OPTIONS.md`, `SPIKE-native-usage.md`, `SPIKE-native-costscan.md` and the spike code under
`Services/Native/`, with the measurements and the current WSL path as context. Nothing has been changed yet:
this report comes first, and the fixes wait for your decisions in section B.

## Reviewers and cost

| Reviewer | Route | Effort | Cost | Findings |
|---|---|---|---|---|
| Gemini 4 Neon | Vertex (GCP credit) | xhigh | US$1.27 | 15 |
| Gemini 4 Carbon | Vertex (GCP credit) | high | US$0.73 (first run hung 30 min with no output, US$0; retried with your yes) | 11 |
| Fable 5.2 | API agent, cap US$20 | xhigh | US$9.16 (API credits) | 20 |
| **Total** | | | **US$11.16** (US$2.00 GCP credit, US$9.16 API credits) | 46 |

Opus and Astra were not run. Round 2 was skipped: all three gave the same verdict and no score differs by more
than 1 point.

## The result

**All three: major revisions. None rejects the direction.** Each says native reads are the right move, because
only removing WSL removes the 1.3 to 1.7 GB VM episode, and that this holds even if all three labelled hypotheses
turn out wrong. What they reject is committing the multi-week parts (the cost port's scope, the sign-in renewal
design, option 6) on the current evidence. Fable's summary: native usage reads behind the setting can start now;
the rest should wait for the revised memo.

| Criterion (1 to 5) | Neon | Carbon | Fable |
|---|---|---|---|
| measurement_validity | 2 | 3 | 3 |
| root_cause | 3 | 3 | 3 |
| option_coverage | 3 | 3 | 3 |
| parity_risk | 2 | 2 | 2 |
| credential_safety | 3 | 3 | 4 |
| target_realism | 2 | 3 | 2 |
| maintenance_burden | 3 | 3 | 3 |
| rollout_plan | 2 | 3 | 3 |

### Against the targets

| Target | Memo says | After the review |
|---|---|---|
| No VM episode per refresh | met by option 4 | holds |
| CPU per refresh, 0.25 CPU-s | met by 4+5 | does not add up from parts already measured (A7) |
| Idle CPU, 2 CPU-s per hour (now 16) | met by 5 | no diagnosis and no mechanism (A8) |
| Resident memory, 90 MB (stretch 60) | needs option 6 | counter undefined; "needs option 6" not shown (A10) |
| Usage parity | matched for all three | Codex and Gemini yes; Claude rests on one unrecorded pair (A6) |
| Cost parity | tokens validated, pricing missing | tokens only on recent logs; 20 of 27 Codex days have no rows (A11) |
| Sign-ins never break | holds | no store writes, confirmed; but expired tokens raise "sign in" (A1, A2) |

## A. Errors confirmed in the files

Counts are by issue, across files (the convergence script undercounts when reviewers file one issue in
different places).

**A1. An expired token is shown as "sign in again" (3 of 3).** `NativeUsageDtoMapper.cs:17,35` maps
`SignInExpired` to `AuthRequired`. Only Claude serves its last reading (`ClaudeNativeClient.cs:106`); Codex
(`CodexNativeClient.cs:77`) and Antigravity (`AntigravityNativeClient.cs:105,123`) fail without it, and the
roll-to-0% at a known reset is not built. With Antigravity's one-hour tokens, the Gemini row would ask for a sign-in
every hour agy has not run. That contradicts the memo's own policy ("ask only when a sign-in is truly revoked").
Fix: carry expiry as a stale state with the last reading; keep `AuthRequired` for missing or rejected sign-ins;
add tests for both.

**A2. Today's route renews tokens; the native route does not, and the memo understates it (2 of 3, plus 3 of 3 on
the unmeasured `claude.exe` renewal).** Each WSL Gemini read runs agy, which renews its own token, and the shim's
Claude route 2 runs the Claude CLI, which renews an expired one (`tools/wsl/codexbar-shim.sh:118-119`). The memo's
"quota only changes when agy runs" is false: the Antigravity IDE is installed here
(`%LOCALAPPDATA%\Programs\antigravity\resources\bin\language_server.exe`) and spends the same quota, as do agy in
WSL and STUDIOPC1. The proposed `claude.exe` renewal has no verified command and is in no CPU or RAM budget (the
shim records about 18 s for a renewal). Fix: a parity row "token renewal and stale readings", and the test in B3.

**A3. The cost index stops for good when the time zone changes (3 of 3).** `NativeCostScanner.cs:233-239` stores a
hash of the serialized zone and throws `IOException` on any change, with no rebuild. The serialized form includes
localized names and rule data, so a display-language change or a Windows zone update trips it too, and you move
between Korea and Australia. Fix: store the zone id, rebuild day totals on a change, rebuild on a mismatched or
corrupt index.

**A4. Three more cost-index defects (2 of 3 each, all seen in the code).**
- Reports write: `Open()` runs `INSERT OR IGNORE` on every call (`NativeCostScanner.cs:170,232`), so a report read
  during a scan waits on the write lock and can fail.
- A rewritten or truncated file bumps its generation (`NativeCostScanner.cs:103`) and no row is ever deleted (there
  is no `DELETE` anywhere in `Services/Native/Cost`), so unkeyed records in that file count twice. The spike report
  names the edge case (`SPIKE-native-costscan.md:122`).
- `IgnoreInaccessible = false` (`NativeCostScanner.cs:57`): one locked or unreadable log fails the whole update.

**A5. The guest CPU evidence is not in the pack (3 of 3).** `results/vm-attribution.txt` is the first, broken probe
run (0.00 s CPU for every process: the bash `$12` bug). The "about 4 CPU-s" comes from one warm refresh at 16:19:21,
analysed inline from the second run's CSVs and never saved. That refresh sits inside the 16:18-16:20 window
`MEASUREMENTS.md:56-57` excludes for spike tests, so whether it was itself affected is unknown. The 8 CPU-s total
adds three separate runs and was never measured end to end. Two of the four host episodes (15:28:40 and 15:34:02,
`MEASUREMENTS.md:16-17`) have no summary file. The direction is safe; the size of the CPU saving and the battery
mark are not. Fix: save the second run's analysis and the missing summaries; rerun 10 or more clean cold refreshes
with nothing else using WSL, with a system-wide counter (Hyper-V logical processor, or a WPR trace).
*Follow-up (done 25 Nov):* the second capture's analysis is now `results/vm-attribution-run2.txt`. Read properly, it
shows about 6.3 CPU-s for that refresh inside the VM (Codex branch 2.79 s plus Gemini branch 3.55 s, both
reaped-children totals), not "about 4": the memo had counted one branch. The test batches were separate process
trees, so they are not in that total. The 15:28 and 15:34 raw data was not kept, so those two rows are now marked
unrecorded.

**A6. The Claude live match is not recorded (3 of 3).** `OPTIONS.md` cites a rerun at 07:35Z (18% vs 16%, 68% vs
68%, Fable-only 8% vs 8%). `SPIKE-native-usage.md` still calls Claude "not comparable (different account)", lists
the scoped weekly window as tested on synthetic data only, and logs no call after 07:27:35. Fix: add the rerun to
the spike report (time, call, values, and that the reference reading was 11 minutes older). Related: the code honors
`CLAUDE_CONFIG_DIR` and `CODEX_HOME` (`ClaudeNativeClient.cs:31`, `CodexNativeClient.cs:25`). Neither is set at user
or machine level here, so the installed app reads the default profile, but any start from a shell that sets them
reads another account, which is what happened once in the spike (B4).

**A7. The 0.25 CPU-s mark does not add up (3 of 3).** The 0.19 s is one warm sample from a console probe
(`SPIKE-native-usage.md:177`). Left out: three status-page fetches, API credits, Gemini spend, UI updates, history
writes, and cost updates (0.109 s even when nothing changed). Option 5 lists "keep HTTPS connections alive"
(`OPTIONS.md:56`) while the memo says servers close idle connections and `NativeHttp.cs:35` closes pooled ones after
90 s. `Measure-Refresh.ps1` bills a fixed 25 s before each marker plus 5 s after to the refresh, which adds idle CPU
and clips a 47 s cold refresh. Fix: a budget table (usage, status, UI, cost, history), 10 or more refreshes measured
inside the packaged app, connection reuse dropped or proven, refreshes bounded by in-app markers.

**A8. Idle CPU has no diagnosis (3 of 3).** The app idles at 16 CPU-s per hour against a mark of 2, with no entry in
the hypotheses (`OPTIONS.md:30-40`) and no item in option 5. At a 5-minute interval, refresh work after 4+5 is
about 3 CPU-s per hour, so idle becomes the largest term. Fix: an ETW trace of an idle hour with the dashboard
closed, listing what wakes the app (timers, position polling, animation).

**A9. Provider status loses its time window (3 of 3).** `CodexBarService.cs:471-475` starts the status prefetch,
runs the WSL usage call (7 to 30 s), then allows 500 ms more (`StatusBudget`, line 44). Natively the usage read
returns in 0 ms (Claude cache hit) to 2.6 s, so a cold status fetch gets about half a second, and the CLI's status
backup is gone at the same time. Fix: give status its own timeout and cadence.

**A10. "Meeting the memory target needs option 6" is not shown (3 of 3).**
- The counter is never named (`OPTIONS.md:132,189`). The baseline is 142.8 MB working set or 183.7 MB private bytes
  depending on which; `Measure-Refresh.ps1` records neither private working set (Task Manager's column).
- The ~104 MB native share is a labelled hypothesis.
- Option 6's "core ~15-30 MB" has no prototype; the bare console probe alone reached 17.6 MB private and 57.1 MB
  working set.
- Cheaper steps are untested: destroying hidden windows instead of hiding them (the memo never mentions it), GC
  conserve-memory, ReadyToRun.
- The installed build has ReadyToRun off: the local install recipe passes `-p:PublishReadyToRun=false`, although
  `CodexBarWin.csproj:117-118` enables it for release publishes. So the baseline measured a JIT build.
- The memo calls option 6 both "the decision this memo hands on" (`OPTIONS.md:135`) and a decision for after
  re-measuring (`OPTIONS.md:203`).

**A11. Cost parity is weaker than "pricing port missing" (3 of 3).** `OPTIONS.md:108` ("older days are absent from
the current session folder") reads as the cause, while `SPIKE-native-costscan.md:50` says whether archived, deleted
or differently scoped logs explain the 20 Codex days is unverified. Claude tokens differ on 6 of 26 days (largest
+336.8M on 20 Nov), and the reference ledger has no Claude day after 20 Nov, so "within 1% over 30 days" cannot be
checked for Claude. An earlier fix (`d991dfb`, 31 Oct: the scan memo outgrew the 4 MiB write cap) shows this has
stalled before; the 20 Nov stop is later and uninvestigated. 3,523 lines over 512 KiB were skipped, effect unknown.
"Validated on tokens" (`OPTIONS.md:113`) holds only for recent days. Fix: find where the 20 days' logs live, print
the roots today's CLI scan covers, find why Claude scans stopped, then restate the parity gate.

**A12. The full-scan time is a warm-cache figure (3 of 3).** The table (`OPTIONS.md:92-93`) presents 11.4-14.7 s
(two workers) against 332 s (one worker) as a worker-count effect. The two-worker runs came straight after the cold
332 s run over the same 10.65 GB, and the spike says cold two-worker speed is unverified
(`SPIKE-native-costscan.md:32`). The first real scan will be cold. Fix: rerun after a reboot with two workers;
run the first scan at low I/O and memory priority.

**A13. The rollout conflicts with the staging and with Anthropic's rate limit (3 of 3).** One setting,
`DataSource = Native | Wsl` (`OPTIONS.md:199`), cannot express "usage native, costs still WSL". The 24 h
side-by-side build runs two pollers on the same Claude account against a rate-limited endpoint, takes pairs minutes
apart, spans no weekly reset or Codex token renewal, lands option 5's UI changes together with the source switch,
and has no rollback trigger. Fix: separate usage and cost source settings (usage per provider), an in-process shadow
mode that reads both sources back to back, 7 days or more, option 5 changes landed separately, and named failure
counts that revert to WSL.

**A14. Four pass marks cannot run as written.**
- Battery (3 of 3): no SRUM rows named; vmmemWSL's energy is not charged to the app's row; the October baseline is
  not attached and predates the 20 Nov failure. Fix: name the rows and take a fresh baseline now.
- Windows Sandbox (2 of 3): no sign-ins exist inside it, so "first good reading" cannot happen. Fix: check install
  without WSL and a clean "not signed in" state.
- Static sign-in check (2 of 3): "`Services/Native` opens no file for writing, imports only `CredReadW`/`CredFree`"
  (`OPTIONS.md:193`) is false for the merged tree; the cost scanner writes `index.sqlite` and imports `winsqlite3`.
  Fix: scope the check to credential locations.
- Test counts are per branch (2,092 and 2,011, `OPTIONS.md:62,87`); the merged tree's run (2,103 passing) is not
  reported.

**A15. Two credential records would print their token (2 of 3).** `ClaudeCredentials` and `CodexCredentials`
(`ClaudeNativeClient.cs:10`, `CodexNativeClient.cs:9`) are records without a `ToString` override, so their
generated `ToString` includes `AccessToken`. Antigravity's records override it. No current log line formats them.
Fix: override both, add a test.

**A16. The opt-in Antigravity refresh has three gaps (2 of 3).** It reads only `access_token` and `expires_in` from
Google's response (`NativeJsonContext.cs:239-240`), so a rotated refresh token would be dropped; `_refreshed` has no
lock (`AntigravityNativeClient.cs:56,99,125`); and a failed renewal has no backoff. Off by default, so no effect
today (B5).

**A17. Effort and upkeep are not costed (3 of 3).** Option 4's "~1-2 weeks" (`OPTIONS.md:55`) covers integration and
the pricing port but not the cost-parity work in A11, renewal (A2), the status fix (A9) or the shadow run (A13). The
options table has no upkeep column (`OPTIONS.md:50`), though option 4 takes over five undocumented endpoints, three
credential formats, two log formats and a price table. Rates compiled into code left 99.93% of Claude tokens
unpriced, and unpriced tokens add up to $0 silently. Fix: split the estimate, add an upkeep column, keep prices as
data updatable without a release, and show the unpriced share in the UI.

**A18. Levers left out (3 of 3 across findings and prose).** Activity- and power-aware polling (all three);
destroying windows before option 6 (Carbon, Fable); NativeAOT for any split (Neon, Carbon, Fable); calling the
Windows builds of the official CLIs directly as renewal and fallback paths (Fable).

**A19. Measurement wording (3 of 3, minor).** "agy x4" (`OPTIONS.md:22`) against the five launches in
`MEASUREMENTS.md:40`; 206 MB is AnonPages while 241 MB is RSS that overlaps the page cache; the guest parts
(315 + 650 + 206 = 1,171 MB) are not reconciled with the host's 1,326-1,657 MB (about 180 MB of host overhead).

**A20. Credential files read once with no retry (2 of 3, minor).** `NativeCredentialFile.ReadAsync`
(`NativeCredentialHelpers.cs:51`) does not retry a file caught mid-rewrite, which would flash a false "signed out".

## B. Your calls

**B1. Which counter is the 90 MB (stretch 60) target?** The original wording named "resident memory" without a
counter. *Recommendation:* Task Manager's memory column (private working set) as the pass mark, with private bytes
reported beside it. Physical RAM is what this laptop lacks, and private bytes counts memory Windows has already
paged out. The baseline needs remeasuring in that counter.

**B2. Option 6 now or later?** *Recommendation:* later. Build stage 1 of 4+5 first, then spend a day on the cheap
measurements: windows destroyed instead of hidden, ReadyToRun on, GC conserve-memory, a blank WinUI window as the
floor, and a VMMap trace. If that still misses the mark, a one-day prototype of a core without XAML, built with
NativeAOT. All three reviewers' "halve it again" plans end at that same design (a NativeAOT tray and strip core
drawn in software so the Intel shader compiler never loads, plus the WinUI dashboard as its own process), so option 6
is the likely end state, but the numbers should decide when.

**B3. Sign-in renewal (you delegated this; here is the revised plan, change it if you disagree).** Before the default
flips to native: one hour on Windows testing which official commands renew a token (`claude.exe`, `agy.exe`), their
CPU, RAM, seconds and any quota use. Then: an expired token shows the last reading marked stale with its age, never
a sign-in prompt; a window rolls to 0% at its known reset with a "not read since" note; the official CLI runs to
renew only when you open the flyout or dashboard, at most every 30 minutes, and only if the test shows it is cheap.
The Antigravity IDE on this laptop means Gemini can go stale while you use it, which the stale label covers.

**B4. Which Claude account should the app read?** *Recommendation:* always the default profile
(`%USERPROFILE%\.claude`, `%USERPROFILE%\.codex`), ignoring `CLAUDE_CONFIG_DIR` and `CODEX_HOME` unless an app
setting names another, and show the path in use on the detail page.

**B5. Should the opt-in Antigravity refresh ship at all?** It mints tokens with a client secret read out of Google's
binary. *Recommendation:* remove it from shipped builds and keep it on a branch, unless the B3 test shows agy's own
renewal is unusable. That also retires A16.

**B6. How much cost history must match before WSL goes?** *Recommendation:* 1% daily parity on the logs on disk, with
the app's ledger kept for older days, once A11 has found where the 20 Codex days went (if they are in
`archived_sessions`, port that folder first).

**B7. Do you run Claude Code, Codex or agy inside WSL?** If you do, a Windows-only scanner never sees those logs, and
reading them through `\\wsl$` would boot the VM. *Recommendation:* if yes, read those logs only while WSL is already
running for another reason.

## C. Questions only a third party can answer

- **Google:** does it rotate refresh tokens for agy's OAuth client, and do its terms let another app use that
  client? (Matters only if B5 keeps the refresh.)
- **Anthropic:** do two pollers on one account share the OAuth usage endpoint's rate limit? No public document. A side
  agent pointed out (25 Nov) that the shadow mode proposed in A13 would itself poll Claude twice, so the rollout now
  compares native readings against readings the WSL route already saved instead.
- **OpenAI:** can Codex keep its sign-in in the OS keyring instead of `auth.json`? Codex's configuration reference
  settles it; if yes, the reader needs that path too.
- **All three:** do the usage endpoints check the client version or OS in the user agent (`claude-code/2.1.0`;
  `antigravity/hub/2.9.1 linux/amd64` sent from Windows)? Watch for it in the first week of native readings.

## D. Checked and not upheld

- **Codex credits would go blank (Neon, Fable).** The model has a `Credits` field, but today's `cache.json` holds
  null for all three providers, so nothing visible is lost. Keep a parity row and port it if the CLI ever fills it.
- **The Claude rerun broke the spike's 2-minute spacing (Neon).** It came about 8 minutes after the spike's last call
  (07:27:35 to 07:35). The missing record (A6) stands; the spacing claim does not.
- **ReadyToRun is "disabled in CLAUDE.md" (Carbon).** The project enables it for release publishes; only the dev
  build command and the local install recipe turn it off. The point that the installed build is JIT stands (A10).
- **Claude would update every 15 minutes, not 10, as a new loss (Fable).** The arithmetic holds on this laptop, which
  refreshes every 300 s (the default is 120 s, `AppSettings.cs:51`), but the WSL shim caches Claude for 10 minutes the
  same way, so it is today's behavior, not a regression. Worth fixing in stage 1 (cache lifetime just under two
  refresh periods).

## E. Single-reviewer findings not yet checked

- Neon: `MapAvailableModels` returns null when every model shows 99.9% or more remaining, so a fresh quota looks
  like an account not onboarded if the summary call times out.
- Neon: cost bands (200K/272K, price cutover dates) are fixed at scan time; storing each event's time would allow
  repricing without a rescan. `CostLogParser.Number` rejects values above `int.MaxValue`.
- Neon: Claude's 429 cooldown lives only in memory and is lost on restart.
- Neon: scanning Google's binary for its OAuth client may trip Defender or SmartScreen heuristics (moot if B5 removes
  it).
- Carbon: Codex key conflicts are ignored (`CostIndex.cs:120`, `WHERE excluded.provider=0`); seen in the code,
  intent unchecked.
- Carbon: manual refresh cannot bypass Claude's 10-minute cache; `ResolvePlan` ignores the parsed auth method; the
  index does not use WAL mode.
- Fable: the whole scan is one transaction, so a cancel or sleep discards it; commit per batch so it resumes.
- Fable: read client versions from the installed binaries instead of pinning them.

## Suggested order after your decisions

1. Correct the memo and spike reports (A5, A6, A10, A11, A12, A14, A17, A19) and save the missing evidence.
2. Fix the spike code (A1, A3, A4, A9, A15, A20; A16 or removal per B5), with tests.
3. Run the measurements that decide B1 to B3: private working set baseline, idle ETW trace, renewal test, windows
   destroyed, ReadyToRun on, cold two-worker scan, a clean guest rerun.
4. Build stage 1: native usage behind a per-provider setting.

Update 25 Nov: the owner handed the work over and accepts a few days of breakage, so the rollout in `OPTIONS.md`
now switches straight to native and drops the shadow run (which would also have doubled Claude's polling).

## Decisions (owner, 25 Nov 2026: "Yes, all of them")

- **B1.** The 90 MB (stretch 60) mark is Task Manager's memory column (private working set); private bytes is
  reported beside it. The baseline is remeasured in that counter.
- **B2.** Option 6 waits. Stage 1 of 4+5 first, then a day of cheap memory measurements (windows destroyed, ReadyToRun
  on, GC conserve-memory, blank-window floor, VMMap), then a one-day NativeAOT core prototype only if still over.
- **B3.** Expiry shows the last reading marked stale with its age, never a sign-in prompt; windows roll to 0% at a
  known reset with a "not read since" note. Renewal through the official `claude` / `agy` commands, on flyout or
  dashboard open at most every 30 minutes, only if a one-hour test on Windows shows it is cheap.
- **B4.** Always the default profiles (`%USERPROFILE%\.claude`, `%USERPROFILE%\.codex`); `CLAUDE_CONFIG_DIR` and
  `CODEX_HOME` are ignored unless an app setting names another path; the path in use shows on the detail page.
- **B5.** The opt-in Antigravity refresh comes out of the shipped code; it is kept on branch
  `perf/antigravity-refresh-optin`.
- **B6.** 1% daily parity on the logs on disk, with the app's ledger kept for older days, once A11 has found where the
  20 Codex days went.
- **B7.** The owner does not use WSL or Docker for anything, so no WSL log folders are needed. Once the native route
  is the default, WSL has no remaining use for this app.
