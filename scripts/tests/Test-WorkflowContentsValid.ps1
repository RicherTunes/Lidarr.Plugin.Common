#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for the F2 workflow-content integrity guard.

.DESCRIPTION
    Exercises Test-WorkflowFileValid and Test-WorkflowFilesValid from
    verify-ecosystem-ci-contract.ps1 with focused fixtures:

      - A file with the per-character-SHA-interleaving corruption pattern FAILS
      - A well-formed workflow (on: + jobs:) PASSES
      - Non-UTF-8 bytes FAIL
      - An oversized file FAILS
      - A file missing 'on:' FAILS
      - A file missing 'jobs:' FAILS
      - A directory with only valid workflows PASSES
      - A directory containing one corrupt workflow FAILS

    Temp fixtures are removed after the test regardless of outcome.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$Verifier  = Join-Path $RepoRoot 'scripts/ci/verify-ecosystem-ci-contract.ps1'

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'F2 Workflow Content Integrity Self-Tests' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $Verifier)) {
    Write-Host "FATAL: verifier not found: $Verifier" -ForegroundColor Red
    exit 1
}

. $Verifier -DefineFunctionsOnly

$passed = 0
$failed = 0

function Test-Assertion {
    param([string]$Name, [scriptblock]$Test)
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
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
    }
}

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "wf-valid-test-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    # ---- Helper ---------------------------------------------------------
    $ValidWorkflow = "name: CI`non:`n  push:`n    branches: [main]`njobs:`n  build:`n    runs-on: ubuntu-latest`n    steps:`n      - run: echo hi`n"
    $CorruptSha    = 'aabbccddee1122334455aabbccddee1122334455'

    function New-CorruptContent([string]$original) {
        ($original.ToCharArray() | ForEach-Object { "$CorruptSha$_" }) -join ''
    }

    # ---- Test-WorkflowFileValid unit tests ------------------------------

    Write-Host 'Unit tests: Test-WorkflowFileValid' -ForegroundColor White

    Test-Assertion 'Valid workflow passes' {
        $f = Join-Path $TempDir 'valid.yml'
        [System.IO.File]::WriteAllText($f, $ValidWorkflow)
        (Test-WorkflowFileValid -FilePath $f).Ok -eq $true
    }

    Test-Assertion 'Per-char SHA interleaving (corrupt) fails' {
        $f = Join-Path $TempDir 'corrupt.yml'
        [System.IO.File]::WriteAllText($f, (New-CorruptContent $ValidWorkflow))
        $r = Test-WorkflowFileValid -FilePath $f
        $r.Ok -eq $false -and $r.Reason -match 'corrupt|SHA|sha'
    }

    Test-Assertion '5+ 40-hex occurrences = corruption (boundary)' {
        $f = Join-Path $TempDir 'sha5x.yml'
        # Exactly 5 copies of a 40-hex SHA embedded — must fail
        $sha5 = ($CorruptSha + ' ') * 5
        $content = "on:`n  push:`njobs:`n  build:`n    runs-on: ubuntu-latest`n# $sha5`n"
        [System.IO.File]::WriteAllText($f, $content)
        $r = Test-WorkflowFileValid -FilePath $f
        $r.Ok -eq $false
    }

    Test-Assertion 'Empty file fails' {
        $f = Join-Path $TempDir 'empty.yml'
        [System.IO.File]::WriteAllBytes($f, [byte[]]@())
        (Test-WorkflowFileValid -FilePath $f).Ok -eq $false
    }

    Test-Assertion 'File > 512 KB fails' {
        $f = Join-Path $TempDir 'toobig.yml'
        # 525000 x's + ~55 chars of prefix = ~525055 bytes > 524288 (512 KB ceiling)
        $pad = 'x' * 525000
        [System.IO.File]::WriteAllText($f, "on:`n  push:`njobs:`n  build:`n    runs-on: ubuntu-latest`n# $pad")
        (Test-WorkflowFileValid -FilePath $f).Ok -eq $false
    }

    Test-Assertion 'Missing on: key fails' {
        $f = Join-Path $TempDir 'no-on.yml'
        [System.IO.File]::WriteAllText($f, "name: CI`njobs:`n  build:`n    runs-on: ubuntu-latest`n")
        $r = Test-WorkflowFileValid -FilePath $f
        $r.Ok -eq $false -and $r.Reason -match 'on'
    }

    Test-Assertion 'Missing jobs: key fails' {
        $f = Join-Path $TempDir 'no-jobs.yml'
        [System.IO.File]::WriteAllText($f, "name: CI`non:`n  push:`n    branches: [main]`n")
        $r = Test-WorkflowFileValid -FilePath $f
        $r.Ok -eq $false -and $r.Reason -match 'jobs'
    }

    Test-Assertion 'Non-existent file fails' {
        (Test-WorkflowFileValid -FilePath (Join-Path $TempDir 'ghost.yml')).Ok -eq $false
    }

    Test-Assertion "Quoted 'on': form is accepted" {
        $f = Join-Path $TempDir 'quoted-on.yml'
        [System.IO.File]::WriteAllText($f, "name: CI`n'on':`n  push:`njobs:`n  build:`n    runs-on: ubuntu-latest`n")
        (Test-WorkflowFileValid -FilePath $f).Ok -eq $true
    }

    # ---- Test-WorkflowFilesValid unit tests -----------------------------

    Write-Host ''
    Write-Host 'Unit tests: Test-WorkflowFilesValid' -ForegroundColor White

    Test-Assertion 'Directory with only valid workflows passes' {
        $dir = Join-Path $TempDir 'wf-valid-dir'
        New-Item -Path "$dir/.github/workflows" -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText("$dir/.github/workflows/ci.yml", $ValidWorkflow)
        $r = Test-WorkflowFilesValid -PluginDir $dir -PluginName 'test'
        $r.Ok -eq $true -and $r.BadFiles.Count -eq 0
    }

    Test-Assertion 'Directory with one corrupt workflow fails (names it)' {
        $dir = Join-Path $TempDir 'wf-corrupt-dir'
        New-Item -Path "$dir/.github/workflows" -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText("$dir/.github/workflows/ci.yml",          $ValidWorkflow)
        [System.IO.File]::WriteAllText("$dir/.github/workflows/bump-common.yml", (New-CorruptContent $ValidWorkflow))
        $r = Test-WorkflowFilesValid -PluginDir $dir -PluginName 'test'
        # Wrap Where-Object in @() so single-match result is always an array with .Count
        $r.Ok -eq $false -and (@($r.BadFiles | Where-Object { $_ -match 'bump-common' }).Count -ge 1)
    }

    Test-Assertion 'Both .github/workflows and .gitea/workflows are checked' {
        $dir = Join-Path $TempDir 'wf-both-dirs'
        New-Item -Path "$dir/.github/workflows" -ItemType Directory -Force | Out-Null
        New-Item -Path "$dir/.gitea/workflows"  -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText("$dir/.github/workflows/ci.yml", $ValidWorkflow)
        [System.IO.File]::WriteAllText("$dir/.gitea/workflows/ci.yml",  $ValidWorkflow)
        $r = Test-WorkflowFilesValid -PluginDir $dir -PluginName 'test'
        $r.Ok -eq $true
    }

    Test-Assertion 'Corrupt file in .gitea/workflows fails' {
        $dir = Join-Path $TempDir 'wf-gitea-corrupt'
        New-Item -Path "$dir/.gitea/workflows" -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText("$dir/.gitea/workflows/ci.yml", (New-CorruptContent $ValidWorkflow))
        $r = Test-WorkflowFilesValid -PluginDir $dir -PluginName 'test'
        $r.Ok -eq $false
    }

    Test-Assertion 'Plugin with no workflow directories passes (vacuously)' {
        $dir = Join-Path $TempDir 'wf-no-dirs'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $r = Test-WorkflowFilesValid -PluginDir $dir -PluginName 'no-wf'
        $r.Ok -eq $true
    }
}
finally {
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host '========================================' -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }

Write-Host ''
Write-Host '[OK] All workflow content integrity tests passed.' -ForegroundColor Green
exit 0
