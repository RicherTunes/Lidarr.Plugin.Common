#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Runs local, secret-free verification across the plugin ecosystem.

.DESCRIPTION
  Intended to keep the merge train moving when CI is blocked (billing, quotas, etc.).
  Produces a JSON report plus a concise console summary.

  This script is best-effort for optional checks:
  - Missing repos are recorded as "missing" (non-fatal in plan mode).
  - Missing optional scripts are recorded as "skipped".

.PARAMETER WorkspaceRoot
  Path containing sibling repos (brainarr/, qobuzarr/, tidalarr/, applemusicarr/, lidarr.plugin.common/).
  Defaults to the parent of this repo (../).

.PARAMETER Mode
  - plan: do not execute commands, only emit the planned checks.
  - quick: run lightweight checks (git status, manifest validation, build).
  - full: run quick + tests where feasible.

.PARAMETER Repos
  Repo folder names to verify. Defaults to the expected ecosystem set.

.PARAMETER OutDir
  Directory where the report and logs are written.

.PARAMETER FailFast
  Stop at the first failed check.
#>

[CmdletBinding()]
param(
  [string]$WorkspaceRoot,
  [ValidateSet('plan', 'quick', 'full')]
  [string]$Mode = 'quick',
  [string[]]$Repos = @('lidarr.plugin.common', 'qobuzarr', 'tidalarr', 'brainarr', 'applemusicarr'),
  [string]$OutDir = 'artifacts/merge-train',
  [switch]$FailFast
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
  return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-DefaultWorkspaceRoot {
  $repoRoot = Get-RepoRoot
  return (Resolve-Path (Join-Path $repoRoot '..')).Path
}

function New-CheckPlan {
  param(
    [Parameter(Mandatory = $true)][string]$repoName,
    [Parameter(Mandatory = $true)][string]$repoPath,
    [Parameter(Mandatory = $true)][string]$name,
    [Parameter(Mandatory = $true)][string]$fileName,
    [Parameter(Mandatory = $true)][string[]]$args
  )

  return [pscustomobject]@{
    repoName = $repoName
    repoPath = $repoPath
    name = $name
    fileName = $fileName
    args = $args
  }
}

function Find-FirstSolutionPath {
  param([Parameter(Mandatory = $true)][string]$repoPath)

  $sln = Get-ChildItem -Path $repoPath -Filter *.sln -File -ErrorAction SilentlyContinue |
    Sort-Object FullName |
    Select-Object -First 1
  if ($sln) { return $sln.FullName }
  return $null
}

function Get-RepoChecks {
  param(
    [Parameter(Mandatory = $true)][string]$repoName,
    [Parameter(Mandatory = $true)][string]$repoPath,
    [Parameter(Mandatory = $true)][string]$mode
  )

  $checks = New-Object System.Collections.Generic.List[object]

  $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'git:head' -fileName 'git' -args @('rev-parse', 'HEAD')))
  $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'git:status' -fileName 'git' -args @('status', '--porcelain')))

  $manifestValidate = Join-Path $repoPath 'scripts/manifest-validate.ps1'
  if (Test-Path -LiteralPath $manifestValidate) {
    $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'scripts:manifest-validate' -fileName 'pwsh' -args @('-NoProfile', '-File', $manifestValidate)))
  } else {
    $checks.Add([pscustomobject]@{
      repoName = $repoName
      repoPath = $repoPath
      name = 'scripts:manifest-validate'
      skipped = $true
      reason = 'script not found'
    })
  }

  $versionSync = Join-Path $repoPath 'scripts/version-sync.ps1'
  if (Test-Path -LiteralPath $versionSync) {
    $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'scripts:version-sync' -fileName 'pwsh' -args @('-NoProfile', '-File', $versionSync)))
  }

  $entrypointsValidate = Join-Path $repoPath 'scripts/entrypoints-validate.ps1'
  if (Test-Path -LiteralPath $entrypointsValidate) {
    $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'scripts:entrypoints-validate' -fileName 'pwsh' -args @('-NoProfile', '-File', $entrypointsValidate, '-Configuration', 'Release', '-SkipBuild')))
  }

    $slnPath = Find-FirstSolutionPath -repoPath $repoPath
    if ($slnPath) {
      if ($mode -in @('quick', 'full')) {
      $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:build' -fileName 'dotnet' -args @('build', $slnPath, '-c', 'Release', '-m:1', '-p:BuildInParallel=false', '--disable-build-servers', '--nologo')))
      }
      if ($mode -eq 'full') {
      $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:test' -fileName 'dotnet' -args @('test', $slnPath, '-c', 'Release', '-m:1', '-p:BuildInParallel=false', '--disable-build-servers', '--nologo')))
      }
    }

  return $checks
}

function Invoke-PlannedCheck {
  param(
    [Parameter(Mandatory = $true)]$plan,
    [Parameter(Mandatory = $true)][string]$logsDir
  )

  if ($plan.PSObject.Properties.Name -contains 'skipped' -and $plan.skipped) {
    return [pscustomobject]@{
      repoName = $plan.repoName
      name = $plan.name
      outcome = 'skipped'
      reason = $plan.reason
      durationMs = 0
      exitCode = $null
      stdoutPath = $null
      stderrPath = $null
    }
  }

  $safeName = ($plan.name -replace '[^a-zA-Z0-9_.-]', '_')
  $stdoutPath = Join-Path $logsDir "$($plan.repoName)-$safeName.stdout.log"
  $stderrPath = Join-Path $logsDir "$($plan.repoName)-$safeName.stderr.log"

  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $process = Start-Process -FilePath $plan.fileName -ArgumentList $plan.args -WorkingDirectory $plan.repoPath -NoNewWindow -PassThru -Wait `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
  $stopwatch.Stop()

  $exitCode = $process.ExitCode
  $outcome = if ($exitCode -eq 0) { 'passed' } else { 'failed' }

  return [pscustomobject]@{
    repoName = $plan.repoName
    name = $plan.name
    outcome = $outcome
    durationMs = [int]$stopwatch.ElapsedMilliseconds
    exitCode = $exitCode
    stdoutPath = $stdoutPath
    stderrPath = $stderrPath
  }
}

function New-RunId {
  return [guid]::NewGuid().ToString('N')
}

$workspace = if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) { Get-DefaultWorkspaceRoot } else { (Resolve-Path $WorkspaceRoot).Path }
$repoRoot = Get-RepoRoot

$runId = New-RunId
$startedAt = [DateTimeOffset]::UtcNow.ToString('o')

$outDirFull = if ([System.IO.Path]::IsPathRooted($OutDir)) {
  [System.IO.Path]::GetFullPath($OutDir)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutDir))
}
New-Item -ItemType Directory -Force -Path $outDirFull | Out-Null
$logsDir = Join-Path $outDirFull "logs-$runId"
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

$reportPath = Join-Path $outDirFull "merge-train-$runId.json"

$repoResults = @()
$checkPlans = @()
$modeForChecks = if ($Mode -eq 'plan') { 'quick' } else { $Mode }

foreach ($repoName in $Repos) {
  $repoPath = Join-Path $workspace $repoName
  if (-not (Test-Path -LiteralPath $repoPath)) {
    $repoResults += [pscustomobject]@{
      repoName = $repoName
      repoPath = $repoPath
      status = 'missing'
      checks = @()
    }
    continue
  }

  $resolvedRepoPath = (Resolve-Path -LiteralPath $repoPath).Path
  $checks = Get-RepoChecks -repoName $repoName -repoPath $resolvedRepoPath -mode $modeForChecks
  $checkPlans += $checks

  $repoResults += [pscustomobject]@{
    repoName = $repoName
    repoPath = $resolvedRepoPath
    status = 'present'
    checks = @()
  }
}

if ($Mode -eq 'plan') {
  $report = [pscustomobject]@{
    schemaVersion = '1.0'
    runId = $runId
    startedAt = $startedAt
    mode = $Mode
    plannedMode = $modeForChecks
    workspaceRoot = $workspace
    reportPath = $reportPath
    repos = $repoResults
    plannedChecks = $checkPlans
    results = @()
    summary = [pscustomobject]@{
      passed = 0
      failed = 0
      skipped = 0
    }
  }

  $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $reportPath -Encoding UTF8
  Write-Host "Planned checks written to: $reportPath"
  exit 0
}

$results = @()
$failed = 0
$passed = 0
$skipped = 0

foreach ($plan in $checkPlans) {
  $result = Invoke-PlannedCheck -plan $plan -logsDir $logsDir
  $results += $result

  switch ($result.outcome) {
    'passed' { $passed++ }
    'failed' {
      $failed++
      Write-Host "[FAIL] $($result.repoName) :: $($result.name) (exit=$($result.exitCode))" -ForegroundColor Red
      if ($FailFast) { break }
    }
    'skipped' { $skipped++ }
  }
}

$completedAt = [DateTimeOffset]::UtcNow.ToString('o')
$report = [pscustomobject]@{
  schemaVersion = '1.0'
  runId = $runId
  startedAt = $startedAt
  completedAt = $completedAt
  mode = $Mode
  workspaceRoot = $workspace
  reportPath = $reportPath
  logsDir = $logsDir
  repos = $repoResults
  results = $results
  summary = [pscustomobject]@{
    passed = $passed
    failed = $failed
    skipped = $skipped
  }
}

$report | ConvertTo-Json -Depth 8 | Out-File -FilePath $reportPath -Encoding UTF8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Merge Train Verification Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Mode: $Mode"
Write-Host "Workspace: $workspace"
Write-Host "Report: $reportPath"
Write-Host "Logs: $logsDir"
Write-Host "Passed: $passed  Failed: $failed  Skipped: $skipped"

if ($failed -gt 0) { exit 1 }
exit 0
