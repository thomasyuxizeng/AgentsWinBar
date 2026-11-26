<#
.SYNOPSIS
  Installs the CodexBar CLI and the AgentsWinBar shim into a WSL distro, from Windows.

.EXAMPLE
  .\install-wsl-shim.ps1 -Distro Ubuntu-24.04
#>
param(
    [string] $Distro = "Ubuntu-24.04",
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $CodexBarVersion = "v0.69.0"
)

$ErrorActionPreference = "Stop"

function ConvertTo-LfBase64([string] $path) {
    $text = (Get-Content -Raw -LiteralPath $path) -replace "`r", ""
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
}

$installB64 = ConvertTo-LfBase64 (Join-Path $PSScriptRoot "install.sh")
$shimB64    = ConvertTo-LfBase64 (Join-Path $PSScriptRoot "codexbar-shim.sh")

Write-Host "Installing into WSL distro '$Distro' (CodexBar CLI $CodexBarVersion)..."
$cmd = 'set -euo pipefail; tmp=$(mktemp -d); trap ''rm -rf "$tmp"'' EXIT; ' +
       "printf '%s' '$installB64' | base64 -d > " + '"$tmp/install.sh"; ' +
       "printf '%s' '$shimB64' | base64 -d > " + '"$tmp/codexbar-shim.sh"; ' +
       "CODEXBAR_VERSION='$CodexBarVersion' bash " + '"$tmp/install.sh" "$tmp/codexbar-shim.sh"'
$previousWslUtf8 = $env:WSL_UTF8
try {
    $env:WSL_UTF8 = '1'
    $output = & wsl -d $Distro -u root -e bash -c $cmd 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host ($_.ToString() -replace "`0", '') }
    if ($exitCode -ne 0) { throw "Installation failed (exit code $exitCode)." }
} finally {
    $env:WSL_UTF8 = $previousWslUtf8
}

Write-Host "`nDone. Sign in to the tools you use inside WSL (see tools\wsl\README.md)."
