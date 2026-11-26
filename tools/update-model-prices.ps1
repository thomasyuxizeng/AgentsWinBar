<#
.SYNOPSIS
    Regenerates the bundled price snapshot (Services/Native/Cost/Pricing/models-dev-prices.json) from models.dev.

.DESCRIPTION
    Keeps only what the native cost scanner prices: the "anthropic", "openai" and "google" providers, and per model the id and
    the cost block (input, output, cache_read, cache_write, context_over_200k, tiers). Rates are USD per million
    tokens, exactly as models.dev publishes them. Keys are sorted so the diff of a regeneration is readable.

.PARAMETER InputPath
    Use a previously downloaded api.json instead of fetching https://models.dev/api.json.

.EXAMPLE
    pwsh tools/update-model-prices.ps1
#>
[CmdletBinding()]
param(
    [string]$InputPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Services\Native\Cost\Pricing\models-dev-prices.json')
)

$ErrorActionPreference = 'Stop'
$source = 'https://models.dev/api.json'
$providers = @('anthropic', 'openai', 'google')
$rateFields = @('input', 'output', 'cache_read', 'cache_write')

if ($InputPath) {
    $text = Get-Content -LiteralPath $InputPath -Raw
} else {
    $text = (Invoke-WebRequest -Uri $source -TimeoutSec 60 -UseBasicParsing).Content
}
$catalog = $text | ConvertFrom-Json -AsHashtable

function Copy-Rates($from) {
    $rates = [ordered]@{}
    foreach ($field in $rateFields) {
        if ($null -ne $from[$field]) { $rates[$field] = $from[$field] }
    }
    return $rates
}

$result = [ordered]@{
    source      = $source
    generatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    providers   = [ordered]@{}
}
foreach ($providerId in $providers) {
    $provider = $catalog[$providerId]
    if ($null -eq $provider -or $null -eq $provider.models) { throw "models.dev has no '$providerId' provider." }
    $models = [ordered]@{}
    foreach ($modelId in ($provider.models.Keys | Sort-Object { $_ } -Culture '')) {
        $cost = $provider.models[$modelId].cost
        if ($null -eq $cost -or $null -eq $cost.input -or $null -eq $cost.output) { continue }
        $entry = Copy-Rates $cost
        if ($null -ne $cost['context_over_200k']) { $entry['context_over_200k'] = Copy-Rates $cost['context_over_200k'] }
        if ($null -ne $cost['tiers']) {
            $tiers = @()
            foreach ($tier in $cost['tiers']) {
                if ($null -eq $tier.tier) { continue }
                $copy = Copy-Rates $tier
                $copy['tier'] = [ordered]@{ type = $tier.tier.type; size = $tier.tier.size }
                $tiers += , $copy
            }
            if ($tiers.Count -gt 0) { $entry['tiers'] = $tiers }
        }
        $models[$modelId] = [ordered]@{ id = $modelId; cost = $entry }
    }
    if ($models.Count -eq 0) { throw "models.dev lists no priced '$providerId' model." }
    $result.providers[$providerId] = [ordered]@{ id = $providerId; models = $models }
}

$json = $result | ConvertTo-Json -Depth 12
$target = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
[IO.File]::WriteAllText($target, $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host ("Wrote {0} anthropic, {1} openai and {2} google models to {3}" -f $result.providers.anthropic.models.Count,
    $result.providers.openai.models.Count, $result.providers.google.models.Count, $target)
