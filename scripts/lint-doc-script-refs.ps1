<#
.SYNOPSIS
    Docval gate: fail when documentation references a repo-relative helper
    script (tools/*.ps1 or scripts/*.ps1) that does not exist on disk.

.DESCRIPTION
    A doc that tells a reader to run `./tools/Foo.ps1` after that script has been
    deleted is a silent lie — exactly the drift left behind when a feature is
    removed but its docs are not (e.g. the 2026-06 PublicApiAnalyzers removal,
    which orphaned tools/Update-PublicApiBaselines.ps1 in five docs).

    Scans every tracked Markdown file under the repo root for tokens matching
    `(tools|scripts)/<path>.ps1` and verifies each referenced file exists.
    Reports every missing reference with the doc + line it appears on.

    This is a CLASS gate, not a PublicAPI-specific one: any future doc that
    points at a removed script trips it.

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

# Matches repo-relative PowerShell helper paths inside docs. Optional leading
# "./" is tolerated. Deliberately NOT matching bare names like ManifestCheck.ps1
# (no folder prefix) — those are ambiguous and not resolvable to one location.
$script:RefPattern = '(?<![\w./-])\.?/?(?<path>(?:tools|scripts)/[\w./-]+\.ps1)'

function Get-ScriptRefs {
    param([string]$Text)
    $refs = @()
    foreach ($m in [regex]::Matches($Text, $script:RefPattern)) {
        $refs += $m.Groups['path'].Value
    }
    return $refs | Select-Object -Unique
}

if ($SelfTest) {
    $cases = @(
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
    $fail = 0
    foreach ($c in $cases) {
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
    if ($fail -gt 0) { Write-Host "$fail self-test case(s) failed" -ForegroundColor Red; if ($CI) { exit 1 }; return $false }
    Write-Host "[OK] all self-test cases passed" -ForegroundColor Green
    return $true
}

$RepoRoot = (Resolve-Path $RepoRoot).Path
$mdFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter *.md -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](node_modules|bin|obj|\.git)[\\/]' }

$missing = @()
foreach ($file in $mdFiles) {
    $lines = Get-Content -LiteralPath $file.FullName
    # Whole-file opt-out: planning/roadmap/playbook docs that intentionally name
    # plugin-side or proposed (not-yet-created) scripts declare it inline.
    if (($lines -join "`n") -match 'docval:ignore-script-refs') { continue }
    $lineNo = 0
    foreach ($line in $lines) {
        $lineNo++
        foreach ($m in [regex]::Matches($line, $script:RefPattern)) {
            $rel = $m.Groups['path'].Value
            if (-not (Test-Path (Join-Path $RepoRoot $rel))) {
                $missing += [pscustomobject]@{
                    Doc  = [IO.Path]::GetRelativePath($RepoRoot, $file.FullName)
                    Line = $lineNo
                    Ref  = $rel
                }
            }
        }
    }
}

Write-Host ""
Write-Host "=== Doc Script-Reference Sentinel ===" -ForegroundColor Cyan
if ($missing.Count -gt 0) {
    Write-Host "[MISSING] docs reference helper scripts that do not exist:" -ForegroundColor Red
    foreach ($x in $missing) {
        Write-Host "  - $($x.Doc):$($x.Line) -> $($x.Ref)" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Fix: update the doc, or restore the script it points to." -ForegroundColor Cyan
    if ($CI) { exit 1 }
    return $false
}

Write-Host "[OK] every tools/*.ps1 and scripts/*.ps1 reference in docs resolves" -ForegroundColor Green
return $true
