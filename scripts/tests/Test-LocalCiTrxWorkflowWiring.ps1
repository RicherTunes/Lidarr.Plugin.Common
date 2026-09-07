#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workflowPath = Join-Path $repoRoot '.gitea/workflows/ci.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$invocation = './scripts/tests/Test-LocalCiTrxRetention.ps1'
$guardInvocation = './scripts/tests/Test-LocalCiTrxWorkflowWiring.ps1'
$buildTestMatch = [regex]::Match($workflow, '(?ms)^  build-test:\s*$(.*?)(?=^  template:)')
$lintMatch = [regex]::Match($workflow, '(?ms)^  lint:\s*$(.*?)(?=^  ecosystem-contract:)')
if (-not $buildTestMatch.Success -or -not $lintMatch.Success) {
    Write-Host 'FAIL: Gitea CI must retain build-test and lint job blocks.' -ForegroundColor Red
    exit 1
}
$count = ([regex]::Matches($workflow, [regex]::Escape($invocation))).Count
if ($count -ne 1) {
    Write-Host "FAIL: Gitea CI must invoke the durable TRX contract exactly once; found $count." -ForegroundColor Red
    exit 1
}
$sdkJobCount = ([regex]::Matches($buildTestMatch.Groups[1].Value, [regex]::Escape($invocation))).Count
if ($sdkJobCount -ne 1) {
    Write-Host 'FAIL: Gitea CI must invoke the durable TRX contract in the SDK-equipped build-test job.' -ForegroundColor Red
    exit 1
}
$guardCount = ([regex]::Matches($workflow, [regex]::Escape($guardInvocation))).Count
if ($guardCount -ne 1) {
    Write-Host "FAIL: Gitea CI must invoke the durable TRX wiring guard exactly once; found $guardCount." -ForegroundColor Red
    exit 1
}
$lintGuardCount = ([regex]::Matches($lintMatch.Groups[1].Value, [regex]::Escape($guardInvocation))).Count
if ($lintGuardCount -ne 1) {
    Write-Host 'FAIL: Gitea CI must invoke the lightweight wiring guard in the lint job.' -ForegroundColor Red
    exit 1
}
Write-Host 'PASS: Gitea CI invokes the durable TRX contract in build-test and the wiring guard in lint exactly once.'
exit 0
