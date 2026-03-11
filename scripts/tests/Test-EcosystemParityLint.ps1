#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for ecosystem-parity-lint.ps1 - verifies it catches seeded structural violations.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lintScript = Join-Path $PSScriptRoot '../ecosystem-parity-lint.ps1'
$testRoot = $null
$failed = 0
$passed = 0

function New-TestRepo {
    param([string]$Root, [string]$Name)
    $repoPath = Join-Path $Root $Name
    New-Item -ItemType Directory -Path $repoPath -Force | Out-Null
    return $repoPath
}

function Add-TestFile {
    param([string]$RepoPath, [string]$RelPath, [string]$Content)
    $fullPath = Join-Path $RepoPath $RelPath
    $dir = Split-Path $fullPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $fullPath -Value $Content -NoNewline
}

function Assert-ExitCode {
    param([int]$Expected, [int]$Actual, [string]$TestName)
    if ($Actual -eq $Expected) {
        Write-Host "  [PASS] $TestName (exit=$Actual)" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $TestName — expected exit=$Expected, got exit=$Actual" -ForegroundColor Red
        $script:failed++
    }
}

function Assert-OutputContains {
    param([object[]]$Output, [string]$Pattern, [string]$TestName)
    $joined = ($Output | ForEach-Object { "$_" }) -join "`n"
    if ($joined -match $Pattern) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $TestName — output did not contain '$Pattern'" -ForegroundColor Red
        $script:failed++
    }
}

function New-GoldenRepo {
    <#
    .SYNOPSIS
        Creates a minimal repo that passes all parity checks.
    #>
    param([string]$Root, [string]$Name)
    $repo = New-TestRepo -Root $Root -Name $Name

    # global.json
    Add-TestFile -RepoPath $repo -RelPath 'global.json' -Content @'
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
'@

    # VERSION
    Add-TestFile -RepoPath $repo -RelPath 'VERSION' -Content '1.0.0'

    # Directory.Build.props (full template)
    Add-TestFile -RepoPath $repo -RelPath 'Directory.Build.props' -Content @'
<Project>
  <PropertyGroup>
    <ILRepackEnabled>false</ILRepackEnabled>
  </PropertyGroup>
  <PropertyGroup>
    <VersionFromFile Condition="'$(VersionFromFile)' == '' And Exists('$(MSBuildThisFileDirectory)VERSION')">$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())</VersionFromFile>
    <Version Condition="'$(Version)' == '' And '$(VersionFromFile)' != ''">$(VersionFromFile)</Version>
  </PropertyGroup>
  <PropertyGroup>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <PropertyGroup>
    <NoWarn>$(NoWarn);SA1200</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="All" />
  </ItemGroup>
  <PropertyGroup Condition="$(MSBuildProjectDirectory.Contains('ext\Lidarr'))">
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
'@

    # Directory.Packages.props
    Add-TestFile -RepoPath $repo -RelPath 'Directory.Packages.props' -Content @'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
'@

    # .gitleaks.toml
    Add-TestFile -RepoPath $repo -RelPath '.gitleaks.toml' -Content @'
title = "Test Security Configuration"
[extend]
useDefault = true
'@

    # .markdownlint.yaml
    Add-TestFile -RepoPath $repo -RelPath '.markdownlint.yaml' -Content 'default: true'

    # .github files
    Add-TestFile -RepoPath $repo -RelPath '.github/CODEOWNERS' -Content '* @testowner'
    Add-TestFile -RepoPath $repo -RelPath '.github/dependabot.yml' -Content 'version: 2'
    Add-TestFile -RepoPath $repo -RelPath '.github/sha-pin-allowlist.json' -Content '{"entries":[]}'
    Add-TestFile -RepoPath $repo -RelPath '.github/ISSUE_TEMPLATE/bug_report.yml' -Content 'name: Bug Report'
    Add-TestFile -RepoPath $repo -RelPath '.github/ISSUE_TEMPLATE/feature_request.yml' -Content 'name: Feature Request'

    # Workflows
    foreach ($wf in @('codeql.yml', 'test-and-coverage.yml', 'notify-failure.yml', 'packaging-gates.yml', 'submodule-pin.yml')) {
        Add-TestFile -RepoPath $repo -RelPath ".github/workflows/$wf" -Content "name: $wf"
    }

    return $repo
}

try {
    Write-Host "=================================================" -ForegroundColor Cyan
    Write-Host "Test-EcosystemParityLint: Seeded Violation Tests" -ForegroundColor Cyan
    Write-Host "=================================================" -ForegroundColor Cyan

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "eco-parity-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    # ─── Test 1: Golden repo passes all checks ───
    Write-Host "`n[TEST 1] Golden repo passes all checks..." -ForegroundColor Cyan
    $golden = New-GoldenRepo -Root $testRoot -Name 'golden'
    $result = & $lintScript -RepoPath $golden -Mode ci *>&1
    Assert-ExitCode -Expected 0 -Actual $LASTEXITCODE -TestName "Golden repo passes"

    # ─── Test 2: Missing global.json detected ───
    Write-Host "`n[TEST 2] Missing global.json detected..." -ForegroundColor Cyan
    $repo2 = New-GoldenRepo -Root $testRoot -Name 'no-globaljson'
    Remove-Item (Join-Path $repo2 'global.json') -Force
    $result = & $lintScript -RepoPath $repo2 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing global.json fails"
    Assert-OutputContains -Output $result -Pattern 'global\.json' -TestName "Output mentions global.json"

    # ─── Test 3: Missing Directory.Packages.props detected ───
    Write-Host "`n[TEST 3] Missing Directory.Packages.props detected..." -ForegroundColor Cyan
    $repo3 = New-GoldenRepo -Root $testRoot -Name 'no-dpp'
    Remove-Item (Join-Path $repo3 'Directory.Packages.props') -Force
    $result = & $lintScript -RepoPath $repo3 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing Directory.Packages.props fails"

    # ─── Test 4: Missing workflow detected ───
    Write-Host "`n[TEST 4] Missing workflow detected..." -ForegroundColor Cyan
    $repo4 = New-GoldenRepo -Root $testRoot -Name 'no-codeql'
    Remove-Item (Join-Path $repo4 '.github/workflows/codeql.yml') -Force
    $result = & $lintScript -RepoPath $repo4 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing codeql.yml fails"
    Assert-OutputContains -Output $result -Pattern 'codeql\.yml' -TestName "Output mentions codeql.yml"

    # ─── Test 5: Missing issue templates detected ───
    Write-Host "`n[TEST 5] Missing issue templates detected..." -ForegroundColor Cyan
    $repo5 = New-GoldenRepo -Root $testRoot -Name 'no-templates'
    Remove-Item (Join-Path $repo5 '.github/ISSUE_TEMPLATE') -Recurse -Force
    $result = & $lintScript -RepoPath $repo5 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing ISSUE_TEMPLATE fails"

    # ─── Test 6: Missing .gitleaks.toml detected ───
    Write-Host "`n[TEST 6] Missing .gitleaks.toml detected..." -ForegroundColor Cyan
    $repo6 = New-GoldenRepo -Root $testRoot -Name 'no-gitleaks'
    Remove-Item (Join-Path $repo6 '.gitleaks.toml') -Force
    $result = & $lintScript -RepoPath $repo6 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing .gitleaks.toml fails"

    # ─── Test 7: Bad global.json SDK version detected ───
    Write-Host "`n[TEST 7] Bad global.json SDK version detected..." -ForegroundColor Cyan
    $repo7 = New-GoldenRepo -Root $testRoot -Name 'bad-sdk'
    Add-TestFile -RepoPath $repo7 -RelPath 'global.json' -Content @'
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestMajor",
    "allowPrerelease": false
  }
}
'@
    $result = & $lintScript -RepoPath $repo7 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Bad SDK version fails"
    Assert-OutputContains -Output $result -Pattern '8\.0\.100' -TestName "Output mentions expected version"

    # ─── Test 8: Missing CODEOWNERS detected ───
    Write-Host "`n[TEST 8] Missing CODEOWNERS detected..." -ForegroundColor Cyan
    $repo8 = New-GoldenRepo -Root $testRoot -Name 'no-codeowners'
    Remove-Item (Join-Path $repo8 '.github/CODEOWNERS') -Force
    $result = & $lintScript -RepoPath $repo8 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Missing CODEOWNERS fails"

    # ─── Test 9: Missing multiple files aggregates violations ───
    Write-Host "`n[TEST 9] Multiple missing files aggregated..." -ForegroundColor Cyan
    $repo9 = New-GoldenRepo -Root $testRoot -Name 'multi-missing'
    Remove-Item (Join-Path $repo9 'global.json') -Force
    Remove-Item (Join-Path $repo9 '.gitleaks.toml') -Force
    Remove-Item (Join-Path $repo9 '.github/CODEOWNERS') -Force
    $result = & $lintScript -RepoPath $repo9 -Mode ci *>&1
    Assert-ExitCode -Expected 1 -Actual $LASTEXITCODE -TestName "Multiple missing files fail"
    # Should show 3+ violations
    $violationLines = @($result | ForEach-Object { "$_" } | Where-Object { $_ -match '\[X\]' })
    if ($violationLines.Count -ge 3) {
        Write-Host "  [PASS] Multiple violations reported ($($violationLines.Count))" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] Expected 3+ violations, got $($violationLines.Count)" -ForegroundColor Red
        $script:failed++
    }

    # ─── Test 10: Interactive mode returns 0 with warnings ───
    Write-Host "`n[TEST 10] Interactive mode is non-blocking..." -ForegroundColor Cyan
    $repo10 = New-GoldenRepo -Root $testRoot -Name 'interactive'
    Remove-Item (Join-Path $repo10 '.gitleaks.toml') -Force
    $result = & $lintScript -RepoPath $repo10 -Mode interactive *>&1
    Assert-ExitCode -Expected 0 -Actual $LASTEXITCODE -TestName "Interactive mode non-blocking"

    # ═══════════════════════════════════════════════════
    Write-Host "`n=================================================" -ForegroundColor Cyan
    $total = $script:passed + $script:failed
    Write-Host "Results: $($script:passed)/$total passed, $($script:failed) failed" -ForegroundColor $(if ($script:failed -gt 0) { 'Red' } else { 'Green' })
    Write-Host "=================================================" -ForegroundColor Cyan

    if ($script:failed -gt 0) {
        Write-Host "FAILED: $($script:failed) test(s) failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: Test-EcosystemParityLint (all tests passed)" -ForegroundColor Green
    exit 0
}
finally {
    if ($testRoot -and (Test-Path $testRoot)) {
        Write-Host "`nCleaning up..." -ForegroundColor Gray
        Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
    }
}
