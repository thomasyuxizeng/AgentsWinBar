<#
.SYNOPSIS
Installs AgentsWinBar as the original Windows user, using native Windows providers by default.
.DESCRIPTION
Run with a 30 minute timeout: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
Use -WithWsl only for providers configured to use WSL. -Distro, -NoSignIn, and
-NoWindowsSignIns apply only to -WithWsl. -SkipWsl is accepted for compatibility.
Exit codes: 0 = setup complete; 3010 = reboot and rerun (only with -WithWsl);
20 = sign-in pending; 30 = elevation declined/admin failed; 40 = release inaccessible;
50 = Windows, hardware, or network precondition; 1 = other failure.
DryRun performs read-only probes and skips staging, installation, launch, sign-in, and usage checks.
No RunOnce entry is created. State remembers parameters, never credentials.
#>
[CmdletBinding()]
param(
    [string] $Repo,
    [string[]] $Providers,
    [string] $Distro,
    [string] $MsixPath,
    [switch] $AllowDeveloperPackage,
    [switch] $NoSignIn,
    [switch] $NoWindowsSignIns,
    [switch] $SkipApp,
    [switch] $WithWsl,
    [switch] $SkipWsl,
    [switch] $Resume,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$stage = Join-Path $env:LOCALAPPDATA 'AgentsWinBar-Setup'
$statePath = Join-Path $stage 'state.json'
$script:state = $null
$script:transcript = $false
$script:phases = [ordered]@{}
$script:human = New-Object 'System.Collections.Generic.List[string]'
$script:exitCode = 1
$script:outcome = 'failed'
$script:failureCode = 1
$script:ghRelease = $false
# Defaults for preflight; replaced by the downloaded package's own PackageDependency once it is read.
$runtimeName = 'Microsoft.WindowsAppRuntime.1.8'
$runtimeMinimum = [version]'8000.731.1532.0'
$script:rememberedState = $null
# SHA-1 thumbprint of the release signing certificate (CN=CodexBarWin OSS, valid to 2036). The installer trusts and
# installs only packages signed by this certificate; change it here, and in the release workflow's pin check, if the
# signing key is ever replaced.
$ExpectedReleaseCertificateThumbprint = '4E280F44AE28900533BBC497F28F62505990F3BD'

function Assert-ReleaseSigner([string] $Thumbprint, [string] $ExpectedThumbprint, [switch] $AllowDeveloperPackage) {
    $expected = $ExpectedThumbprint -replace '\s', ''
    if ($expected -and $expected -notmatch '^[a-fA-F0-9]{40}$') { throw 'Expected release certificate thumbprint must contain 40 hexadecimal characters.' }
    if ($AllowDeveloperPackage) {
        Write-Warning 'LOCAL DEVELOPER PACKAGE: release certificate pinning is explicitly bypassed.'
    } elseif (-not $expected) {
        throw 'This installer has no release certificate thumbprint pinned, so it cannot verify the MSIX signer. Nothing has been trusted.'
    } elseif ($Thumbprint -ine $expected) {
        throw 'MSIX signer does not match ExpectedReleaseCertificateThumbprint. Nothing has been trusted.'
    }
}

function New-AdminCommand($Plan) {
    # Both code and inputs cross UAC in the command line, never through a mutable script or plan file.
    $planBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Plan | ConvertTo-Json -Compress)))
    $adminScript = @'
$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
$reboot = $false
$protectedStage = $null
$runtimeCopy = $null
try {
    foreach ($name in @('Trust', 'EnableWsl', 'UpdateWsl', 'InstallRuntime', 'AllowDeveloperPackage')) {
        if ($plan.$name -isnot [bool]) { throw 'Invalid administrator plan.' }
    }
    if ($plan.Trust) {
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($plan.CertificateBase64))
        try {
            if ($plan.Thumbprint -notmatch '^[a-fA-F0-9]{40}$' -or $cert.Subject -cne 'CN=CodexBarWin OSS' -or $cert.Thumbprint -ine $plan.Thumbprint) { throw 'Certificate mismatch.' }
            if ($plan.ExpectedThumbprint -and -not $plan.AllowDeveloperPackage -and $cert.Thumbprint -ine ($plan.ExpectedThumbprint -replace '\s', '')) { throw 'Release certificate pin mismatch.' }
            $store = New-Object Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
            try { $store.Open('ReadWrite'); $store.Add($cert) } finally { $store.Dispose() }
        } finally { $cert.Dispose() }
    }
    if ($plan.EnableWsl -or $plan.UpdateWsl) {
        $env:WSL_UTF8 = '1'
        $wsl = Join-Path ([Environment]::GetFolderPath('System')) 'wsl.exe'
    }
    if ($plan.EnableWsl) {
        & $wsl --install --no-distribution
        if ($LASTEXITCODE -notin @(0, 3010)) { throw 'WSL enable failed.' }
        if ($LASTEXITCODE -eq 3010) { $reboot = $true }
    }
    if ($plan.UpdateWsl) {
        & $wsl --update
        if ($LASTEXITCODE -notin @(0, 3010)) { throw 'WSL update failed.' }
        if ($LASTEXITCODE -eq 3010) { $reboot = $true }
    }
    if ($plan.InstallRuntime) {
        if (-not [IO.Path]::IsPathRooted($plan.Runtime) -or [IO.Path]::GetExtension($plan.Runtime) -ine '.exe' -or $plan.RuntimeSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid runtime plan.' }
        # Create with an explicit ACL under Program Files before copying any user-writable input.
        $protectedRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath('ProgramFiles')).TrimEnd('\') + '\'
        $candidate = [IO.Path]::GetFullPath((Join-Path $protectedRoot ('AgentsWinBar-Setup-' + [Guid]::NewGuid().ToString('N'))))
        if (-not $candidate.StartsWith($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Directory]::Exists($candidate)) { throw 'Invalid protected staging path.' }
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $identity = New-Object Security.Principal.SecurityIdentifier($sid)
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        [void][IO.Directory]::CreateDirectory($candidate, $acl)
        $protectedStage = $candidate
        $runtimeCopy = Join-Path $protectedStage 'runtime.exe'
        Copy-Item -LiteralPath $plan.Runtime -Destination $runtimeCopy
        if ((Get-FileHash -LiteralPath $runtimeCopy -Algorithm SHA256).Hash -ine $plan.RuntimeSha256) { throw 'Runtime SHA256 mismatch.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $runtimeCopy
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') { throw 'Runtime signature mismatch.' }
        $process = Start-Process -FilePath $runtimeCopy -ArgumentList '--quiet' -WorkingDirectory $protectedStage -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -notin @(0, 3010)) { throw 'Runtime install failed.' }
        if ($process.ExitCode -eq 3010) { $reboot = $true }
    }
} catch { exit 30 } finally {
    # Delete only the known file and empty directory, never recurse through an installer-created tree.
    if ($runtimeCopy) { try { [IO.File]::Delete($runtimeCopy) } catch { } }
    if ($protectedStage) { try { [IO.Directory]::Delete($protectedStage) } catch { } }
}
if ($reboot) { exit 3010 } else { exit 0 }
'@
    $command = '$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $planBase64 + ''')) | ConvertFrom-Json' + "`n" + $adminScript
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
}

function Quote-Native([string] $Value) {
    # CommandLineToArgvW escaping, including trailing backslashes before the closing quote.
    # WSL's own parser requires simple option tokens to remain unquoted.
    if ($Value -and $Value -notmatch '[\s"]') { return $Value }
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Quote-Sh([string] $Value) {
    $apostrophe = [string][char]39
    $replacement = $apostrophe + [char]34 + $apostrophe + [char]34 + $apostrophe
    $apostrophe + $Value.Replace($apostrophe, $replacement) + $apostrophe
}
function Invoke-Native([string] $File, [string[]] $Arguments, [int] $Timeout = 120, [switch] $NoElevation) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $resolved = Get-Command $File -ErrorAction SilentlyContinue
    if ($resolved -and $resolved.Source) { $File = $resolved.Source }
    $psi.FileName = $File
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-Native $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    if ($NoElevation) { $psi.EnvironmentVariables['__COMPAT_LAYER'] = 'RunAsInvoker' }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $psi
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout * 1000)) {
            try { $process.Kill() } catch { }
            return [pscustomobject]@{ Code = -1; Output = ''; Error = 'Command timed out.' }
        }
        [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.Result.Replace("`0", '').Trim(); Error = $stderr.Result.Replace("`0", '').Trim() }
    } catch {
        [pscustomobject]@{ Code = -1; Output = ''; Error = 'Command could not be started.' }
    } finally { $process.Dispose() }
}
function Wsl([string] $Command, [string] $Name, [switch] $Root, [switch] $Interactive, [int] $Timeout = 120) {
    $arguments = @()
    if ($Name) { $arguments += @('-d', $Name) }
    if ($Root) { $arguments += @('-u', 'root') }
    $arguments += @('--exec', 'bash')
    if ($Interactive) { $arguments += '-ic' } else { $arguments += '-c' }
    # A CRLF checkout (git autocrlf) puts CR in every here-string; bash rejects 'set -e\r' with exit 2.
    $arguments += $Command.Replace("`r", '')
    Invoke-Native 'wsl.exe' $arguments $Timeout
}
function Require-Native($Result, [string] $Message) {
    if ($Result.Code -ne 0) { throw "$Message (exit $($Result.Code))." }
}
function Phase([string] $Name, [string] $Message, [bool] $Good = $true) {
    $script:phases[$Name] = [pscustomobject]@{ Good = $Good; Message = $Message }
    Write-Host "$Name`: $Message"
    if ($script:state -and -not $DryRun) {
        if ($Good -and $script:state.completedPhases -notcontains $Name) { $script:state.completedPhases += $Name }
        Save-State
    }
}
function Save-State {
    $temporary = $statePath + '.new'
    $script:state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}
function Write-Utf8([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText($Path, $Content.Replace("`r", ''), (New-Object Text.UTF8Encoding($false)))
}
function Fail([int] $Code, [string] $Message) { $script:failureCode = $Code; throw $Message }
function Finish([int] $Code, [string] $Outcome) { $script:exitCode = $Code; $script:outcome = $Outcome }
function Runtime-Installed {
    $packages = @(Get-AppxPackage -Name $runtimeName -ErrorAction SilentlyContinue)
    @($packages | Where-Object { ([string]$_.Architecture -ieq $arch) -and ([version]$_.Version -ge $runtimeMinimum) }).Count -gt 0
}
function Get-Distros {
    # Registry names/default GUID avoid localized and UTF-16 wsl --list output.
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (-not (Test-Path $key)) { return @() }
    $default = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).DefaultDistribution
    @(Get-ChildItem -LiteralPath $key | ForEach-Object {
        $entry = Get-ItemProperty -LiteralPath $_.PSPath
        if ($entry.DistributionName) {
            [pscustomobject]@{ Name = $entry.DistributionName; Default = ($_.PSChildName -eq $default); Uid = $entry.DefaultUid }
        }
    })
}
function Usable-Distro([string] $Name) {
    $probe = Wsl '. /etc/os-release; case " $ID $ID_LIKE " in *ubuntu*|*debian*) exit 0;; *) exit 1;; esac' $Name
    $probe.Code -eq 0
}
function Linux-Tool([string] $Tool, [string] $Name) {
    $command = 'p=$(command -v ' + $Tool + ') || exit 1; p=$(readlink -f "$p") || exit 1; case "$p" in /mnt/*|*.exe|*.cmd|*.bat) exit 1;; esac; test -x "$p"'
    (Wsl $command $Name -Interactive).Code -eq 0
}
function Test-Usage([string] $Provider) {
    # Match the app: default distribution, default user, interactive bash, source cli.
    # Never emit raw JSON: it can contain account identity and diagnostic secrets.
    # Returns 'live', 'stale' (old or rate-limited data, not a sign-in problem), 'none' (no usage: sign-in or
    # provider problem) or 'noise' (text before the JSON, which the app's parser rejects).
    $command = "codexbar usage --provider $Provider --format json --source cli"
    if ($Provider -eq 'claude') {
        # Cooldown markers left by an earlier probe would turn this one into the shim's synthetic
        # "rate limited" answer and hide a missing sign-in.
        $command = 'rm -f /var/tmp/codexbar-claude-$(id -u)-oauth-cooldown /var/tmp/codexbar-claude-$(id -u)-cli-broken 2>/dev/null; ' + $command
        # Exit 4 = Claude Code is installed and reports no sign-in. Only the loggedIn flag is read, never printed.
        $auth = Wsl 'command -v claude >/dev/null || exit 3; claude auth status --json 2>/dev/null | python3 -c "import json,sys; sys.exit(0 if json.load(sys.stdin).get(\"loggedIn\") else 4)"' -Interactive -Timeout 60
        if ($auth.Code -eq 4) { return 'none' }
    }
    $result = Wsl $command -Interactive -Timeout 180
    # The app deserializes stdout from its first character, so a .bashrc echo or Ubuntu's sudo banner in
    # front of valid JSON or output from an EXIT trap after it still leaves the app with no data.
    # wsl.exe prints its own errors (e.g. WSL_E_DISTRO_NOT_FOUND) on stdout with a failing exit code; those are
    # not shell noise, so only a successful run or JSON that starts later in the output counts.
    $trimmed = $result.Output.TrimStart()
    if ($trimmed -and -not ($trimmed.StartsWith('[') -or $trimmed.StartsWith('{')) -and
        ($result.Code -eq 0 -or $trimmed.IndexOf('[') -gt 0)) { return 'noise' }
    $status = 'none'
    $rateLimitPattern = '(?i)rate[\s_-]*limit|too many requests|\b429\b'
    $authPattern = '(?i)"kind"\s*:\s*"(?:auth|authentication)"|\b(?:401|403|unauthorized|forbidden)\b|(?:log[\s_-]*in|sign[\s_-]*in|authentication)\s+(?:is\s+)?required|(?:expired|invalid)\s+(?:credentials?|tokens?)|(?:credentials?|tokens?)\s+(?:have\s+|has\s+|is\s+)?(?:expired|invalid)'
    if ($result.Error -match $rateLimitPattern) { $status = 'stale' }
    $nativeAuthFailure = $result.Error -match $authPattern
    try {
        $start = $result.Output.IndexOf('[')
        $end = $result.Output.LastIndexOf(']')
        if ($start -lt 0 -or $end -lt $start) {
            if ($result.Output -match $rateLimitPattern) { return 'stale' }
            return $status
        }
        if ($result.Output.Substring($end + 1).Trim()) { return 'noise' }
        $items = @($result.Output.Substring($start, $end - $start + 1) | ConvertFrom-Json)
        foreach ($item in $items) {
            if ($item.provider -ne $Provider) { continue }
            $errorText = ''
            if ($item.error) { $errorText = $item.error | ConvertTo-Json -Depth 8 -Compress }
            if ($errorText -match $rateLimitPattern) { $status = 'stale'; continue }
            # Rate limits take precedence; actual auth failures cannot reuse cached usage.
            if ($nativeAuthFailure -or $errorText -match $authPattern) { continue }
            if (-not $item.usage) { continue }
            $blocked = $result.Code -ne 0 -or [bool]$item.error
            $fresh = $result.Error -notmatch $rateLimitPattern
            if ($item.usage.updatedAt) {
                try { $fresh = $fresh -and [datetimeoffset]$item.usage.updatedAt -ge [datetimeoffset]::UtcNow.AddMinutes(-30) } catch { $fresh = $false }
            }
            $windows = @($item.usage.primary, $item.usage.secondary, $item.usage.tertiary)
            foreach ($extra in @($item.usage.extraRateWindows)) { if ($extra) { $windows += $extra.window } }
            foreach ($window in $windows) {
                if ($window -and $null -ne $window.usedPercent) {
                    $windowFresh = $fresh
                    if ($window.resetsAt) {
                        try { $windowFresh = $windowFresh -and [datetimeoffset]$window.resetsAt -ge [datetimeoffset]::UtcNow.AddMinutes(-5) } catch { $windowFresh = $false }
                    }
                    $percent = 0.0
                    if ([double]::TryParse([string]$window.usedPercent, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$percent) -and $percent -ge 0 -and $percent -le 100) {
                        if ($windowFresh) {
                            # Fresh fields alongside an auth/provider error do not prove usage works.
                            if (-not $blocked) { return 'live' }
                            continue
                        }
                        $status = 'stale'
                    }
                }
            }
        }
    } catch { }
    return $status
}
function Windows-CodexAvailable {
    $auth = Join-Path $env:USERPROFILE '.codex\auth.json'
    if (-not (Test-Path -LiteralPath $auth)) { return $false }
    try {
        # Only auth_mode is used; never display, serialize, or copy this object.
        $mode = (Get-Content -Raw -LiteralPath $auth | ConvertFrom-Json).auth_mode
        return $mode -eq 'chatgpt'
    } catch { return $false }
}
function Test-WindowsSignIn([string] $Provider) {
    switch ($Provider) {
        'claude' { $path = Join-Path $env:USERPROFILE '.claude\.credentials.json' }
        'codex' { $path = Join-Path $env:USERPROFILE '.codex\auth.json' }
        'gemini' {
            $target = Invoke-Native 'cmdkey.exe' @('/list:gemini:antigravity') 10
            if ($target.Code -eq 0 -and $target.Output -match '(?m)^[ \t]*(?:[^:\r\n]+:[ \t]*LegacyGeneric:target=|Target:[ \t]*)gemini:antigravity[ \t]*\r?$') { return 'present' }
            return 'missing'
        }
        default { return 'missing' }
    }
    if ((Test-Path -LiteralPath $path -PathType Leaf -ErrorAction SilentlyContinue) -and
        (Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue).Length -gt 0) { return 'present' }
    return 'missing'
}
function Get-AppUsageStatus([string] $Path, [string] $Provider, [datetimeoffset] $LaunchTime) {
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'none' }
        $cache = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json
        $entry = $cache.items.$Provider
        if ($entry.provider -ne $Provider -or -not $entry.fetchedAt -or -not $cache.timestamp) { return 'none' }
        $fetched = [datetimeoffset]$entry.fetchedAt
        if ($fetched -lt $LaunchTime -or [datetimeoffset]$cache.timestamp -lt $LaunchTime) { return 'none' }

        if ($entry.error -match 'rate[\s_-]*limit|too many requests|\b429\b') { return 'stale' }
        if ($entry.error -or $entry.authRequired -or $entry.commandNotFound -or $entry.isLoading -or
            $entry.status -eq 'offline' -or $entry.usageFetchOutcome -in @(2, 3, 'Unavailable', 'Failed')) { return 'none' }

        $now = [datetimeoffset]::UtcNow
        $fresh = -not $entry.usageIsStale -and $fetched -ge $now.AddMinutes(-30)
        if ($entry.dataUpdatedAt) {
            try { $fresh = $fresh -and [datetimeoffset]$entry.dataUpdatedAt -ge $now.AddMinutes(-30) } catch { $fresh = $false }
        }
        $windows = @($entry.session, $entry.weekly, $entry.tertiary)
        foreach ($extra in $entry.extraWindows) { $windows += $extra.window }
        $status = 'none'
        foreach ($window in $windows) {
            if ($null -eq $window) { continue }
            $value = $window.reportedPercent
            if ($null -eq $value -and $window.limit -gt 0) { $value = 100.0 * $window.used / $window.limit }
            $percent = 0.0
            if (-not ([double]::TryParse([string]$value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$percent) -and $percent -ge 0 -and $percent -le 100)) { continue }
            try {
                if ($fresh -and (-not $window.resetAt -or [datetimeoffset]$window.resetAt -ge $now.AddMinutes(-5))) { return 'live' }
            } catch { }
            $status = 'stale'
        }
        return $status
    } catch {
        # A missing, unreadable, or malformed cache is retried without exposing its contents.
        return 'none'
    }
}
# The data folder of the app's old name (CodexBarWin) that AgentsWinBar imports on its first launch, in the app's
# order: the most recently written old packaged LocalCache with settings.json, then the old unpackaged folder.
# Only file presence and write times are read, never contents. Returns $null when there is nothing to import.
function Find-LegacyAppData([string] $LocalAppData) {
    $packaged = @(Get-ChildItem -LiteralPath (Join-Path $LocalAppData 'Packages') -Directory -Filter 'CodexBarWin_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\CodexBarWin' } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'settings.json') -PathType Leaf } |
        Sort-Object -Descending { (Get-ChildItem -LiteralPath $_ -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc })
    if ($packaged.Count) { return $packaged[0] }
    $unpackaged = Join-Path $LocalAppData 'CodexBarWin'
    if (Test-Path -LiteralPath (Join-Path $unpackaged 'settings.json') -PathType Leaf) { return $unpackaged }
    return $null
}
# What the human is told when the old CodexBarWin package is still installed. Setup never removes it.
function Get-LegacyAppNotice([bool] $HasData) {
    $notice = 'The old CodexBarWin app is still installed. '
    if ($HasData) { $notice += 'AgentsWinBar copies its settings, history and costs on its first launch (the old files stay in place). Once AgentsWinBar shows your data, ' }
    else { $notice += 'When you no longer need it, ' }
    return $notice + 'remove the old app in PowerShell with: Get-AppxPackage -Name CodexBarWin | Remove-AppxPackage'
}
function Resolve-Release {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        if ($DryRun -or (Invoke-Native 'gh' @('auth', 'status')).Code -eq 0) {
            $release = Invoke-Native 'gh' @('release', 'view', '--repo', $Repo, '--json', 'tagName,assets')
            if ($release.Code -eq 0) {
                $script:ghRelease = $true
                return ($release.Output | ConvertFrom-Json)
            }
        }
    }
    try {
        $public = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing -Headers @{ 'User-Agent' = 'AgentsWinBar-Setup'; Accept = 'application/vnd.github+json' } -TimeoutSec 30
        return [pscustomobject]@{ tagName = $public.tag_name; assets = $public.assets }
    } catch {
        Fail 40 "Cannot access the latest release for $Repo. Check the network connection and the repository name (-Repo owner/name), then rerun."
    }
}
function Download-Asset([string] $Name, [string] $Directory) {
    $asset = @($release.assets | Where-Object { $_.name -ceq $Name })
    if ($asset.Count -ne 1) { Fail 40 "The release does not contain $Name." }
    $target = Join-Path $Directory $Name
    if ($script:ghRelease) {
        $download = Invoke-Native 'gh' @('release', 'download', $release.tagName, '--repo', $Repo, '--pattern', $Name, '--dir', $Directory, '--clobber') 600
        if ($download.Code -ne 0) { Fail 40 "Could not download $Name using authenticated GitHub access." }
    } else {
        try { Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile $target -UseBasicParsing -TimeoutSec 600 } catch { Fail 40 "Could not download $Name." }
    }
    $target
}
function Read-Package([string] $Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw 'MSIX has no AppxManifest.xml.' }
        # The manifest is read before the signature is checked: refuse DTDs and external entities.
        $xmlSettings = New-Object Xml.XmlReaderSettings
        $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlSettings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($entry.Open(), $xmlSettings)
        try { $manifest = New-Object Xml.XmlDocument; $manifest.XmlResolver = $null; $manifest.Load($reader) } finally { $reader.Dispose() }
        $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        $application = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
        if ($identity.Name -ne 'AgentsWinBar' -or $identity.Publisher -ne 'CN=CodexBarWin OSS') { throw 'MSIX identity or publisher is not AgentsWinBar.' }
        if ($identity.ProcessorArchitecture -and $identity.ProcessorArchitecture -ne 'neutral' -and $identity.ProcessorArchitecture -ine $arch) { throw 'MSIX architecture differs from the OS architecture.' }
        $runtime = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency'][starts-with(@Name, 'Microsoft.WindowsAppRuntime.')]")
        $runtimeVersion = $null
        if ($runtime) {
            if ([string]$runtime.Name -notmatch '^Microsoft\.WindowsAppRuntime\.\d+\.\d+$') { throw 'MSIX names an unexpected Windows App Runtime package.' }
            $runtimeVersion = [version]$runtime.MinVersion
        }
        [pscustomobject]@{ Version = [version]$identity.Version; AppId = [string]$application.Id
            RuntimeName = $(if ($runtime) { [string]$runtime.Name } else { '' }); RuntimeMinVersion = $runtimeVersion }
    } finally { $zip.Dispose() }
}

try {
    # Reload only explicitly unbound parameters; passing a new flag overrides remembered values.
    if (($Resume -or $PSScriptRoot -eq $stage) -and (Test-Path -LiteralPath $statePath)) {
        $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath $statePath | ConvertFrom-Json
        $script:rememberedState = $saved
        $reloadNames = @('Repo', 'Providers', 'Distro', 'MsixPath', 'NoWindowsSignIns')
        if ($Resume) { $reloadNames += @('NoSignIn', 'SkipApp') }
        foreach ($name in $reloadNames) {
            if (-not $PSBoundParameters.ContainsKey($name)) {
                $value = $saved.parameters.$name
                if ($null -ne $value) { Set-Variable -Name $name -Value $value }
            }
        }
    }
    # WSL requires an explicit opt-in on each invocation, including staged reruns and -Resume.
    # Ordinary reruns must also recognize this installer's interrupted provisioning.
    # Parameter reload still follows the explicit Resume/staged-script rules above.
    if (-not $script:rememberedState -and (Test-Path -LiteralPath $statePath)) {
        $script:rememberedState = Get-Content -Raw -Encoding UTF8 -LiteralPath $statePath | ConvertFrom-Json
    }
    if (-not $Repo) {
        # Only a checkout whose root holds this script names the repository; git would otherwise walk up into any
        # unrelated repository the script happens to be saved in.
        if ($PSScriptRoot -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
            $remote = Invoke-Native 'git' @('-C', $PSScriptRoot, 'remote', 'get-url', 'origin')
            if ($remote.Code -eq 0 -and $remote.Output -match 'github\.com[:/]([^/]+/[^/]+?)(?:\.git)?$') { $Repo = $Matches[1] }
        }
        if (-not $Repo) { $Repo = 'thomasyuxizeng/AgentsWinBar' }
    }
    if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repo must be a GitHub owner/repository name.' }
    if (-not $Providers) {
        $Providers = @()
        if ((Test-Path (Join-Path $env:USERPROFILE '.claude')) -or (Get-Command claude -ErrorAction SilentlyContinue)) { $Providers += 'claude' }
        if ((Test-Path (Join-Path $env:USERPROFILE '.codex')) -or (Get-Command codex -ErrorAction SilentlyContinue)) { $Providers += 'codex' }
        $antigravity = @((Join-Path $env:LOCALAPPDATA 'Programs\Antigravity'), (Join-Path $env:APPDATA 'Antigravity'), (Join-Path $env:USERPROFILE '.antigravity'))
        if ((Get-Command agy -ErrorAction SilentlyContinue) -or @($antigravity | Where-Object { Test-Path $_ }).Count) { $Providers += 'gemini' }
        if (-not $Providers) { $Providers = @('claude', 'codex') }
    }
    $Providers = @($Providers | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Select-Object -Unique)
    foreach ($provider in $Providers) { if (@('claude', 'codex', 'gemini') -notcontains $provider) { throw "Invalid provider: $provider. Allowed: claude, codex, gemini." } }
    if ($WithWsl -and $Distro -and ($Distro -match '[\x00-\x1f]' -or $Distro.StartsWith('-'))) { throw 'Invalid distro name.' }
    if ($MsixPath) { $MsixPath = (Resolve-Path -LiteralPath $MsixPath).Path }
    if ($AllowDeveloperPackage -and -not $MsixPath) { throw 'AllowDeveloperPackage requires an explicit local MsixPath.' }
    $nativeArch = ''
    try { $nativeArch = [string][Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { }
    if (-not $nativeArch) { $nativeArch = $env:PROCESSOR_ARCHITEW6432 }
    if (-not $nativeArch) { $nativeArch = $env:PROCESSOR_ARCHITECTURE }
    if (-not $nativeArch) { Fail 50 'Unable to determine the native Windows OS architecture.' }
    switch ($nativeArch.ToUpperInvariant()) { 'AMD64' { $arch = 'x64' }; 'X64' { $arch = 'x64' }; 'ARM64' { $arch = 'ARM64' }; default { Fail 50 'AgentsWinBar requires an x64 or ARM64 Windows OS.' } }
    $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    if ($build -lt 22000) { Fail 50 'Install Windows 11 (build 22000 or newer), then rerun.' }
    $installed = @(Get-AppxPackage -Name 'AgentsWinBar' -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1)
    $installedVersion = 'no current-user package visible'
    if ($installed.Count) { $installedVersion = [string]$installed[0].Version }
    # The app's old name: its data is imported by AgentsWinBar's first launch; the old package is never removed here.
    $legacyInstalled = @(Get-AppxPackage -Name 'CodexBarWin' -ErrorAction SilentlyContinue).Count -gt 0
    $legacyData = Find-LegacyAppData $env:LOCALAPPDATA
    $runtimePresent = Runtime-Installed
    $windowsDescription = "Windows build $build; OS $arch; app $installedVersion; runtime $runtimeName "
    $enableWsl = $false
    if ($WithWsl) {
        $env:WSL_UTF8 = '1'
        $distros = @(Get-Distros)
        $preflightDistroNames = @($distros | ForEach-Object { $_.Name })
        $default = @($distros | Where-Object { $_.Default } | Select-Object -First 1)
        $defaultName = ''
        if ($default.Count) { $defaultName = $default[0].Name }
        $wslVersion = Invoke-Native 'wsl.exe' @('--version')
        $wslModern = $false
        if ($wslVersion.Code -eq 0 -and $wslVersion.Output -match '(\d+\.\d+\.\d+)') { $wslModern = [version]$Matches[1] -ge [version]'2.4.0' }
        $wslStatus = Invoke-Native 'wsl.exe' @('--status')
        $wslProbeBlocked = ($wslStatus.Output + $wslStatus.Error + $wslVersion.Output + $wslVersion.Error) -match 'E_ACCESSDENIED|Access is denied|access denied'
        $featureErrors = @()
        $features = @(Get-CimInstance Win32_OptionalFeature -ErrorAction SilentlyContinue -ErrorVariable +featureErrors | Where-Object { $_.Name -in @('Microsoft-Windows-Subsystem-Linux', 'VirtualMachinePlatform') })
        # Win32_OptionalFeature.InstallState: 1 Enabled, 2 Disabled, 3 Absent (payload removed), 4 Unknown.
        # A feature enabled but not yet active shows as 1 while Windows has a reboot pending.
        $rebootPending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
        $featurePending = $rebootPending -and $wslStatus.Code -ne 0 -and @($features | Where-Object { $_.InstallState -eq 1 }).Count -gt 0
        # A rerun before the reboot that WSL's enablement needs goes straight to the reboot verdict, without a second UAC.
        $enableWsl = -not $featurePending -and (($wslStatus.Code -ne 0 -and -not $distros.Count) -or @($features | Where-Object { $_.InstallState -in @(2, 3) }).Count -gt 0)
        $wslRuns = $false
        $defaultRuns = $false
        if ($defaultName) { $defaultRuns = (Invoke-Native 'wsl.exe' @('-d', $defaultName, '--exec', 'true')).Code -eq 0; $wslRuns = $defaultRuns }
        if (-not $wslRuns) { foreach ($entry in $distros) { if ((Invoke-Native 'wsl.exe' @('-d', $entry.Name, '--exec', 'true')).Code -eq 0) { $wslRuns = $true; break } } }
        if (-not $wslRuns) {
            $processors = @(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue)
            $hypervisor = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).HypervisorPresent
            if (-not $processors.Count -and -not $hypervisor) { Write-Host 'Hardware virtualization could not be queried in this process; it remains unverified.' }
            if (-not $hypervisor -and $processors.Count -gt 0 -and @($processors | Where-Object { $_.VirtualizationFirmwareEnabled }).Count -eq 0) { Fail 50 'Enable hardware virtualization (Intel VT-x / AMD-V) in firmware, then rerun.' }
        }
        $wslDescription = "modern=$wslModern; features pending=$featurePending; default distro='$defaultName'; registered=$($distros.Name -join ', ')"
        if ($wslProbeBlocked) { $wslDescription = "modern=$wslModern; distro state unverified: WSL returned E_ACCESSDENIED; registered/default distro queries may be hidden by the agent sandbox" }
        $runtimeDescription = "matching current-user package visible=$runtimePresent"
        if ($wslProbeBlocked -and -not $runtimePresent) { $runtimeDescription += ' (absence is inconclusive in this restricted process)' }
        Phase 'Preflight' "${windowsDescription}$runtimeDescription; WSL $wslDescription; Windows reboot pending=$rebootPending."
        if ($wslProbeBlocked -and -not $DryRun) { Fail 50 'WSL access was denied in this process. Run the same command in ordinary Windows PowerShell outside the agent sandbox; no installation state has been changed.' }
        # Select/probe an existing usable candidate before any administrator/system action,
        # including when Docker is the default and Ubuntu is registered beside it.
        $preflightCandidate = ''
        if ($Distro -and $distros.Name -contains $Distro -and (Usable-Distro $Distro)) { $preflightCandidate = $Distro }
        if (-not $preflightCandidate -and $defaultName -and $defaultRuns -and (Usable-Distro $defaultName)) { $preflightCandidate = $defaultName }
        if (-not $preflightCandidate) {
            foreach ($entry in $distros) { if (Usable-Distro $entry.Name) { $preflightCandidate = $entry.Name; break } }
        }
        if ($preflightCandidate) {
            $net = Wsl 'if command -v curl >/dev/null; then curl -sSfI --connect-timeout 10 --max-time 20 https://github.com >/dev/null; elif command -v python3 >/dev/null; then python3 -c "import urllib.request; urllib.request.urlopen(\"https://github.com\", timeout=20)" >/dev/null; else exit 77; fi' $preflightCandidate
            if ($net.Code -eq 77) { Write-Host 'WSL network probe deferred until curl is installed; no HTTP client is present.' }
            elseif ($net.Code -ne 0) { if ($DryRun) { Phase 'WSL network' 'GitHub network probe failed; fix the Windows/WSL network, proxy, or VPN before setup.' $false; $script:failureCode = 50 } else { Fail 50 'WSL cannot reach https://github.com. Check the Windows/WSL network, proxy, and VPN, then rerun.' } }
        } elseif ($defaultName -and -not $wslRuns) {
            if (-not $DryRun -and -not $enableWsl -and -not $featurePending) { Fail 50 'The default WSL distro cannot run. Run wsl --status and wsl --exec true outside the agent sandbox, correct the reported WSL problem, then rerun.' }
            Write-Host 'WSL execution is unavailable; distro packages, CLI state, network, and sign-ins cannot be verified in this process.'
        }
    } else {
        Phase 'Preflight' "${windowsDescription}matching current-user package visible=$runtimePresent."
    }

    # Release access is established before staging or changing Windows/WSL state.
    $releaseAccessible = $true
    try { $release = Resolve-Release } catch {
        if (-not $DryRun) { throw }
        $releaseAccessible = $false
        $release = [pscustomobject]@{ tagName = 'v0.0.0'; assets = @() }
        Phase 'Release access' $_.Exception.Message $false
        $script:human.Add($_.Exception.Message)
    }
    if ($release.tagName -notmatch '^v\d+\.\d+\.\d+$') { Fail 40 'Latest release tag must have the form v1.2.3.' }
    $assetName = "AgentsWinBar-$($release.tagName)-$arch.msix"
    $checksumName = "AgentsWinBar-$($release.tagName)-checksums.sha256"
    if (-not @($release.assets | Where-Object { $_.name -ceq $checksumName }).Count) { $checksumName = 'AgentsWinBar-checksums.sha256' }
    if ($releaseAccessible -and -not $SkipApp -and -not $MsixPath -and -not @($release.assets | Where-Object { $_.name -ceq $assetName }).Count) { Fail 40 "Latest release lacks $assetName." }
    if ($releaseAccessible) { Phase 'Release access' "Latest release $($release.tagName) is accessible in $Repo." }

    if ($WithWsl) {
        $tools = Join-Path $PSScriptRoot 'tools\wsl'
        $pinText = Get-Content -Raw -LiteralPath (Join-Path $tools 'install-wsl-shim.ps1')
        if ($pinText -notmatch '\$CodexBarVersion\s*=\s*"(v\d+\.\d+\.\d+)"') { throw 'Cannot determine the pinned CodexBar version.' }
        $pin = $Matches[1]
        $shimText = (Get-Content -Raw -LiteralPath (Join-Path $tools 'codexbar-shim.sh')).Replace("`r", '')
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $shimHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($shimText)))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
        $chosen = $Distro
        if (-not $chosen) { $chosen = $preflightCandidate }
        if (-not $chosen) { $chosen = 'Ubuntu-24.04' }
        $cliReady = $false
        if ($wslRuns -and $distros.Name -contains $chosen) {
            $versionProbe = Wsl 'codexbar --version' $chosen -Interactive
            $hashProbe = Wsl 'sha256sum /opt/codexbar/codexbar-shim' $chosen
            $cliReady = $versionProbe.Code -eq 0 -and $versionProbe.Output -match ([regex]::Escape($pin.TrimStart('v')) + '(?!\d)') -and $hashProbe.Code -eq 0 -and $hashProbe.Output.StartsWith($shimHash)
        }
    }
    if ($DryRun) {
        if (-not $releaseAccessible) { $assetName = "AgentsWinBar-<latest-tag>-$arch.msix"; $checksumName = 'the versioned release checksums file (latest tag unverified)' }
        $trustPlan = 'unknown until the verified package is downloaded; use its actual signer, never the release .cer alone'
        $packagePath = $MsixPath
        if (-not $packagePath -and $releaseAccessible) { $candidate = Join-Path (Join-Path $stage 'downloads') $assetName; if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate } }
        if ($packagePath) {
            $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
            if ($signature.SignerCertificate) { $trustPlan = 'present=' + [string](Test-Path ('Cert:\LocalMachine\TrustedPeople\' + $signature.SignerCertificate.Thumbprint)) + ' (package will be checksum/signature verified before use)' }
        }
        Phase 'Download / signer' "Would download/verify SHA256 for $assetName using $checksumName (local MSIX overrides download). Signer trust: $trustPlan."
        if ($WithWsl) {
            $enablePlan = [string]$enableWsl; $updatePlan = [string](-not $wslModern -and -not $enableWsl)
            if ($wslProbeBlocked) { $enablePlan = 'unverified'; $updatePlan = 'unverified' }
            Phase 'Elevation' "At most one UAC: certificate if untrusted; WSL enable=$enablePlan; WSL update=$updatePlan; runtime install only if absent (visible=$runtimePresent), first try verified redistributable unelevated. WinGet is used for download because its install command can open another UAC."
            Phase 'Reboot' "Needed only if WSL enablement requires it; no auto-resume. Existing pending reboot=$rebootPending."
            $defaultPlan = $defaultName
            if ($wslProbeBlocked) { $defaultPlan = 'unverified'; $chosenPlan = 'unverified; prefer usable default, then existing Debian/Ubuntu, else Ubuntu-24.04' } else { $chosenPlan = $chosen }
            Phase 'Distro' "Would use '$chosenPlan' as the default (current '$defaultPlan'); preserve an existing default user, including root. Only a distro installed by setup or interrupted setup provisioning receives a locked-password user."
            $cliPlan = [string]$cliReady
            if (-not $wslRuns) { $cliPlan = 'unverified (WSL execution unavailable)' }
            Phase 'WSL tools' "Pinned CLI $pin and exact shim already match=$cliPlan. Otherwise install only missing apt packages, then staged tools/wsl installer."
            foreach ($provider in $Providers) {
                $tool = $provider; if ($provider -eq 'gemini') { $tool = 'agy' }
                $present = 'unverified (WSL execution unavailable)'
                if ($wslRuns -and $distros.Name -contains $chosen) { $present = [string](Linux-Tool $tool $chosen) }
                $bridge = $provider -eq 'codex' -and -not $NoWindowsSignIns -and (Windows-CodexAvailable)
                Phase "Provider $provider" "Linux CLI present=$present; Windows Codex read-only bridge eligible=$bridge (enabled only if current WSL usage fails)."
            }
        } else {
            Phase 'Elevation' "At most one UAC for the package signer and Windows App Runtime. If the runtime is absent (visible=$runtimePresent), first try the verified redistributable unelevated."
        }
        $releasePlan = $release.tagName
        if (-not $releaseAccessible) { $releasePlan = 'the latest release (tag unverified)' }
        $legacyPlan = 'no CodexBarWin data to import'
        if ($legacyData) { $legacyPlan = 'CodexBarWin data found; the app imports it on first launch instead of preseeded settings' }
        Phase 'App' "Would install/upgrade only if older than $releasePlan; current=$installedVersion; SkipApp=$SkipApp. Preseed only absent settings, then launch. Old app installed=$legacyInstalled; $legacyPlan."
        if ($legacyInstalled -and -not $SkipApp) { $script:human.Add((Get-LegacyAppNotice ([bool]$legacyData))) }
        if ($WithWsl) {
            Phase 'Sign-ins' "Not probed in DryRun because usage probes can write shim caches. Real run classifies live, stale, or no usage; only no usage opens one sign-in console unless NoSignIn=$NoSignIn. Stale/rate-limit results need no sign-in. NoWindowsSignIns=$NoWindowsSignIns."
        } else {
            Phase 'Sign-ins' "Would check Windows sign-in presence for $($Providers -join ', ')."
            if (-not $SkipApp) { Phase 'Usage' "Would wait up to 120 seconds for readings after app launch for $($Providers -join ', ')." }
        }
        $script:human.Add('DryRun changed nothing. Run the same command without -DryRun to perform verified installation and determine pending sign-ins.')
        if (-not $releaseAccessible) { Finish 40 'access' } elseif ($script:failureCode -eq 50) { Finish 50 'failed' } else { Finish 0 'ok' }
    } else {
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        $stagedScript = Join-Path $stage 'install.ps1'
        if ([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($stagedScript)) { Copy-Item -LiteralPath $PSCommandPath -Destination $stagedScript -Force }
        if ($WithWsl) {
            $stagedTools = Join-Path $stage 'tools\wsl'
            if ([IO.Path]::GetFullPath($tools) -ne [IO.Path]::GetFullPath($stagedTools)) {
                New-Item -ItemType Directory -Path $stagedTools -Force | Out-Null
                Get-ChildItem -LiteralPath $tools -File | Copy-Item -Destination $stagedTools -Force
            }
        }
        $parameters = [ordered]@{ Repo = $Repo; Providers = $Providers; Distro = $Distro; MsixPath = $MsixPath; NoSignIn = [bool]$NoSignIn; NoWindowsSignIns = [bool]$NoWindowsSignIns; SkipApp = [bool]$SkipApp; WithWsl = [bool]$WithWsl }
        $managedDistro = ''; $managedUser = ''
        if ($script:rememberedState) { $managedDistro = [string]$script:rememberedState.managedDistro; $managedUser = [string]$script:rememberedState.managedUser }
        $script:state = [pscustomobject]@{ parameters = $parameters; completedPhases = @('Preflight', 'Release access'); release = $release.tagName; managedDistro = $managedDistro; managedUser = $managedUser; localExpectedSha256 = '' }
        Save-State
        Start-Transcript -Path (Join-Path $stage ('setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) -Append | Out-Null
        $script:transcript = $true
        $downloads = Join-Path $stage 'downloads'
        New-Item -ItemType Directory -Path $downloads -Force | Out-Null
        $trustNeeded = $false
        $signerThumbprint = ''
        $certificateBase64 = ''
        $package = $null
        if (-not $SkipApp) {
            $checksums = Download-Asset $checksumName $downloads
            $expected = $null
            foreach ($line in Get-Content -LiteralPath $checksums) {
                if ($line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$' -and $Matches[2] -ceq $assetName) { $expected = $Matches[1]; break }
            }
            if (-not $expected -and -not $MsixPath) { throw "Checksums lack an exact entry for $assetName." }
            if ($MsixPath) {
                $msix = Join-Path $downloads 'local-package.msix'
                if ([IO.Path]::GetFullPath($MsixPath) -ne [IO.Path]::GetFullPath($msix)) { Copy-Item -LiteralPath $MsixPath -Destination $msix -Force }
                $script:state.parameters.MsixPath = $msix; Save-State
                $localExpected = ''
                if ((Split-Path $MsixPath -Leaf) -ceq $assetName -and $expected) { $localExpected = $expected }
                elseif ($script:rememberedState -and $script:rememberedState.parameters.MsixPath -eq $MsixPath) { $localExpected = [string]$script:rememberedState.localExpectedSha256 }
                if ($localExpected -and (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash -ine $localExpected) { throw 'Local release MSIX SHA256 mismatch.' }
                $script:state.localExpectedSha256 = $localExpected; Save-State
                Write-Host 'Using local MSIX; an arbitrary local build has no published release checksum. Its identity and signer are still checked.'
            } else {
                $msix = Join-Path $downloads $assetName
                if (-not (Test-Path -LiteralPath $msix) -or (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash -ine $expected) { $msix = Download-Asset $assetName $downloads }
                if ((Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash -ine $expected) { throw 'MSIX SHA256 mismatch. Nothing has been installed.' }
            }
            $package = Read-Package $msix
            if ($package.RuntimeName) {
                $runtimeName = $package.RuntimeName
                $runtimeMinimum = $package.RuntimeMinVersion
                $runtimePresent = Runtime-Installed
            }
            $signature = Get-AuthenticodeSignature -LiteralPath $msix
            $signer = $signature.SignerCertificate
            # Before the self-signed certificate is trusted, Windows reports UnknownError ("terminated in a root
            # certificate which is not trusted"). Accept that only when an untrusted root is the chain's sole problem;
            # tampering still fails here (HashMismatch) and against the release checksum above.
            $untrustedRootOnly = $false
            if ($signer -and $signature.Status -eq 'UnknownError') {
                $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
                $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                [void]$chain.Build($signer)
                $chainProblems = @($chain.ChainStatus | ForEach-Object { [string]$_.Status })
                $untrustedRootOnly = $chainProblems.Count -gt 0 -and @($chainProblems | Where-Object { $_ -ne 'UntrustedRoot' }).Count -eq 0
            }
            if (-not $signer -or $signer.Subject -cne 'CN=CodexBarWin OSS' -or -not ($signature.Status -in @('Valid', 'NotTrusted') -or $untrustedRootOnly)) { throw "MSIX signature is absent, damaged, or unverified (status $($signature.Status)), or has an unexpected subject." }
            $signerThumbprint = $signer.Thumbprint
            Assert-ReleaseSigner $signerThumbprint $ExpectedReleaseCertificateThumbprint -AllowDeveloperPackage:$AllowDeveloperPackage
            $certificateBase64 = [Convert]::ToBase64String($signer.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            $trustNeeded = -not (Test-Path ('Cert:\LocalMachine\TrustedPeople\' + $signerThumbprint))
            $cerName = "AgentsWinBar-$($release.tagName)-Certificate.cer"
            if (-not @($release.assets | Where-Object { $_.name -ceq $cerName }).Count) { $cerName = 'AgentsWinBar-Certificate.cer' }
            if (@($release.assets | Where-Object { $_.name -ceq $cerName }).Count) {
                $cer = Download-Asset $cerName $downloads
                $published = New-Object Security.Cryptography.X509Certificates.X509Certificate2($cer)
                if ($published.Thumbprint -ne $signerThumbprint) {
                    if ($ExpectedReleaseCertificateThumbprint -and -not $AllowDeveloperPackage) { throw 'Published certificate differs from the pinned MSIX signer.' }
                    Write-Warning 'Published certificate differs from the MSIX signer (known v0.9.9 ARM64 issue); using the MSIX signer.'
                }
            }
            Phase 'Download / signer' "Package verified; actual signer $signerThumbprint; machine trust needed=$trustNeeded."
        } else { Phase 'Download / signer' 'App skipped; release access verified, no MSIX or trust change.' }

        # winget can explicitly ShellExecute with 'runas' even for a silent user-scope
        # install. Download instead, then launch the verified runtime without ShellExecute.
        # This preserves the harder one-UAC requirement; a failed attempt joins the batch.
        $runtimeAdmin = $false
        $runtimeReboot = $false
        $runtimeInstaller = ''
        $runtimeSha256 = ''
        if (-not $SkipApp -and -not $runtimePresent) {
            if (Get-Command winget -ErrorAction SilentlyContinue) {
                $wingetDirectory = Join-Path $downloads 'runtime-winget'
                New-Item -ItemType Directory -Path $wingetDirectory -Force | Out-Null
                $attempt = Invoke-Native 'winget' @('download', '--id', $runtimeName, '-e', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--architecture', $arch.ToLowerInvariant(), '--download-directory', $wingetDirectory) 900
                if ($attempt.Code -eq 0) {
                    $runtimeCandidate = @(Get-ChildItem -LiteralPath $wingetDirectory -Filter '*.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
                    if ($runtimeCandidate.Count) { $runtimeInstaller = $runtimeCandidate[0].FullName }
                }
            }
            $runtimePresent = Runtime-Installed
            if (-not $runtimePresent) {
                if (-not $runtimeInstaller) {
                    $runtimeInstaller = Join-Path $downloads "windowsappruntimeinstall-$($arch.ToLowerInvariant()).exe"
                    Invoke-WebRequest -Uri "https://aka.ms/windowsappsdk/$($runtimeName -replace '^Microsoft\.WindowsAppRuntime\.', '')/latest/windowsappruntimeinstall-$($arch.ToLowerInvariant()).exe" -OutFile $runtimeInstaller -UseBasicParsing -TimeoutSec 600
                }
                $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
                if ($runtimeSignature.Status -ne 'Valid' -or -not $runtimeSignature.SignerCertificate -or $runtimeSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') { throw 'Windows App Runtime download is not validly Microsoft-signed.' }
                $runtimeSha256 = (Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256).Hash
                $attempt = Invoke-Native $runtimeInstaller @('--quiet') 900 -NoElevation
                if ($attempt.Code -eq 3010) { $runtimeReboot = $true }
                $runtimePresent = Runtime-Installed
                $runtimeAdmin = -not $runtimePresent
            }
        }
        # `wsl --install --no-distribution` already installs the current WSL; an update right after it can fail
        # until the reboot and would turn a reboot into an admin failure.
        $updateWsl = $WithWsl -and -not $wslModern -and -not $enableWsl
        if ($trustNeeded -or $enableWsl -or $updateWsl -or $runtimeAdmin) {
            $adminPlan = [ordered]@{ CertificateBase64 = $certificateBase64; Thumbprint = $signerThumbprint; ExpectedThumbprint = $ExpectedReleaseCertificateThumbprint; AllowDeveloperPackage = [bool]$AllowDeveloperPackage; Trust = [bool]$trustNeeded; EnableWsl = [bool]$enableWsl; UpdateWsl = [bool]$updateWsl; Runtime = $runtimeInstaller; RuntimeSha256 = $runtimeSha256; InstallRuntime = [bool]$runtimeAdmin }
            $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + (New-AdminCommand $adminPlan)
            if ($arguments.Length -gt 30000) { throw 'Administrator command exceeds the safe Windows command-line length.' }
            $adminActions = if ($WithWsl) { 'certificate, WSL, and runtime' } else { 'certificate and runtime' }
            Write-Host "Approve one UAC prompt for the batched $adminActions actions. Keep this command running; this can take several minutes."
            try {
                $powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
                $adminProcess = Start-Process -FilePath $powershell -Verb RunAs -ArgumentList $arguments -WorkingDirectory ([Environment]::GetFolderPath('System')) -WindowStyle Hidden -PassThru -Wait
            } catch { Fail 30 'UAC was declined or the administrator process could not start. Rerun and approve its single UAC prompt.' }
            $adminResult = [pscustomobject]@{ Reboot = ($adminProcess.ExitCode -eq 3010) }
            if ($adminProcess.ExitCode -notin @(0, 3010)) { Fail 30 "Administrator step failed (exit $($adminProcess.ExitCode)). Rerun setup after correcting this Windows issue." }
            Phase 'Elevation' 'Single batched elevation completed; subsequent operations remain in the original user account.'
        } else { $adminResult = [pscustomobject]@{ Reboot = $false }; Phase 'Elevation' 'No administrator actions needed; no UAC prompt.' }
        $enabledNeedsReboot = $false
        if ($WithWsl -and $enableWsl) {
            $afterStatus = Invoke-Native 'wsl.exe' @('--status')
            $afterRebootPending = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
            $enabledNeedsReboot = $afterStatus.Code -ne 0 -or $afterRebootPending
        }
        if ($runtimeAdmin -and -not ($WithWsl -and ($runtimeReboot -or $adminResult.Reboot -or $enabledNeedsReboot -or ($featurePending -and -not $wslRuns))) -and -not (Runtime-Installed)) {
            # Elevation may have used another account; register per-user runtime
            # packages again from the original process without permitting another UAC.
            $registerRuntime = Invoke-Native $runtimeInstaller @('--quiet') 900 -NoElevation
            if ($registerRuntime.Code -eq 3010) { $runtimeReboot = $true }
        }
        if ($WithWsl -and ($runtimeReboot -or $adminResult.Reboot -or $enabledNeedsReboot -or ($featurePending -and -not $wslRuns))) {
            Phase 'Reboot' 'Reboot, then run the same command again' $false
            $script:human.Add('Reboot, then run the same command again. Staged alternative: powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AgentsWinBar-Setup\install.ps1" -WithWsl')
            Finish 3010 'reboot'
        } else {
            if ($WithWsl) {
                Phase 'Reboot' 'No setup reboot required.'
                $distros = @(Get-Distros)
                $installDistro = $distros.Name -notcontains $chosen
                $resumeProvisioning = $script:state.managedDistro -eq $chosen
                $provisionUser = ($installDistro -and $preflightDistroNames -notcontains $chosen) -or $resumeProvisioning
                if ($installDistro -and $preflightDistroNames -contains $chosen) { throw "Previously registered distro '$chosen' disappeared during setup; rerun after restoring it." }
                if ($installDistro -and $chosen -ne 'Ubuntu-24.04') { throw "Requested distro '$chosen' is not registered. Only Ubuntu-24.04 is provisioned automatically." }
                if ($provisionUser) {
                    $username = [regex]::Replace(([string]$env:USERNAME).ToLowerInvariant(), '[^a-z0-9_-]', '')
                    if (-not $username -or $username -notmatch '^[a-z_]') { $username = 'cbw' + $username }
                    $username = $username.Substring(0, [Math]::Min(24, $username.Length))
                    if ($username -eq 'root') { $username = 'cbwuser' }
                    if ($script:state.managedDistro -eq $chosen -and $script:state.managedUser) { $username = $script:state.managedUser }
                    if ($username -notmatch '^[a-z_][a-z0-9_-]{0,31}$') { throw 'Remembered Linux user name is invalid.' }
                    # Never choose an existing system account as the interactive user.
                    $account = Wsl ('id -u ' + $username) $chosen -Root
                    $suffix = 0
                    $baseUser = $username
                    while ($account.Code -eq 0 -and [int]$account.Output -lt 1000) {
                        $suffix++; $username = 'cbw' + $baseUser + $suffix
                        $account = Wsl ('id -u ' + $username) $chosen -Root
                    }
                    $script:state.managedDistro = $chosen; $script:state.managedUser = $username; Save-State
                }
                if ($installDistro) {
                    Require-Native (Invoke-Native 'wsl.exe' @('--install', '-d', 'Ubuntu-24.04', '--no-launch') 1200) 'Distro installation failed'
                }
                if ($provisionUser) {
                    # Existing accounts retain their password; newly created accounts are locked.
                    # .hushlogin stops Ubuntu's "use sudo" banner, which bash -ic would print ahead of the app's JSON
                    # forever (a locked account never runs sudo, so the banner never retires itself).
                    $provision = 'set -e; if ! id ' + $username + ' >/dev/null 2>&1; then useradd -m -s /bin/bash -G sudo ' + $username + '; passwd -l ' + $username + ' >/dev/null; fi; h=$(getent passwd ' + $username + ' | cut -d: -f6); [ -e "$h/.hushlogin" ] || install -o ' + $username + ' -g "$(id -gn ' + $username + ')" -m 644 /dev/null "$h/.hushlogin"'
                    Require-Native (Wsl $provision $chosen -Root -Timeout 300) 'User provisioning failed'
                    $setUser = Invoke-Native 'wsl.exe' @('--manage', $chosen, '--set-default-user', $username)
                    if ($setUser.Code -ne 0) {
                        $conf = @'
set -e
p=/etc/wsl.conf; t=$(mktemp /etc/.wsl.conf.XXXXXX)
trap 'rm -f "$t"' EXIT
if [ -f "$p" ]; then
  awk 'BEGIN{user=0} /^\[/{user=($0 ~ /^\[user\][[:space:]]*$/)} !user{print}' "$p" > "$t"
fi
printf '\n[user]\ndefault=USERNAME\n' >> "$t"
chmod 644 "$t"; mv "$t" "$p"
'@
                        Require-Native (Wsl ($conf.Replace('USERNAME', $username)) $chosen -Root) 'Default-user configuration failed'
                        Require-Native (Invoke-Native 'wsl.exe' @('--terminate', $chosen)) 'Distro termination failed'
                    }
                    $who = Invoke-Native 'wsl.exe' @('-d', $chosen, '--exec', 'whoami')
                    if ($who.Code -ne 0 -or $who.Output.Trim() -ne $username) { throw 'Distro default-user verification failed.' }
                }
                if (-not (Usable-Distro $chosen)) { throw "'$chosen' must be a usable Ubuntu or Debian distro." }
                if ($provisionUser) {
                    $userProbe = Invoke-Native 'wsl.exe' @('-d', $chosen, '--exec', 'id', '-u')
                    if ($userProbe.Code -ne 0 -or $userProbe.Output.Trim() -eq '0') { throw "'$chosen' non-root default-user verification failed." }
                    $script:state.managedDistro = ''; $script:state.managedUser = ''; Save-State
                } else { Write-Host "Preserving '$chosen' existing default user, including root if configured; no user provisioning is needed." }
                if ($defaultName -ne $chosen) { Require-Native (Invoke-Native 'wsl.exe' @('--set-default', $chosen)) 'Setting default distro failed'; Write-Host "Changed WSL default distro from '$defaultName' to '$chosen' because the app uses the default distro." }
                Phase 'Distro' "Using '$chosen'; existing default user preserved unless setup provisioned this distro."
                $apt = @'
set -e
missing=()
for p in curl ca-certificates tar python3 coreutils; do
  dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q 'install ok installed' || missing+=("$p")
done
if [ ${#missing[@]} -gt 0 ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get -o DPkg::Lock::Timeout=300 update
  apt-get -o DPkg::Lock::Timeout=300 install -y "${missing[@]}"
fi
'@
                Require-Native (Wsl $apt $chosen -Root -Timeout 1200) 'WSL package installation failed'
                $network = Wsl 'curl -sSfI --connect-timeout 10 --max-time 20 https://github.com >/dev/null' $chosen
                if ($network.Code -ne 0) { Fail 50 'The selected WSL distro cannot reach GitHub. Correct its network/proxy/VPN and rerun.' }
                if (-not $cliReady) {
                    $install = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $stagedTools 'install-wsl-shim.ps1'), '-Distro', $chosen, '-CodexBarVersion', $pin) 1200
                    Require-Native $install 'CodexBar CLI/shim installation failed'
                }
                $installedCli = Wsl 'codexbar --version' -Interactive
                Require-Native $installedCli 'CodexBar is not on the app PATH'
                $installedShim = Wsl 'sha256sum /opt/codexbar/codexbar-shim'
                if ($installedCli.Output -notmatch ('(?<!\d)' + [regex]::Escape($pin.TrimStart('v')) + '(?!\d)') -or $installedShim.Code -ne 0 -or -not $installedShim.Output.StartsWith($shimHash)) { throw 'Installed CLI version or shim checksum does not match the staged installer.' }
                Phase 'WSL tools' "Pinned CLI $pin and shim installed (already matched=$cliReady)."

                $removeBridge = @'
set -e
p="$HOME/.config/codexbar/shim.env"
if [ -f "$p" ]; then
  t=$(mktemp "${p}.XXXXXX")
  trap 'rm -f "$t"' EXIT
  grep -v '^CODEXBAR_CODEX_HOME=' "$p" > "$t" || [ $? -eq 1 ]
  chmod 600 "$t"; mv "$t" "$p"
fi
'@
                if ($NoWindowsSignIns) {
                    Require-Native (Wsl $removeBridge) 'Removing Windows sign-in bridge failed'
                }
                $bridgeUsed = -not $NoWindowsSignIns -and (Wsl 'test -f "$HOME/.config/codexbar/shim.env" && grep -q ''^CODEXBAR_CODEX_HOME='' "$HOME/.config/codexbar/shim.env"').Code -eq 0
                $bridgeSafe = (Wsl 'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin; ! command -v codex >/dev/null 2>&1 && ! command -v codex.exe >/dev/null 2>&1').Code -eq 0
                if ($Providers -contains 'codex' -and $bridgeUsed -and -not $bridgeSafe) {
                    Require-Native (Wsl $removeBridge) 'Removing unsafe Windows sign-in bridge failed'
                    $bridgeUsed = $false
                    Write-Host 'Codex: the minimal system PATH contains a Codex CLI, so the read-only Windows bridge cannot be used safely. Using a separate Linux sign-in.'
                }
                # Ubuntu prints a "use sudo" banner in every interactive shell of a sudo-group user who never ran sudo,
                # ahead of the app's JSON. ~/.hushlogin silences it; root and users who have used sudo are unaffected.
                [void](Wsl '[ "$(id -u)" -ne 0 ] && [ ! -e "$HOME/.sudo_as_admin_successful" ] && [ ! -e "$HOME/.hushlogin" ] && id -nG | grep -qwE "sudo|admin" && touch "$HOME/.hushlogin"; true')
                foreach ($provider in $Providers) {
                    $working = Test-Usage $provider
                    if ($working -eq 'noise') {
                        Fail 1 "Text is printed outside codexbar's JSON in the default WSL user's interactive shell (bash -ic), so the app cannot read $provider usage. Remove echo/printf output from ~/.bashrc (or move it under a check for an interactive terminal), then rerun."
                    }
                    if ($working -ne 'none') {
                        Phase "Provider $provider" "Usage is $working; provider CLI installation and sign-in skipped, existing sign-in preserved."
                        continue
                    }
                    $linuxHome = $null
                    if ($provider -eq 'codex' -and -not $NoWindowsSignIns -and $bridgeSafe -and (Windows-CodexAvailable)) {
                        $linuxHome = Invoke-Native 'wsl.exe' @('--exec', 'wslpath', '-a', '-u', (Join-Path $env:USERPROFILE '.codex'))
                        # A custom automount root (not /mnt/<drive>) is left alone: the bridge is an optimisation.
                        if ($linuxHome.Code -ne 0 -or $linuxHome.Output -notmatch '^/mnt/[a-z]/[^\r\n]+$') {
                            Write-Host 'Codex: the Windows Codex folder is not under /mnt/<drive> in WSL; using a separate Linux sign-in instead of the read-only bridge.'
                            $linuxHome = $null
                        }
                    }
                    if ($linuxHome) {
                        $envValue = 'CODEXBAR_CODEX_HOME=' + $linuxHome.Output
                        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($envValue + "`n"))
                        $writeBridge = @'
set -e
umask 077
mkdir -p "$HOME/.config/codexbar"
p="$HOME/.config/codexbar/shim.env"; t=$(mktemp "${p}.XXXXXX")
trap 'rm -f "$t"' EXIT
if [ -f "$p" ]; then grep -v '^CODEXBAR_CODEX_HOME=' "$p" > "$t" || [ $? -eq 1 ]; fi
printf '%s' ENCODED | base64 -d >> "$t"
chmod 600 "$t"; mv "$t" "$p"
'@
                        Require-Native (Wsl $writeBridge.Replace('ENCODED', (Quote-Sh $encoded))) 'Configuring read-only Windows sign-in bridge failed'
                        $bridged = Test-Usage 'codex'
                        if ($bridged -eq 'live' -or $bridged -eq 'stale') {
                            $bridgeUsed = $true
                            Phase 'Provider codex' "Codex: using your Windows sign-in (read-only, usage $bridged); if usage goes stale, use Codex on Windows once or run install.ps1 -WithWsl -NoWindowsSignIns."
                            continue
                        }
                        # The Windows sign-in didn't produce usage (expired, or a different setup): undo the bridge and
                        # fall through to a Linux Codex install and sign-in.
                        Require-Native (Wsl $removeBridge) 'Removing the non-working Windows sign-in bridge failed'
                        Write-Host 'Codex: the Windows sign-in did not return usage through the bridge; using a separate Linux sign-in.'
                    }
                    if ($provider -eq 'codex' -and $bridgeUsed) {
                        Phase 'Provider codex' 'Read-only Windows sign-in bridge is active; a Linux Codex install and login are skipped.'
                        continue
                    }
                    $tool = $provider; if ($provider -eq 'gemini') { $tool = 'agy' }
                    if (-not (Linux-Tool $tool $chosen)) {
                        switch ($provider) {
                            'claude' { $url = 'https://claude.ai/install.sh'; $shell = 'bash' }
                            'codex' { $url = 'https://chatgpt.com/codex/install.sh'; $shell = 'sh' }
                            'gemini' { $url = 'https://antigravity.google/cli/install.sh'; $shell = 'bash' }
                        }
                        # Installers are downloaded before execution; failures cannot be hidden by a pipe.
                        $providerInstall = 'set -e; t=$(mktemp); trap ''rm -f "$t"'' EXIT; curl -fsSL --retry 3 --connect-timeout 15 --max-time 300 ' + (Quote-Sh $url) + ' -o "$t"; ' + $shell + ' "$t"; mkdir -p "$HOME/.local/bin"; if ! grep -q ''# AgentsWinBar setup PATH'' "$HOME/.bashrc"; then printf ''\n# AgentsWinBar setup PATH\nexport PATH="$HOME/.local/bin:$HOME/.antigravity/bin:$PATH"\n'' >> "$HOME/.bashrc"; fi'
                        Require-Native (Wsl $providerInstall $chosen -Timeout 900) "$provider Linux CLI installation failed"
                    }
                    if (-not (Linux-Tool $tool $chosen)) { throw "$provider did not resolve to a Linux CLI under bash -ic." }
                    Phase "Provider $provider" 'Linux CLI checked; usage unavailable, sign-in or provider recovery needed.'
                }
            }

            if (-not $SkipApp) {
                if ((Get-AuthenticodeSignature -LiteralPath $msix).Status -ne 'Valid') { throw 'Verified MSIX signature remains untrusted after the certificate phase.' }
                if (-not (Runtime-Installed)) { throw "$runtimeName remains unavailable to the original user after installation." }
                if (-not $installed.Count -or [version]$installed[0].Version -lt $package.Version) {
                    # The deployment service can fail to open packages in new folders directly under %LOCALAPPDATA%
                    # (0x80073CF0 / 0x80070003, seen where Codex's Windows sandbox has changed that folder's ACLs),
                    # while %TEMP% works. Install from a fresh %TEMP% copy and re-check its hash after copying.
                    $deployDirectory = Join-Path ([IO.Path]::GetTempPath()) ('AgentsWinBar-deploy-' + [Guid]::NewGuid().ToString('N'))
                    New-Item -ItemType Directory -Path $deployDirectory -Force | Out-Null
                    try {
                        $deployMsix = Join-Path $deployDirectory (Split-Path $msix -Leaf)
                        Copy-Item -LiteralPath $msix -Destination $deployMsix -Force
                        if ((Get-FileHash -LiteralPath $deployMsix -Algorithm SHA256).Hash -ine (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash) { throw 'MSIX copy for deployment does not match the verified package.' }
                        Add-AppxPackage -Path $deployMsix -ForceApplicationShutdown
                    } finally { Remove-Item -LiteralPath $deployDirectory -Recurse -Force -ErrorAction SilentlyContinue }
                }
                $installed = @(Get-AppxPackage -Name AgentsWinBar | Sort-Object Version -Descending | Select-Object -First 1)
                if (-not $installed.Count) { throw 'AgentsWinBar app registration could not be verified.' }
                # Full-trust packaged apps have LocalAppData redirected to LocalCache\Local.
                $settingsDirectory = Join-Path $env:LOCALAPPDATA ("Packages\$($installed[0].PackageFamilyName)\LocalCache\Local\AgentsWinBar")
                $settingsPath = Join-Path $settingsDirectory 'settings.json'
                $unpackagedSettings = Join-Path $env:LOCALAPPDATA 'AgentsWinBar\settings.json'
                $legacyData = Find-LegacyAppData $env:LOCALAPPDATA
                # Preseeded settings would stop the app's one-time import of CodexBarWin data, so they wait for none.
                if (-not (Test-Path -LiteralPath $settingsPath) -and -not (Test-Path -LiteralPath $unpackagedSettings) -and -not $legacyData) {
                    try {
                        New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
                        $settingsProviders = @(); $order = 0
                        foreach ($id in @('claude', 'codex', 'gemini')) { $display = $id.Substring(0, 1).ToUpperInvariant() + $id.Substring(1); $settingsProviders += @{ id = $id; displayName = $display; isEnabled = ($Providers -contains $id); order = $order }; $order++ }
                        $settingsJson = @{ version = 1; providers = $settingsProviders; startMinimized = $true } | ConvertTo-Json -Depth 6
                        # CreateNew prevents overwriting a file the app creates concurrently.
                        $stream = New-Object IO.FileStream($settingsPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $bytes = [Text.Encoding]::UTF8.GetBytes($settingsJson); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                    } catch { }
                }
                $importPending = [bool]$legacyData -and -not (Test-Path -LiteralPath $settingsPath)
                $appMessage = "Installed version $($installed[0].Version); existing settings preserved."
                if ($importPending) { $appMessage += ' CodexBarWin settings, history and costs are imported on first launch.' }
                Phase 'App' $appMessage
                if ($legacyInstalled) { $script:human.Add((Get-LegacyAppNotice $importPending)) }
            } else { Phase 'App' 'Skipped by -SkipApp.' }

            if ($WithWsl) {
                $usageStates = @{}
                foreach ($provider in $Providers) { $usageStates[$provider] = Test-Usage $provider }
                $noisy = @($Providers | Where-Object { $usageStates[$_] -eq 'noise' })
                if ($noisy.Count) { Fail 1 ("Text is printed outside codexbar's JSON for " + ($noisy -join ', ') + " in the default WSL user's interactive shell (bash -ic), so the app cannot read it. Remove echo/printf output from ~/.bashrc, then rerun.") }
                $pending = @($Providers | Where-Object { $usageStates[$_] -notin @('live', 'stale') })
                if ($pending.Count -gt 0 -and -not $NoSignIn) {
                    $loginLines = New-Object 'System.Collections.Generic.List[string]'
                    $loginLines.Add('set +e')
                    $loginLines.Add('umask 077; WORK=$(mktemp -d); trap ''rm -rf "$WORK"'' EXIT')
                    $loginLines.Add('printf ''#!/bin/sh\nexec /mnt/c/Windows/explorer.exe "$1"\n'' > "$WORK/browser"; chmod 700 "$WORK/browser"')
                    $loginLines.Add('if command -v wslview >/dev/null; then export BROWSER=wslview; else export BROWSER="$WORK/browser"; fi')
                    foreach ($provider in $pending) {
                        $loginLines.Add('printf ''\n=== ' + $provider + ': approve the browser sign-in; never share passwords or tokens with your agent ===\n''')
                        switch ($provider) {
                            'codex' {
                                if ($bridgeUsed) { $loginLines.Add('printf ''Windows sign-in bridge is active. Use Codex on Windows once to refresh it; no Linux login will be run.\n''') }
                                else { $loginLines.Add('if codex login --help 2>&1 | grep -q -- ''--device-auth''; then codex login --device-auth; else codex login; fi') }
                            }
                            'claude' { $loginLines.Add('if claude auth --help 2>&1 | grep -q ''login''; then claude auth login; elif claude --help 2>&1 | grep -q ''login''; then claude login; else printf ''Sign in, then exit immediately. Do not ask this agent to do work.\n''; claude; fi') }
                            'gemini' { $loginLines.Add('if agy --help 2>&1 | grep -Eq ''^[[:space:]]+login([[:space:]]|$)''; then agy login; else printf ''Sign in with Google, then exit immediately - it is an agent; do not type anything else.\n''; agy; fi') }
                        }
                    }
                    $loginLines.Add('printf ''\nSign-in sequence finished. This window will close.\n''')
                    $loginScript = Join-Path $stage 'sign-in.sh'
                    Write-Utf8 $loginScript ($loginLines -join "`n")
                    $loginPath = Invoke-Native 'wsl.exe' @('--exec', 'wslpath', '-a', '-u', $loginScript)
                    Require-Native $loginPath 'Translating sign-in script path failed'
                    # Credentials and login output stay in this human-owned window, outside the transcript.
                    $argumentLine = '--exec bash -ic ' + (Quote-Native ('bash ' + (Quote-Sh $loginPath.Output)))
                    Start-Process -FilePath 'wsl.exe' -ArgumentList $argumentLine -Wait | Out-Null
                    foreach ($provider in $Providers) { $usageStates[$provider] = Test-Usage $provider }
                    $noisy = @($Providers | Where-Object { $usageStates[$_] -eq 'noise' })
                    if ($noisy.Count) { Fail 1 ("Text is printed outside codexbar's JSON for " + ($noisy -join ', ') + " in the default WSL user's interactive shell (bash -ic), so the app cannot read it. Remove echo/printf output from ~/.bashrc, then rerun.") }
                    $pending = @($Providers | Where-Object { $usageStates[$_] -notin @('live', 'stale') })
                }
            }
            $launched = $false
            if (-not $SkipApp) {
                $launchTime = [datetimeoffset]::UtcNow
                try {
                    Start-Process -FilePath 'explorer.exe' -ArgumentList ('shell:AppsFolder\' + $installed[0].PackageFamilyName + '!' + $package.AppId) | Out-Null
                    $launched = $true
                } catch { Write-Warning 'Automatic app launch failed; launch AgentsWinBar from Start.' }
            }
            if ($WithWsl) {
                $stale = @($Providers | Where-Object { $usageStates[$_] -eq 'stale' })
                if ($stale.Count) { Phase 'Stale usage' (($stale -join ', ') + ': temporarily stale (rate limit or provider issue), not a sign-in problem.') }
                if ($pending.Count) {
                    Phase 'Sign-ins' ("Usage unavailable: " + ($pending -join ', ') + '. Sign-in or provider/network recovery may be needed.') $false
                    $script:human.Add('Approve pending browser sign-ins when setup is rerun: powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AgentsWinBar-Setup\install.ps1" -WithWsl')
                    if ($bridgeUsed -and $pending -contains 'codex') { $script:human.Add('Use Codex on Windows once to refresh its sign-in, or rerun install.ps1 -WithWsl -NoWindowsSignIns for a separate Linux sign-in.') }
                    Finish 20 'signin'
                } else { Phase 'Sign-ins' 'Every selected provider has live or temporarily stale usage; no sign-in needed.'; Finish 0 'ok' }
            } else {
                $missingSignIns = @($Providers | Where-Object { (Test-WindowsSignIn $_) -eq 'missing' })
                foreach ($provider in $missingSignIns) {
                    switch ($provider) {
                        'claude' { $script:human.Add('Claude: run claude in a Windows terminal once and sign in.') }
                        'codex' { $script:human.Add('Codex: sign in to the Windows Codex app or run codex login in a Windows terminal once.') }
                        'gemini' { $script:human.Add('Gemini: sign in to Antigravity on Windows once.') }
                    }
                }
                if ($launched) {
                    $cachePath = Join-Path $settingsDirectory 'cache.json'
                    $usageStates = @{}
                    $deadline = $launchTime.AddSeconds(120)
                    do {
                        foreach ($provider in $Providers) {
                            if ($usageStates[$provider] -notin @('live', 'stale')) {
                                $usageStates[$provider] = Get-AppUsageStatus $cachePath $provider $launchTime
                            }
                        }
                        $unconfirmed = @($Providers | Where-Object { $usageStates[$_] -eq 'none' })
                        $remaining = ($deadline - [datetimeoffset]::UtcNow).TotalMilliseconds
                        if (-not $unconfirmed.Count -or $remaining -le 0) { break }
                        Start-Sleep -Milliseconds ([int][Math]::Min(2000, $remaining))
                    } while ([datetimeoffset]::UtcNow -lt $deadline)
                    foreach ($provider in $Providers) { Phase "Usage $provider" $usageStates[$provider] }
                    if ($unconfirmed.Count) {
                        Phase 'Usage' (($unconfirmed -join ', ') + ': not confirmed yet (the app reads every few minutes).')
                    }
                }
                if ($missingSignIns.Count) {
                    Phase 'Sign-ins' ("Windows sign-in missing: " + ($missingSignIns -join ', ') + '.') $false
                    Finish 20 'signin'
                } else {
                    Phase 'Sign-ins' 'Every selected provider has a Windows sign-in present.'
                    Finish 0 'ok'
                }
            }
        }
    }
} catch {
    Phase 'Failure' $_.Exception.Message $false
    $script:human.Add($_.Exception.Message)
    switch ($script:failureCode) { 30 { Finish 30 'uac' }; 40 { Finish 40 'access' }; 50 { Finish 50 'failed' }; default { Finish 1 'failed' } }
} finally {
    $expectedPhases = @('Preflight', 'Release access', 'Download / signer', 'Elevation')
    if ($WithWsl) { $expectedPhases += @('Reboot', 'Distro', 'WSL tools') }
    $expectedPhases += @('App', 'Sign-ins')
    foreach ($name in $expectedPhases) {
        if (-not $script:phases.Contains($name)) { $script:phases[$name] = [pscustomobject]@{ Good = $false; Message = 'Not reached; rerun after resolving the reported failure.' } }
    }
    Write-Host "`nSetup summary:"
    foreach ($entry in $script:phases.GetEnumerator()) {
        # ASCII marks: consoles on legacy code pages render check marks as '?' or 'V'.
        $mark = '[ok]'
        if (-not $entry.Value.Good) { $mark = '[FAIL]' }
        Write-Host "$mark $($entry.Key): $($entry.Value.Message)"
    }
    Write-Host "`nWhat the human still needs to do:"
    if ($script:human.Count) { foreach ($action in $script:human) { Write-Host "- $action" } } else { Write-Host '- Nothing.' }
    if ($script:transcript) { try { Stop-Transcript | Out-Null } catch { } }
    Write-Host "AGENTSWINBAR_SETUP=$($script:outcome)"
}
exit $script:exitCode
