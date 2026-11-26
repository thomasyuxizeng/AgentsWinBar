# Performance

AgentsWinBar sits in the tray all day, so it is built to hold little memory and to spend almost no CPU when nothing is
happening. This page compares 1.6.0 with the last release that read usage through WSL (1.5.4) and says how each number
was measured, so you can check them on your own PC.

## Results

| Measure | 1.5.4 (WSL route) | 1.6.0 (native) |
|---|---|---|
| Working set at idle (median) | 133 to 157 MB | 37 MB |
| Private bytes at idle (median) | 176 to 196 MB | 118 MB |
| Private working set at idle (median, Task Manager's "Memory" column) | not recorded | 17 MB |
| Extra memory during each refresh | a WSL virtual machine of 1.3 to 1.5 GB for about 33 seconds | none |
| CPU per refresh | about 10 CPU seconds (about 2.5 in the app, the rest in WSL) | 0.11 to 0.25 CPU seconds (median 0.18) |
| CPU between refreshes | 15.9 CPU seconds per hour | 3.8 CPU seconds per hour |

All figures are for the app with its windows closed (tray icon and taskbar strip only), three providers signed in
(Claude, Codex, Gemini), on the same Windows 11 laptop (Intel Core i5-1340P). The app refreshes every 5 minutes and,
with adaptive refresh on (the default), stretches that to 10 and then 15 minutes while usage stays unchanged. The
1.6.0 figures come from a 45-minute run of the installed 1.6.0 release candidate with nobody using the PC (8
refreshes, no WSL virtual machine started).
The 1.5.4 figures come from an 11-minute run of a local 1.5.4 build with the same settings; its CPU per refresh adds
three separately measured parts (app, programs in WSL, WSL start-up) and was never measured end to end.

## What still costs something

- **Opening a window.** The flyout and the dashboard build their pages when they open and release them when they
  close. While a cost view is open the app scans new lines in your Claude Code and Codex logs; the first scan after
  an install reads every log once (seconds of CPU on a large history), later scans read only what changed.
- **Renewing an expired sign-in.** When a Claude or Gemini sign-in has expired, the app runs the provider's own tool
  once (`claude auth status --json` or `agy models`). Those are separate processes, not the app: one `agy models` run
  measured about 1.7 CPU seconds and peaked at about 220 MB, then exited. A renewal runs only for an expired sign-in,
  at most every 30 minutes, and backs off to every 2 and then 6 hours while it keeps failing. If you use the provider's
  own app regularly, its sign-in rarely expires and these runs rarely happen.
- **Status checks.** Each provider's status page is read at most every 5 minutes, together with a refresh.

## How it was measured

Both scripts are in `tools/perf` and only read process counters; they change nothing.

```powershell
# Samples the app (and any WSL VM) every 250 ms for 45 minutes, then prints medians, peaks and CPU per refresh.
pwsh -NoProfile -File tools/perf/Measure-Refresh.ps1 -Minutes 45

# What the app's committed memory is made of (GC heap, JIT code, native heap, mapped images).
pwsh -NoProfile -File tools/perf/Get-MemoryMap.ps1 -ProcessId (Get-Process AgentsWinBar).Id
```

`Measure-Refresh.ps1` samples every 250 ms, marks a refresh when the app rewrites `cache.json`, and counts the app's
CPU from 25 seconds before to 5 seconds after that moment as the refresh; everything else counts as idle. Private
working set is sampled every 5 seconds through Windows' Process performance counters, which is what Task Manager
shows as "Memory".

Memory varies a few MB with the number of providers, the theme and Windows' own trimming, so compare medians over a
long run, not single readings.
