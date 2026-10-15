param(
    [string] $ReportPath = 'licenses.json',
    [string] $AllowlistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) '.github/license-allowlist.json')
)
$ErrorActionPreference = 'Stop'

function Test-AllowedLicenseExpression([string] $Expression) {
    $allowed = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'Unlicense', 'CC0-1.0')
    if ($Expression -match '[^a-zA-Z0-9.()\s-]') { return $false }
    $tokens = [regex]::Matches($Expression, '[a-zA-Z0-9.-]+|[()]')
    $expectLicense = $true
    $depth = 0
    foreach ($token in $tokens) {
        $value = $token.Value
        if ($expectLicense) {
            if ($value -eq '(') { $depth++; continue }
            if ($allowed -cnotcontains $value) { return $false }
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

# Exceptions must identify the exact package, version, and reported license after owner review.
$allowlistJson = Get-Content -LiteralPath $AllowlistPath -Raw -Encoding UTF8
if ($allowlistJson -notmatch '^\s*\[') { throw 'License allowlist must be a JSON array.' }
$allowlist = $allowlistJson | ConvertFrom-Json
foreach ($entry in $allowlist) {
    foreach ($name in @('PackageName', 'PackageVersion', 'License')) {
        if ($entry.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.$name)) { throw 'Invalid license allowlist entry.' }
    }
}
$violations = New-Object 'System.Collections.Generic.List[string]'
foreach ($package in $licenses) {
    $name = [string]$package.PackageName
    if (-not $name) { $name = [string]$package.PackageId }
    $version = [string]$package.PackageVersion
    if (-not $version) { $version = [string]$package.Version }
    $license = $package.License
    if (-not $name -or $license -isnot [string] -or [string]::IsNullOrWhiteSpace($license)) {
        $violations.Add('Missing package name or license metadata.'); continue
    }
    # Match bare SPDX IDs, -only/-or-later, legacy + suffixes, and IDs within compound expressions.
    if ($license -match '(?i)\b(?:GPL|AGPL|LGPL)-[0-9]') {
        $violations.Add("${name}: forbidden license $license"); continue
    }
    if (Test-AllowedLicenseExpression $license) { continue }
    $approved = @($allowlist | Where-Object { $_.PackageName -ceq $name -and $_.PackageVersion -ceq $version -and $_.License -ceq $license })
    if ($approved.Count -eq 0) { $violations.Add("${name}: unknown or unapproved license $license") }
}
if ($violations.Count) { throw ("License policy failed:`n" + ($violations -join "`n")) }
Write-Host "License policy passed for $($licenses.Count) packages."
