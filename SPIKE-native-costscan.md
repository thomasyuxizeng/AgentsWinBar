# Native incremental cost scanner spike

The standalone scanner indexes Claude and Codex JSONL on Windows and returns the existing `CostDataDto`. The probe and unit tests are its only callers. The Release probe links the same code and uses BelowNormal process priority.

## Method

Each worker-count trial used an empty index and its own probe process. Each process ran a full scan, an immediate update, and an update after appending 100 synthetic assistant lines to a fixture under the probe directory. The fixture was empty during the first two phases, and the append was on the current day. The live input roots and saved app snapshots were opened read-only. No disk cache was flushed. The managed peak is the highest sampled estimate from `GC.GetTotalMemory(false)` at approximately 5 ms intervals and at phase boundaries. Private peak is the Windows process lifetime peak commit charge. MB are decimal. CPU is the process CPU-time delta and includes the sampling thread. Timings cover index updates, with the report projection and CLI comparison run after the timed phases.

The index batches file cursors, deduplication and day totals in one SQLite transaction using the Windows inbox library. Paths and deduplication IDs are 128-bit SHA-256 fingerprints. Only selected usage metadata is persisted. Ordinary updates seek to the last complete newline in changed files. Price changes apply to the stored daily token buckets through `ReadReport`, including when the source files are unavailable.

## Measurements and targets

| Trial | Phase | Workers | Wall ms | CPU ms | Allocated MB | Peak managed MB | Peak private MB | Files opened | Bytes read |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | Full | 1 | 332457.8 | 28562.5 | 5.70 | 4.26 | 22.52 | 2340 | 10649053718 |
| 1 | Live catch-up | 1 | 291.7 | 93.8 | 2.59 | 6.87 | 22.52 | 2 | 376202 |
| 1 | Append | 1 | 232.2 | 78.1 | 3.63 | 8.99 | 24.38 | 1 | 130580 |
| 2 | Full | 2 | 11367.4 | 15656.2 | 12.02 | 9.32 | 32.03 | 2340 | 10649561207 |
| 2 | Live catch-up | 1 | 137.9 | 109.4 | 2.59 | 11.91 | 32.03 | 1 | 17910 |
| 2 | Append | 1 | 109.3 | 93.8 | 2.59 | 14.80 | 32.03 | 1 | 130580 |
| 3 | Full | 2 | 14727.3 | 22187.5 | 10.73 | 8.75 | 33.34 | 2340 | 10650192276 |
| 3 | Live catch-up | 1 | 195.0 | 203.1 | 2.46 | 10.09 | 33.34 | 1 | 7822 |
| 3 | No change | 1 | 107.9 | 109.4 | 2.45 | 12.30 | 33.34 | 0 | 0 |
| 3 | Append | 1 | 167.6 | 156.2 | 2.45 | 15.06 | 33.34 | 1 | 130580 |

Each run discovered 2,339 live files plus the empty fixture. The final run discovered 10,650,177,253 bytes of live input and skipped 3,523 oversized lines. No candidate parse failures or files disappearing before open were reported. Each append added 130,580 bytes and opened exactly the fixture. Earlier immediate passes picked up live data, so those rows are labeled live catch-up.

The indexes occupied **8,278,016 bytes**, **8,261,632 bytes**, and **8,368,128 bytes** on disk after each respective probe. The third run used the final code with a 12 MiB SQLite page-cache budget, up from 4 MiB in the baseline runs. The repeated runs did not control filesystem cache state or background load. The measurements do not isolate the effect of worker count or the cache change.

| Target | Result |
|---|---|
| Full scan in 60 s or less | **Met with two workers** at 11.37 s and 14.73 s. The first, one-worker run missed at 332.46 s. Cold two-worker performance is unverified. |
| Full-scan managed peak of 64 MB or less | **Met** in all three trials. The highest sampled peak was 9.32 MB. |
| No-change CPU of 100 ms or less | **Missed** at 109.4 ms with 0 files opened and 0 bytes read in the final trial. |
| Burst CPU of 100 ms or less | **Missed in the final run** at 156.2 ms. Earlier runs met it at 78.1 and 93.8 ms. The 100-line burst was controlled, and its representativeness of live bursts is unverified. |
| Within 1% of the CLI for every completed day | **Missed** for both providers. Claude matched on 0 of 26 referenced days, with 4 missing references. Codex matched on 2 of 27 referenced days, with 3 missing references. |

To reproduce from the worktree, build the probe in Release and pass a fresh directory for each full trial.

```powershell
dotnet build tools/CostProbe/CostProbe.csproj -c Release -nodeReuse:false
& ./tools/CostProbe/bin/Release/net10.0-windows/CostProbe.exe .spike-data/fresh-index 2
```

## Completed-day parity

The comparison covers October 26 through November 24, 2026 in Korea Standard Time. The probe read both saved snapshots and selected a ledger row first, with a cache row as a fallback. All matched references came from the ledger, which can retain days whose logs are gone. Missing references are unverified. A native zero can represent unpriced usage or no indexed usage. Deltas are native minus CLI. The three probes produced identical completed-day tables.

- **Claude.** The native token count exactly matched 20 of 26 referenced days, but 99.93% of native tokens on those days were unpriced. The bundled table lacks rates for newer models in the reference, including Claude Opus 5.5, Sonnet 5.5 and Haiku 5.5. The CLI's exact catalog and any overrides are unverified. Six days had more native tokens. Five differences were below 0.1%. On November 20 the native scanner had 336,796,515 more tokens and the last successful Claude cache was from that day. The source of the Claude token differences is unverified.
- **Codex.** On 20 referenced days through November 16, the current session tree produced no day rows even though the ledger retained costs. Whether archived, deleted or differently scoped logs account for those days is unverified. November 18 and 21 matched. November 19, 20, 22 and 23 had identical token totals. Applying the bundled long-context rates above 200K instead of 272K to the stored GPT-6.2 bands reproduced all four CLI costs. The pricing source or override responsible for the observed threshold is unverified. On November 24 a targeted read found 1,995,013 request tokens at or before the ledger timestamp, exactly the ledger's total, and 1,396,799 after it, exactly the native excess. This is consistent with the saved snapshot being taken before the day ended.

| Provider | Date | Native USD | CLI USD | Delta USD | Delta % | CLI source |
|---|---|---:|---:|---:|---:|---|
| claude | 2026-10-26 | 0.000000 | 24.293984 | -24.293984 | -100.00% | Ledger |
| claude | 2026-10-27 | 0.000000 | 123.623035 | -123.623035 | -100.00% | Ledger |
| claude | 2026-10-28 | 0.000000 | 83.490040 | -83.490040 | -100.00% | Ledger |
| claude | 2026-10-29 | 7.174959 | 202.207277 | -195.032319 | -96.45% | Ledger |
| claude | 2026-10-30 | 0.000000 | 17.423372 | -17.423372 | -100.00% | Ledger |
| claude | 2026-10-31 | 0.000000 | 214.044086 | -214.044086 | -100.00% | Ledger |
| claude | 2026-11-01 | 0.000000 | 187.869714 | -187.869714 | -100.00% | Ledger |
| claude | 2026-11-02 | 0.000000 | 135.306015 | -135.306015 | -100.00% | Ledger |
| claude | 2026-11-03 | 0.000000 | 178.200276 | -178.200276 | -100.00% | Ledger |
| claude | 2026-11-04 | 0.000000 | 183.417066 | -183.417066 | -100.00% | Ledger |
| claude | 2026-11-05 | 0.000000 | 162.213653 | -162.213653 | -100.00% | Ledger |
| claude | 2026-11-06 | 0.000000 | 115.479383 | -115.479383 | -100.00% | Ledger |
| claude | 2026-11-07 | 0.000000 | 71.868425 | -71.868425 | -100.00% | Ledger |
| claude | 2026-11-08 | 0.000000 | 147.842479 | -147.842479 | -100.00% | Ledger |
| claude | 2026-11-09 | 0.000000 | 112.120080 | -112.120080 | -100.00% | Ledger |
| claude | 2026-11-10 | 0.000000 | 54.188611 | -54.188611 | -100.00% | Ledger |
| claude | 2026-11-11 | 0.000000 | 73.428731 | -73.428731 | -100.00% | Ledger |
| claude | 2026-11-12 | 0.000000 | 89.603537 | -89.603537 | -100.00% | Ledger |
| claude | 2026-11-13 | 0.000000 | 109.130162 | -109.130162 | -100.00% | Ledger |
| claude | 2026-11-14 | 0.000000 | 133.150046 | -133.150046 | -100.00% | Ledger |
| claude | 2026-11-15 | 0.000000 | 147.342276 | -147.342276 | -100.00% | Ledger |
| claude | 2026-11-16 | 0.000000 | 393.041181 | -393.041181 | -100.00% | Ledger |
| claude | 2026-11-17 | 0.000000 | 278.528782 | -278.528782 | -100.00% | Ledger |
| claude | 2026-11-18 | 0.000000 | 222.098312 | -222.098312 | -100.00% | Ledger |
| claude | 2026-11-19 | 0.000000 | 172.741051 | -172.741051 | -100.00% | Ledger |
| claude | 2026-11-20 | 0.000000 | 273.046949 | -273.046949 | -100.00% | Ledger |
| claude | 2026-11-21 | 0.000000 | — | — | — | Missing |
| claude | 2026-11-22 | 0.000000 | — | — | — | Missing |
| claude | 2026-11-23 | 0.000000 | — | — | — | Missing |
| claude | 2026-11-24 | 0.000000 | — | — | — | Missing |
| codex | 2026-10-26 | 0.000000 | 44.461246 | -44.461246 | -100.00% | Ledger |
| codex | 2026-10-27 | 0.000000 | 32.111020 | -32.111020 | -100.00% | Ledger |
| codex | 2026-10-28 | 0.000000 | 27.224356 | -27.224356 | -100.00% | Ledger |
| codex | 2026-10-29 | 0.000000 | 35.168987 | -35.168987 | -100.00% | Ledger |
| codex | 2026-10-30 | 0.000000 | — | — | — | Missing |
| codex | 2026-10-31 | 0.000000 | 9.468460 | -9.468460 | -100.00% | Ledger |
| codex | 2026-11-01 | 0.000000 | 12.942630 | -12.942630 | -100.00% | Ledger |
| codex | 2026-11-02 | 0.000000 | 21.407051 | -21.407051 | -100.00% | Ledger |
| codex | 2026-11-03 | 0.000000 | 23.375323 | -23.375323 | -100.00% | Ledger |
| codex | 2026-11-04 | 0.000000 | 20.008640 | -20.008640 | -100.00% | Ledger |
| codex | 2026-11-05 | 0.000000 | 2.213128 | -2.213128 | -100.00% | Ledger |
| codex | 2026-11-06 | 0.000000 | 21.248410 | -21.248410 | -100.00% | Ledger |
| codex | 2026-11-07 | 0.000000 | 0.436522 | -0.436522 | -100.00% | Ledger |
| codex | 2026-11-08 | 0.000000 | 16.210222 | -16.210222 | -100.00% | Ledger |
| codex | 2026-11-09 | 0.000000 | — | — | — | Missing |
| codex | 2026-11-10 | 0.000000 | 0.090899 | -0.090899 | -100.00% | Ledger |
| codex | 2026-11-11 | 0.000000 | 10.192034 | -10.192034 | -100.00% | Ledger |
| codex | 2026-11-12 | 0.000000 | 4.683442 | -4.683442 | -100.00% | Ledger |
| codex | 2026-11-13 | 0.000000 | 5.369335 | -5.369335 | -100.00% | Ledger |
| codex | 2026-11-14 | 0.000000 | 17.521205 | -17.521205 | -100.00% | Ledger |
| codex | 2026-11-15 | 0.000000 | 5.726333 | -5.726333 | -100.00% | Ledger |
| codex | 2026-11-16 | 0.000000 | 1.250216 | -1.250216 | -100.00% | Ledger |
| codex | 2026-11-17 | 0.000000 | — | — | — | Missing |
| codex | 2026-11-18 | 10.992350 | 10.992350 | 0.000000 | 0.00% | Ledger |
| codex | 2026-11-19 | 30.860899 | 36.728942 | -5.868043 | -15.98% | Ledger |
| codex | 2026-11-20 | 14.284439 | 15.534576 | -1.250136 | -8.05% | Ledger |
| codex | 2026-11-21 | 10.778661 | 10.778661 | 0.000000 | 0.00% | Ledger |
| codex | 2026-11-22 | 32.454414 | 39.544488 | -7.090074 | -17.93% | Ledger |
| codex | 2026-11-23 | 22.905096 | 23.288524 | -0.383428 | -1.65% | Ledger |
| codex | 2026-11-24 | 3.021398 | 1.238040 | +1.783358 | +144.05% | Ledger |

## Upstream coverage and divergences

- Source review used CodexBar main at `a26ca57b518667a659d69524c473af2735069612`. Upstream defaults to its process calendar. The native scanner uses the Windows local time zone by default, with an explicit zone supported for tests and callers. The saved snapshots do not establish the CLI's actual WSL time zone or executable revision. [Fetcher](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/CostUsageFetcher.swift), [scan executor](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/CostUsageScanExecutor.swift), [models and calendar](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/CostUsageModels.swift).
- Claude uses request/message keys and falls back to session/message keys. Complete snapshots beat incomplete ones. Across files, the parent/sidechain preferences are followed, with a path-fingerprint tie-break. Upstream breaks those ties by lexical path order, resolves rows within a scan window, and can apply provider filters, including Vertex detection. The price impact of those differences is unverified. [Claude scanner](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/Vendored/CostUsage/CostUsageScanner%2BClaude.swift).
- Codex handles common request records and following cumulative mirrors, plus legacy total and last-usage records. The spike omits upstream request ownership recovery, fork baselines, archived sessions, alternative homes, interleaved or resetting counters, one-shot response envelopes, and auxiliary priority traces. It attributes models at the current turn context and service tier at the most recent settings event, with no reconciliation of later evidence. The effect of each individual difference is unverified. [Codex scanner](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/Vendored/CostUsage/CostUsageScanner.swift).
- Pricing uses selected bundled rates and aliases, including long-context buckets and separate Claude cache write TTLs. Upstream resolves additional rates via models.dev, provider-specific rules, custom overlays, and historical pricing logic. The spike leaves unsupported models unpriced and does not reproduce upstream pricing coverage or per-request nanodollar rounding. Tier rates and rate changes inside the stored bands can be reapplied without any log I/O. A new threshold or historical cutover outside the stored bands would require an index change. [Bundled and historical pricing](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/Vendored/CostUsage/CostUsagePricing.swift).
- Both providers use a 512 KiB cap per complete line in the spike. Upstream's Codex parser caps its line buffer at 256 KiB and can recover context from truncated prefixes. The spike skips oversized lines and uses literal ASCII prefilters and bounded integer extraction. Escaped field names, numeric strings and other uncommon shapes can diverge. An unfinished tail is read again when the file next changes because the index stores no raw line fragments. [JSONL reader](https://github.com/steipete/CodexBar/blob/a26ca57b518667a659d69524c473af2735069612/Sources/CodexBarCore/Vendored/CostUsage/CostUsageJsonl.swift).
- The index retains committed usage after rotation or deletion. It detects shrinkage and changes in the opened Windows file identity, and skips reparse points. An in-place rewrite that reaches the old length before an update can escape detection, and moving or copying an unkeyed file to another scanned path can count it again. These cases are unverified on live inputs.

## Build verification

The Release app build succeeded with `-p:WindowsSDKBuildToolsArchFolder=x64`. MSBuild otherwise selected the x86 resource DLL for the x64 .NET host, which caused the MSIX recipe task to fail with Win32 error 193. The successful build had one missing publish-profile warning and zero errors. The Release probe built with zero warnings and errors.

The final x64 Debug unit run passed all 2,011 tests, with zero failures or skips.
