<#
.SYNOPSIS
Tests installer helpers in isolation. Never runs setup, WSL, login, or system installation.
#>
$ErrorActionPreference = 'Stop'
trap {
    Write-Host $_ -ForegroundColor Red
    exit 1
}
$installerPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'install.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer has PowerShell syntax errors.' }

$helperNames = @('Quote-Sh', 'Quote-Native', 'Test-Usage')
$helpers = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $helperNames
}, $true))
if ($helpers.Count -ne $helperNames.Count) { throw 'Expected installer helper definitions were not found.' }
# Evaluate only function definitions, never the installer body or prerequisite probes.
foreach ($helper in $helpers) { . ([scriptblock]::Create($helper.Extent.Text)) }

function Assert-Equal($Actual, $Expected, [string] $Name) {
    if ($Actual -cne $Expected) { throw "Failed: $Name" }
}

$nativeCases = @(
    @{ Value = ''; Expected = '""' },
    @{ Value = 'hello world'; Expected = '"hello world"' },
    @{ Value = '--version'; Expected = '--version' },
    @{ Value = '--exec'; Expected = '--exec' },
    @{ Value = 'C:\folder\'; Expected = 'C:\folder\' },
    @{ Value = 'C:\folder\file.exe'; Expected = 'C:\folder\file.exe' },
    @{ Value = 'C:\a path\'; Expected = '"C:\a path\\"' },
    @{ Value = 'C:\A B\\'; Expected = '"C:\A B\\\\"' },
    @{ Value = 'a"b'; Expected = '"a\"b"' },
    @{ Value = 'a\"b'; Expected = '"a\\\"b"' }
)
$caseNumber = 0
foreach ($case in $nativeCases) {
    $caseNumber++
    Assert-Equal (Quote-Native $case.Value) $case.Expected "native quoting case $caseNumber"
}
Assert-Equal (Quote-Sh '') "''" 'empty shell argument'
Assert-Equal (Quote-Sh "O'Brien") ("'O'" + [char]34 + "'" + [char]34 + "'Brien'") 'apostrophe shell argument'

$bashPath = $null
$bash = Get-Command bash.exe -ErrorAction SilentlyContinue
if ($bash -and $bash.Source -match '[\\/]Git[\\/].*[\\/]bash\.exe$') { $bashPath = $bash.Source }
if (-not $bashPath) {
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) {
            $candidate = Join-Path $base 'Git\usr\bin\bash.exe'
            if (Test-Path -LiteralPath $candidate) { $bashPath = $candidate; break }
        }
    }
}
if ($bashPath) {
    foreach ($value in @('C:/a path/file', "C:/O'Brien/a file", 'literal/$(printf UNSAFE)/`printf UNSAFE`')) {
        $processInfo = New-Object Diagnostics.ProcessStartInfo
        $processInfo.FileName = $bashPath
        $processInfo.Arguments = (Quote-Native '-c') + ' ' + (Quote-Native ("printf '%s' " + (Quote-Sh $value)))
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $processInfo
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Shell quoting test timed out.' }
            Assert-Equal $process.ExitCode 0 'shell quoting process'
            Assert-Equal $stderr.Result '' 'shell quoting stderr'
            Assert-Equal $stdout.Result $value 'literal shell round trip'
        } finally { $process.Dispose() }
    }
} else { Write-Host 'Git Bash unavailable; shell round-trip checks skipped.' }

$script:mockWslResult = $null
function Wsl([string] $Command, [string] $Name, [switch] $Root, [switch] $Interactive, [int] $Timeout) {
    Assert-Equal $Command 'codexbar usage --provider codex --format json --source cli' 'usage command'
    Assert-Equal $Name '' 'default distro usage'
    Assert-Equal ([bool]$Root) $false 'default user usage'
    Assert-Equal ([bool]$Interactive) $true 'interactive bash usage'
    Assert-Equal $Timeout 180 'usage timeout'
    $script:mockWslResult
}
function Check-Usage([string] $Name, [string] $Json, [string] $Expected, [int] $ExitCode = 0, [string] $ErrorText = 'synthetic-private-diagnostic') {
    $script:mockWslResult = [pscustomobject]@{ Code = $ExitCode; Output = $Json; Error = $ErrorText }
    # A status string must be the only emitted value across every PowerShell output stream.
    # This also catches accidental logging of raw JSON, identity, or diagnostics.
    $emitted = @(& { Test-Usage 'codex' } *>&1)
    if ($emitted.Count -ne 1 -or $emitted[0] -isnot [string] -or $emitted[0] -cnotin @('live', 'stale', 'none')) { throw "Failed: $Name emitted invalid status output" }
    Assert-Equal $emitted[0] $Expected $Name
}
Check-Usage 'zero percent is real usage' '[{"provider":"codex","usage":{"primary":{"usedPercent":0},"identity":{"email":"synthetic-private-identity"}}}]' 'live'
Check-Usage 'secondary usage' '[{"provider":"codex","usage":{"secondary":{"usedPercent":100}}}]' 'live'
Check-Usage 'extra window usage' '[{"provider":"codex","usage":{"extraRateWindows":[{"window":{"usedPercent":42}}]}}]' 'live'
Check-Usage 'error object rejected' '[{"provider":"codex","error":{"message":"synthetic-private-error"},"usage":{"primary":{"usedPercent":0}}}]' 'none'
Check-Usage 'wrong provider rejected' '[{"provider":"claude","usage":{"primary":{"usedPercent":0}}}]' 'none'
Check-Usage 'malformed JSON rejected' '[broken-json]' 'none'
Check-Usage 'missing windows rejected' '[{"provider":"codex","usage":{}}]' 'none'
Check-Usage 'missing percentage rejected' '[{"provider":"codex","usage":{"primary":{"resetsAt":"2099-01-01T00:00:00Z"}}}]' 'none'
Check-Usage 'negative percentage rejected' '[{"provider":"codex","usage":{"primary":{"usedPercent":-1}}}]' 'none'
Check-Usage 'over 100 rejected' '[{"provider":"codex","usage":{"primary":{"usedPercent":101}}}]' 'none'
Check-Usage 'native failure rejected' '[{"provider":"codex","usage":{"primary":{"usedPercent":0}}}]' 'none' 3
Check-Usage 'rate-limited provider error is stale' '[{"provider":"codex","error":{"message":"Rate limit exceeded (429)"}}]' 'stale' 1
Check-Usage 'rate-limited native error is stale' '' 'stale' 1 'Too many requests (429)'
Check-Usage 'auth error without usage is none' '[{"provider":"codex","error":{"kind":"auth","message":"Login required"}}]' 'none' 1

$fresh = [datetimeoffset]::UtcNow.AddMinutes(-1).ToString('o')
$stale = [datetimeoffset]::UtcNow.AddHours(-2).ToString('o')
$olderThanThirtyMinutes = [datetimeoffset]::UtcNow.AddMinutes(-31).ToString('o')
$future = [datetimeoffset]::UtcNow.AddHours(2).ToString('o')
$expired = [datetimeoffset]::UtcNow.AddHours(-2).ToString('o')
foreach ($case in @(
    @{ Name = 'fresh timestamps'; Updated = $fresh; Reset = $future; Expected = 'live' },
    @{ Name = 'old cache is stale'; Updated = $stale; Reset = $future; Expected = 'stale' },
    @{ Name = 'usage older than 30 minutes is stale'; Updated = $olderThanThirtyMinutes; Reset = $future; Expected = 'stale' },
    @{ Name = 'expired window is stale'; Updated = $fresh; Reset = $expired; Expected = 'stale' },
    @{ Name = 'malformed updatedAt is stale'; Updated = 'invalid'; Reset = $future; Expected = 'stale' },
    @{ Name = 'malformed resetsAt is stale'; Updated = $fresh; Reset = 'invalid'; Expected = 'stale' }
)) {
    $item = @{ provider = 'codex'; usage = @{ updatedAt = $case.Updated; primary = @{ usedPercent = 0; resetsAt = $case.Reset } } }
    $json = ConvertTo-Json -InputObject @($item) -Depth 8 -Compress
    Check-Usage $case.Name $json $case.Expected
}
Write-Host 'Installer helper mock tests passed.'
exit 0
