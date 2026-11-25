#Requires -Version 7
<#
.SYNOPSIS
  Samples CPU time and memory of CodexBarWin, the WSL VM and the WSL helper processes, then summarises each refresh.

.DESCRIPTION
  Read-only. Samples every -IntervalMs for -Minutes and writes a CSV, then prints:
    * one row per WSL VM episode (vmmemWSL present): duration, peak working set, peak private bytes, CPU seconds;
    * app totals: CPU seconds per hour, CPU seconds attributed to refreshes, idle CPU seconds per hour,
      median and peak working set and private bytes, and the refresh count.
  A refresh is marked by a change of cache.json's last write time (the app writes it after every refresh).
  CPU attributed to a refresh is the app's CPU in the window [marker - PreSeconds, marker + PostSeconds].

.EXAMPLE
  pwsh -NoProfile -File tools/perf/Measure-Refresh.ps1 -Minutes 11
  pwsh -NoProfile -File tools/perf/Measure-Refresh.ps1 -SummarizeOnly -Out <existing csv>
#>
param(
    [double]$Minutes = 11,
    [int]$IntervalMs = 250,
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ("codexbar-perf\refresh-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))),
    [string]$CacheFile = (Join-Path $env:LOCALAPPDATA 'Packages\CodexBarWin_w05y65rx3mcz6\LocalCache\Local\CodexBarWin\cache.json'),
    [string]$AppProcess = 'CodexBarWin',
    [int]$PreSeconds = 25,
    [int]$PostSeconds = 5,
    [switch]$SummarizeOnly
)

$ErrorActionPreference = 'Stop'
$names = @($AppProcess, 'vmmemWSL', 'vmmem', 'wsl', 'wslhost', 'wslrelay')

function Get-CpuSeconds($p) {
    try { return [math]::Round($p.TotalProcessorTime.TotalSeconds, 3) } catch { return '' }
}

if (-not $SummarizeOnly) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $endMs = $Minutes * 60000
    $writer = [IO.StreamWriter]::new($Out, $false, [Text.UTF8Encoding]::new($false))
    $writer.WriteLine('t_ms,clock,proc,pid,ws_mb,private_mb,cpu_s,cache_mtime')
    try {
        while ($sw.ElapsedMilliseconds -lt $endMs) {
            $t = $sw.ElapsedMilliseconds
            $clock = (Get-Date).ToString('HH:mm:ss.fff')
            $mtime = try { (Get-Item -LiteralPath $CacheFile).LastWriteTimeUtc.Ticks } catch { '' }
            foreach ($p in (Get-Process -Name $names -ErrorAction SilentlyContinue)) {
                $writer.WriteLine(('{0},{1},{2},{3},{4},{5},{6},{7}' -f $t, $clock, $p.ProcessName, $p.Id,
                    [math]::Round($p.WorkingSet64 / 1MB, 1), [math]::Round($p.PrivateMemorySize64 / 1MB, 1),
                    (Get-CpuSeconds $p), $mtime))
            }
            $writer.Flush()
            $sleep = $IntervalMs - ($sw.ElapsedMilliseconds - $t)
            if ($sleep -gt 0) { Start-Sleep -Milliseconds $sleep }
        }
    } finally { $writer.Dispose() }
}

# ---- Summary ----
$rows = Import-Csv $Out | ForEach-Object {
    [pscustomobject]@{
        t = [long]$_.t_ms; clock = $_.clock; proc = $_.proc; pid = [int]$_.pid
        ws = [double]$_.ws_mb; priv = [double]$_.private_mb
        cpu = if ($_.cpu_s -eq '') { $null } else { [double]$_.cpu_s }
        mtime = $_.cache_mtime
    }
}
if (-not $rows) { Write-Output "No samples in $Out"; return }
$runSeconds = (($rows | Measure-Object t -Maximum).Maximum - ($rows | Measure-Object t -Minimum).Minimum) / 1000.0

function Get-CpuDelta($samples) {
    # Sum, per pid, of last minus first CPU reading. A pid seen once contributes its whole reading
    # (it started inside the window), which slightly overstates short-lived helpers that pre-existed.
    $total = 0.0; $known = $false
    foreach ($g in ($samples | Where-Object { $null -ne $_.cpu } | Group-Object pid)) {
        $known = $true
        $s = $g.Group | Sort-Object t
        $total += if ($s.Count -gt 1) { $s[-1].cpu - $s[0].cpu } else { $s[0].cpu }
    }
    if ($known) { [math]::Round($total, 3) } else { 'n/a' }
}

# VM episodes: contiguous vmmem presence (gap <= 2 s).
$vm = $rows | Where-Object proc -like 'vmmem*' | Sort-Object t
$episodes = @(); $cur = $null
foreach ($r in $vm) {
    if ($cur -and ($r.t - $cur.lastT) -le 2000) { $cur.samples += $r; $cur.lastT = $r.t }
    else { if ($cur) { $episodes += $cur }; $cur = [pscustomobject]@{ startT = $r.t; lastT = $r.t; samples = @($r) } }
}
if ($cur) { $episodes += $cur }

$helpers = $rows | Where-Object { $_.proc -in 'wsl', 'wslhost', 'wslrelay' }
$app = $rows | Where-Object proc -eq $AppProcess | Sort-Object t

Write-Output "Samples: $Out ($([math]::Round($runSeconds/60,1)) min)"
Write-Output ''
Write-Output "WSL VM episodes: $($episodes.Count)"
$episodes | ForEach-Object {
    $s = $_.samples; $from = $_.startT - 2000; $to = $_.lastT + 2000
    [pscustomobject]@{
        start = $s[0].clock; seconds = [math]::Round(($_.lastT - $_.startT) / 1000.0, 1)
        vm_peak_ws_mb = ($s | Measure-Object ws -Maximum).Maximum
        vm_peak_private_mb = ($s | Measure-Object priv -Maximum).Maximum
        vm_cpu_s = Get-CpuDelta $s
        helpers_cpu_s = Get-CpuDelta ($helpers | Where-Object { $_.t -ge $from -and $_.t -le $to })
        app_cpu_s = Get-CpuDelta ($app | Where-Object { $_.t -ge $from -and $_.t -le $to })
    }
} | Format-Table -AutoSize | Out-String -Width 200 | Write-Output

# Refresh markers from cache.json write times.
$markers = @(); $prev = $null
foreach ($r in $app) { if ($r.mtime -and $prev -and $r.mtime -ne $prev) { $markers += $r.t }; if ($r.mtime) { $prev = $r.mtime } }
$appCpuTotal = Get-CpuDelta $app
$refreshCpu = 0.0; $refreshSecs = 0.0
foreach ($m in $markers) {
    $w = $app | Where-Object { $_.t -ge ($m - $PreSeconds * 1000) -and $_.t -le ($m + $PostSeconds * 1000) }
    $d = Get-CpuDelta $w; if ($d -ne 'n/a') { $refreshCpu += $d }; $refreshSecs += $PreSeconds + $PostSeconds
}
$idleHours = [math]::Max(($runSeconds - $refreshSecs) / 3600.0, 1e-9)
$appSorted = $app | Sort-Object ws
$median = { param($xs) $a = @($xs | Sort-Object); if ($a.Count) { $a[[int][math]::Floor($a.Count / 2)] } else { 'n/a' } }
Write-Output 'App process:'
[pscustomobject]@{
    refreshes = $markers.Count
    cpu_s_total = $appCpuTotal
    cpu_s_per_hour = if ($appCpuTotal -ne 'n/a') { [math]::Round($appCpuTotal / ($runSeconds / 3600.0), 2) } else { 'n/a' }
    cpu_s_per_refresh = if ($markers.Count) { [math]::Round($refreshCpu / $markers.Count, 3) } else { 'n/a' }
    idle_cpu_s_per_hour = if ($appCpuTotal -ne 'n/a') { [math]::Round(($appCpuTotal - $refreshCpu) / $idleHours, 2) } else { 'n/a' }
    ws_median_mb = & $median ($app.ws); ws_peak_mb = ($app | Measure-Object ws -Maximum).Maximum
    private_median_mb = & $median ($app.priv); private_peak_mb = ($app | Measure-Object priv -Maximum).Maximum
} | Format-List | Out-String | Write-Output
