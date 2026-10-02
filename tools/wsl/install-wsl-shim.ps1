<#
.SYNOPSIS
  Installs the CodexBar CLI and the CodexBarWin shim into a WSL distro, from Windows.

.EXAMPLE
  .\install-wsl-shim.ps1 -Distro Ubuntu-24.04
#>
param(
    [string] $Distro = "Ubuntu-24.04",
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
$cmd = "echo $installB64 | base64 -d > /tmp/cbw-install.sh; " +
       "echo $shimB64 | base64 -d > /tmp/codexbar-shim.sh; " +
       "CODEXBAR_VERSION=$CodexBarVersion bash /tmp/cbw-install.sh /tmp/codexbar-shim.sh"
wsl -d $Distro -u root -e bash -c $cmd
if ($LASTEXITCODE -ne 0) { throw "Installation failed (exit code $LASTEXITCODE)." }

Write-Host "`nDone. Sign in to the tools you use inside WSL (see tools\wsl\README.md)."
