<#
.SYNOPSIS
Tests release security policies in isolation; never runs setup, UAC, WSL, or network commands.
#>
param(
    [ValidateSet('Signer', 'Elevation', 'Licenses', 'Workflow')][string] $Group = 'Signer',
    [string] $SourceRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$root = $SourceRoot
$installer = Join-Path $root 'install.ps1'
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$errors)
if ($errors.Count) { throw 'Installer syntax is invalid.' }

function Assert-True([bool] $Value, [string] $Name) {
    if (-not $Value) { throw "Failed: $Name" }
}
function Assert-Throws([scriptblock] $Action, [string] $Name) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-True $threw $Name
}
function Get-InstallerFunction([string] $Name) {
    $node = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }.GetNewClosure(), $true)
    if (-not $node) { throw "Missing installer helper: $Name" }
    $node.Extent.Text
}

if ($Group -eq 'Signer') {
    . ([scriptblock]::Create((Get-InstallerFunction 'Assert-ReleaseSigner')))
    $pin = 'A' * 40
    Assert-ReleaseSigner $pin $pin
    Assert-ReleaseSigner $pin.ToLowerInvariant() (" `t" + $pin + ' ')
    Assert-Throws { Assert-ReleaseSigner ('B' * 40) $pin } 'unrecognized same-subject key is rejected'
    Assert-Throws { Assert-ReleaseSigner $pin 'bad-pin' } 'malformed pin is rejected'
    Assert-Throws { Assert-ReleaseSigner $pin 'bad-pin' -AllowDeveloperPackage } 'override does not hide a malformed pin'
    $pinCall = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Assert-ReleaseSigner' }, $true)
    $trustCheck = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$trustNeeded' -and $n.Right.Extent.Text -like '*TrustedPeople*' }, $true)
    Assert-True ($null -ne $pinCall -and $null -ne $trustCheck -and $pinCall.Extent.StartOffset -lt $trustCheck.Extent.StartOffset) 'release pin is enforced before planning certificate trust'
    Assert-True ($pinCall.Extent.Text.Contains('$ExpectedReleaseCertificateThumbprint') -and $pinCall.Extent.Text.Contains('-AllowDeveloperPackage:$AllowDeveloperPackage')) 'trust flow uses configured pin and explicit override'
    $warnings = @(Assert-ReleaseSigner $pin '' 3>&1)
    Assert-True ($warnings.Count -eq 1 -and $warnings[0] -is [System.Management.Automation.WarningRecord] -and $warnings[0].Message -like '*SECURITY WARNING*unset*') 'unset pin explicitly warns and preserves legacy acceptance'
    $warnings = @(Assert-ReleaseSigner ('B' * 40) $pin -AllowDeveloperPackage 3>&1)
    Assert-True ($warnings.Count -eq 1 -and $warnings[0].Message -like '*LOCAL DEVELOPER PACKAGE*') 'explicit developer override warns'

    $guard = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$AllowDeveloperPackage -and -not $MsixPath' }, $true)
    Assert-True ($null -ne $guard) 'developer override requires local path'
    $AllowDeveloperPackage = $true; $MsixPath = ''
    Assert-Throws { & ([scriptblock]::Create($guard.Extent.Text)) } 'remote developer override is rejected'
    $AllowDeveloperPackage = $false; $ExpectedReleaseCertificateThumbprint = $pin
    $signerThumbprint = $pin; $published = [pscustomobject]@{ Thumbprint = 'B' * 40 }
    $mismatch = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$published.Thumbprint -ne $signerThumbprint' }, $true)
    Assert-True ($null -ne $mismatch) 'published certificate consistency check exists'
    Assert-Throws { & ([scriptblock]::Create($mismatch.Extent.Text)) } 'published certificate mismatch fails pinned normal releases'
    $ExpectedReleaseCertificateThumbprint = ''
    $warnings = @(& ([scriptblock]::Create($mismatch.Extent.Text)) 3>&1)
    Assert-True ($warnings.Count -eq 1 -and $warnings[0] -is [System.Management.Automation.WarningRecord]) 'published mismatch preserves legacy warning with empty pin'
    $reloads = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$reloadNames' }, $true))
    Assert-True ($reloads.Count -gt 0 -and @($reloads | Where-Object { $_.Extent.Text -match 'AllowDeveloperPackage' }).Count -eq 0) 'developer bypass cannot be silently restored from mutable saved state'
}

if ($Group -eq 'Elevation') {
    . ([scriptblock]::Create((Get-InstallerFunction 'New-AdminCommand')))
    $unicodePath = 'C:\Users\' + [char]0xD55C + [char]0xAE00 + '\a''b;$(throw ''injected'')\runtime.exe'
    $inputPlan = [ordered]@{
        CertificateBase64 = [Convert]::ToBase64String((New-Object byte[] 1600)); Thumbprint = 'A' * 40
        ExpectedThumbprint = 'A' * 40; AllowDeveloperPackage = $false; Trust = $true
        EnableWsl = $true; UpdateWsl = $false; InstallRuntime = $true; Runtime = $unicodePath; RuntimeSha256 = 'B' * 64
    }
    $encoded = New-AdminCommand $inputPlan
    $inputPlan.Runtime = 'tampered-after-encoding.exe'
    Assert-True ($encoded -match '^[A-Za-z0-9+/=]+$') 'helper uses a command-line-safe base64 payload'
    Assert-True (($encoded.Length + 150) -lt 30000) 'normal certificate and plan fit the guarded Windows command line'
    $decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded))
    $helperErrors = $null
    $helperAst = [System.Management.Automation.Language.Parser]::ParseInput($decoded, [ref]$null, [ref]$helperErrors)
    Assert-True ($helperErrors.Count -eq 0) 'encoded administrator payload parses in Windows PowerShell'
    # Evaluate only the data-decoding assignment, never the administrator helper.
    . ([scriptblock]::Create($helperAst.EndBlock.Statements[0].Extent.Text))
    Assert-True ($plan.Runtime -ceq $unicodePath -and $plan.Thumbprint -ceq ('A' * 40)) 'plan snapshot preserves Unicode and literal metacharacters'
    Assert-True ($decoded -notmatch 'admin\.(ps1|json)|admin-(plan|result)\.json|Import-Certificate|Set-Content') 'administrator never consumes or writes mutable staged control files'
    Assert-True ($decoded.Contains('$store.Add($cert)')) 'certificate is imported from encoded bytes'
    Assert-True ($decoded.Contains('$env:PSModulePath = Join-Path $PSHOME ''Modules''')) 'administrator does not autoload modules from user-writable paths'
    Assert-True ($decoded.Contains('-WorkingDirectory $protectedStage')) 'elevated runtime starts in protected working directory'
    Assert-True ($decoded.Contains('$cert.Thumbprint -ine $plan.Thumbprint') -and $decoded.Contains('Release certificate pin mismatch.')) 'administrator revalidates certificate identity and configured pin'
    Assert-True ($decoded.Contains('$acl.SetAccessRuleProtection($true, $false)') -and $decoded.Contains("@('S-1-5-18', 'S-1-5-32-544')")) 'runtime staging grants only SYSTEM and Administrators'
    Assert-True ($decoded.Contains('[IO.Directory]::CreateDirectory($candidate, $acl)')) 'protected staging ACL is applied at creation'
    Assert-True ($decoded -notmatch 'Remove-Item|-Recurse' -and $decoded.Contains('[IO.File]::Delete($runtimeCopy)') -and $decoded.Contains('[IO.Directory]::Delete($protectedStage)')) 'privileged cleanup deletes only the known file and empty staging directory'
    $copyIndex = $decoded.IndexOf('Copy-Item -LiteralPath $plan.Runtime -Destination $runtimeCopy')
    $hashIndex = $decoded.IndexOf('Get-FileHash -LiteralPath $runtimeCopy')
    $signatureIndex = $decoded.IndexOf('Get-AuthenticodeSignature -LiteralPath $runtimeCopy')
    $startIndex = $decoded.IndexOf('Start-Process -FilePath $runtimeCopy')
    Assert-True ($copyIndex -gt 0 -and $hashIndex -gt $copyIndex -and $signatureIndex -gt $hashIndex -and $startIndex -gt $signatureIndex) 'runtime is copied, hash checked, signature checked, then launched from protected staging'
    $runtimeGuard = $helperAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -like '*IsPathRooted*' }, $true)
    $condition = [scriptblock]::Create($runtimeGuard.Clauses[0].Item1.Extent.Text)
    foreach ($bad in @(@{ Runtime = 'relative.exe'; RuntimeSha256 = 'B' * 64 }, @{ Runtime = 'C:\runtime.cmd'; RuntimeSha256 = 'B' * 64 }, @{ Runtime = 'C:\runtime.exe'; RuntimeSha256 = '' })) {
        $plan = [pscustomobject]$bad
        Assert-True (& $condition) 'unsafe runtime plan is rejected before copying'
    }
    $plan = [pscustomobject]@{ Runtime = 'C:\runtime.exe'; RuntimeSha256 = 'B' * 64 }
    Assert-True (-not (& $condition)) 'valid runtime plan passes structural validation'
    $signatureGuard = $helperAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -like '*signature.Status*' }, $true)
    $signatureCondition = [scriptblock]::Create($signatureGuard.Clauses[0].Item1.Extent.Text)
    foreach ($signature in @(
        [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $null },
        [pscustomobject]@{ Status = 'NotTrusted'; SignerCertificate = [pscustomobject]@{ Subject = 'O=Microsoft Corporation' } },
        [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'O=Microsoft Corporation Evil' } }
    )) { Assert-True (& $signatureCondition) 'missing, untrusted, or wrong runtime signer fails' }
    $signature = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft, O=Microsoft Corporation, C=US' } }
    Assert-True (-not (& $signatureCondition)) 'valid Microsoft runtime signer passes'
    $source = [IO.File]::ReadAllText($installer)
    Assert-True ($source -notmatch 'admin\.ps1|admin-plan\.json|admin-result\.json') 'parent has no mutable administrator script, plan, or result file'
    Assert-True ($source.Contains('-EncodedCommand ') -and $source.Contains("'WindowsPowerShell\v1.0\powershell.exe'")) 'UAC uses encoded payload and system PowerShell path'
    Assert-True ($source.Contains("-WorkingDirectory ([Environment]::GetFolderPath('System'))")) 'elevated PowerShell starts in the system working directory'
    $resultAssignment = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$adminResult' -and $n.Right.Extent.Text -like '*adminProcess.ExitCode*' }, $true)
    Assert-True ($null -ne $resultAssignment) 'administrator reboot result comes from process exit code'
    foreach ($code in @(0, 3010, 30)) {
        $adminProcess = [pscustomobject]@{ ExitCode = $code }
        . ([scriptblock]::Create($resultAssignment.Extent.Text))
        Assert-True ($adminResult.Reboot -eq ($code -eq 3010)) "reboot propagation for exit $code"
    }
    $lengthGuard = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$arguments.Length -gt 30000' }, $true)
    Assert-True ($null -ne $lengthGuard) 'oversized UAC commands fail before elevation'
    $arguments = 'A' * 30001
    Assert-Throws { & ([scriptblock]::Create($lengthGuard.Extent.Text)) } 'oversized encoded payload is rejected'
    $arguments = 'A' * 30000
    & ([scriptblock]::Create($lengthGuard.Extent.Text))
    $failureGuard = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$adminProcess.ExitCode -notin @(0, 3010)' }, $true)
    Assert-True ($null -ne $failureGuard) 'only successful and reboot-required administrator exits are accepted'
    foreach ($code in @(0, 3010, 30, 1)) {
        $adminProcess = [pscustomobject]@{ ExitCode = $code }
        Assert-True ((& ([scriptblock]::Create($failureGuard.Clauses[0].Item1.Extent.Text))) -eq ($code -notin @(0, 3010))) "administrator failure propagation for exit $code"
    }
}

if ($Group -eq 'Licenses') {
    $checker = Join-Path $root 'tools/check-licenses.ps1'
    $checkerErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($checker, [ref]$null, [ref]$checkerErrors)
    Assert-True ($checkerErrors.Count -eq 0) 'license checker parses in Windows PowerShell'
    $fixture = Join-Path $PSScriptRoot ('security-test-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($fixture)
    $report = Join-Path $fixture 'licenses.json'
    try {
        Assert-Throws { & $checker -ReportPath $report } 'absent license report fails'
        foreach ($invalid in @('', '[]', '[ ]', 'null', '{}', '[null]', '[{}]', '[', '[{"PackageName":"test","License":""}]', '[{"PackageName":"test","License":["MIT"]}]')) {
            [IO.File]::WriteAllText($report, $invalid)
            Assert-Throws { & $checker -ReportPath $report } "invalid or empty license report fails: $invalid"
        }
        # The policy fails on GPL-family licenses (anywhere in an expression, or by name) and on missing license information
        foreach ($license in @('GPL-2.0', 'GPL-3.0-only', 'GPL-2.0+', 'AGPL-3.0-or-later', 'LGPL-2.1-only', 'MIT OR GPL-3.0-only', 'MIT AND (Apache-2.0 OR AGPL-3.0-or-later)', 'GNU General Public License v3.0', 'https://www.gnu.org/licenses/gpl-3.0.html', 'Unknown')) {
            # Keep the report an array in both Windows PowerShell and PowerShell 7.
            [IO.File]::WriteAllText($report, ('[' + (@{ PackageName = 'test'; PackageVersion = '1.0'; License = $license } | ConvertTo-Json -Compress) + ']'))
            Assert-Throws { & $checker -ReportPath $report } "GPL-family or missing license fails: $license"
        }
        # Plain permissive SPDX expressions pass without a warning
        foreach ($license in @('MIT', 'Apache-2.0', '(MIT OR Apache-2.0) AND BSD-3-Clause', 'BSD-2-Clause', 'ISC')) {
            [IO.File]::WriteAllText($report, ('[' + (@{ PackageName = 'test'; License = $license } | ConvertTo-Json -Compress) + ']'))
            $output = (& $checker -ReportPath $report 6>&1) | Out-String
            Assert-True ($output -notmatch '::warning::') "permissive license passes quietly: $license"
        }
        # Anything else that is not GPL-family (Microsoft's license files and URLs, unusual identifiers) passes with a warning
        foreach ($license in @('license.txt', 'sdk_license.txt', 'https://aka.ms/WinSDKLicenseURL', 'Owner-reviewed SDK license', 'MIT OR', '(MIT', 'MIT Apache-2.0')) {
            [IO.File]::WriteAllText($report, ('[' + (@{ PackageName = 'test'; PackageVersion = '1.0'; License = $license } | ConvertTo-Json -Compress) + ']'))
            $output = (& $checker -ReportPath $report 6>&1) | Out-String
            Assert-True ($output -match '::warning::') "non-SPDX license warns instead of failing: $license"
        }
        [IO.File]::WriteAllText($report, '[{"PackageId":"first","Version":"1.0","License":"MIT"},{"PackageName":"second","PackageVersion":"1.0","License":"Apache-2.0"}]')
        $null = & $checker -ReportPath $report 6>$null
        [IO.File]::WriteAllText($report, '[{"PackageName":"first","License":"MIT"},{"PackageName":"second","License":"AGPL-3.0-only"}]')
        Assert-Throws { & $checker -ReportPath $report } 'one permitted row cannot hide a later forbidden row'
        [IO.File]::WriteAllText($report, '[{"PackageName":"first","License":"license.txt"},{"PackageName":"second","License":"GPL-3.0-only"}]')
        Assert-Throws { & $checker -ReportPath $report } 'a warning row cannot hide a later forbidden row'
    } finally {
        [IO.File]::Delete($report)
        [IO.Directory]::Delete($fixture)
    }
    $licenseWorkflow = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/licenses.yml'))
    Assert-True ($licenseWorkflow -notmatch 'continue-on-error:\s*true') 'report failures cannot be ignored'
    Assert-True (([regex]::Matches($licenseWorkflow, 'if \(\$LASTEXITCODE -ne 0\)')).Count -eq 2) 'both report generators check native exit status'
    # nuget-license 4.x rejects --output-type ("Unrecognized option"); --output is the format and --file-output the target.
    Assert-True ($licenseWorkflow -notmatch '--output-type') 'workflow does not use the option nuget-license 4.x rejects'
    Assert-True ($licenseWorkflow.Contains('nuget-license --input CodexBarWin.sln --output JsonPretty --file-output licenses.json')) 'JSON report uses valid nuget-license options'
    Assert-True ($licenseWorkflow.Contains('nuget-license --input CodexBarWin.sln --output Table --file-output licenses.txt')) 'table report uses valid nuget-license options'
    Assert-True ($licenseWorkflow.Contains('./tools/check-licenses.ps1 -ReportPath licenses.json')) 'workflow uses tested fail-closed policy'
    Assert-True ($licenseWorkflow.Contains('if-no-files-found: error')) 'empty artifact upload fails'
}

if ($Group -eq 'Workflow') {
    $release = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/release.yml'))
    $test = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/test.yml'))
    $licenses = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/licenses.yml'))
    $jobs = @{}
    foreach ($match in [regex]::Matches($release, '(?ms)^  ([a-z][a-z-]*):\r?\n(.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)')) { $jobs[$match.Groups[1].Value] = $match.Groups[2].Value }
    foreach ($id in @('validate', 'cert', 'build', 'release', 'tests', 'licenses')) { Assert-True $jobs.ContainsKey($id) "release job $id exists" }
    foreach ($id in @('cert', 'build', 'release')) {
        $needs = [regex]::Match($jobs[$id], '(?m)^    needs: \[([^\]]+)\]').Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() }
        foreach ($dependency in @('validate', 'tests', 'licenses')) { Assert-True ($needs -contains $dependency) "$id is gated by $dependency" }
        foreach ($dependency in $needs) { Assert-True $jobs.ContainsKey($dependency) "$id references existing job $dependency" }
        Assert-True ($jobs[$id] -notmatch '(?m)^    if:') "$id cannot override default success gating"
    }
    Assert-True ($jobs.tests.Contains('uses: ./.github/workflows/test.yml') -and $jobs.licenses.Contains('uses: ./.github/workflows/licenses.yml')) 'tag gates reuse repository workflows'
    Assert-True ($test -match '(?m)^  workflow_call:\s*$') 'test workflow is callable'
    Assert-True ($licenses -match '(?m)^  workflow_call:\s*$') 'license workflow is callable'
    Assert-True ($test.Contains('group: ${{ github.workflow }}-tests-${{ github.ref }}')) 'called tests cannot cancel the release caller through concurrency group collision'
    Assert-True ($release -match '(?m)^permissions:\r?\n  contents: read\s*$') 'release default token is read-only'
    Assert-True ($jobs.release -match '(?m)^    permissions:\r?\n      contents: write\s*$') 'publish job has contents write'
    Assert-True (([regex]::Matches($release, 'contents: write')).Count -eq 1) 'only publish job has contents write'
    Assert-True ($release -notmatch 'RestorePackagesWithLockFile|--locked-mode|RestoreLockedMode') 'unverified NuGet lock mode is not enabled'
    Assert-True ($release.Contains('secrets.SIGNING_PFX_BASE64') -and $release.Contains('secrets.SIGNING_PFX_PASSWORD')) 'signing secret names are preserved'
}

Write-Host "$Group release security tests passed."
