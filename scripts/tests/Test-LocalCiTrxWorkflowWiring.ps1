#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$workflowPath = Join-Path $repoRoot '.gitea/workflows/ci.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$invocation = './scripts/tests/Test-LocalCiTrxRetention.ps1'
$guardInvocation = './scripts/tests/Test-LocalCiTrxWorkflowWiring.ps1'
$count = ([regex]::Matches($workflow, [regex]::Escape($invocation))).Count
if ($count -ne 1) {
    Write-Host "FAIL: Gitea CI must invoke the durable TRX contract exactly once; found $count." -ForegroundColor Red
    exit 1
}
$guardCount = ([regex]::Matches($workflow, [regex]::Escape($guardInvocation))).Count
if ($guardCount -ne 1) {
    Write-Host "FAIL: Gitea CI must invoke the durable TRX wiring guard exactly once; found $guardCount." -ForegroundColor Red
    exit 1
}
Write-Host 'PASS: Gitea CI invokes the durable TRX contract and wiring guard exactly once.'
exit 0
