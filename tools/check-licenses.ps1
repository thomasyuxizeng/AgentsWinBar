param(
    [string] $ReportPath = 'licenses.json'
)
$ErrorActionPreference = 'Stop'

# The project rule is "no GPL-family dependencies" (the app is MIT). This check enforces exactly that and nothing more:
#   fails: a missing or malformed report, a package without license information, and any GPL, AGPL or LGPL license
#   warns: a license that is not a plain permissive SPDX expression (a license file, a URL, an unusual identifier).
# Microsoft's own packages (the Windows App SDK, WebView2, the SDK build tools) are reported as license files or URLs, so a
# per-package approval list for them would have to be redone at every dependency update. They are listed as warnings instead.

function Test-PermissiveLicenseExpression([string] $Expression) {
    $permissive = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'Unlicense', 'CC0-1.0')
    if ($Expression -match '[^a-zA-Z0-9.()\s-]') { return $false }
    $tokens = [regex]::Matches($Expression, '[a-zA-Z0-9.-]+|[()]')
    $expectLicense = $true
    $depth = 0
    foreach ($token in $tokens) {
        $value = $token.Value
        if ($expectLicense) {
            if ($value -eq '(') { $depth++; continue }
            if ($permissive -cnotcontains $value) { return $false }
            $expectLicense = $false
        } else {
            if ($value -eq ')') { $depth--; if ($depth -lt 0) { return $false }; continue }
            if ($value -cnotin @('AND', 'OR')) { return $false }
            $expectLicense = $true
        }
    }
    -not $expectLicense -and $depth -eq 0
}

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { throw 'License report is missing.' }
$json = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($json) -or $json -notmatch '^\s*\[') { throw 'License report must be a non-empty JSON array.' }
$licenses = $json | ConvertFrom-Json
if ($null -eq $licenses -or @($licenses).Count -eq 0) { throw 'License report contains no packages.' }

# Bare SPDX IDs, -only/-or-later, legacy + suffixes and IDs within compound expressions, or the license's own name.
$forbidden = '(?i)\b(?:GPL|AGPL|LGPL)-[0-9]|general public license'
$violations = New-Object 'System.Collections.Generic.List[string]'
$warnings = New-Object 'System.Collections.Generic.List[string]'
foreach ($package in $licenses) {
    $name = [string]$package.PackageName
    if (-not $name) { $name = [string]$package.PackageId }
    $version = [string]$package.PackageVersion
    if (-not $version) { $version = [string]$package.Version }
    $license = $package.License
    if (-not $name -or $license -isnot [string] -or [string]::IsNullOrWhiteSpace($license) -or $license -ieq 'Unknown') {
        $violations.Add("${name}: missing license information"); continue
    }
    if ($license -match $forbidden) {
        $violations.Add("${name}: forbidden license $license"); continue
    }
    if (-not (Test-PermissiveLicenseExpression $license)) {
        $warnings.Add("$name $version has a license that is not a plain SPDX identifier ($license); check it is not a GPL-family license.")
    }
}
if ($violations.Count) { throw ("License policy failed:`n" + ($violations -join "`n")) }
foreach ($warning in $warnings) { Write-Host "::warning::$warning" }
Write-Host "License policy passed for $($licenses.Count) packages ($($warnings.Count) with a license file or URL instead of an SPDX identifier)."
