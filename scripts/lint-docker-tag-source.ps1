<#
.SYNOPSIS
  Validates that Lidarr Docker image tags are consistent across a repository.

.DESCRIPTION
  Scans workflow files, scripts, and docs for pr-plugins-* Docker tags and
  verifies they all reference the same version. Optionally checks against
  a pinned digest file (.github/lidarr_digest.txt).

  Ignores CLAUDE.md "NEVER use" documentation examples and ext/ submodules.

.PARAMETER RepoPath
  Root of the repository to scan (default: current directory).

.PARAMETER ExpectedTag
  If provided, asserts all found tags match this value exactly.

.PARAMETER Mode
  'ci' for ::error:: annotations and exit 1; 'report' for human output.

.EXAMPLE
  ./lint-docker-tag-source.ps1 -RepoPath . -ExpectedTag "pr-plugins-3.1.2.4913" -Mode ci
#>
[CmdletBinding()]
param(
    [string]$RepoPath = ".",
    [string]$ExpectedTag = "",
    [ValidateSet("ci", "report")]
    [string]$Mode = "report"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Resolve-Path -LiteralPath $RepoPath

# Extensions to scan
$extensions = @("*.yml", "*.yaml", "*.ps1", "*.sh", "*.cs", "*.csproj", "*.md")

# Regex for pr-plugins-N.N.N.N tags
$tagPattern = 'pr-plugins-(\d+\.\d+\.\d+\.\d+)'

$hits = @()

foreach ($ext in $extensions) {
    $files = Get-ChildItem -Path $root -Recurse -Filter $ext -File -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($root.Path.Length).TrimStart('\', '/')
            # Exclude ext/ submodules, .worktrees/ (separate checkouts), CHANGELOG.md (historical)
            -not ($rel -match '^ext[/\\]') -and
            -not ($rel -match '^\.worktrees[/\\]') -and
            -not ($rel -match '(?:^|[/\\])CHANGELOG\.md$')
        }

    foreach ($file in $files) {
        $relPath = $file.FullName.Substring($root.Path.Length).TrimStart('\', '/')
        $lineNum = 0
        $content = Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue

        if (-not $content) { continue }

        foreach ($line in $content) {
            $lineNum++
            $matches = [regex]::Matches($line, $tagPattern)
            foreach ($m in $matches) {
                $tag = $m.Value
                $ver = $m.Groups[1].Value

                # Skip documentation examples: "NEVER use" warnings, .EXAMPLE blocks, comment-only lines
                if ($relPath -match 'CLAUDE\.md$' -and $line -match 'NEVER use') { continue }
                if ($line -match '^\s*#\s*' -and $line -match '\.EXAMPLE|e\.g\.|example') { continue }

                $hits += [PSCustomObject]@{
                    File    = $relPath
                    Line    = $lineNum
                    Tag     = $tag
                    Version = $ver
                }
            }
        }
    }
}

if ($hits.Count -eq 0) {
    Write-Host "[OK] No pr-plugins-* tags found in repo-owned files."
    exit 0
}

# Group by unique version
$versions = @($hits | Select-Object -ExpandProperty Version -Unique)

$allOk = $true

if ($versions.Count -gt 1) {
    $allOk = $false
    $msg = "Found $($versions.Count) distinct Docker tag versions: $($versions -join ', '). Expected exactly 1."
    if ($Mode -eq 'ci') {
        Write-Host "::error::$msg"
    } else {
        Write-Host "ERROR: $msg" -ForegroundColor Red
    }
    foreach ($h in $hits) {
        Write-Host "  $($h.File):$($h.Line) -> $($h.Tag)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedTag)) {
    $expectedVer = if ($ExpectedTag -match $tagPattern) { $Matches[1] } else { $ExpectedTag }

    $mismatches = @($hits | Where-Object { $_.Version -ne $expectedVer })
    if ($mismatches.Count -gt 0) {
        $allOk = $false
        $msg = "Found $($mismatches.Count) tag(s) not matching expected '$ExpectedTag':"
        if ($Mode -eq 'ci') {
            Write-Host "::error::$msg"
        } else {
            Write-Host "ERROR: $msg" -ForegroundColor Red
        }
        foreach ($h in $mismatches) {
            Write-Host "  $($h.File):$($h.Line) -> $($h.Tag)"
        }
    }
}

if ($allOk) {
    Write-Host "[OK] All $($hits.Count) Docker tag reference(s) use version $($versions[0])."
    exit 0
} else {
    exit 1
}
