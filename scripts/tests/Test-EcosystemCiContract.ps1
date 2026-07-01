#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for verify-ecosystem-ci-contract.ps1.

.DESCRIPTION
    Builds a tiny fake ecosystem in a temp dir, dot-sources the verifier's pure
    assertion functions via -DefineFunctionsOnly, and exercises each failure mode:

      - A passing plugin (all assertions green)
      - A plugin with a mismatched ext-common-sha.txt sentinel
      - A plugin whose .gitea/workflows/ci.yml does not invoke the doc-refs lint
      - A plugin with an undeclared extra .github/workflows file

    Also runs the full verifier end-to-end against the fake ecosystem with a fake
    manifest and checks that only the good plugin reports PASS while the bad ones
    report FAIL.

    Temp fixtures are removed after the test regardless of outcome.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir  = $PSScriptRoot
$RepoRoot   = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$Verifier   = Join-Path $RepoRoot 'scripts/ci/verify-ecosystem-ci-contract.ps1'

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'Ecosystem CI-Contract Self-Tests' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ---- Preflight: verifier script must exist --------------------------------
if (-not (Test-Path -LiteralPath $Verifier)) {
    Write-Host "FATAL: verifier script not found: $Verifier" -ForegroundColor Red
    Write-Host '  (Run this test AFTER implementing verify-ecosystem-ci-contract.ps1)' -ForegroundColor Yellow
    exit 1
}

# Dot-source to bring pure assertion functions into scope (no network, no side effects)
. $Verifier -DefineFunctionsOnly

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
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
    }
}

# ============================================================
# Fixture setup
# ============================================================

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eco-ci-contract-test-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

$FakeCommonDir = Join-Path $TempDir 'fake-common'

try {
    # --- Create a minimal fake Common git repo ---------------------------
    New-Item -ItemType Directory -Path $FakeCommonDir -Force | Out-Null
    git init $FakeCommonDir --quiet 2>$null | Out-Null
    git -C $FakeCommonDir config user.email 'eco-test@example.invalid' 2>$null | Out-Null
    git -C $FakeCommonDir config user.name 'EcoTest' 2>$null | Out-Null
    New-Item -Path "$FakeCommonDir/placeholder.txt" -ItemType File -Force | Out-Null
    git -C $FakeCommonDir add placeholder.txt 2>$null | Out-Null
    git -C $FakeCommonDir commit -m 'init' --quiet 2>$null | Out-Null
    $FakeCommonSha = (git -C $FakeCommonDir rev-parse HEAD 2>$null).Trim()

    # Helper: create a fake plugin git repo
    function New-FakePlugin {
        param(
            [string]$Dir,
            [string]$SentinelSha,       # SHA written to ext-common-sha.txt (may differ from gitlink)
            [string]$GitlinkSha = $FakeCommonSha,
            [bool]$WireDocRefs = $true,
            [bool]$WireSharedRunner = $true,
            [bool]$WireVerify = $true,
            [int]$GithubWorkflowCount = 0
        )
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        git init $Dir --quiet 2>$null | Out-Null
        git -C $Dir config user.email 'eco-test@example.invalid' 2>$null | Out-Null
        git -C $Dir config user.name 'EcoTest' 2>$null | Out-Null

        # Create the gitlink via git plumbing so the test is hermetic (no network,
        # no file-transport security setting needed, no real remote repo required).
        # 'git submodule add' with file:// is blocked by CVE-2022-39253 hardening.
        New-Item -Path (Join-Path $Dir 'ext/Lidarr.Plugin.Common') -ItemType Directory -Force | Out-Null
        git -C $Dir update-index --add --cacheinfo "160000,$GitlinkSha,ext/Lidarr.Plugin.Common" 2>$null | Out-Null
        $gitmodulesContent = "[submodule `"ext/Lidarr.Plugin.Common`"]`n`tpath = ext/Lidarr.Plugin.Common`n`turl = ../fake-common"
        [System.IO.File]::WriteAllText((Join-Path $Dir '.gitmodules'), $gitmodulesContent)

        # Write sentinel (may be intentionally wrong)
        $shaBytes = [System.Text.Encoding]::ASCII.GetBytes($SentinelSha + "`n")
        [System.IO.File]::WriteAllBytes((Join-Path $Dir 'ext-common-sha.txt'), $shaBytes)

        # Create .gitea/workflows/ci.yml — must include 'on:' + 'jobs:' for F2 validity check
        New-Item -Path (Join-Path $Dir '.gitea/workflows') -ItemType Directory -Force | Out-Null
        $ciYmlPath = Join-Path $Dir '.gitea/workflows/ci.yml'
        if ($WireSharedRunner) {
            Set-Content $ciYmlPath 'name: CI
on:
  push:
    branches: [main]
  pull_request:
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - name: Shared plugin lint gates
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci'
        }
        elseif ($WireDocRefs) {
            Set-Content $ciYmlPath 'name: CI
on:
  push:
    branches: [main]
  pull_request:
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - name: Doc-refs lint
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/lint-doc-script-refs.ps1 -RepoRoot . -CI'
        }
        else {
            Set-Content $ciYmlPath 'name: CI
on:
  push:
    branches: [main]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Build
        run: pwsh ./scripts/some-other-script.ps1'
        }

        if ($WireVerify) {
            Add-Content $ciYmlPath '
  verify:
    runs-on: ubuntu-latest
    steps:
      - name: Verify local build/test/package contract
        run: pwsh ./scripts/verify-local.ps1'
        }

        # Create GitHub workflows if needed (valid YAML with on: + jobs: for F2 check)
        if ($GithubWorkflowCount -gt 0) {
            New-Item -Path (Join-Path $Dir '.github/workflows') -ItemType Directory -Force | Out-Null
            for ($i = 0; $i -lt $GithubWorkflowCount; $i++) {
                Set-Content (Join-Path $Dir ".github/workflows/extra$i.yml") "name: Extra$i
on:
  push:
jobs:
  extra:
    runs-on: ubuntu-latest
    steps:
      - run: echo placeholder"
            }
        }

        git -C $Dir add --all 2>$null | Out-Null
        git -C $Dir commit -m 'init' --quiet 2>$null | Out-Null
    }

    function Set-FakeGithubCiMirror {
        param(
            [string]$Dir,
            [bool]$WireSharedRunner = $true,
            [bool]$WireVerify = $true,
            [bool]$WirePin = $true,
            [bool]$WireSecretScan = $true,
            [bool]$WireGitHubOnlyGuard = $true,
            [bool]$WireFallbackLintSubset = $false,
            [bool]$ContinueOnError = $false,
            [bool]$DisableVerifyJob = $false,
            [bool]$SwallowLintFailure = $false,
            [string]$VerifyNeeds = '[lint, secret-scan]'
        )

        New-Item -Path (Join-Path $Dir '.github/workflows') -ItemType Directory -Force | Out-Null

        $secretRun = if ($WireSecretScan) {
            'gitleaks detect --source . --redact --exit-code 1'
        } else {
            'echo no secret scan'
        }

        $lintRun = if ($WireSharedRunner) {
            'pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci'
        } else {
            'pwsh ./scripts/local-only-lint.ps1'
        }
        if ($SwallowLintFailure) {
            $lintRun = "$lintRun || true"
        }

        $pinStep = if ($WirePin) {
            @'
      - name: Common submodule pin guard
        run: bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh --verify-only --path ext/Lidarr.Plugin.Common
'@
        } else {
            @'
      - name: Placeholder
        run: echo no pin guard
'@
        }

        $verifyRun = if ($WireVerify) { 'pwsh ./scripts/verify-local.ps1' } else { 'pwsh ./scripts/build-only.ps1' }
        $jobGuard = if ($WireGitHubOnlyGuard) {
            "    if: `${{ github.server_url == 'https://github.com' }}"
        } else {
            ''
        }
        $verifyGuard = if ($DisableVerifyJob) {
            '    if: false'
        } else {
            $jobGuard
        }
        $continueOnErrorBlock = if ($ContinueOnError) {
            '    continue-on-error: true'
        } else {
            ''
        }
        $verifyNeedsBlock = if ([string]::IsNullOrWhiteSpace($VerifyNeeds)) {
            ''
        } else {
            "    needs: $VerifyNeeds"
        }
        $fallbackLint = if ($WireFallbackLintSubset) {
            @'
      - name: Fallback lint subset
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Check VersionContract -Mode ci
'@
        } else {
            ''
        }

        Set-Content (Join-Path $Dir '.github/workflows/ci.yml') @"
name: CI
on:
  push:
    branches: [main]
  pull_request:
jobs:
  secret-scan:
$jobGuard
    runs-on: ubuntu-latest
    steps:
      - run: $secretRun
  lint:
$jobGuard
$continueOnErrorBlock
    runs-on: ubuntu-latest
    steps:
      - name: Shared plugin lint gates
        run: $lintRun
$fallbackLint
  verify:
$verifyGuard
$verifyNeedsBlock
    runs-on: ubuntu-latest
    steps:
$pinStep
      - name: Full local-ci verification
        run: $verifyRun
"@
    }

    # --- Plugin A: everything correct (PASS) -----------------------------
    $DirA = Join-Path $TempDir 'plugin-pass'
    New-FakePlugin -Dir $DirA -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0

    # --- Plugin F: declared GitHub mirror with full CI parity (PASS) ------
    $DirF = Join-Path $TempDir 'plugin-good-ghmirror'
    New-FakePlugin -Dir $DirF -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
    Set-FakeGithubCiMirror -Dir $DirF

    # --- Plugin A2: same pin, CRLF sentinel (PASS on Windows checkouts) ---
    $DirA2 = Join-Path $TempDir 'plugin-pass-crlf-sentinel'
    New-FakePlugin -Dir $DirA2 -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
    [System.IO.File]::WriteAllBytes(
        (Join-Path $DirA2 'ext-common-sha.txt'),
        [System.Text.Encoding]::ASCII.GetBytes($FakeCommonSha + "`r`n"))
    git -C $DirA2 add ext-common-sha.txt 2>$null | Out-Null
    git -C $DirA2 commit -m 'use crlf sentinel' --quiet 2>$null | Out-Null

    # --- Plugin B: sentinel mismatch (FAIL) ------------------------------
    $BadSha  = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $DirB    = Join-Path $TempDir 'plugin-bad-pin'
    New-FakePlugin -Dir $DirB -SentinelSha $BadSha -WireDocRefs $true -GithubWorkflowCount 0

    # --- Plugin C: no doc-refs lint in ci.yml (FAIL) ---------------------
    $DirC = Join-Path $TempDir 'plugin-no-docref'
    New-FakePlugin -Dir $DirC -SentinelSha $FakeCommonSha -WireDocRefs $false -WireSharedRunner $false -GithubWorkflowCount 0

    # --- Plugin D: undeclared .github/workflows file (FAIL) --------------
    # manifest expects 0 but dir has 1
    $DirD = Join-Path $TempDir 'plugin-extra-wf'
    New-FakePlugin -Dir $DirD -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 1

    # --- Plugin E: corrupt .github/workflows file (FAIL) -----------------
    # Simulates the amazon bug: a Common SHA interleaved between every character
    # of the original YAML by a broken sed, producing 40-60 KB of garbage.
    $DirE = Join-Path $TempDir 'plugin-corrupt-wf'
    New-FakePlugin -Dir $DirE -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
    # manifest expects 1 for DirE so we can add the corrupt file without triggering
    # the count check — we want ONLY the content check to fire
    New-Item -Path (Join-Path $DirE '.github/workflows') -ItemType Directory -Force | Out-Null
    $corruptSha  = 'aabbccddee1122334455aabbccddee1122334455'
    $origContent = "name: CI`non:`n  push:`n    branches: [main]`njobs:`n  build:`n    runs-on: ubuntu-latest`n"
    $corruptContent = ($origContent.ToCharArray() | ForEach-Object { "$corruptSha$_" }) -join ''
    [System.IO.File]::WriteAllText((Join-Path $DirE '.github/workflows/bump-common.yml'), $corruptContent)

    # --- Write a fake manifest for the fake ecosystem --------------------
    $FakeManifestPath = Join-Path $TempDir 'fake-manifest.json'
    @{
        plugins = @(
            @{ repoDir = 'plugin-pass';        giteaOwner = 'Test'; giteaRepo = 'PluginPass';       giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-bad-pin';     giteaOwner = 'Test'; giteaRepo = 'PluginBadPin';     giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-no-docref';   giteaOwner = 'Test'; giteaRepo = 'PluginNoDocRef';   giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-extra-wf';    giteaOwner = 'Test'; giteaRepo = 'PluginExtraWf';    giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-corrupt-wf';  giteaOwner = 'Test'; giteaRepo = 'PluginCorruptWf';  giteaPrimary = $true; mirrorWorkflows = 1 }
            @{ repoDir = 'plugin-good-ghmirror'; giteaOwner = 'Test'; giteaRepo = 'PluginGoodGhMirror'; giteaPrimary = $true; mirrorWorkflows = 1 }
        )
    } | ConvertTo-Json -Depth 5 | Set-Content $FakeManifestPath

    # ============================================================
    # Unit tests: pure assertion functions
    # ============================================================

    Write-Host 'Unit tests: Test-CommonPinIntegrity' -ForegroundColor White

    Test-Assertion 'Pin integrity: valid plugin A returns Ok=$true' {
        $r = Test-CommonPinIntegrity -PluginDir $DirA -PluginName 'plugin-pass'
        $r.Ok -eq $true -and $r.Sha -eq $FakeCommonSha
    }

    Test-Assertion 'Pin integrity: CRLF sentinel returns Ok=$true' {
        $r = Test-CommonPinIntegrity -PluginDir $DirA2 -PluginName 'plugin-pass-crlf-sentinel'
        $r.Ok -eq $true -and $r.Sha -eq $FakeCommonSha
    }

    Test-Assertion 'Pin integrity: sentinel mismatch returns Ok=$false' {
        $r = Test-CommonPinIntegrity -PluginDir $DirB -PluginName 'plugin-bad-pin'
        $r.Ok -eq $false -and $r.Reason -match '[Mm]ismatch|differ|match'
    }

    Test-Assertion 'Pin integrity: missing ext-common-sha.txt returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'no-sentinel'
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
        git init $tmpDir --quiet 2>$null | Out-Null
        $r = Test-CommonPinIntegrity -PluginDir $tmpDir -PluginName 'no-sentinel'
        $r.Ok -eq $false
    }

    Write-Host ''
    Write-Host 'Unit tests: Test-DocRefsLintWired' -ForegroundColor White

    Test-Assertion 'DocRefs lint: wired plugin A returns Ok=$true' {
        $r = Test-DocRefsLintWired -PluginDir $DirA -PluginName 'plugin-pass'
        $r.Ok -eq $true
    }

    Test-Assertion 'DocRefs lint: missing invocation returns Ok=$false' {
        $r = Test-DocRefsLintWired -PluginDir $DirC -PluginName 'plugin-no-docref'
        $r.Ok -eq $false
    }

    Test-Assertion 'DocRefs lint: run-plugin-lint-gates.ps1 is accepted as alternate invocation' {
        $tmpDir = Join-Path $TempDir 'via-runner'
        New-Item -Path "$tmpDir/.gitea/workflows" -ItemType Directory -Force | Out-Null
        Set-Content "$tmpDir/.gitea/workflows/ci.yml" @'
name: CI
run: pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath .
'@
        $r = Test-DocRefsLintWired -PluginDir $tmpDir -PluginName 'via-runner'
        $r.Ok -eq $true
    }

    Test-Assertion 'DocRefs lint: missing ci.yml returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'no-ci-yml'
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
        $r = Test-DocRefsLintWired -PluginDir $tmpDir -PluginName 'no-ci-yml'
        $r.Ok -eq $false
    }

    Write-Host ''
    Write-Host 'Unit tests: Test-SharedPluginLintRunnerWired' -ForegroundColor White

    Test-Assertion 'Shared lint runner: wired plugin A returns Ok=$true' {
        $r = Test-SharedPluginLintRunnerWired -PluginDir $DirA -PluginName 'plugin-pass'
        $r.Ok -eq $true
    }

    Test-Assertion 'Shared lint runner: direct doc-ref-only workflow returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-direct-docref-only'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $false -GithubWorkflowCount 0
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'direct-docref-only'
        $r.Ok -eq $false
    }

    Test-Assertion 'Shared lint runner: direct parity fallback subset returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-direct-parity-fallback'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        Add-Content -LiteralPath $ciYml @'
      - name: Compatibility fallback
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 -RepoPath . -Check VersionContract -Mode ci
'@
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'direct-parity-fallback'
        $r.Ok -eq $false -and $r.Reason -match 'direct.*ecosystem-parity-lint|fallback|subset'
    }

    Test-Assertion 'Shared lint runner: direct secret-scan fallback returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-direct-secret-scan-fallback'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        Add-Content -LiteralPath $ciYml @'
      - name: Compatibility fallback
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/lint-gitea-secret-scan.ps1 -RepoPath . -CI
'@
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'direct-secret-scan-fallback'
        $r.Ok -eq $false -and $r.Reason -match 'lint-gitea-secret-scan|fallback|subset'
    }

    Test-Assertion 'Shared lint runner: skip switch returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-runner-skip-switch'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        $content = [System.IO.File]::ReadAllText($ciYml)
        $content = $content.Replace('-Mode ci', '-Mode ci -SkipEcosystemParity')
        [System.IO.File]::WriteAllText($ciYml, $content)
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'runner-skip-switch'
        $r.Ok -eq $false -and $r.Reason -match 'SkipEcosystemParity|skip'
    }

    Test-Assertion 'Shared lint runner: skip alias returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-runner-skip-alias'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        $content = [System.IO.File]::ReadAllText($ciYml)
        $content = $content.Replace('-Mode ci', '-Mode ci -SkipVersionContract')
        [System.IO.File]::WriteAllText($ciYml, $content)
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'runner-skip-alias'
        $r.Ok -eq $false -and $r.Reason -match 'SkipVersionContract|skip'
    }

    Test-Assertion 'Shared lint runner: commented-only runner returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-commented-runner'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $false -WireSharedRunner $false -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        Add-Content -LiteralPath $ciYml @'
      # run: pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath .
'@
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'commented-runner'
        $r.Ok -eq $false -and $r.Reason -match 'run-plugin-lint-gates'
    }

    Test-Assertion 'Shared lint runner: commented fallback script name does not fail direct-script guard' {
        $tmpDir = Join-Path $TempDir 'plugin-commented-fallback'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -GithubWorkflowCount 0
        $ciYml = Join-Path $tmpDir '.gitea/workflows/ci.yml'
        Add-Content -LiteralPath $ciYml @'
      # Historic fallback called ecosystem-parity-lint.ps1 directly.
'@
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'commented-fallback'
        $r.Ok -eq $true
    }

    Test-Assertion 'Shared lint runner: missing ci.yml returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-no-ci-shared-runner'
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
        $r = Test-SharedPluginLintRunnerWired -PluginDir $tmpDir -PluginName 'no-ci-yml'
        $r.Ok -eq $false
    }

    Write-Host ''
    Write-Host 'Unit tests: Test-PluginVerifyJobWired' -ForegroundColor White

    Test-Assertion 'Verify job: wired plugin A returns Ok=$true' {
        $r = Test-PluginVerifyJobWired -PluginDir $DirA -PluginName 'plugin-pass'
        $r.Ok -eq $true
    }

    Test-Assertion 'Verify job: missing verify-local invocation returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-no-verify'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -WireSharedRunner $true -WireVerify $false -GithubWorkflowCount 0
        $r = Test-PluginVerifyJobWired -PluginDir $tmpDir -PluginName 'plugin-no-verify'
        $r.Ok -eq $false
    }

    Test-Assertion 'Verify job: commented-out verify-local invocation returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-commented-verify'
        $wf = Join-Path $tmpDir '.gitea/workflows'
        New-Item -ItemType Directory -Path $wf -Force | Out-Null
        @'
name: CI
on: [push]
jobs:
  verify:
    steps:
      # run: pwsh ./scripts/verify-local.ps1
      - run: pwsh ./scripts/build-only.ps1
'@ | Set-Content -LiteralPath (Join-Path $wf 'ci.yml')
        $r = Test-PluginVerifyJobWired -PluginDir $tmpDir -PluginName 'plugin-commented-verify'
        $r.Ok -eq $false
    }

    Test-Assertion 'Verify job: missing ci.yml returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-no-ci-verify'
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
        $r = Test-PluginVerifyJobWired -PluginDir $tmpDir -PluginName 'no-ci-yml'
        $r.Ok -eq $false
    }

    Write-Host ''
    Write-Host 'Unit tests: Test-WorkflowMirrorCount' -ForegroundColor White

    Test-Assertion 'WorkflowCount: 0 expected, 0 actual => Ok=$true' {
        $r = Test-WorkflowMirrorCount -PluginDir $DirA -Expected 0
        $r.Ok -eq $true
    }

    Test-Assertion 'WorkflowCount: 0 expected, 1 actual => Ok=$false' {
        $r = Test-WorkflowMirrorCount -PluginDir $DirD -Expected 0
        $r.Ok -eq $false -and $r.ActualCount -eq 1
    }

    Test-Assertion 'WorkflowCount: 1 expected, 1 actual => Ok=$true' {
        $r = Test-WorkflowMirrorCount -PluginDir $DirD -Expected 1
        $r.Ok -eq $true
    }

    Test-Assertion 'WorkflowCount: missing .github/workflows counts as 0' {
        # Plugin C has no .github dir at all
        $r = Test-WorkflowMirrorCount -PluginDir $DirC -Expected 0
        $r.Ok -eq $true
    }

    Write-Host ''
    Write-Host 'Unit tests: Test-GitHubCiMirrorContract' -ForegroundColor White

    Test-Assertion 'GitHub CI mirror: expected 0 and no ci.yml returns Ok=$true' {
        $r = Test-GitHubCiMirrorContract -PluginDir $DirA -Expected 0
        $r.Ok -eq $true
    }

    Test-Assertion 'GitHub CI mirror: full mirror ci.yml returns Ok=$true' {
        $r = Test-GitHubCiMirrorContract -PluginDir $DirF -Expected 1
        $r.Ok -eq $true
    }

    Test-Assertion 'GitHub CI mirror: workflow count without ci.yml returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-ci'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 1
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'ci\.yml|ci\.yaml'
    }

    Test-Assertion 'GitHub CI mirror: missing shared runner returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-runner'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WireSharedRunner $false
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'run-plugin-lint-gates'
    }

    Test-Assertion 'GitHub CI mirror: missing verify-local returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-verify'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WireVerify $false
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'verify-local'
    }

    Test-Assertion 'GitHub CI mirror: missing pin guard returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-pin'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WirePin $false
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'repin-common-submodule'
    }

    Test-Assertion 'GitHub CI mirror: missing secret scan returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-secret-scan'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WireSecretScan $false
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'gitleaks'
    }

    Test-Assertion 'GitHub CI mirror: missing GitHub-only job guard returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-no-github-only-guard'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WireGitHubOnlyGuard $false
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'github\.server_url|GitHub-only'
    }

    Test-Assertion 'GitHub CI mirror: fallback lint subset returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-fallback-subset'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -WireFallbackLintSubset $true
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'fallback|subset|direct lint'
    }

    Test-Assertion 'GitHub CI mirror: continue-on-error returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-continue-on-error'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -ContinueOnError $true
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'continue-on-error'
    }

    Test-Assertion 'GitHub CI mirror: lint failure swallowing returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-swallow-lint'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -SwallowLintFailure $true
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match '\|\| true|swallow|failure'
    }

    Test-Assertion 'GitHub CI mirror: disabled verify job returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-disabled-verify'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -DisableVerifyJob $true
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'if:\s*false|disabled'
    }

    Test-Assertion 'GitHub CI mirror: verify without lint and secret-scan needs returns Ok=$false' {
        $tmpDir = Join-Path $TempDir 'plugin-ghmirror-missing-needs'
        New-FakePlugin -Dir $tmpDir -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        Set-FakeGithubCiMirror -Dir $tmpDir -VerifyNeeds ''
        $r = Test-GitHubCiMirrorContract -PluginDir $tmpDir -Expected 1
        $r.Ok -eq $false -and $r.Reason -match 'needs|lint|secret-scan'
    }

    # ============================================================
    # Unit tests: Test-WorkflowFileValid (F2 — content integrity)
    # ============================================================

    Write-Host ''
    Write-Host 'Unit tests: Test-WorkflowFileValid (F2)' -ForegroundColor White

    Test-Assertion 'WorkflowFileValid: valid .gitea/workflows/ci.yml returns Ok=$true' {
        $r = Test-WorkflowFileValid -FilePath (Join-Path $DirA '.gitea/workflows/ci.yml')
        $r.Ok -eq $true
    }

    Test-Assertion 'WorkflowFileValid: corrupt file (SHA interleaved) returns Ok=$false' {
        $tmpFile = Join-Path $TempDir 'corrupt-workflow.yml'
        $sha  = 'aabbccddee1122334455aabbccddee1122334455'
        $orig = "name: CI`non:`n  push:`njobs:`n  build:`n    runs-on: ubuntu-latest`n"
        $corrupt = ($orig.ToCharArray() | ForEach-Object { "$sha$_" }) -join ''
        [System.IO.File]::WriteAllText($tmpFile, $corrupt)
        $r = Test-WorkflowFileValid -FilePath $tmpFile
        $r.Ok -eq $false -and $r.Reason -match 'corruption|corrupt|SHA|sha'
    }

    Test-Assertion 'WorkflowFileValid: empty file returns Ok=$false' {
        $tmpFile = Join-Path $TempDir 'empty-workflow.yml'
        [System.IO.File]::WriteAllBytes($tmpFile, [byte[]]@())
        $r = Test-WorkflowFileValid -FilePath $tmpFile
        $r.Ok -eq $false
    }

    Test-Assertion 'WorkflowFileValid: file missing on: key returns Ok=$false' {
        $tmpFile = Join-Path $TempDir 'no-on-workflow.yml'
        [System.IO.File]::WriteAllText($tmpFile, "name: CI`njobs:`n  build:`n    runs-on: ubuntu-latest`n")
        $r = Test-WorkflowFileValid -FilePath $tmpFile
        $r.Ok -eq $false -and $r.Reason -match 'on'
    }

    Test-Assertion 'WorkflowFileValid: file missing jobs: key returns Ok=$false' {
        $tmpFile = Join-Path $TempDir 'no-jobs-workflow.yml'
        [System.IO.File]::WriteAllText($tmpFile, "name: CI`non:`n  push:`n    branches: [main]`n")
        $r = Test-WorkflowFileValid -FilePath $tmpFile
        $r.Ok -eq $false -and $r.Reason -match 'jobs'
    }

    Test-Assertion 'WorkflowFileValid: non-existent file returns Ok=$false' {
        $r = Test-WorkflowFileValid -FilePath (Join-Path $TempDir 'nonexistent.yml')
        $r.Ok -eq $false
    }

    # ============================================================
    # Unit tests: Test-WorkflowFilesValid (F2 — per-plugin scan)
    # ============================================================

    Write-Host ''
    Write-Host 'Unit tests: Test-WorkflowFilesValid (F2)' -ForegroundColor White

    Test-Assertion 'WorkflowFilesValid: clean plugin returns Ok=$true' {
        $r = Test-WorkflowFilesValid -PluginDir $DirA -PluginName 'plugin-pass'
        $r.Ok -eq $true -and $r.BadFiles.Count -eq 0
    }

    Test-Assertion 'WorkflowFilesValid: plugin with corrupt .github/workflows file returns Ok=$false' {
        $r = Test-WorkflowFilesValid -PluginDir $DirE -PluginName 'plugin-corrupt-wf'
        $r.Ok -eq $false -and $r.BadFiles.Count -ge 1
    }

    Test-Assertion 'WorkflowFilesValid: plugin with no workflow dirs returns Ok=$true (vacuously)' {
        $emptyDir = Join-Path $TempDir 'plugin-no-wf-dirs'
        New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
        $r = Test-WorkflowFilesValid -PluginDir $emptyDir -PluginName 'no-wf-dirs'
        $r.Ok -eq $true
    }

    # ============================================================
    # End-to-end: run full verifier against fake ecosystem
    # ============================================================

    Write-Host ''
    Write-Host 'End-to-end: full verifier against fake ecosystem' -ForegroundColor White

    Test-Assertion 'Full verifier exits non-zero when bad plugins present' {
        $output = & pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1
        $LASTEXITCODE -ne 0
    }

    Test-Assertion 'Full verifier reports plugin-pass as PASS' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-pass.*PASS|PASS.*plugin-pass'
    }

    Test-Assertion 'Full verifier reports plugin-good-ghmirror as PASS' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-good-ghmirror.*PASS|PASS.*plugin-good-ghmirror'
    }

    Test-Assertion 'Full verifier reports plugin-bad-pin as FAIL' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-bad-pin.*FAIL|FAIL.*plugin-bad-pin'
    }

    Test-Assertion 'Full verifier reports plugin-no-docref as FAIL' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-no-docref.*FAIL|FAIL.*plugin-no-docref'
    }

    Test-Assertion 'Full verifier reports plugin-extra-wf as FAIL' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-extra-wf.*FAIL|FAIL.*plugin-extra-wf'
    }

    Test-Assertion 'Full verifier reports plugin-corrupt-wf as FAIL (F2)' {
        $output = (& pwsh -NoProfile -File $Verifier -EcosystemRoot $TempDir -ManifestPath $FakeManifestPath -CI *>&1) -join "`n"
        $output -match 'plugin-corrupt-wf.*FAIL|FAIL.*plugin-corrupt-wf'
    }

    Test-Assertion 'Full verifier exits non-zero in CI when a manifest repo directory is missing' {
        $missingRoot = Join-Path $TempDir 'missing-repo-ecosystem'
        New-Item -ItemType Directory -Path $missingRoot -Force | Out-Null

        $manifest = Join-Path $missingRoot 'manifest.json'
        @{
            plugins = @(
                @{ repoDir = 'plugin-missing'; giteaOwner = 'Test'; giteaRepo = 'PluginMissing'; giteaPrimary = $true; mirrorWorkflows = 0 }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content $manifest

        $output = & pwsh -NoProfile -File $Verifier -EcosystemRoot $missingRoot -ManifestPath $manifest -CI *>&1
        $LASTEXITCODE -ne 0 -and (($output -join "`n") -match 'plugin-missing.*FAIL|directory not found|not present')
    }

    # ============================================================
    # Fail-by-default: cross-plugin SHA agreement divergence
    # ============================================================

    Write-Host ''
    Write-Host 'Cross-plugin SHA agreement (fail by default)' -ForegroundColor White

    Test-Assertion 'All-same SHA set => no agreement warning in output' {
        # Plugin A is the only passing plugin in the fake ecosystem.
        # Agreement is detected only across plugins that PASS the pin check.
        # With one passing plugin, divergence cannot occur.
        $shas = @($FakeCommonSha, $FakeCommonSha)
        $r = Test-CommonPinAgreement -Shas $shas
        $r.Diverged -eq $false
    }

    Test-Assertion 'Divergent SHAs emit divergence flag' {
        $shas = @($FakeCommonSha, 'cccccccccccccccccccccccccccccccccccccccc')
        $r = Test-CommonPinAgreement -Shas $shas
        $r.Diverged -eq $true
    }

    Test-Assertion 'Full verifier exits non-zero when otherwise-clean plugins pin different Common SHAs' {
        $divergentRoot = Join-Path $TempDir 'divergent-ecosystem'
        New-Item -ItemType Directory -Path $divergentRoot -Force | Out-Null

        $shaC = 'cccccccccccccccccccccccccccccccccccccccc'
        New-FakePlugin -Dir (Join-Path $divergentRoot 'plugin-a') -SentinelSha $FakeCommonSha -GitlinkSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0
        New-FakePlugin -Dir (Join-Path $divergentRoot 'plugin-b') -SentinelSha $shaC -GitlinkSha $shaC -WireDocRefs $true -GithubWorkflowCount 0

        $manifest = Join-Path $divergentRoot 'manifest.json'
        @{
            plugins = @(
                @{ repoDir = 'plugin-a'; giteaOwner = 'Test'; giteaRepo = 'PluginA'; giteaPrimary = $true; mirrorWorkflows = 0 }
                @{ repoDir = 'plugin-b'; giteaOwner = 'Test'; giteaRepo = 'PluginB'; giteaPrimary = $true; mirrorWorkflows = 0 }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content $manifest

        $output = & pwsh -NoProfile -File $Verifier -EcosystemRoot $divergentRoot -ManifestPath $manifest -CI *>&1
        $LASTEXITCODE -ne 0 -and (($output -join "`n") -match 'different Common SHAs|Diverged|pin different')
    }

    Test-Assertion 'Single SHA (one plugin passing) is not diverged' {
        $shas = @($FakeCommonSha)
        $r = Test-CommonPinAgreement -Shas $shas
        $r.Diverged -eq $false
    }

    Test-Assertion 'Empty SHA list (no plugins passing) is not diverged' {
        $shas = @()
        $r = Test-CommonPinAgreement -Shas $shas
        $r.Diverged -eq $false
    }

    Write-Host ''
    Write-Host 'Branch-protection status context matching' -ForegroundColor White

    Test-Assertion 'Status context: exact configured context satisfies requirement' {
        Test-StatusContextRequirementSatisfied `
            -RequiredContext 'CI / lint' `
            -ConfiguredContexts @('CI / lint', 'CI / verify')
    }

    Test-Assertion 'Status context: Gitea wildcard context satisfies base requirement' {
        Test-StatusContextRequirementSatisfied `
            -RequiredContext 'CI / lint' `
            -ConfiguredContexts @('CI / lint*', 'CI / verify*')
    }

    Test-Assertion 'Status context: unrelated wildcard does not satisfy requirement' {
        -not (Test-StatusContextRequirementSatisfied `
            -RequiredContext 'CI / build-test' `
            -ConfiguredContexts @('CI / lint*', 'CI / verify*'))
    }

    Test-Assertion 'Branch-protection targets include plugins plus Common' {
        $targets = @(Get-BranchProtectionTargets -Plugins @(
            [pscustomobject]@{ giteaOwner = 'RicherTunes'; giteaRepo = 'Qobuzarr' }
            [pscustomobject]@{ giteaOwner = 'RicherTunes'; giteaRepo = 'Tidalarr' }
        ))

        $repos = @($targets | Select-Object -ExpandProperty Repo)
        $repos -contains 'Qobuzarr' -and
            $repos -contains 'Tidalarr' -and
            $repos -contains 'Lidarr.Plugin.Common'
    }

    Test-Assertion 'Branch-protection targets require Common build-test and secret-scan contexts' {
        $targets = @(Get-BranchProtectionTargets -Plugins @())
        $common = $targets | Where-Object { $_.Repo -eq 'Lidarr.Plugin.Common' } | Select-Object -First 1

        $null -ne $common -and
            $common.RequiredContexts -contains 'CI / lint' -and
            $common.RequiredContexts -contains 'CI / build-test' -and
            $common.RequiredContexts -contains 'CI / secret-scan'
    }

    Write-Host ''
    Write-Host 'Common workflow live ecosystem guard wiring' -ForegroundColor White

    Test-Assertion 'Common Gitea workflow runs the live ecosystem CI contract' {
        $workflow = Get-Content -LiteralPath (Join-Path $RepoRoot '.gitea/workflows/ci.yml') -Raw
        $workflow -match '(?m)^\s+ecosystem-contract:\s*$' -and
            $workflow -match 'verify-ecosystem-ci-contract\.ps1\s+-EcosystemRoot\s+\.\.\s+-CI' -and
            $workflow -match 'GITHUB_EVENT_NAME' -and
            $workflow -match 'GITHUB_REF_NAME'
    }
}
finally {
    # Always remove temp fixtures
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host '========================================' -ForegroundColor Cyan

if ($failed -gt 0) {
    exit 1
}

Write-Host ''
Write-Host '[OK] All ecosystem CI-contract tests passed.' -ForegroundColor Green
exit 0
