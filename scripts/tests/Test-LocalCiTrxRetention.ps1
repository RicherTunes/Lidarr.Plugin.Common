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
        @'
namespace FailingTests;

[TestClass]
public sealed class UnitTest1
{
    [TestMethod]
    public void IntentionalFailureProducesEvidence() => Assert.AreEqual(1, 2);
}
'@ | Set-Content -LiteralPath $testFile -Encoding UTF8
    }
    & dotnet test (Join-Path $ProjectDirectory "$(Split-Path $ProjectDirectory -Leaf).csproj") `
        --logger 'trx;LogFileName=input.trx' --results-directory $ResultsDirectory --nologo 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    Assert-Contract (($exitCode -eq 0) -eq $ShouldPass) "Real dotnet test exit did not match ShouldPass=$ShouldPass."
    $trx = Join-Path $ResultsDirectory 'input.trx'
    Assert-Contract (Test-Path -LiteralPath $trx -PathType Leaf) 'Real dotnet test did not produce input.trx.'
    return $trx
}

function Invoke-RealSkippedXunitRun {
    param([string]$ProjectDirectory, [string]$ResultsDirectory)

    New-Item -ItemType Directory -Force -Path $ProjectDirectory, $ResultsDirectory | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $ProjectDirectory 'SkippedTests.csproj') -Encoding UTF8
    @'
using Xunit;
public sealed class SkippedTests
{
    [Fact(Skip = "intentional receipt fixture")]
    public void SkippedResultMustBeCounted() { }
}
'@ | Set-Content -LiteralPath (Join-Path $ProjectDirectory 'SkippedTests.cs') -Encoding UTF8
    & dotnet test (Join-Path $ProjectDirectory 'SkippedTests.csproj') `
        --logger 'trx;LogFileName=input.trx' --results-directory $ResultsDirectory --nologo 2>&1 | Out-Null
    Assert-Contract ($LASTEXITCODE -eq 0) 'Real skipped xUnit test run failed unexpectedly.'
    return (Join-Path $ResultsDirectory 'input.trx')
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

    $skippedInput = Invoke-RealSkippedXunitRun -ProjectDirectory (Join-Path $sandbox 'SkippedTests') -ResultsDirectory (Join-Path $sandbox 'skipped-input')
    $skippedReceipt = Publish-LocalCiTrxEvidence -TrxPath $skippedInput -DestinationDirectory $durable -ProjectName 'SkippedTests'
    $skippedSummary = Get-LocalCiTrxSummary -TrxPath $skippedReceipt
    Assert-Contract ($skippedSummary.Passed -eq 0 -and $skippedSummary.Failed -eq 0 -and $skippedSummary.Skipped -eq 1) 'Saved xUnit skipped outcome is not counted truthfully.'

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
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if ($resolvedSandbox.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedSandbox -Leaf).StartsWith('local-ci-receipts-contract-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}
