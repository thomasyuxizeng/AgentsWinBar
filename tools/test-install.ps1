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

$helperNames = @('Quote-Sh', 'Quote-Native', 'Test-Usage', 'Read-Package')
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

# A CRLF checkout must not hand bash carriage returns (bash rejects 'set -e\r' with exit 2).
$realWsl = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wsl'
}, $true)
if (-not $realWsl) { throw 'Installer Wsl helper was not found.' }
& {
    function Invoke-Native([string] $File, [string[]] $Arguments, [int] $Timeout) { $Arguments }
    . ([scriptblock]::Create($realWsl.Extent.Text))
    $passed = @(Wsl "set -e`r`necho ok`r`n" 'Ubuntu' -Root)
    Assert-Equal $passed[-1] "set -e`necho ok`n" 'Wsl strips carriage returns from bash scripts'
    Assert-Equal ($passed[0..($passed.Count - 2)] -join ' ') '-d Ubuntu -u root --exec bash -c' 'Wsl argument order'
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
$script:mockAuthCode = 0
$script:usageCommands = New-Object 'System.Collections.Generic.List[string]'
function Wsl([string] $Command, [string] $Name, [switch] $Root, [switch] $Interactive, [int] $Timeout) {
    Assert-Equal $Name '' 'default distro usage'
    Assert-Equal ([bool]$Root) $false 'default user usage'
    Assert-Equal ([bool]$Interactive) $true 'interactive bash usage'
    if ($Command -like '*claude auth status*') { return [pscustomobject]@{ Code = $script:mockAuthCode; Output = ''; Error = '' } }
    if ($Command -notmatch 'codexbar usage --provider (codex|claude) --format json --source cli$') { throw "Failed: unexpected usage command $Command" }
    Assert-Equal $Timeout 180 'usage timeout'
    $script:usageCommands.Add($Command)
    $script:mockWslResult
}
function Check-Usage([string] $Name, [string] $Json, [string] $Expected, [int] $ExitCode = 0, [string] $ErrorText = 'synthetic-private-diagnostic', [string] $Provider = 'codex') {
    $script:mockWslResult = [pscustomobject]@{ Code = $ExitCode; Output = $Json; Error = $ErrorText }
    # A status string must be the only emitted value across every PowerShell output stream.
    # This also catches accidental logging of raw JSON, identity, or diagnostics.
    $emitted = @(& { Test-Usage $Provider } *>&1)
    if ($emitted.Count -ne 1 -or $emitted[0] -isnot [string] -or $emitted[0] -cnotin @('live', 'stale', 'none', 'noise')) { throw "Failed: $Name emitted invalid status output" }
    Assert-Equal $emitted[0] $Expected $Name
}
Check-Usage 'sudo banner before JSON is noise' ("To run a command as administrator (user `"root`"), use `"sudo <command>`".`n" + '[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]') 'noise'
Check-Usage 'leading whitespace is not noise' ("`n  " + '[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]') 'live'
Check-Usage 'EXIT trap after JSON is noise' ('[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]' + "`nGoodbye!") 'noise'
Check-Usage 'trailing whitespace is not noise' ('[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]' + " `r`n`t") 'live'
Check-Usage 'trailing noise on a failing run is noise' ('[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]' + "`nGoodbye!") 'noise' 1
Check-Usage 'wsl.exe error on stdout is not noise' 'There is no distribution with the supplied name. Error code: Wsl/Service/WSL_E_DISTRO_NOT_FOUND' 'none' -1
Check-Usage 'banner before JSON on a failing run is noise' ("Welcome!`n" + '[{"provider":"codex","usage":{"primary":{"usedPercent":5}}}]') 'noise' 1

# Claude: the sign-in state comes from `claude auth status` first; probes clear the shim's cooldown markers.
$script:mockAuthCode = 4
$script:usageCommands.Clear()
Check-Usage 'claude signed out is none' '[{"provider":"claude","usage":{"primary":{"usedPercent":5}}}]' 'none' -Provider claude
Assert-Equal $script:usageCommands.Count 0 'signed-out claude skips the usage probe'
$script:mockAuthCode = 0
Check-Usage 'claude signed in and live' '[{"provider":"claude","usage":{"primary":{"usedPercent":31}}}]' 'live' -Provider claude
if ($script:usageCommands[0] -notlike 'rm -f /var/tmp/codexbar-claude-$(id -u)-oauth-cooldown /var/tmp/codexbar-claude-$(id -u)-cli-broken*') { throw 'Failed: claude probe clears the shim cooldown markers' }
Check-Usage 'claude expired token is none' '[{"provider":"claude","error":{"message":"Claude OAuth token expired. CodexBar CLI does not launch Claude to refresh credentials."}}]' 'none' 3 -Provider claude
$script:mockAuthCode = 3
Check-Usage 'claude CLI absent falls back to usage' '[{"provider":"claude","usage":{"primary":{"usedPercent":31}}}]' 'live' -Provider claude
$script:mockAuthCode = 0
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

# Read-Package takes the Windows App Runtime dependency from the package's own manifest.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-TestPackage([string] $RuntimeName) {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('cbw-test-' + [Guid]::NewGuid().ToString('N') + '.msix')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry('AppxManifest.xml').Open())
        try {
            $writer.Write(@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="CodexBarWin" Publisher="CN=CodexBarWin OSS" Version="1.2.3.0" ProcessorArchitecture="x64" />
  <Dependencies><PackageDependency Name="$RuntimeName" MinVersion="9000.1.2.0" Publisher="CN=Microsoft Corporation" /></Dependencies>
  <Applications><Application Id="App" /></Applications>
</Package>
"@)
        } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $path
}
$arch = 'x64'
$packagePath = New-TestPackage 'Microsoft.WindowsAppRuntime.1.9'
try {
    $package = Read-Package $packagePath
    Assert-Equal $package.RuntimeName 'Microsoft.WindowsAppRuntime.1.9' 'runtime name from manifest'
    Assert-Equal $package.RuntimeMinVersion ([version]'9000.1.2.0') 'runtime minimum from manifest'
    Assert-Equal $package.Version ([version]'1.2.3.0') 'package version'
    Assert-Equal $package.AppId 'App' 'application id'
} finally { Remove-Item -LiteralPath $packagePath -Force }
$packagePath = New-TestPackage 'Microsoft.WindowsAppRuntime.evil; rm'
$rejected = $false
try { [void](Read-Package $packagePath) } catch { $rejected = $true } finally { Remove-Item -LiteralPath $packagePath -Force }
if (-not $rejected) { throw 'Failed: unexpected runtime dependency name was accepted' }
# Evaluate isolated installer statements with mocks, never the installer body or elevation.
$adminAssignment = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$adminScript'
}, $true)
$adminText = $adminAssignment.Right.Expression.Value
$adminAst = [System.Management.Automation.Language.Parser]::ParseInput($adminText, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Embedded administrator helper has PowerShell syntax errors.' }
# The administrator helper is inspected only; it is never executed by these tests.
$testStage = Join-Path ([IO.Path]::GetTempPath()) ('cbw-json-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testStage | Out-Null
try {
    # BOM-less UTF-8 reproduces files written by PowerShell 7 and read by Windows PowerShell 5.1.
    $unicodePath = 'C:\Users\' + [char]0xD55C + [char]0xAE00 + '\runtime.exe'
    $statePath = Join-Path $testStage 'state.json'
    $json = @{ parameters = @{ MsixPath = $unicodePath } } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($statePath, $json, (New-Object Text.UTF8Encoding($false)))
    $jsonReads = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Get-Content' -and
        $node.Extent.Text -match '\$statePath\b'
    }, $true))
    Assert-Equal $jsonReads.Count 2 'both state readers covered'
    foreach ($reader in $jsonReads) {
        $read = & ([scriptblock]::Create($reader.Extent.Text)) | ConvertFrom-Json
        Assert-Equal $read.parameters.MsixPath $unicodePath 'BOM-less UTF-8 state path'
    }
} finally { Remove-Item -LiteralPath $testStage -Recurse -Force }

# Both original-user runtime invocations must propagate 3010 to the common reboot decision.
$runtimeChecks = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -match '^\$(attempt|registerRuntime)\.Code -eq 3010$'
}, $true))
Assert-Equal $runtimeChecks.Count 2 'both unelevated runtime reboot checks covered'
foreach ($check in $runtimeChecks) {
    foreach ($code in @(0, 3010)) {
        $attempt = $registerRuntime = [pscustomobject]@{ Code = $code }
        $runtimeReboot = $false
        . ([scriptblock]::Create($check.Extent.Text))
        Assert-Equal $runtimeReboot ($code -eq 3010) "original-user runtime reboot $code"
    }
}
$rebootDecision = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -like '$runtimeReboot -or *$enabledNeedsReboot*'
}, $true)
$runtimeReboot = $true; $adminResult = [pscustomobject]@{ Reboot = $false }
$enabledNeedsReboot = $false; $featurePending = $false; $wslRuns = $true
Assert-Equal (& ([scriptblock]::Create($rebootDecision.Clauses[0].Item1.Extent.Text))) $true 'original-user reboot reaches setup reboot decision'
$registration = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -like '$runtimeAdmin -and *Runtime-Installed*'
}, $true)
function Runtime-Installed { $false }
function Invoke-Native([string] $File, [string[]] $Arguments, [int] $Timeout, [switch] $NoElevation) {
    Assert-Equal ([bool]$NoElevation) $true 'runtime registration stays unelevated'
    $script:runtimeCalls++
    [pscustomobject]@{ Code = 3010 }
}
$runtimeAdmin = $true; $runtimeInstaller = 'mock-runtime.exe'
foreach ($reason in @('none', 'runtime', 'admin', 'wsl', 'features')) {
    $runtimeReboot = $reason -eq 'runtime'
    $adminResult = [pscustomobject]@{ Reboot = ($reason -eq 'admin') }
    $enabledNeedsReboot = $reason -eq 'wsl'
    $featurePending = $reason -eq 'features'; $wslRuns = $false
    $script:runtimeCalls = 0
    . ([scriptblock]::Create($registration.Extent.Text))
    Assert-Equal $script:runtimeCalls ([int]($reason -eq 'none')) "registration skips known reboot: $reason"
    Assert-Equal (& ([scriptblock]::Create($rebootDecision.Clauses[0].Item1.Extent.Text))) $true "runtime registration or existing reboot reaches setup: $reason"
}

# Exercise the actual pending filters and noise guards before and after the sign-in window.
$pendingAssignments = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$pending'
}, $true))
Assert-Equal $pendingAssignments.Count 2 'both provider readiness filters covered'
$Providers = @('codex', 'claude', 'gemini')
$usageStates = @{ codex = 'live'; claude = 'stale'; gemini = 'noise' }
foreach ($assignment in $pendingAssignments) {
    . ([scriptblock]::Create($assignment.Extent.Text))
    Assert-Equal ($pending -join ',') 'gemini' 'only live and stale are ready'
    $usageStates.gemini = 'none'
    . ([scriptblock]::Create($assignment.Extent.Text))
    Assert-Equal ($pending -join ',') 'gemini' 'missing usage stays pending'
    $usageStates.gemini = 'noise'
}
$noiseGuards = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -eq '$noisy.Count'
}, $true))
Assert-Equal $noiseGuards.Count 2 'noise checked again after sign-in'
function Fail([int] $Code, [string] $Message) { throw "setup-failure-${Code}: $Message" }
$noisy = @('gemini')
foreach ($guard in $noiseGuards) {
    $rejected = $false
    try { . ([scriptblock]::Create($guard.Extent.Text)) } catch { $rejected = $_.Exception.Message -like 'setup-failure-1: *gemini*' }
    Assert-Equal $rejected $true 'shell noise fails setup before success reporting'
}
Write-Host 'Installer helper mock tests passed.'
exit 0
