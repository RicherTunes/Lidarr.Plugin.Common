#!/usr/bin/env pwsh
# Test args redaction patterns through the module

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-json-output.psm1") -Force
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-diagnostics.psm1") -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Args Redaction Pattern Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Test-ArgsRedaction {
    param([string[]]$InputArgs, [string]$Description, [scriptblock]$Assertion)

    $context = @{
        LidarrUrl = 'http://localhost:8686'
        ContainerName = 'test'
        ContainerId = 'test123'
        ContainerStartedAt = $null
        ImageTag = 'test'
        ImageId = $null
        ImageDigest = $null
        RequestedGate = 'test'
        Plugins = @('Test')
        EffectiveGates = @('Test')
        EffectivePlugins = @('Test')
        StopReason = $null
        RedactionSelfTestExecuted = $true
        RedactionSelfTestPassed = $true
        RunnerArgs = $InputArgs
        DiagnosticsBundlePath = $null
        DiagnosticsIncludedFiles = @()
        LidarrVersion = $null
        LidarrBranch = $null
        SourceShas = @{ Common = $null; Qobuzarr = $null; Tidalarr = $null; Brainarr = $null }
        SourceProvenance = @{ Common = 'unknown'; Qobuzarr = 'unknown'; Tidalarr = 'unknown'; Brainarr = 'unknown' }
    }

    $results = @([PSCustomObject]@{
        Gate = 'Test'
        PluginName = 'Test'
        Outcome = 'success'
        Success = $true
        Errors = @()
        Details = @{}
        StartTime = [DateTime]::UtcNow
        EndTime = [DateTime]::UtcNow
    })

    $json = ConvertTo-E2ERunManifest -Results $results -Context $context
    $obj = $json | ConvertFrom-Json
    $resultArgs = $obj.runner.args

    $pass = & $Assertion $resultArgs $json
    if ($pass) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
        Write-Host "         Input: $($InputArgs -join ' ')" -ForegroundColor Yellow
        Write-Host "         Result: $($resultArgs -join ' ')" -ForegroundColor Yellow
        $script:failed++
    }
}

Write-Host ""
Write-Host "Test Group: -Param value style" -ForegroundColor Yellow

Test-ArgsRedaction -InputArgs @('-ApiKey', 'abc123secret') -Description "-ApiKey value: key redacted" -Assertion {
    param($resultArgs, $jsonStr)
    ($resultArgs -join ' ') -match '-ApiKey' -and ($resultArgs -join ' ') -match '\[REDACTED\]' -and $jsonStr -notmatch 'abc123secret'
}

Test-ArgsRedaction -InputArgs @('-Gate', 'bootstrap', '-ApiKey', 'xyz789') -Description "-ApiKey in middle: only key redacted" -Assertion {
    param($resultArgs, $jsonStr)
    ($resultArgs -contains '-Gate') -and ($resultArgs -contains 'bootstrap') -and ($resultArgs -contains '[REDACTED]') -and $jsonStr -notmatch 'xyz789'
}

Write-Host ""
Write-Host "Test Group: -Param=value style" -ForegroundColor Yellow

Test-ArgsRedaction -InputArgs @('-ApiKey=secretvalue') -Description "-ApiKey=value: redacted" -Assertion {
    param($resultArgs, $jsonStr)
    ($resultArgs -join ' ') -match '-ApiKey=\[REDACTED\]' -and $jsonStr -notmatch 'secretvalue'
}

Write-Host ""
Write-Host "Test Group: Hex string detection" -ForegroundColor Yellow

Test-ArgsRedaction -InputArgs @('abcdef1234567890abcdef12') -Description "24 hex chars: auto-redacted" -Assertion {
    param($resultArgs, $jsonStr)
    $resultArgs -contains '[REDACTED]' -and $jsonStr -notmatch 'abcdef1234567890abcdef12'
}

Test-ArgsRedaction -InputArgs @('abcdef1234567890abcdef1234567890') -Description "32 hex chars (API key length): auto-redacted" -Assertion {
    param($resultArgs, $jsonStr)
    $resultArgs -contains '[REDACTED]' -and $jsonStr -notmatch 'abcdef1234567890abcdef1234567890'
}

Test-ArgsRedaction -InputArgs @('not-a-key') -Description "Non-hex string: preserved" -Assertion {
    param($resultArgs, $jsonStr)
    $resultArgs -contains 'not-a-key'
}

Write-Host ""
Write-Host "Test Group: Case sensitivity" -ForegroundColor Yellow

Test-ArgsRedaction -InputArgs @('-apikey', 'secret') -Description "Lowercase -apikey: handled" -Assertion {
    param($resultArgs, $jsonStr)
    # Note: Our patterns are case-sensitive, so -apikey won't match -ApiKey
    # This test documents current behavior
    $jsonStr -notmatch '"secret"' -or $resultArgs -contains '[REDACTED]'
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) { exit 1 }
exit 0
