<#
.SYNOPSIS
    Docval gate: fail when documentation references a repo-relative helper
    script (tools/*.ps1 or scripts/*.ps1) or CI workflow file
    (.github/workflows/*.yml or .gitea/workflows/*.yml) that does not exist
    on disk.

.DESCRIPTION
    A doc that tells a reader to run `./tools/Foo.ps1` after that script has been
    deleted is a silent lie — exactly the drift left behind when a feature is
    removed but its docs are not (e.g. the 2026-06 PublicApiAnalyzers removal,
    which orphaned tools/Update-PublicApiBaselines.ps1 in five docs).

    Similarly, a doc that references `.github/workflows/ci.yml` after that workflow
    has been renamed or removed is silent drift that misleads contributors.

    Two passes over every tracked Markdown file under the repo root:
      Pass 1 — Script refs: `(tools|scripts)/<path>.ps1`
      Pass 2 — Workflow refs: `(.github|.gitea)/workflows/<path>.yml|.yaml`

    Each pass reports every missing reference with the doc + line it appears on.

    Opt-outs (whole-file):
      docval:ignore-script-refs   — suppresses Pass 1 for that file
      docval:ignore-workflow-refs — suppresses Pass 2 for that file

    This is a CLASS gate, not a PublicAPI-specific one: any future doc that
    points at a removed script or workflow trips it.

.PARAMETER RepoRoot
    Repository root to scan. Defaults to the parent of this script's folder.

.PARAMETER SelfTest
    Run the built-in fixtures instead of scanning the repo. Exits non-zero on
    any fixture failure.

.PARAMETER CI
    Exit with code 1 on any missing reference (otherwise returns $false).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$SelfTest,
    [switch]$CI
)

$ErrorActionPreference = "Stop"

# Pass 1: repo-relative PowerShell helper paths inside docs.
# Optional leading "./" is tolerated. Bare names (no folder prefix) are ignored.
$script:RefPattern = '(?<![\w./-])\.?/?(?<path>(?:tools|scripts)/[\w./-]+\.ps1)'

# Pass 2: CI workflow file references (.github/workflows/*.yml|.yaml or
# .gitea/workflows/*.yml|.yaml). Excludes cross-repo prefixed paths like
# `brainarr/.github/workflows/foo.yml` via the look-behind (`.` after `/`).
$script:WorkflowRefPattern = '(?<![\w./-])(?<path>\.(?:github|gitea)/workflows/[\w./-]+\.ya?ml)(?![\w.])'

function Get-ScriptRefs {
    param([string]$Text)
    $refs = @()
    foreach ($m in [regex]::Matches($Text, $script:RefPattern)) {
        $refs += $m.Groups['path'].Value
    }
    return $refs | Select-Object -Unique
}

function Get-WorkflowRefs {
    param([string]$Text)
    $refs = @()
    foreach ($m in [regex]::Matches($Text, $script:WorkflowRefPattern)) {
        $refs += $m.Groups['path'].Value
    }
    return $refs | Select-Object -Unique
}

if ($SelfTest) {
    # ── Script-ref pattern cases ─────────────────────────────────────────────
    $scriptCases = @(
        @{ Name = "plain ref";        Text = "Run ./tools/Foo.ps1 now.";            Expect = @("tools/Foo.ps1") }
        @{ Name = "no leading slash"; Text = "see scripts/lint.ps1 for details";    Expect = @("scripts/lint.ps1") }
        @{ Name = "nested path";      Text = "pwsh tools/DocTools/lint-docs.ps1";   Expect = @("tools/DocTools/lint-docs.ps1") }
        @{ Name = "backtick code";    Text = '`./scripts/test.ps1` runs tests';     Expect = @("scripts/test.ps1") }
        @{ Name = "bare name ignored";Text = "confirm ManifestCheck.ps1 passes";    Expect = @() }
        @{ Name = "non-ps1 ignored";  Text = "edit tools/config.json by hand";      Expect = @() }
        @{ Name = "dedup";            Text = "tools/A.ps1 then tools/A.ps1 again";   Expect = @("tools/A.ps1") }
        @{ Name = "two distinct";     Text = "tools/A.ps1 and scripts/B.ps1";        Expect = @("tools/A.ps1","scripts/B.ps1") }
        @{ Name = "psm1 ignored";     Text = "Import-Module ./tools/PluginPack.psm1";Expect = @() }
    )

    # ── Workflow-ref pattern cases ────────────────────────────────────────────
    $workflowCases = @(
        @{ Name = "gitea workflow";         Text = 'see `.gitea/workflows/ci.yml`';                  Expect = @(".gitea/workflows/ci.yml") }
        @{ Name = "github workflow";        Text = 'run `.github/workflows/release.yml`';            Expect = @(".github/workflows/release.yml") }
        @{ Name = "yaml extension";         Text = 'file `.github/workflows/build.yaml`';            Expect = @(".github/workflows/build.yaml") }
        @{ Name = "no leading dot-slash";   Text = '.gitea/workflows/ci.yml is the gate';            Expect = @(".gitea/workflows/ci.yml") }
        @{ Name = "cross-repo excluded";    Text = 'brainarr/.github/workflows/ci.yml';             Expect = @() }
        @{ Name = "url excluded";           Text = 'uses: Org/Repo/.github/workflows/foo.yml@main'; Expect = @() }
        @{ Name = "link excluded";          Text = '[ci](../.github/workflows/ci.yml)';             Expect = @() }
        @{ Name = "deep path";             Text = '.github/workflows/e2e/bootstrap.yml';            Expect = @(".github/workflows/e2e/bootstrap.yml") }
        @{ Name = "dedup workflow";         Text = '.github/workflows/ci.yml and .github/workflows/ci.yml'; Expect = @(".github/workflows/ci.yml") }
        @{ Name = "non-workflow ignored";   Text = '.github/workflows/ci.yml.bak extra';            Expect = @() }
    )

    $fail = 0

    Write-Host "--- Script-ref cases ---"
    foreach ($c in $scriptCases) {
        $got = @(Get-ScriptRefs -Text $c.Text)
        $exp = @($c.Expect)
        $ok = ($got.Count -eq $exp.Count) -and (-not (Compare-Object $got $exp))
        if ($ok) {
            Write-Host "[PASS] $($c.Name)" -ForegroundColor Green
        } else {
            Write-Host "[FAIL] $($c.Name): expected [$($exp -join ', ')] got [$($got -join ', ')]" -ForegroundColor Red
            $fail++
        }
    }

    Write-Host "--- Workflow-ref cases ---"
    foreach ($c in $workflowCases) {
        $got = @(Get-WorkflowRefs -Text $c.Text)
        $exp = @($c.Expect)
        $ok = ($got.Count -eq $exp.Count) -and (-not (Compare-Object $got $exp))
        if ($ok) {
            Write-Host "[PASS] $($c.Name)" -ForegroundColor Green
        } else {
            Write-Host "[FAIL] $($c.Name): expected [$($exp -join ', ')] got [$($got -join ', ')]" -ForegroundColor Red
            $fail++
        }
    }

    if ($fail -gt 0) { Write-Host "$fail self-test case(s) failed" -ForegroundColor Red; if ($CI) { exit 1 }; return $false }
    Write-Host "[OK] all self-test cases passed" -ForegroundColor Green
    return $true
}

$RepoRoot = (Resolve-Path $RepoRoot).Path
$mdFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch '[\\/](node_modules|bin|obj|\.git|ext|\.worktrees)[\\/]' -and
        $_.FullName -notmatch '[\\/]\.claude[\\/]worktrees[\\/]'
    }

$missingScript   = @()
$missingWorkflow = @()

foreach ($file in $mdFiles) {
    $lines = Get-Content -LiteralPath $file.FullName
    $fullText = $lines -join "`n"
    $ignoreScript   = $fullText -match 'docval:ignore-script-refs'
    $ignoreWorkflow = $fullText -match 'docval:ignore-workflow-refs'

    $lineNo = 0
    foreach ($line in $lines) {
        $lineNo++

        # Pass 1: script refs
        if (-not $ignoreScript) {
            foreach ($m in [regex]::Matches($line, $script:RefPattern)) {
                $rel = $m.Groups['path'].Value
                if (-not (Test-Path (Join-Path $RepoRoot $rel))) {
                    $missingScript += [pscustomobject]@{
                        Doc  = [IO.Path]::GetRelativePath($RepoRoot, $file.FullName)
                        Line = $lineNo
                        Ref  = $rel
                    }
                }
            }
        }

        # Pass 2: workflow refs
        if (-not $ignoreWorkflow) {
            foreach ($m in [regex]::Matches($line, $script:WorkflowRefPattern)) {
                $rel = $m.Groups['path'].Value
                if (-not (Test-Path (Join-Path $RepoRoot $rel))) {
                    $missingWorkflow += [pscustomobject]@{
                        Doc  = [IO.Path]::GetRelativePath($RepoRoot, $file.FullName)
                        Line = $lineNo
                        Ref  = $rel
                    }
                }
            }
        }
    }
}

Write-Host ""
Write-Host "=== Doc Script/Workflow-Reference Sentinel ===" -ForegroundColor Cyan

$anyMissing = $false

if ($missingScript.Count -gt 0) {
    $anyMissing = $true
    Write-Host "[MISSING] docs reference helper scripts that do not exist:" -ForegroundColor Red
    foreach ($x in $missingScript) {
        Write-Host "  - $($x.Doc):$($x.Line) -> $($x.Ref)" -ForegroundColor Red
    }
}

if ($missingWorkflow.Count -gt 0) {
    $anyMissing = $true
    Write-Host "[MISSING] docs reference CI workflow files that do not exist:" -ForegroundColor Red
    foreach ($x in $missingWorkflow) {
        Write-Host "  - $($x.Doc):$($x.Line) -> $($x.Ref)" -ForegroundColor Red
    }
}

if ($anyMissing) {
    Write-Host ""
    Write-Host "Fix: update the doc, restore the file it points to, or add a docval:ignore-* comment." -ForegroundColor Cyan
    if ($CI) { exit 1 }
    return $false
}

Write-Host "[OK] every tools/*.ps1, scripts/*.ps1, and .{github,gitea}/workflows/*.yml reference in docs resolves" -ForegroundColor Green
return $true
