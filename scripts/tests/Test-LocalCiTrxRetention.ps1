#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$modulePath = Join-Path $repoRoot 'scripts/lib/local-ci-receipts.psm1'
$sandbox = Join-Path ([IO.Path]::GetTempPath()) "local-ci-receipts-contract-$([guid]::NewGuid().ToString('N'))"
$failures = [Collections.Generic.List[string]]::new()

function Assert-Contract {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { $failures.Add($Message) }
}

function Invoke-RealTestRun {
    param([string]$ProjectDirectory, [string]$ResultsDirectory, [bool]$ShouldPass)

    New-Item -ItemType Directory -Force -Path $ProjectDirectory, $ResultsDirectory | Out-Null
    & dotnet new mstest --framework net8.0 --no-restore --output $ProjectDirectory | Out-Null
    if (-not $ShouldPass) {
        $testFile = Join-Path $ProjectDirectory 'UnitTest1.cs'
        (Get-Content -LiteralPath $testFile -Raw).Replace('Assert.Fail();', 'Assert.AreEqual(1, 2);') |
            Set-Content -LiteralPath $testFile -Encoding UTF8
    }
    & dotnet test (Join-Path $ProjectDirectory "$(Split-Path $ProjectDirectory -Leaf).csproj") `
        --logger 'trx;LogFileName=input.trx' --results-directory $ResultsDirectory --nologo 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-Contract (($exitCode -eq 0) -eq $ShouldPass) "Real dotnet test exit did not match ShouldPass=$ShouldPass."
    $trx = Join-Path $ResultsDirectory 'input.trx'
    Assert-Contract (Test-Path -LiteralPath $trx -PathType Leaf) 'Real dotnet test did not produce input.trx.'
    return $trx
}

try {
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
        throw "Durable local-CI receipt module is missing: $modulePath"
    }
    Import-Module $modulePath -Force

    $durable = Join-Path $sandbox 'durable'
    $passingInput = Invoke-RealTestRun -ProjectDirectory (Join-Path $sandbox 'PassingTests') -ResultsDirectory (Join-Path $sandbox 'passing-input') -ShouldPass $true
    $first = Publish-LocalCiTrxEvidence -TrxPath $passingInput -DestinationDirectory $durable -ProjectName 'PassingTests'
    $second = Publish-LocalCiTrxEvidence -TrxPath $passingInput -DestinationDirectory $durable -ProjectName 'PassingTests'

    Assert-Contract (Test-Path -LiteralPath $first -PathType Leaf) 'Successful test TRX was not retained.'
    Assert-Contract (Test-Path -LiteralPath $second -PathType Leaf) 'Repeated successful test TRX was not retained.'
    Assert-Contract ($first -cne $second) 'Repeated publication clobbered the prior TRX path.'
    Assert-Contract ((Get-ChildItem -LiteralPath $durable -Filter '*.trx').Count -eq 2) 'Durable directory does not contain both successful receipts.'

    $passingSummary = Get-LocalCiTrxSummary -TrxPath $first
    Assert-Contract ($passingSummary.Passed -eq 1 -and $passingSummary.Failed -eq 0 -and $passingSummary.Skipped -eq 0) 'Saved successful TRX summary is incorrect.'

    $failingInput = Invoke-RealTestRun -ProjectDirectory (Join-Path $sandbox 'FailingTests') -ResultsDirectory (Join-Path $sandbox 'failing-input') -ShouldPass $false
    $failedReceipt = Publish-LocalCiTrxEvidence -TrxPath $failingInput -DestinationDirectory $durable -ProjectName 'FailingTests'
    $failedSummary = Get-LocalCiTrxSummary -TrxPath $failedReceipt
    Assert-Contract (Test-Path -LiteralPath $failedReceipt -PathType Leaf) 'Failed test TRX was not retained.'
    Assert-Contract ($failedSummary.Passed -eq 0 -and $failedSummary.Failed -eq 1 -and $failedSummary.Skipped -eq 0) 'Saved failing TRX summary is incorrect.'

    $invalidDestination = Join-Path $sandbox 'destination-is-a-file'
    Set-Content -LiteralPath $invalidDestination -Value 'not a directory' -Encoding UTF8
    $writeFailedClosed = $false
    try {
        $null = Publish-LocalCiTrxEvidence -TrxPath $passingInput -DestinationDirectory $invalidDestination -ProjectName 'PassingTests'
    }
    catch { $writeFailedClosed = $true }
    Assert-Contract $writeFailedClosed 'Invalid nominated evidence destination did not fail closed.'

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'PASS: durable local-CI TRX receipt contract (real success/failure inputs, no clobber, matching summaries, fail closed).'
    exit 0
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
