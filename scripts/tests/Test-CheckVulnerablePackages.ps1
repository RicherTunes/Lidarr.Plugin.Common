#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for check-vulnerable-packages.ps1 (dependency-CVE gate, audit A-5).

.DESCRIPTION
    Dot-sources the checker's pure verdict function via -DefineFunctionsOnly and
    exercises it against canned `dotnet list package --vulnerable --format json`
    reports:

      - Clean report (no vulnerable packages)            -> pass
      - High-severity top-level vulnerability            -> fail at default threshold
      - Moderate-severity only, default threshold (High) -> pass
      - Moderate-severity only, -FailOnSeverity Moderate -> fail
      - High-severity TRANSITIVE vulnerability           -> fail (transitives count)
      - Unknown/garbage severity string                  -> fail (fail-closed)
      - Malformed JSON                                   -> fail (fail-closed)

    No network, no dotnet invocation — verdict logic only.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$Checker   = Join-Path $RepoRoot 'scripts/ci/check-vulnerable-packages.ps1'

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'Vulnerable-Packages Gate Self-Tests' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $Checker)) {
    Write-Host "FATAL: checker script not found: $Checker" -ForegroundColor Red
    Write-Host '  (Run this test AFTER implementing check-vulnerable-packages.ps1)' -ForegroundColor Yellow
    exit 1
}

. $Checker -DefineFunctionsOnly

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
            Write-Host ' PASS' -ForegroundColor Green
            $script:passed++
        }
        else {
            Write-Host ' FAIL' -ForegroundColor Red
            $script:failed++
        }
    }
    catch {
        Write-Host " FAIL ($($_.Exception.Message))" -ForegroundColor Red
        $script:failed++
    }
}

function New-Report {
    param(
        [array]$TopLevel = @(),
        [array]$Transitive = @()
    )
    @{
        version  = 1
        projects = @(
            @{
                path       = 'src/Fake.csproj'
                frameworks = @(
                    @{
                        framework          = 'net8.0'
                        topLevelPackages   = $TopLevel
                        transitivePackages = $Transitive
                    }
                )
            }
        )
    } | ConvertTo-Json -Depth 10
}

function New-VulnPackage {
    param(
        [string]$Id,
        [string]$Severity
    )
    @{
        id              = $Id
        resolvedVersion = '1.0.0'
        vulnerabilities = @(
            @{
                severity      = $Severity
                advisoryurl   = 'https://github.com/advisories/GHSA-fake'
            }
        )
    }
}

# ---- Clean report without frameworks property (real dotnet output shape) ----
Test-Assertion 'clean report where projects omit the frameworks property passes' {
    # When nothing is vulnerable, `dotnet list package --vulnerable --format json`
    # emits projects WITHOUT a frameworks property at all.
    $reportJson = @{ version = 1; projects = @(@{ path = 'src/Fake.csproj' }) } | ConvertTo-Json -Depth 5
    $verdict = Get-VulnerabilityVerdict -ReportJson $reportJson -FailOnSeverity 'High'
    (-not $verdict.ShouldFail) -and ($verdict.Findings.Count -eq 0)
}

# ---- Clean report ----------------------------------------------------------
Test-Assertion 'clean report passes' {
    $verdict = Get-VulnerabilityVerdict -ReportJson (New-Report) -FailOnSeverity 'High'
    (-not $verdict.ShouldFail) -and ($verdict.Findings.Count -eq 0)
}

# ---- High top-level fails at default threshold ------------------------------
Test-Assertion 'High top-level vulnerability fails at High threshold' {
    $report = New-Report -TopLevel @(New-VulnPackage -Id 'Bad.Package' -Severity 'High')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'High'
    $verdict.ShouldFail -and ($verdict.Findings.Count -eq 1) -and ($verdict.Findings[0].PackageId -eq 'Bad.Package')
}

# ---- Critical also fails at High threshold ----------------------------------
Test-Assertion 'Critical vulnerability fails at High threshold' {
    $report = New-Report -TopLevel @(New-VulnPackage -Id 'Worse.Package' -Severity 'Critical')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'High'
    $verdict.ShouldFail
}

# ---- Moderate passes at High threshold but is still reported ----------------
Test-Assertion 'Moderate-only report passes at High threshold (but is surfaced)' {
    $report = New-Report -TopLevel @(New-VulnPackage -Id 'Meh.Package' -Severity 'Moderate')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'High'
    (-not $verdict.ShouldFail) -and ($verdict.Findings.Count -eq 1)
}

# ---- Moderate fails when the threshold is lowered ----------------------------
Test-Assertion 'Moderate fails at Moderate threshold' {
    $report = New-Report -TopLevel @(New-VulnPackage -Id 'Meh.Package' -Severity 'Moderate')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'Moderate'
    $verdict.ShouldFail
}

# ---- Transitive vulnerabilities count ---------------------------------------
Test-Assertion 'High TRANSITIVE vulnerability fails (transitives count)' {
    $report = New-Report -Transitive @(New-VulnPackage -Id 'Sneaky.Transitive' -Severity 'High')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'High'
    $verdict.ShouldFail -and ($verdict.Findings[0].PackageId -eq 'Sneaky.Transitive')
}

# ---- Unknown severity fails closed ------------------------------------------
Test-Assertion 'unknown severity string fails closed' {
    $report = New-Report -TopLevel @(New-VulnPackage -Id 'Weird.Package' -Severity 'Bananas')
    $verdict = Get-VulnerabilityVerdict -ReportJson $report -FailOnSeverity 'High'
    $verdict.ShouldFail
}

# ---- Malformed JSON fails closed ---------------------------------------------
Test-Assertion 'malformed JSON fails closed' {
    $threw = $false
    try {
        Get-VulnerabilityVerdict -ReportJson '{"not":"a report' -FailOnSeverity 'High' | Out-Null
    }
    catch {
        $threw = $true
    }
    $threw
}

# ---- Summary -----------------------------------------------------------------
Write-Host ''
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
if ($failed -gt 0) { exit 1 }
exit 0
