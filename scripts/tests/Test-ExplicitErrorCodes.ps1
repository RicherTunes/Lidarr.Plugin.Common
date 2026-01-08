$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Explicit ErrorCodes (Queue + Metadata)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$jsonModule = Join-Path $repoRoot 'scripts/lib/e2e-json-output.psm1'

if (-not (Test-Path $jsonModule)) {
    throw "Module not found: $jsonModule"
}

Import-Module $jsonModule -Force

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Assert-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

Write-Host "`nTest Group: ErrorCode set in Details becomes top-level errorCode" -ForegroundColor Yellow

$queueFail = [PSCustomObject]@{
    Gate = 'Grab'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('Grab succeeded but item not found in queue (waited 60s; queue has 3 items)')
    Details = @{
        ErrorCode = 'E2E_QUEUE_NOT_FOUND'
        QueueTimeoutSec = 60
        QueueCount = 3
    }
}

$metaFail = [PSCustomObject]@{
    Gate = 'Metadata'
    PluginName = 'Qobuzarr'
    Outcome = 'failed'
    Errors = @("E2E_METADATA_MISSING: Audio file '01 - So What.flac' missing required tags")
    Details = @{
        ErrorCode = 'E2E_METADATA_MISSING'
        MissingTags = @('01 - So What.flac: Missing tags: artist, album')
        SampleFile = '01 - So What.flac'
    }
}

$json = ConvertTo-E2ERunManifest -Results @($queueFail, $metaFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}

$m = $json | ConvertFrom-Json

$queueResult = $m.results | Where-Object { $_.gate -eq 'Grab' -and $_.plugin -eq 'Tidalarr' } | Select-Object -First 1
$metaResult = $m.results | Where-Object { $_.gate -eq 'Metadata' -and $_.plugin -eq 'Qobuzarr' } | Select-Object -First 1

Assert-Equal "Grab errorCode is E2E_QUEUE_NOT_FOUND" $queueResult.errorCode 'E2E_QUEUE_NOT_FOUND'
Assert-True "Grab details has no errorCode key (moved to top-level)" (-not ($queueResult.details.PSObject.Properties.Name -contains 'errorCode'))

Assert-Equal "Metadata errorCode is E2E_METADATA_MISSING" $metaResult.errorCode 'E2E_METADATA_MISSING'
Assert-True "Metadata details has missingTags array" (@(@($metaResult.details.missingTags) | Where-Object { $null -ne $_ }).Count -gt 0)
Assert-Equal "Metadata details.sampleFile preserved" $metaResult.details.sampleFile '01 - So What.flac'
Assert-True "Metadata details has no errorCode key (moved to top-level)" (-not ($metaResult.details.PSObject.Properties.Name -contains 'errorCode'))

Write-Host "`nSummary: $passed passed, $failed failed" -ForegroundColor Cyan
if ($failed -gt 0) {
    throw "$failed assertions failed"
}
