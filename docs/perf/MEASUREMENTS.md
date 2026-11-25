# CodexBarWin refresh cost: measurements (25 Nov 2026)

Machine: i5-1340P (16 logical CPUs), 13.7 GB RAM, Windows 11 Pro 26300. App: local build 1.5.4.1 (JIT, no
ReadyToRun), 3 providers enabled (Claude, Codex, Gemini via Antigravity), refresh every 300 s (adaptive).
`.wslconfig`: `memory=4GB`, `vmIdleTimeout=10000`, `guiApplications=false`, `autoMemoryReclaim=gradual`;
`/etc/wsl.conf` has `systemd=false`. So the VM shuts down 10 s after each refresh and cold-boots on the next.

Scripts: `tools/perf/Measure-Refresh.ps1` (host, 250 ms samples), `tools/perf/vm-monitor.sh` (guest, 0.5 s samples),
`tools/perf/Get-MemoryMap.ps1` (committed memory by kind), plus `dotnet-trace` and `dotnet-gcdump` 10.0.750501.
Raw CSVs are kept outside the repo (session scratchpad, `perf\`).

## 1. Host view: the WSL VM per refresh (no WSL commands of our own running)

| Refresh | VM up | vmmemWSL peak working set | peak private |
|---|---|---|---|
| 15:28:40 | 47 s | 1,657 MB | 1,690 MB |
| 15:34:02 | 37 s | 1,429 MB | 1,442 MB |
| 15:53:35 | 33 s | 1,466 MB | 1,470 MB |
| 15:58:43 | 34 s | 1,326 MB | 1,330 MB |

The VM climbs from 88 MB to 1,233-1,550 MB within 6 s of starting. Windows reports vmmemWSL CPU time as 0, so VM CPU
was measured from inside the guest (section 3).

## 2. Guest view: where the VM's memory goes

With only the probe running (VM booted, 16:02:28-16:03:50): guest used 313-336 MB (page cache 90 MB, kernel slab
55 MB, process memory 2 MB).

At the first refresh after boot (16:04:00), page cache jumped from 90 MB to 743 MB and stayed there. Process
memory (AnonPages) peaked at 206 MB during refreshes and fell back to 2 MB after each. Peak guest used: 1,269 MB.

Programs each refresh starts (from `/proc`, peak resident memory):

| Provider | What runs | Peak RSS |
|---|---|---|
| all | `bash -ic` batch shell, `timeout`, `head -c 4194304` per provider | 2-4 MB each |
| all | `/opt/codexbar/CodexBarCLI` (one per provider) | 42-50 MB each |
| Claude | CodexBarCLI `--source oauth` (one HTTPS call) | 50 MB |
| Codex | `node /usr/bin/codex --version`, then `node codex app-server` and the native `codex` binary | 47 + 112-117 MB |
| Gemini | `agy --version` (twice), `agy -p /usage --output-format json`, which starts `agy --bg-updater`, which runs `agy --version` again; plus `python3` for the shim's reshaping | 77-132, 233-241, 173, 78, 11 MB |

Program sizes on the VM's disk: `agy` 263 MB, `CodexBarCLI` 161 MB, `node` 120 MB, `@openai/codex` package 427 MB.
Loading them is what fills the ~650 MB of page cache. Because the VM shuts down after each refresh, every
refresh loads them again from the virtual disk.

## 3. CPU per refresh

- App process (host, cold-boot runs 15:53 and 15:58): 2.50 s and 2.73 s of CPU in the window around each refresh;
  42 CPU-s per hour in total, 16 CPU-s per hour between refreshes. (The later warm-VM run's app numbers are
  inflated by an attached trace and are not used.)
- Guest, refresh at 16:19:21-16:19:29 (warm cache): the batch shells' reaped-children CPU reached 3.55 s for one
  provider branch; per program: CodexBarCLI 1.07 s (Codex) and 1.79 s (Antigravity), `codex` 0.85 s + 0.25 s,
  `agy -p` 0.65 s, `node` 0.25 s. Lower bound for the whole refresh inside the VM: about 4 CPU-s.
- Guest boot: 1.47 CPU-s used by the time the probe's first sample ran.
- Caveat: the guest probe forks several tools per process per sample, so whole-VM CPU totals are dominated by the
  probe itself and are not used. A 130 s burst at 16:18-16:20 included the spike agents' unit tests, which run
  WSL batch tests (`yes runaway`, `startup noise`); it is excluded.
- Estimate per refresh, cold path: about 2.5 (app) + 4 (guest programs) + 1.5 (boot) = **about 8 CPU-s**, so about
  **95 CPU-s per hour** at the 5-minute cadence, before Hyper-V overhead on the host.

## 4. The app process itself

- Working set 133-157 MB, private bytes 176-196 MB (peak since start 295 MB working set).
- `dotnet-gcdump`: **7.4 MB live managed heap**, 84,842 objects. The largest single array is
  `UsageHistorySample[]` (262 KB).
- `Get-MemoryMap.ps1` (committed): GC heap 54.8 MB (so ~47 MB committed but empty), native heaps and other private
  103.8 MB, stacks 1.3 MB, images 345.5 MB (mostly shared), mapped 202.1 MB (includes the runtime's W^X code maps).
  Largest images: `igc64.dll` 82.7 MB (the Intel graphics shader compiler, pulled in by GPU-rendered XAML),
  `Microsoft.Windows.SDK.NET.dll` 23.7 MB, `System.Private.CoreLib.dll` 15.3 MB, `Microsoft.ui.xaml.dll` 14.4 MB,
  `igd10um64xe.dll` 12.4 MB.
- `dotnet-trace` (thread time, so blocked time counts too; refresh windows minus an idle window, ms per 30 s):
  waiting on the WSL output pipe 18,510; HTTP connect and DNS 1,955; XAML control creation for `ProviderCard`
  1,036 and `UsageMeter` 131; `TaskbarWindow.Rebuild` 390 and `Reposition` 351; TLS handshakes 290;
  `MainViewModel.RefreshCoreAsync` 266; history file write 136. Indicative only: thread time is not CPU time.

## 5. Logs read by cost scans

Claude: 2,255 JSONL files, 10,042 MB. Codex: 65 files, 102 MB. Cost scans run every 30 min while a window is
visible, through the VM over `/mnt/c`.

## 6. Sign-ins on Windows

| Provider | Where | Access token lifetime | Renewed by |
|---|---|---|---|
| Claude | `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth`) | 8.0 h | every running Claude Code session |
| Codex | `%USERPROFILE%\.codex\auth.json` | 10.0 days (7.8 left) | the Codex CLI |
| Antigravity | Credential Manager, target `gemini:antigravity` (Go oauth2 token JSON) | about 1 h | agy only, which is not resident |
