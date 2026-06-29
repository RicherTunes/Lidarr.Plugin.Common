#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for the Gitea secret-scan workflow guard.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lintScript = Join-Path $PSScriptRoot '..' 'lint-gitea-secret-scan.ps1'
$testRoot = $null
$passed = 0
$failed = 0

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Details = ''
    )

    if ($Condition) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
        return
    }

    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    if ($Details) {
        Write-Host "         $Details" -ForegroundColor DarkGray
    }
    $script:failed++
}

function New-FixtureRepo {
    param(
        [string]$Root,
        [string]$Name,
        [string]$Workflow
    )

    $repo = Join-Path $Root $Name
    $workflowDir = Join-Path $repo '.gitea/workflows'
    New-Item -ItemType Directory -Path $workflowDir -Force | Out-Null
    Set-Content -Path (Join-Path $workflowDir 'ci.yml') -Value $Workflow -NoNewline
    return $repo
}

function Invoke-Lint {
    param([string]$RepoPath)

    $output = & pwsh -NoProfile -File $lintScript -RepoPath $RepoPath -CI *>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { "$_" }) -join "`n"
    }
}

try {
    Write-Host '=================================================' -ForegroundColor Cyan
    Write-Host 'Test-GiteaSecretScanLint' -ForegroundColor Cyan
    Write-Host '=================================================' -ForegroundColor Cyan

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "gitea-secret-scan-lint-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    $validWorkflow = @'
name: CI
on:
  pull_request:
jobs:
  secret-scan:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Gitleaks secret scan
        run: |
          echo "expected  /tmp/gitleaks.tgz" | sha256sum -c -
          /tmp/gitleaks detect --source . --no-banner --redact --exit-code 1
  lint:
    runs-on: ubuntu-latest
    steps:
      - run: echo lint
  verify:
    needs:
      - lint
      - secret-scan
    runs-on: ubuntu-latest
    steps:
      - run: echo verify
'@

    Write-Host "`n[TEST 1] Valid workflow passes..." -ForegroundColor Cyan
    $repo = New-FixtureRepo -Root $testRoot -Name 'valid' -Workflow $validWorkflow
    $result = Invoke-Lint -RepoPath $repo
    Assert-True 'Valid secret-scan workflow exits 0' ($result.ExitCode -eq 0) $result.Output

    Write-Host "`n[TEST 2] Gitleaks outside secret-scan fails..." -ForegroundColor Cyan
    $misplacedWorkflow = @'
name: CI
on:
  pull_request:
jobs:
  secret-scan:
    runs-on: ubuntu-latest
    steps:
      - run: echo no-op
  lint:
    runs-on: ubuntu-latest
    steps:
      - run: |
          sha256sum -c -
          /tmp/gitleaks detect --source . --no-banner --redact --exit-code 1
  verify:
    needs: [lint, secret-scan]
    runs-on: ubuntu-latest
    steps:
      - run: echo verify
'@
    $repo = New-FixtureRepo -Root $testRoot -Name 'misplaced' -Workflow $misplacedWorkflow
    $result = Invoke-Lint -RepoPath $repo
    Assert-True 'Misplaced gitleaks command exits non-zero' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Misplaced gitleaks output names secret-scan job' ($result.Output -match 'secret-scan') $result.Output

    Write-Host "`n[TEST 3] verify without secret-scan dependency fails..." -ForegroundColor Cyan
    $missingNeedsWorkflow = $validWorkflow -replace '\s+secret-scan\r?\n', "`n"
    $repo = New-FixtureRepo -Root $testRoot -Name 'missing-needs' -Workflow $missingNeedsWorkflow
    $result = Invoke-Lint -RepoPath $repo
    Assert-True 'Missing verify secret-scan need exits non-zero' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Missing need output names verify needs' ($result.Output -match 'verify.*needs') $result.Output

    $total = $passed + $failed
    Write-Host "`nResults: $passed/$total passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
    Write-Host '=================================================' -ForegroundColor Cyan

    if ($failed -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    if ($testRoot -and (Test-Path $testRoot)) {
        Remove-Item -Path $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
