<#
.SYNOPSIS
    Reads the CodexBarWin permanent cost ledger and displays lifetime, last-30-days, and custom range totals.

.DESCRIPTION
    Pure read-only report tool for querying lifetime AI tool costs and token counts recorded
    by CodexBarWin. Survives local CLI log rotations.

.PARAMETER Path
    Path to cost-ledger.json. Defaults to the installed package's LocalCache ledger,
    falling back to %LOCALAPPDATA%\CodexBarWin\cost-ledger.json.

.PARAMETER Provider
    Optional provider filter (e.g. 'claude', 'codex', 'gemini').

.PARAMETER From
    Optional start date (yyyy-MM-dd) for a custom range report.

.PARAMETER To
    Optional end date (yyyy-MM-dd) for a custom range report.

.EXAMPLE
    .\tools\cost-ledger-report.ps1
    .\tools\cost-ledger-report.ps1 -Provider claude
    .\tools\cost-ledger-report.ps1 -From 2026-09-01 -To 2026-09-30
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter()]
    [string]$Path = '',

    [Parameter(Position = 0)]
    [string]$Provider = '',

    [Parameter()]
    [string]$From = '',

    [Parameter()]
    [string]$To = ''
)

$ErrorActionPreference = 'Stop'

function Resolve-CostLedgerPath([string]$OverridePath, [string]$LocalAppData = $env:LOCALAPPDATA) {
    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) { return $OverridePath }
    if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
        try {
            $packages = @(Get-AppxPackage -Name CodexBarWin -ErrorAction Stop | Sort-Object { [version]$_.Version } -Descending)
            foreach ($package in $packages) {
                $packagedPath = Join-Path $LocalAppData "Packages\$($package.PackageFamilyName)\LocalCache\Local\CodexBarWin\cost-ledger.json"
                if (Test-Path -LiteralPath $packagedPath -PathType Leaf) { return $packagedPath }
            }
        } catch {
            Write-Verbose "Package lookup unavailable; using the unpackaged ledger path."
        }
    }
    return (Join-Path $LocalAppData 'CodexBarWin\cost-ledger.json')
}

$Path = Resolve-CostLedgerPath -OverridePath $Path
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    Write-Host "Cost ledger file not found at: $Path" -ForegroundColor Yellow
    Write-Host "CodexBarWin creates cost-ledger.json once a cost scan completes."
    exit 0
}

try {
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $ledger = $raw | ConvertFrom-Json
}
catch {
    Write-Error "Failed to parse cost ledger JSON at $($Path): $_"
    exit 1
}

if (-not $ledger -or -not $ledger.providers) {
    Write-Host "Cost ledger at $Path is empty or has no providers recorded."
    exit 0
}

$today = [DateTime]::Today
$thirtyDaysAgo = $today.AddDays(-29)

function Parse-DateOnly([string]$dateStr) {
    if ([string]::IsNullOrWhiteSpace($dateStr)) { return $null }
    try {
        return [DateTime]::ParseExact($dateStr.Trim(), 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
    } catch {
        return $null
    }
}

$fromDate = Parse-DateOnly $From
$toDate = Parse-DateOnly $To

$providerProps = $ledger.providers.PSObject.Properties | Where-Object {
    if ($Provider) {
        $_.Name -eq $Provider -or $_.Name -like "*$Provider*"
    } else {
        $true
    }
}

if (-not $providerProps) {
    Write-Host "No providers found matching '$Provider' in $Path" -ForegroundColor Yellow
    exit 0
}

$lifetimeRows = @()
$last30Rows = @()
$rangeRows = @()

$grandLifetimeCost = 0.0
$grandLifetimeTokens = 0
$grandLast30Cost = 0.0
$grandLast30Tokens = 0
$grandRangeCost = 0.0
$grandRangeTokens = 0

foreach ($prop in $providerProps) {
    $pName = $prop.Name
    $daysObj = $prop.Value
    if (-not $daysObj) { continue }

    $dayProps = $daysObj.PSObject.Properties

    $pLifetimeCost = 0.0
    $pLifetimeTokens = 0
    $pInputTokens = 0
    $pOutputTokens = 0
    $pCacheReadTokens = 0
    $pCacheWriteTokens = 0
    $pReasoningTokens = 0
    $pDaysWithData = 0
    $pFirstDate = $null
    $pLastDate = $null

    $pLast30Cost = 0.0
    $pLast30Tokens = 0
    $pLast30DaysWithData = 0

    $pRangeCost = 0.0
    $pRangeTokens = 0
    $pRangeDaysWithData = 0

    $sortedDays = $dayProps | Sort-Object { $_.Name }

    foreach ($dp in $sortedDays) {
        $dayDateStr = $dp.Name
        $entry = $dp.Value
        if (-not $entry) { continue }

        $parsedDate = Parse-DateOnly $dayDateStr
        if (-not $parsedDate) {
            continue
        }

        $cost = if ($entry.costUSD) { [double]$entry.costUSD } else { 0.0 }
        $tokens = if ($entry.totalTokens) { [int64]$entry.totalTokens } else { 0 }
        $input = if ($entry.inputTokens) { [int64]$entry.inputTokens } else { 0 }
        $output = if ($entry.outputTokens) { [int64]$entry.outputTokens } else { 0 }
        $cacheRead = if ($entry.cacheReadTokens) { [int64]$entry.cacheReadTokens } else { 0 }
        $cacheWrite = if ($entry.cacheWriteTokens) { [int64]$entry.cacheWriteTokens } else { 0 }
        $reasoning = if ($entry.reasoningTokens) { [int64]$entry.reasoningTokens } else { 0 }

        $hasData = ($cost -gt 0) -or ($tokens -gt 0) -or ($input -gt 0) -or ($output -gt 0) -or ($cacheRead -gt 0) -or ($cacheWrite -gt 0) -or ($reasoning -gt 0)

        if ($hasData) {
            $pDaysWithData++
            if (-not $pFirstDate) { $pFirstDate = $dayDateStr }
            $pLastDate = $dayDateStr

            $pLifetimeCost += $cost
            $pLifetimeTokens += $tokens
            $pInputTokens += $input
            $pOutputTokens += $output
            $pCacheReadTokens += $cacheRead
            $pCacheWriteTokens += $cacheWrite
            $pReasoningTokens += $reasoning
        }

        # Check last 30 days
        if ($parsedDate.Date -ge $thirtyDaysAgo.Date -and $parsedDate.Date -le $today.Date) {
            if ($hasData) {
                $pLast30Cost += $cost
                $pLast30Tokens += $tokens
                $pLast30DaysWithData++
            }
        }

        # Check custom range
        if ($fromDate -and $toDate) {
            if ($parsedDate.Date -ge $fromDate.Date -and $parsedDate.Date -le $toDate.Date) {
                if ($hasData) {
                    $pRangeCost += $cost
                    $pRangeTokens += $tokens
                    $pRangeDaysWithData++
                }
            }
        }
    }

    $grandLifetimeCost += $pLifetimeCost
    $grandLifetimeTokens += $pLifetimeTokens
    $grandLast30Cost += $pLast30Cost
    $grandLast30Tokens += $pLast30Tokens
    $grandRangeCost += $pRangeCost
    $grandRangeTokens += $pRangeTokens

    $lifetimeRows += [PSCustomObject]@{
        Provider    = $pName
        'Cost (USD)' = "{0:N2}" -f $pLifetimeCost
        'Tokens'    = "{0:N0}" -f $pLifetimeTokens
        'Days'      = $pDaysWithData
        'First Day' = if ($pFirstDate) { $pFirstDate } else { '-' }
        'Last Day'  = if ($pLastDate) { $pLastDate } else { '-' }
    }

    $last30Rows += [PSCustomObject]@{
        Provider    = $pName
        'Cost (USD)' = "{0:N2}" -f $pLast30Cost
        'Tokens'    = "{0:N0}" -f $pLast30Tokens
        'Days w/Data' = "$pLast30DaysWithData / 30"
    }

    if ($fromDate -and $toDate) {
        $rangeRows += [PSCustomObject]@{
            Provider    = $pName
            'Cost (USD)' = "{0:N2}" -f $pRangeCost
            'Tokens'    = "{0:N0}" -f $pRangeTokens
            'Days w/Data' = $pRangeDaysWithData
        }
    }
}

Write-Host "=== CODEXBARWIN COST LEDGER REPORT ===" -ForegroundColor Cyan
Write-Host "Ledger Path: $Path"
Write-Host "Report Date: $($today.ToString('yyyy-MM-dd'))"
Write-Host ""

Write-Host "--- LIFETIME TOTALS ---" -ForegroundColor Green
$lifetimeRows | Format-Table -AutoSize
Write-Host ("Lifetime Combined: {0:C2} USD across {1:N0} tokens" -f $grandLifetimeCost, $grandLifetimeTokens) -ForegroundColor Green
Write-Host ""

Write-Host "--- LAST 30 DAYS ($($thirtyDaysAgo.ToString('yyyy-MM-dd')) to $($today.ToString('yyyy-MM-dd'))) ---" -ForegroundColor Yellow
$last30Rows | Format-Table -AutoSize
Write-Host ("Last 30 Days Combined: {0:C2} USD across {1:N0} tokens" -f $grandLast30Cost, $grandLast30Tokens) -ForegroundColor Yellow
Write-Host ""

if ($fromDate -and $toDate) {
    Write-Host "--- CUSTOM RANGE ($($fromDate.ToString('yyyy-MM-dd')) to $($toDate.ToString('yyyy-MM-dd'))) ---" -ForegroundColor Magenta
    $rangeRows | Format-Table -AutoSize
    Write-Host ("Range Combined: {0:C2} USD across {1:N0} tokens" -f $grandRangeCost, $grandRangeTokens) -ForegroundColor Magenta
    Write-Host ""
}
