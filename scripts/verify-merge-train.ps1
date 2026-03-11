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

.PARAMETER SkipIntegration
  Skip integration tests (tests with 'Integration', 'Live', or 'EndToEnd' in their name).
  Useful for Docker mode where secrets/live services are unavailable.

.PARAMETER SkipPerformance
  Skip timing-sensitive tests (tests with [Trait("Category", "Benchmark")] or [Trait("Category", "Slow")]).
  Useful for Docker/CI mode where wall-clock timing is unreliable.
#>

[CmdletBinding()]
param(
  [string]$WorkspaceRoot,
  [ValidateSet('plan', 'quick', 'full')]
  [string]$Mode = 'quick',
  [string[]]$Repos = @('lidarr.plugin.common', 'qobuzarr', 'tidalarr', 'brainarr', 'applemusicarr'),
  [string]$OutDir = 'artifacts/merge-train',
  [switch]$FailFast,
  [switch]$Docker,
  [switch]$IgnoreWarningsAsErrors,
  [switch]$SkipIntegration,
  [switch]$SkipPerformance
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

function Convert-ToDockerHostPath {
  param([Parameter(Mandatory = $true)][string]$path)
  return ($path -replace '\\', '/')
}

function New-DockerDotNetPlan {
  param(
    [Parameter(Mandatory = $true)][string]$repoName,
    [Parameter(Mandatory = $true)][string]$repoPath,
    [Parameter(Mandatory = $true)][string]$name,
    [Parameter(Mandatory = $true)][string[]]$dotnetArgs,
    [string]$nugetConfigPath,
    [string]$nugetConfigContainerPath,
    [string]$nugetPackagesPath
  )

  $repoPathFull = [System.IO.Path]::GetFullPath($repoPath)
  $mountRepo = (Convert-ToDockerHostPath $repoPathFull) + ':/repo'

  $args = New-Object System.Collections.Generic.List[string]
  foreach ($value in @('run', '--rm', '-w', '/repo', '-v', $mountRepo)) { $args.Add([string]$value) }

  if ($nugetPackagesPath) {
    $nugetPackagesFull = [System.IO.Path]::GetFullPath($nugetPackagesPath)
    $mountPackages = (Convert-ToDockerHostPath $nugetPackagesFull) + ':/nuget'
    foreach ($value in @('-v', $mountPackages, '-e', 'NUGET_PACKAGES=/nuget')) { $args.Add([string]$value) }
  }

  if ($nugetConfigPath) {
    $nugetConfigFull = [System.IO.Path]::GetFullPath($nugetConfigPath)
    $containerPath = if ([string]::IsNullOrWhiteSpace($nugetConfigContainerPath)) { '/repo/NuGet.config' } else { $nugetConfigContainerPath }
    $mountConfig = (Convert-ToDockerHostPath $nugetConfigFull) + ':' + $containerPath + ':ro'
    foreach ($value in @('-v', $mountConfig)) { $args.Add([string]$value) }
  }

  $args.Add('mcr.microsoft.com/dotnet/sdk:8.0')
  $args.Add('dotnet')
  foreach ($value in $dotnetArgs) { $args.Add([string]$value) }

  return (New-CheckPlan -repoName $repoName -repoPath $repoPath -name $name -fileName 'docker' -args $args.ToArray())
}

function Find-FirstSolutionPath {
  param([Parameter(Mandatory = $true)][string]$repoPath)

  $sln = Get-ChildItem -Path $repoPath -Filter *.sln -File -ErrorAction SilentlyContinue |
    Sort-Object FullName |
    Select-Object -First 1
  if ($sln) { return $sln.FullName }
  return $null
}

function Convert-ToContainerRelativePath {
  param(
    [Parameter(Mandatory = $true)][string]$repoPath,
    [Parameter(Mandatory = $true)][string]$path
  )

  $repoFull = [System.IO.Path]::GetFullPath($repoPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
  $pathFull = [System.IO.Path]::GetFullPath($path)

  $repoPrefix = $repoFull + [System.IO.Path]::DirectorySeparatorChar
  if (-not $pathFull.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Cannot compute container-relative path. '$pathFull' is not under repo '$repoFull'."
  }

  $relative = [System.IO.Path]::GetRelativePath($repoFull, $pathFull)
  if ($relative.StartsWith('..')) {
    throw "Computed relative path escapes repo root: $relative"
  }

  return ($relative -replace '\\', '/')
}

function Get-DotNetArgs {
  param(
    [Parameter(Mandatory = $true)][string]$command,
    [Parameter(Mandatory = $true)][string]$target,
    [Parameter(Mandatory = $true)][bool]$ignoreWarningsAsErrors,
    [Parameter(Mandatory = $true)][bool]$noRestore,
    [Parameter(Mandatory = $true)][bool]$noBuild,
    [Parameter(Mandatory = $false)][bool]$skipIntegration = $false,
    [Parameter(Mandatory = $false)][bool]$skipPerformance = $false
  )

  $args = New-Object System.Collections.Generic.List[string]
  $args.Add($command)
  $args.Add($target)
  foreach ($value in @('-c', 'Release', '-m:1', '-p:BuildInParallel=false', '--disable-build-servers', '--nologo')) { $args.Add([string]$value) }

  if ($noRestore) { $args.Add('--no-restore') }
  if ($noBuild -and $command -eq 'test') { $args.Add('--no-build') }

  if ($ignoreWarningsAsErrors) {
    # Escape hatch for local merge-train runs; do not use as a substitute for fixing warnings.
    $args.Add('-p:TreatWarningsAsErrors=false')
  }

  if (($skipIntegration -or $skipPerformance) -and $command -eq 'test') {
    # Build filter expression for excluded test categories
    $filters = @()
    if ($skipIntegration) {
      # Exclude integration tests when running without secrets/live services.
      # Matches: *.Integration.*, *.IntegrationTests.*, *Live*, *EndToEnd*
      $filters += 'FullyQualifiedName!~Integration'
      $filters += 'FullyQualifiedName!~Live'
      $filters += 'FullyQualifiedName!~EndToEnd'
    }
    if ($skipPerformance) {
      # Exclude timing-sensitive tests with wall-clock assertions (unreliable in CI/Docker)
      $filters += 'Category!=Benchmark'
      $filters += 'Category!=Slow'
    }
    $args.Add('--filter')
    $args.Add($filters -join '&')
  }

  return $args.ToArray()
}

function Get-DotNetRestoreArgs {
  param(
    [Parameter(Mandatory = $true)][string]$target,
    [Parameter(Mandatory = $true)][string]$configFile
  )

  $args = New-Object System.Collections.Generic.List[string]
  $args.Add('restore')
  $args.Add($target)
  foreach ($value in @('--configfile', $configFile, '--disable-parallel', '--nologo')) { $args.Add([string]$value) }
  return $args.ToArray()
}

function Get-RepoChecks {
  param(
    [Parameter(Mandatory = $true)][string]$repoName,
    [Parameter(Mandatory = $true)][string]$repoPath,
    [Parameter(Mandatory = $true)][string]$mode,
    [Parameter(Mandatory = $true)][bool]$docker,
    [Parameter(Mandatory = $true)][string]$logsDir,
    [Parameter(Mandatory = $false)][bool]$skipIntegration = $false,
    [Parameter(Mandatory = $false)][bool]$skipPerformance = $false
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
      if ($docker) {
        $slnRelative = Convert-ToContainerRelativePath -repoPath $repoPath -path $slnPath
        $nugetConfigPath = $null
        $nugetConfigContainerPath = $null
        if ($repoName -eq 'applemusicarr') {
          $nugetConfigDir = Join-Path $logsDir 'nuget-configs'
          New-Item -ItemType Directory -Force -Path $nugetConfigDir | Out-Null
          $nugetConfigPath = Join-Path $nugetConfigDir 'applemusicarr.NuGet.Config'
          # Override the repo's NuGet.config (repo config takes precedence over user config).
          # If the repo doesn't have one, still mount at a conventional path to keep behavior stable.
          $nugetConfigContainerPath = if (Test-Path -LiteralPath (Join-Path $repoPath 'NuGet.config')) { '/repo/NuGet.config' } else { '/repo/NuGet.Config' }
          @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="lidarr-taglib" value="https://pkgs.dev.azure.com/Lidarr/Lidarr/_packaging/Taglib/nuget/v3/index.json" />
  </packageSources>
</configuration>
'@ | Out-File -FilePath $nugetConfigPath -Encoding UTF8
        } else {
          # In docker mode, always prefer an explicit --configfile pointing at the repo-root config
          # to prevent submodule NuGet.config files from affecting restore.
          if (Test-Path -LiteralPath (Join-Path $repoPath 'NuGet.config')) {
            $nugetConfigContainerPath = '/repo/NuGet.config'
          } elseif (Test-Path -LiteralPath (Join-Path $repoPath 'NuGet.Config')) {
            $nugetConfigContainerPath = '/repo/NuGet.Config'
          }
        }

        $nugetPackagesPath = Join-Path $logsDir "nuget-$repoName"
        if ($nugetConfigContainerPath) {
          $checks.Add((New-DockerDotNetPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:restore' -dotnetArgs (Get-DotNetRestoreArgs -target $slnRelative -configFile $nugetConfigContainerPath) -nugetConfigPath $nugetConfigPath -nugetConfigContainerPath $nugetConfigContainerPath -nugetPackagesPath $nugetPackagesPath))
        }
        $checks.Add((New-DockerDotNetPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:build' -dotnetArgs (Get-DotNetArgs -command 'build' -target $slnRelative -ignoreWarningsAsErrors ([bool]$IgnoreWarningsAsErrors) -noRestore ([bool]$nugetConfigContainerPath) -noBuild $false) -nugetConfigPath $nugetConfigPath -nugetConfigContainerPath $nugetConfigContainerPath -nugetPackagesPath $nugetPackagesPath))
      } else {
        $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:build' -fileName 'dotnet' -args (Get-DotNetArgs -command 'build' -target $slnPath -ignoreWarningsAsErrors ([bool]$IgnoreWarningsAsErrors) -noRestore $false -noBuild $false)))
      }
    }
    if ($mode -eq 'full') {
      if ($docker) {
        $slnRelative = Convert-ToContainerRelativePath -repoPath $repoPath -path $slnPath
        $nugetPackagesPath = Join-Path $logsDir "nuget-$repoName"
        $checks.Add((New-DockerDotNetPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:test' -dotnetArgs (Get-DotNetArgs -command 'test' -target $slnRelative -ignoreWarningsAsErrors ([bool]$IgnoreWarningsAsErrors) -noRestore $true -noBuild $true -skipIntegration $skipIntegration -skipPerformance $skipPerformance) -nugetPackagesPath $nugetPackagesPath))
      } else {
        $checks.Add((New-CheckPlan -repoName $repoName -repoPath $repoPath -name 'dotnet:test' -fileName 'dotnet' -args (Get-DotNetArgs -command 'test' -target $slnPath -ignoreWarningsAsErrors ([bool]$IgnoreWarningsAsErrors) -noRestore $false -noBuild $false -skipIntegration $skipIntegration -skipPerformance $skipPerformance)))
      }
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

if ($Docker -and $Mode -ne 'plan') {
  $dockerOk = $false
  try {
    $null = & docker version 2>$null
    if ($LASTEXITCODE -eq 0) { $dockerOk = $true }
  } catch { }

  if (-not $dockerOk) {
    $result = [pscustomobject]@{
      repoName = 'global'
      name = 'docker:available'
      outcome = 'failed'
      durationMs = 0
      exitCode = 1
      stdoutPath = $null
      stderrPath = $null
      message = 'Docker is required but not available. Install Docker Desktop / ensure the daemon is running, or rerun without -Docker.'
    }

    $report = [pscustomobject]@{
      schemaVersion = '1.0'
      runId = $runId
      startedAt = $startedAt
      completedAt = [DateTimeOffset]::UtcNow.ToString('o')
      mode = $Mode
      docker = $true
      workspaceRoot = $workspace
      reportPath = $reportPath
      logsDir = $logsDir
      repos = $repoResults
      results = @($result)
      summary = [pscustomobject]@{
        passed = 0
        failed = 1
        skipped = 0
      }
    }

    $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $reportPath -Encoding UTF8
    Write-Error $result.message
    exit 1
  }
}

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
  $checks = Get-RepoChecks -repoName $repoName -repoPath $resolvedRepoPath -mode $modeForChecks -docker ([bool]$Docker) -logsDir $logsDir -skipIntegration ([bool]$SkipIntegration) -skipPerformance ([bool]$SkipPerformance)
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
    docker = [bool]$Docker
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
