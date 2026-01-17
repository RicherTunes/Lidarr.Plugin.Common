#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Hermetic tests for scripts/verify-merge-train.ps1 (plan mode only).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptUnderTest = Join-Path $PSScriptRoot '../verify-merge-train.ps1'

function New-TempDir {
  $root = Join-Path ([System.IO.Path]::GetTempPath()) ("merge-train-test-{0}" -f ([guid]::NewGuid().ToString('N').Substring(0, 10)))
  New-Item -ItemType Directory -Force -Path $root | Out-Null
  return $root
}

function Add-Repo {
  param(
    [Parameter(Mandatory = $true)][string]$workspaceRoot,
    [Parameter(Mandatory = $true)][string]$repoName,
    [switch]$WithSolution,
    [switch]$WithNuGetConfig
  )

  $repoPath = Join-Path $workspaceRoot $repoName
  New-Item -ItemType Directory -Force -Path $repoPath | Out-Null
  if ($WithSolution) {
    Set-Content -Path (Join-Path $repoPath "$repoName.sln") -Value "# fake"
  }
  if ($WithNuGetConfig) {
    Set-Content -Path (Join-Path $repoPath "NuGet.config") -Value "<configuration />"
  }
  return $repoPath
}

$failed = 0
$workspaceRoot = $null

try {
  Write-Host "========================================" -ForegroundColor Cyan
  Write-Host "Test-VerifyMergeTrain: Plan Mode Tests" -ForegroundColor Cyan
  Write-Host "========================================" -ForegroundColor Cyan

  $workspaceRoot = New-TempDir

  Add-Repo -workspaceRoot $workspaceRoot -repoName 'lidarr.plugin.common' -WithSolution | Out-Null
  Add-Repo -workspaceRoot $workspaceRoot -repoName 'qobuzarr' | Out-Null
  Add-Repo -workspaceRoot $workspaceRoot -repoName 'applemusicarr' -WithSolution -WithNuGetConfig | Out-Null

  $outDir = Join-Path $workspaceRoot "out"
  $repos = @('lidarr.plugin.common', 'qobuzarr', 'applemusicarr', 'missingrepo')

  Write-Host "`n[TEST] plan mode writes a JSON report..." -ForegroundColor Cyan
  & $scriptUnderTest -WorkspaceRoot $workspaceRoot -Mode plan -Repos $repos -OutDir $outDir | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  [FAIL] Expected exit=0, got exit=$LASTEXITCODE" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] Exit code 0" -ForegroundColor Green
  }

  $reportFile = Get-ChildItem -Path $outDir -Filter 'merge-train-*.json' -File | Sort-Object LastWriteTime | Select-Object -Last 1
  if (-not $reportFile) {
    Write-Host "  [FAIL] No report file found in $outDir" -ForegroundColor Red
    $failed++
    throw "Report file missing"
  }

  $report = Get-Content -Raw -LiteralPath $reportFile.FullName | ConvertFrom-Json
  if ($report.mode -ne 'plan') { Write-Host "  [FAIL] Expected mode=plan" -ForegroundColor Red; $failed++ } else { Write-Host "  [PASS] mode=plan" -ForegroundColor Green }
  if ($report.plannedMode -ne 'quick') { Write-Host "  [FAIL] Expected plannedMode=quick" -ForegroundColor Red; $failed++ } else { Write-Host "  [PASS] plannedMode=quick" -ForegroundColor Green }

  Write-Host "`n[TEST] missing repo is recorded as missing..." -ForegroundColor Cyan
  $missing = $report.repos | Where-Object { $_.repoName -eq 'missingrepo' } | Select-Object -First 1
  if (-not $missing) {
    Write-Host "  [FAIL] missingrepo not present in report.repos" -ForegroundColor Red
    $failed++
  } elseif ($missing.status -ne 'missing') {
    Write-Host "  [FAIL] Expected missingrepo.status=missing, got $($missing.status)" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] missingrepo.status=missing" -ForegroundColor Green
  }

  Write-Host "`n[TEST] dotnet:build check is planned only for repos with a .sln..." -ForegroundColor Cyan
  $buildChecks = $report.plannedChecks | Where-Object { $_.name -eq 'dotnet:build' }
  $lidarrBuild = $buildChecks | Where-Object { $_.repoName -eq 'lidarr.plugin.common' } | Select-Object -First 1
  $appleBuild = $buildChecks | Where-Object { $_.repoName -eq 'applemusicarr' } | Select-Object -First 1
  $qobuzBuild = $buildChecks | Where-Object { $_.repoName -eq 'qobuzarr' } | Select-Object -First 1

  if (-not $lidarrBuild) {
    Write-Host "  [FAIL] Expected dotnet:build planned for lidarr.plugin.common" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] dotnet:build planned for lidarr.plugin.common" -ForegroundColor Green
  }

  if (-not $appleBuild) {
    Write-Host "  [FAIL] Expected dotnet:build planned for applemusicarr" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] dotnet:build planned for applemusicarr" -ForegroundColor Green
  }

  if ($qobuzBuild) {
    Write-Host "  [FAIL] Did not expect dotnet:build planned for qobuzarr (no .sln)" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] qobuzarr has no dotnet:build plan without .sln" -ForegroundColor Green
  }

  Write-Host "`n[TEST] docker plan uses docker for dotnet:build..." -ForegroundColor Cyan
  & $scriptUnderTest -WorkspaceRoot $workspaceRoot -Mode plan -Docker -Repos $repos -OutDir $outDir | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  [FAIL] Expected exit=0, got exit=$LASTEXITCODE" -ForegroundColor Red
    $failed++
  }

  $dockerReportFile = Get-ChildItem -Path $outDir -Filter 'merge-train-*.json' -File | Sort-Object LastWriteTime | Select-Object -Last 1
  $dockerReport = Get-Content -Raw -LiteralPath $dockerReportFile.FullName | ConvertFrom-Json
  if (-not $dockerReport.docker) {
    Write-Host "  [FAIL] Expected docker=true in report" -ForegroundColor Red
    $failed++
  } else {
    Write-Host "  [PASS] docker=true in report" -ForegroundColor Green
  }

  $dockerBuild = $dockerReport.plannedChecks | Where-Object { $_.repoName -eq 'lidarr.plugin.common' -and $_.name -eq 'dotnet:build' } | Select-Object -First 1
  if (-not $dockerBuild) {
    Write-Host "  [FAIL] Expected dotnet:build planned (docker mode)" -ForegroundColor Red
    $failed++
  } elseif ($dockerBuild.fileName -ne 'docker') {
    Write-Host "  [FAIL] Expected dotnet:build fileName=docker, got $($dockerBuild.fileName)" -ForegroundColor Red
    $failed++
  } else {
    $args = @($dockerBuild.args)
    $buildIndex = [Array]::IndexOf($args, 'build')
    if ($buildIndex -lt 0 -or $buildIndex -ge ($args.Length - 1)) {
      Write-Host "  [FAIL] Could not locate 'build' arg in docker plan" -ForegroundColor Red
      $failed++
    } else {
      $buildTarget = [string]$args[$buildIndex + 1]
      if ($buildTarget -match '^[a-zA-Z]:') {
        Write-Host "  [FAIL] Expected container-relative path, got Windows path: $buildTarget" -ForegroundColor Red
        $failed++
      } elseif ($buildTarget -match '\\\\') {
        Write-Host "  [FAIL] Expected forward slashes, got backslashes: $buildTarget" -ForegroundColor Red
        $failed++
      } else {
        Write-Host "  [PASS] dotnet:build uses container-relative path in plan ($buildTarget)" -ForegroundColor Green
      }
    }
  }

  Write-Host "`n[TEST] docker plan mounts minimal NuGet.config over /repo for applemusicarr..." -ForegroundColor Cyan
  $appleDockerBuild = $dockerReport.plannedChecks | Where-Object { $_.repoName -eq 'applemusicarr' -and $_.name -eq 'dotnet:build' } | Select-Object -First 1
  if (-not $appleDockerBuild) {
    Write-Host "  [FAIL] Expected applemusicarr dotnet:build planned (docker mode)" -ForegroundColor Red
    $failed++
  } else {
    $args = @($appleDockerBuild.args)
    $mountArg = $args | Where-Object { $_ -like '*:/repo/NuGet.config:ro' } | Select-Object -First 1
    if (-not $mountArg) {
      Write-Host "  [FAIL] Expected minimal NuGet.config mount to /repo/NuGet.config:ro" -ForegroundColor Red
      $failed++
    } else {
      Write-Host "  [PASS] Minimal NuGet.config mounted to /repo (overrides repo config)" -ForegroundColor Green
    }
  }

  Write-Host "`n[TEST] ignore warnings-as-errors adds TreatWarningsAsErrors=false..." -ForegroundColor Cyan
  & $scriptUnderTest -WorkspaceRoot $workspaceRoot -Mode plan -Docker -IgnoreWarningsAsErrors -Repos @('applemusicarr') -OutDir $outDir | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Host "  [FAIL] Expected exit=0, got exit=$LASTEXITCODE" -ForegroundColor Red
    $failed++
  }

  $ignoreReportFile = Get-ChildItem -Path $outDir -Filter 'merge-train-*.json' -File | Sort-Object LastWriteTime | Select-Object -Last 1
  $ignoreReport = Get-Content -Raw -LiteralPath $ignoreReportFile.FullName | ConvertFrom-Json
  $ignoreBuild = $ignoreReport.plannedChecks | Where-Object { $_.repoName -eq 'applemusicarr' -and $_.name -eq 'dotnet:build' } | Select-Object -First 1
  if (-not $ignoreBuild) {
    Write-Host "  [FAIL] Expected applemusicarr dotnet:build planned (ignore mode)" -ForegroundColor Red
    $failed++
  } else {
    $args = @($ignoreBuild.args)
    $hasFlag = $args -contains '-p:TreatWarningsAsErrors=false'
    if (-not $hasFlag) {
      Write-Host "  [FAIL] Expected -p:TreatWarningsAsErrors=false in docker plan" -ForegroundColor Red
      $failed++
    } else {
      Write-Host "  [PASS] TreatWarningsAsErrors=false present in plan" -ForegroundColor Green
    }
  }
}
finally {
  if ($workspaceRoot -and (Test-Path -LiteralPath $workspaceRoot)) {
    Remove-Item -LiteralPath $workspaceRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if ($failed -gt 0) {
  Write-Error "Test-VerifyMergeTrain failed: $failed failure(s)."
  exit 1
}

Write-Host "`nAll Test-VerifyMergeTrain checks passed." -ForegroundColor Green
exit 0
