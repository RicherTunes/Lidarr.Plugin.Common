#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Dependency-CVE gate (audit A-5): fails CI when NuGet packages carry known
    vulnerabilities at or above a severity threshold.

.DESCRIPTION
    Runs `dotnet list <target> package --vulnerable --include-transitive --format json`
    and fails (exit 1) when any direct OR transitive package carries a vulnerability
    whose severity is >= -FailOnSeverity (default: High). Findings below the threshold
    are printed as warnings so they stay visible without blocking.

    Fail-closed: unparseable output, unknown severity strings, or a failing
    `dotnet list` invocation all fail the gate rather than passing silently.

    Owned by Lidarr.Plugin.Common; plugins invoke it from their pinned submodule:
      pwsh ext/Lidarr.Plugin.Common/scripts/ci/check-vulnerable-packages.ps1 -Target <sln|csproj>

.PARAMETER Target
    Path to the solution or project to scan. Required unless -DefineFunctionsOnly.

.PARAMETER FailOnSeverity
    Minimum severity that fails the gate: Low | Moderate | High | Critical.
    Default: High.

.PARAMETER SkipRestore
    Skip the `dotnet restore` preflight (use when the CI job has already restored).

.PARAMETER DefineFunctionsOnly
    Dot-source mode for self-tests: define the pure verdict function and return.
#>
[CmdletBinding()]
param(
    [string]$Target,
    [ValidateSet('Low', 'Moderate', 'High', 'Critical')]
    [string]$FailOnSeverity = 'High',
    [switch]$SkipRestore,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SeverityRank {
    param([string]$Severity)
    switch (($Severity ?? '').Trim().ToLowerInvariant()) {
        'low'      { return 1 }
        'moderate' { return 2 }
        'high'     { return 3 }
        'critical' { return 4 }
        # Fail-closed: an unrecognized severity is treated as above every
        # threshold so a renamed/new advisory level can never slip through.
        default    { return 99 }
    }
}

function Get-VulnerabilityVerdict {
    <#
    .SYNOPSIS
        Pure verdict over a `dotnet list package --vulnerable --format json` report.
    .OUTPUTS
        pscustomobject with:
          Findings   – every vulnerable package (any severity), with PackageId,
                       Severity, Scope (TopLevel/Transitive), Project, AdvisoryUrl
          ShouldFail – $true when any finding is >= the threshold
    #>
    param(
        [Parameter(Mandatory)][string]$ReportJson,
        [Parameter(Mandatory)][string]$FailOnSeverity
    )

    $report = $ReportJson | ConvertFrom-Json  # throws on malformed input (fail-closed)
    $threshold = Get-SeverityRank $FailOnSeverity
    $findings = [System.Collections.Generic.List[object]]::new()

    $projects = if ($report.PSObject.Properties['projects']) { @($report.projects) } else { @() }
    foreach ($project in $projects) {
        # A project with no vulnerable packages omits the frameworks property entirely.
        $frameworks = if ($project.PSObject.Properties['frameworks']) { @($project.frameworks) } else { @() }
        foreach ($framework in $frameworks) {
            foreach ($scope in @('topLevelPackages', 'transitivePackages')) {
                $packages = if ($framework.PSObject.Properties[$scope]) { @($framework.$scope) } else { @() }
                foreach ($package in $packages) {
                    foreach ($vuln in @($package.vulnerabilities ?? @())) {
                        $findings.Add([pscustomobject]@{
                            PackageId   = $package.id
                            Version     = $package.resolvedVersion
                            Severity    = $vuln.severity
                            Scope       = if ($scope -eq 'topLevelPackages') { 'TopLevel' } else { 'Transitive' }
                            Project     = $project.path
                            AdvisoryUrl = $vuln.advisoryurl
                        })
                    }
                }
            }
        }
    }

    [pscustomobject]@{
        Findings   = $findings
        ShouldFail = [bool]($findings | Where-Object { (Get-SeverityRank $_.Severity) -ge $threshold })
    }
}

if ($DefineFunctionsOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($Target)) {
    Write-Host 'ERROR: -Target <sln|csproj> is required.' -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $Target)) {
    Write-Host "ERROR: target not found: $Target" -ForegroundColor Red
    exit 1
}

if (-not $SkipRestore) {
    Write-Host "Restoring $Target ..."
    dotnet restore $Target --verbosity quiet | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'ERROR: dotnet restore failed; cannot evaluate vulnerabilities (failing closed).' -ForegroundColor Red
        exit 1
    }
}

Write-Host "Scanning $Target for vulnerable packages (threshold: $FailOnSeverity) ..."
$rawOutput = dotnet list $Target package --vulnerable --include-transitive --format json 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: dotnet list package --vulnerable failed (failing closed):' -ForegroundColor Red
    $rawOutput | Out-Host
    exit 1
}

try {
    $verdict = Get-VulnerabilityVerdict -ReportJson ($rawOutput -join "`n") -FailOnSeverity $FailOnSeverity
}
catch {
    Write-Host "ERROR: could not parse dotnet list output as JSON (failing closed): $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if ($verdict.Findings.Count -eq 0) {
    Write-Host 'OK: no known-vulnerable packages (direct or transitive).' -ForegroundColor Green
    exit 0
}

$threshold = Get-SeverityRank $FailOnSeverity
foreach ($finding in $verdict.Findings) {
    $blocking = (Get-SeverityRank $finding.Severity) -ge $threshold
    $color = if ($blocking) { 'Red' } else { 'Yellow' }
    $label = if ($blocking) { 'BLOCKING' } else { 'warning' }
    Write-Host ("  [{0}] {1} {2} ({3}, {4}) {5} - {6}" -f $label, $finding.PackageId, $finding.Version, $finding.Severity, $finding.Scope, $finding.Project, $finding.AdvisoryUrl) -ForegroundColor $color
}

if ($verdict.ShouldFail) {
    Write-Host "FAIL: vulnerable packages at or above '$FailOnSeverity' severity. Update the packages above (or raise a justified allowlist discussion) before merging." -ForegroundColor Red
    exit 1
}

Write-Host "OK: findings exist but all are below the '$FailOnSeverity' threshold." -ForegroundColor Yellow
exit 0
