#!/usr/bin/env pwsh
<#
.SYNOPSIS
    TDD tests for the workflow-reference sentinel in lint-doc-script-refs.ps1.
.DESCRIPTION
    Tests the .github/workflows/*.yml and .gitea/workflows/*.yml validation pass
    added alongside the existing script-ref pass.
    Creates temp fixture repos with controlled markdown, runs the lint gate as an
    external pwsh process (so exit codes are always fresh), and asserts expected
    pass/fail behaviour.
      1. Missing workflow ref  -> lint must fail (exit 1)
      2. Present workflow ref  -> lint must pass (exit 0)
      3. docval:ignore-workflow-refs opt-out -> lint must pass despite missing ref
      4. .claude/worktrees markdown is ignored
      5. -SelfTest includes workflow pattern cases and passes
#>
$ErrorActionPreference = 'Stop'
$lintScript = Join-Path $PSScriptRoot '..' 'lint-doc-script-refs.ps1'
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
if (-not $pwsh) { $pwsh = 'pwsh' }

$fail = 0

function New-TempRepoRoot {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) "wfref-test-$(Get-Random)"
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}
function Remove-TempRepoRoot([string]$path) {
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
}
function Invoke-Lint([string]$repoRoot, [string[]]$extraArgs) {
    $args = @('-NonInteractive', '-File', $lintScript, '-RepoRoot', $repoRoot, '-CI') + $extraArgs
    & $pwsh @args | Out-Null
    return $LASTEXITCODE
}

# Test 1: missing workflow ref must be flagged
$root1 = New-TempRepoRoot
try {
    [System.IO.File]::WriteAllText(
        (Join-Path $root1 'doc.md'),
        "# Test

See `.github/workflows/missing.yml` for details."
    )
    $code = Invoke-Lint $root1
    if ($code -ne 0) {
        Write-Host "[PASS] Test 1: missing workflow ref correctly flagged (exit $code)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Test 1: expected exit 1 for missing workflow ref but got exit 0" -ForegroundColor Red
        $fail++
    }
} finally { Remove-TempRepoRoot $root1 }

# Test 2: present workflow ref must pass
$root2 = New-TempRepoRoot
try {
    $wfDir = Join-Path $root2 '.gitea' 'workflows'
    New-Item -ItemType Directory -Path $wfDir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $wfDir 'ci.yml'), 'name: CI')
    [System.IO.File]::WriteAllText(
        (Join-Path $root2 'doc.md'),
        "# Test

Runs via `.gitea/workflows/ci.yml`."
    )
    $code = Invoke-Lint $root2
    if ($code -eq 0) {
        Write-Host "[PASS] Test 2: present workflow ref correctly passes (exit $code)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Test 2: expected exit 0 for present workflow ref but got exit $code" -ForegroundColor Red
        $fail++
    }
} finally { Remove-TempRepoRoot $root2 }

# Test 3: docval:ignore-workflow-refs opt-out suppresses missing ref
$root3 = New-TempRepoRoot
try {
    [System.IO.File]::WriteAllText(
        (Join-Path $root3 'doc.md'),
        "<!-- docval:ignore-workflow-refs -->
# Test

Plugins configure `.github/workflows/release.yml` themselves."
    )
    $code = Invoke-Lint $root3
    if ($code -eq 0) {
        Write-Host "[PASS] Test 3: docval:ignore-workflow-refs suppresses missing ref (exit $code)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Test 3: opt-out should suppress failure but got exit $code" -ForegroundColor Red
        $fail++
    }
} finally { Remove-TempRepoRoot $root3 }

# Test 4: generated Claude worktrees are not repo docs
$root4 = New-TempRepoRoot
try {
    $agentDocs = Join-Path $root4 '.claude/worktrees/agent-1/docs'
    New-Item -ItemType Directory -Path $agentDocs -Force | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $agentDocs 'stale.md'),
        "# Generated worktree doc

Mentions `.github/workflows/missing.yml`, but this worktree is not part of repo docs."
    )
    $code = Invoke-Lint $root4
    if ($code -eq 0) {
        Write-Host "[PASS] Test 4: .claude/worktrees markdown is ignored (exit $code)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] Test 4: .claude/worktrees content should be ignored but got exit $code" -ForegroundColor Red
        $fail++
    }
} finally { Remove-TempRepoRoot $root4 }

# Test 5: -SelfTest includes workflow pattern cases and passes
$null = & $pwsh -NonInteractive -File $lintScript -SelfTest -CI
if ($LASTEXITCODE -eq 0) {
    Write-Host "[PASS] Test 5: -SelfTest passes (workflow pattern cases included)" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Test 5: -SelfTest failed (check workflow self-test cases)" -ForegroundColor Red
    $fail++
}

Write-Host ""
if ($fail -gt 0) {
    Write-Host "$fail test(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All lint-doc-workflow-refs tests passed." -ForegroundColor Green
