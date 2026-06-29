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
            [bool]$WireDocRefs = $true,
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
        git -C $Dir update-index --add --cacheinfo "160000,$FakeCommonSha,ext/Lidarr.Plugin.Common" 2>$null | Out-Null
        $gitmodulesContent = "[submodule `"ext/Lidarr.Plugin.Common`"]`n`tpath = ext/Lidarr.Plugin.Common`n`turl = ../fake-common"
        [System.IO.File]::WriteAllText((Join-Path $Dir '.gitmodules'), $gitmodulesContent)

        # Write sentinel (may be intentionally wrong)
        $shaBytes = [System.Text.Encoding]::ASCII.GetBytes($SentinelSha + "`n")
        [System.IO.File]::WriteAllBytes((Join-Path $Dir 'ext-common-sha.txt'), $shaBytes)

        # Create .gitea/workflows/ci.yml
        New-Item -Path (Join-Path $Dir '.gitea/workflows') -ItemType Directory -Force | Out-Null
        $ciYmlPath = Join-Path $Dir '.gitea/workflows/ci.yml'
        if ($WireDocRefs) {
            Set-Content $ciYmlPath 'name: CI
jobs:
  lint:
    steps:
      - name: Doc-refs lint
        run: pwsh ./ext/Lidarr.Plugin.Common/scripts/lint-doc-script-refs.ps1 -RepoRoot . -CI'
        }
        else {
            Set-Content $ciYmlPath 'name: CI
jobs:
  lint:
    steps:
      - name: Build
        run: pwsh ./scripts/some-other-script.ps1'
        }

        # Create GitHub workflows if needed
        if ($GithubWorkflowCount -gt 0) {
            New-Item -Path (Join-Path $Dir '.github/workflows') -ItemType Directory -Force | Out-Null
            for ($i = 0; $i -lt $GithubWorkflowCount; $i++) {
                Set-Content (Join-Path $Dir ".github/workflows/extra$i.yml") "name: Extra$i"
            }
        }

        git -C $Dir add --all 2>$null | Out-Null
        git -C $Dir commit -m 'init' --quiet 2>$null | Out-Null
    }

    # --- Plugin A: everything correct (PASS) -----------------------------
    $DirA = Join-Path $TempDir 'plugin-pass'
    New-FakePlugin -Dir $DirA -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 0

    # --- Plugin B: sentinel mismatch (FAIL) ------------------------------
    $BadSha  = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $DirB    = Join-Path $TempDir 'plugin-bad-pin'
    New-FakePlugin -Dir $DirB -SentinelSha $BadSha -WireDocRefs $true -GithubWorkflowCount 0

    # --- Plugin C: no doc-refs lint in ci.yml (FAIL) ---------------------
    $DirC = Join-Path $TempDir 'plugin-no-docref'
    New-FakePlugin -Dir $DirC -SentinelSha $FakeCommonSha -WireDocRefs $false -GithubWorkflowCount 0

    # --- Plugin D: undeclared .github/workflows file (FAIL) --------------
    # manifest expects 0 but dir has 1
    $DirD = Join-Path $TempDir 'plugin-extra-wf'
    New-FakePlugin -Dir $DirD -SentinelSha $FakeCommonSha -WireDocRefs $true -GithubWorkflowCount 1

    # --- Write a fake manifest for the fake ecosystem --------------------
    $FakeManifestPath = Join-Path $TempDir 'fake-manifest.json'
    @{
        plugins = @(
            @{ repoDir = 'plugin-pass';      giteaOwner = 'Test'; giteaRepo = 'PluginPass';    giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-bad-pin';   giteaOwner = 'Test'; giteaRepo = 'PluginBadPin';  giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-no-docref'; giteaOwner = 'Test'; giteaRepo = 'PluginNoDocRef'; giteaPrimary = $true; mirrorWorkflows = 0 }
            @{ repoDir = 'plugin-extra-wf';  giteaOwner = 'Test'; giteaRepo = 'PluginExtraWf'; giteaPrimary = $true; mirrorWorkflows = 0 }
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

    # ============================================================
    # Warn-not-fail: cross-plugin SHA agreement divergence
    # ============================================================

    Write-Host ''
    Write-Host 'Cross-plugin SHA agreement (warn, not fail)' -ForegroundColor White

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
