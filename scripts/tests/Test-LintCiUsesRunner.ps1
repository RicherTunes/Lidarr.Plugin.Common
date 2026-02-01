#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for lint-ci-uses-runner.ps1 script.

.DESCRIPTION
    Validates the lint script correctly detects raw dotnet test calls
    and respects allowlist patterns. Prevents regressions that could
    cause false positives/negatives in plugin CI.

    Tests cover:
    - Detection of raw dotnet test calls
    - Allowlist pattern matching
    - Expiration handling
    - Line pattern matching
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$LintScript = Join-Path $RepoRoot "scripts/lint-ci-uses-runner.ps1"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Lint CI Uses Runner Self-Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Create temporary test fixtures
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-test-$(Get-Random)"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
$FixturesDir = Join-Path $TempDir ".github/workflows"
New-Item -ItemType Directory -Path $FixturesDir -Force | Out-Null

$passed = 0
$failed = 0

function Test-Assertion {
    param(
        [string]$Name,
        [scriptblock]$Test
    )

    Write-Host "  Testing: $Name..." -NoNewline

    try {
        $result = & $Test
        if ($result) {
            Write-Host " PASS" -ForegroundColor Green
            $script:passed++
            return $true
        } else {
            Write-Host " FAIL" -ForegroundColor Red
            $script:failed++
            return $false
        }
    }
    catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
        return $false
    }
}

try {
    Write-Host "Creating test fixtures..." -ForegroundColor DarkGray

    # Fixture 1: Workflow with raw dotnet test (violation)
    $violationYml = @"
name: Test
on: push
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Test
        run: dotnet test --configuration Release
"@
    Set-Content -Path (Join-Path $FixturesDir "violation.yml") -Value $violationYml

    # Fixture 2: Workflow using unified runner (compliant)
    $compliantYml = @"
name: Test
on: push
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Test
        shell: pwsh
        run: |
          `$script = "ext/lidarr.plugin.common/scripts/test.ps1"
          & `$script -CI
"@
    Set-Content -Path (Join-Path $FixturesDir "compliant.yml") -Value $compliantYml

    # Fixture 3: Workflow with dotnet test --list-tests (should be allowlisted)
    $listTestsYml = @"
name: Test
on: push
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: List tests
        run: dotnet test --list-tests
"@
    Set-Content -Path (Join-Path $FixturesDir "list-tests.yml") -Value $listTestsYml

    # Fixture 4: Mixed workflow (one compliant, one violation)
    $mixedYml = @"
name: Test
on: push
jobs:
  unit:
    runs-on: ubuntu-latest
    steps:
      - name: Test
        shell: pwsh
        run: |
          `$script = "ext/lidarr.plugin.common/scripts/test.ps1"
          & `$script -CI
  integration:
    runs-on: ubuntu-latest
    steps:
      - name: Test
        run: dotnet test --filter Category=Integration
"@
    Set-Content -Path (Join-Path $FixturesDir "mixed.yml") -Value $mixedYml

    Write-Host "Detection Tests:" -ForegroundColor White

    # Test 1: Detects violation
    Test-Assertion "Detects raw dotnet test" {
        $result = & $LintScript -Path $TempDir 2>&1
        $exitCode = $LASTEXITCODE
        # Script exits 0 in report mode, check output
        ($result -join "`n") -match "violation\.yml"
    }

    # Test 2: Compliant workflow passes
    Test-Assertion "Compliant workflow not flagged" {
        $result = & $LintScript -Path $TempDir 2>&1
        -not (($result -join "`n") -match "compliant\.yml.*violation")
    }

    # Test 3: --list-tests pattern recognized
    Test-Assertion "--list-tests recognized in output" {
        $result = & $LintScript -Path $TempDir 2>&1
        $output = $result -join "`n"
        # The list-tests.yml has dotnet test but with --list-tests
        # Should be flagged but can be allowlisted
        $output -match "list-tests\.yml"
    }

    Write-Host ""
    Write-Host "Allowlist Tests:" -ForegroundColor White

    # Create allowlist with various patterns
    $allowlist = @{
        patterns = @(
            @{ file = "*.yml"; line_pattern = "--list-tests"; reason = "Test discovery" }
            @{ file = "violation.yml"; reason = "Testing only" }
        )
    }
    Set-Content -Path (Join-Path $TempDir ".github/test-runner-allowlist.json") -Value ($allowlist | ConvertTo-Json -Depth 5)

    # Test 4: Allowlist file pattern works
    Test-Assertion "File pattern allowlist works" {
        $result = & $LintScript -Path $TempDir 2>&1
        $output = $result -join "`n"
        # violation.yml should now be allowlisted
        -not ($output -match "violation\.yml.*:.*dotnet test")
    }

    # Test 5: Line pattern allowlist works
    Test-Assertion "Line pattern allowlist works" {
        $result = & $LintScript -Path $TempDir 2>&1
        $output = $result -join "`n"
        # list-tests.yml should be allowlisted by --list-tests pattern
        -not ($output -match "list-tests\.yml.*violation")
    }

    Write-Host ""
    Write-Host "Expiration Tests:" -ForegroundColor White

    # Create allowlist with expired entry
    $yesterday = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd')
    $tomorrow = (Get-Date).AddDays(1).ToString('yyyy-MM-dd')

    $allowlistExpired = @{
        patterns = @(
            @{ file = "mixed.yml"; expiresOn = $yesterday; owner = "test"; reason = "Expired" }
        )
    }
    Set-Content -Path (Join-Path $TempDir ".github/test-runner-allowlist.json") -Value ($allowlistExpired | ConvertTo-Json -Depth 5)

    # Test 6: Expired allowlist entry is not honored
    Test-Assertion "Expired allowlist entry not honored" {
        $result = & $LintScript -Path $TempDir 2>&1
        $output = $result -join "`n"
        # mixed.yml has an expired exemption, should be flagged
        $output -match "mixed\.yml"
    }

    # Create allowlist with valid expiration
    $allowlistValid = @{
        patterns = @(
            @{ file = "mixed.yml"; expiresOn = $tomorrow; owner = "test"; reason = "Valid" }
        )
    }
    Set-Content -Path (Join-Path $TempDir ".github/test-runner-allowlist.json") -Value ($allowlistValid | ConvertTo-Json -Depth 5)

    # Test 7: Valid expiration is honored
    Test-Assertion "Valid expiration allowlist honored" {
        $result = & $LintScript -Path $TempDir 2>&1
        $output = $result -join "`n"
        # mixed.yml should be allowlisted
        -not ($output -match "mixed\.yml.*violation")
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
    Write-Host "========================================" -ForegroundColor Cyan

}
finally {
    # Cleanup
    if (Test-Path $TempDir) {
        Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failed -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "[OK] All lint script tests passed." -ForegroundColor Green
exit 0
