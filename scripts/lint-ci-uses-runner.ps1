#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint workflow files to ensure they use the unified test runner.

.DESCRIPTION
    Scans GitHub Actions workflow files for raw `dotnet test` calls that should
    instead use the unified test runner (ext/Lidarr.Plugin.Common/scripts/test.ps1).

    This prevents drift where workflows define their own filter logic instead of
    using the centralized runner that handles:
    - Category exclusions (Integration, Packaging, LibraryLinking, Benchmark, Slow)
    - State=Quarantined exclusion
    - Consistent CI annotations and TRX parsing

.PARAMETER Path
    Root path of the repository to scan. Defaults to current directory.

.PARAMETER AllowlistPath
    Path to a JSON file containing allowlisted patterns. If not specified,
    looks for .github/test-runner-allowlist.json in the repo.

.PARAMETER CI
    Enable CI mode with GitHub Actions annotations and non-zero exit on violations.

.PARAMETER Fix
    Show suggested fixes for each violation (does not auto-fix).

.EXAMPLE
    ./lint-ci-uses-runner.ps1 -Path /path/to/repo -CI
    # Runs in CI mode, fails if violations found

.EXAMPLE
    ./lint-ci-uses-runner.ps1 -Fix
    # Shows violations with suggested fixes

.NOTES
    Allowlist format (.github/test-runner-allowlist.json):
    {
      "patterns": [
        { "file": "mutation-tests.yml", "reason": "Stryker requires direct dotnet test invocation" },
        { "file": "registry.yml", "line_pattern": "--list-tests", "reason": "Test discovery, not execution" }
      ]
    }
#>

[CmdletBinding()]
param(
    [string]$Path = ".",
    [string]$AllowlistPath = "",
    [switch]$CI,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

#region Allowlist Loading

function Get-Allowlist {
    param([string]$RepoPath, [string]$ExplicitPath)

    $allowlistFile = if ($ExplicitPath) {
        $ExplicitPath
    } else {
        Join-Path $RepoPath ".github/test-runner-allowlist.json"
    }

    if (Test-Path $allowlistFile) {
        try {
            $content = Get-Content $allowlistFile -Raw | ConvertFrom-Json
            # Ensure patterns is always an array (PowerShell may unwrap single-item arrays)
            $patterns = @($content.patterns)
            return $patterns
        } catch {
            Write-Warning "Failed to parse allowlist at ${allowlistFile}: $_"
            return @()
        }
    }

    # Default allowlist for common exceptions
    return @(
        @{ file = "*.yml"; line_pattern = "--list-tests"; reason = "Test discovery, not execution" }
    )
}

function Test-Allowlisted {
    param(
        [string]$FileName,
        [string]$Line,
        [int]$LineNumber,
        [array]$Allowlist,
        [ref]$ExpiredEntries
    )

    foreach ($entry in $Allowlist) {
        # Safely access properties (PSCustomObject from JSON may not have all fields)
        $entryFile = $null
        $entryLinePattern = $null
        $entryExpiresOn = $null
        $entryOwner = $null
        $entryReason = $null

        if ($entry.PSObject.Properties['file']) { $entryFile = $entry.file }
        if ($entry.PSObject.Properties['line_pattern']) { $entryLinePattern = $entry.line_pattern }
        if ($entry.PSObject.Properties['expiresOn']) { $entryExpiresOn = $entry.expiresOn }
        if ($entry.PSObject.Properties['owner']) { $entryOwner = $entry.owner }
        if ($entry.PSObject.Properties['reason']) { $entryReason = $entry.reason }

        # Check file pattern match
        $fileMatch = $false
        if ($entryFile) {
            if ($entryFile -like "*") {
                $fileMatch = $FileName -like $entryFile
            } else {
                $fileMatch = $FileName -eq $entryFile
            }
        } else {
            $fileMatch = $true  # No file restriction
        }

        if (-not $fileMatch) { continue }

        # Check line pattern match (if specified)
        $lineMatch = $false
        if ($entryLinePattern) {
            if ($Line -match [regex]::Escape($entryLinePattern)) {
                $lineMatch = $true
            }
        } else {
            # File-level allowlist (entire file is exempt)
            $lineMatch = $true
        }

        if (-not $lineMatch) { continue }

        # Check expiration
        if ($entryExpiresOn) {
            try {
                $expiryDate = [DateTime]::Parse($entryExpiresOn)
                if ($expiryDate -lt (Get-Date)) {
                    # Expired exemption - track it and don't allow
                    if ($ExpiredEntries) {
                        $ExpiredEntries.Value += @{
                            File = $FileName
                            Line = $LineNumber
                            Entry = $entry
                            ExpiredOn = $expiryDate
                        }
                    }
                    continue  # Don't allow expired exemptions
                }
            } catch {
                Write-Warning "Invalid expiresOn date '$entryExpiresOn' in allowlist entry for $entryFile"
            }
        }

        return @{ Allowed = $true; Reason = $entryReason; Owner = $entryOwner }
    }

    return @{ Allowed = $false; Reason = $null }
}

#endregion

#region Workflow Scanning

function Find-DotnetTestViolations {
    param(
        [string]$WorkflowDir,
        [array]$Allowlist,
        [ref]$ExpiredExemptions
    )

    $violations = @()
    $expiredEntries = @()

    $workflowFiles = Get-ChildItem -Path $WorkflowDir -Filter "*.yml" -File -ErrorAction SilentlyContinue
    $workflowFiles += Get-ChildItem -Path $WorkflowDir -Filter "*.yaml" -File -ErrorAction SilentlyContinue

    foreach ($file in $workflowFiles) {
        $lines = Get-Content $file.FullName -ErrorAction SilentlyContinue
        if (-not $lines) { continue }

        # Join lines for multi-line block detection
        $fullContent = $lines -join "`n"

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $lineNum = $i + 1

            # Match dotnet test invocations (various patterns)
            # - Direct: dotnet test
            # - With timeout: timeout 25m dotnet test
            # - Run block: run: dotnet test
            # - Run block continuation: dotnet test (after run: |)
            # - Inside multi-line bash/pwsh blocks
            if ($line -match '\bdotnet\s+test\b') {
                $expiredRef = [ref]$expiredEntries
                $allowResult = Test-Allowlisted -FileName $file.Name -Line $line -LineNumber $lineNum -Allowlist $Allowlist -ExpiredEntries $expiredRef
                $expiredEntries = $expiredRef.Value

                if (-not $allowResult.Allowed) {
                    $violations += @{
                        File = $file.FullName
                        FileName = $file.Name
                        Line = $lineNum
                        Content = $line.Trim()
                        Context = Get-LineContext -Lines $lines -LineIndex $i
                    }
                }
            }
        }
    }

    if ($ExpiredExemptions) {
        $ExpiredExemptions.Value = $expiredEntries
    }

    return $violations
}

function Get-LineContext {
    param(
        [array]$Lines,
        [int]$LineIndex,
        [int]$ContextLines = 2
    )

    $start = [Math]::Max(0, $LineIndex - $ContextLines)
    $end = [Math]::Min($Lines.Count - 1, $LineIndex + $ContextLines)

    $context = @()
    for ($i = $start; $i -le $end; $i++) {
        $prefix = if ($i -eq $LineIndex) { ">>> " } else { "    " }
        $context += "$prefix$($i + 1): $($Lines[$i])"
    }

    return $context -join "`n"
}

#endregion

#region Suggested Fixes

function Get-SuggestedFix {
    param([string]$Content)

    # Analyze the dotnet test command to suggest unified runner equivalent
    $suggestion = @{
        Command = 'ext/Lidarr.Plugin.Common/scripts/test.ps1'
        Params = @()
    }

    # Detect common patterns
    if ($Content -match '--configuration\s+Release') {
        $suggestion.Params += '-Configuration Release'
    }

    if ($Content -match '--no-build') {
        $suggestion.Params += '-NoBuild'
    }

    if ($Content -match '--collect.*XPlat Code Coverage') {
        $suggestion.Params += '-Coverage'
    }

    if ($Content -match '--filter\s+"?Category=Integration"?') {
        $suggestion.Params += '-Category Integration'
    } elseif ($Content -match '--filter\s+"?Category=Packaging"?') {
        $suggestion.Params += '-Category Packaging'
    } elseif ($Content -match '--filter\s+"?Category=Benchmark"?') {
        $suggestion.Params += '-Category Benchmark'
    }

    if ($Content -match '--results-directory\s+(\S+)') {
        $dir = $Matches[1]
        $suggestion.Params += "-OutputDir $dir"
    }

    # Always suggest -CI for workflow context
    $suggestion.Params += '-CI'

    $paramStr = if ($suggestion.Params.Count -gt 0) { " " + ($suggestion.Params -join " ") } else { "" }

    return @"
Suggested replacement:
  shell: pwsh
  run: |
    `$script = "$($suggestion.Command)"
    if (Test-Path `$script) {
      & `$script$paramStr
    } else {
      Write-Error "FATAL: Unified test runner not found at `$script"
      exit 1
    }
"@
}

#endregion

#region Main Execution

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Unified Test Runner Adoption Lint" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$repoPath = Resolve-Path $Path
Write-Host "Repository: $repoPath" -ForegroundColor White

$workflowDir = Join-Path $repoPath ".github/workflows"
if (-not (Test-Path $workflowDir)) {
    Write-Host "No .github/workflows directory found - skipping" -ForegroundColor Yellow
    exit 0
}

Write-Host "Scanning: $workflowDir" -ForegroundColor White
Write-Host ""

# Load allowlist
$allowlist = Get-Allowlist -RepoPath $repoPath -ExplicitPath $AllowlistPath
Write-Host "Allowlist entries: $($allowlist.Count)" -ForegroundColor DarkGray

# Find violations
$expiredExemptions = @()
$expiredRef = [ref]$expiredExemptions
$violations = @(Find-DotnetTestViolations -WorkflowDir $workflowDir -Allowlist $allowlist -ExpiredExemptions $expiredRef)
$expiredExemptions = @($expiredRef.Value)

Write-Host ""

# Report expired exemptions first (these are critical)
if ($expiredExemptions.Count -gt 0) {
    Write-Host "[ERROR] Found $($expiredExemptions.Count) expired allowlist exemption(s)" -ForegroundColor Red
    Write-Host ""
    Write-Host "These exemptions have expired and must be addressed:" -ForegroundColor Red
    Write-Host ""

    foreach ($exp in $expiredExemptions) {
        $owner = if ($exp.Entry.owner) { " (owner: $($exp.Entry.owner))" } else { "" }
        Write-Host "  - $($exp.Entry.file): expired on $($exp.ExpiredOn.ToString('yyyy-MM-dd'))$owner" -ForegroundColor Yellow
        Write-Host "    Reason: $($exp.Entry.reason)" -ForegroundColor DarkGray

        if ($CI) {
            Write-Host "::error::Expired allowlist exemption: $($exp.Entry.file) expired on $($exp.ExpiredOn.ToString('yyyy-MM-dd'))"
        }
    }
    Write-Host ""
}

if ($violations.Count -eq 0 -and $expiredExemptions.Count -eq 0) {
    Write-Host "[PASS] No raw 'dotnet test' calls found in workflows" -ForegroundColor Green
    Write-Host "All test execution uses the unified test runner." -ForegroundColor Green
    exit 0
}

if ($violations.Count -eq 0 -and $expiredExemptions.Count -gt 0) {
    Write-Host "[FAIL] Expired allowlist exemptions must be resolved" -ForegroundColor Red
    exit 1
}

# Report violations
Write-Host "[WARN] Found $($violations.Count) raw 'dotnet test' call(s)" -ForegroundColor Yellow
Write-Host ""
Write-Host "These should be migrated to the unified test runner:" -ForegroundColor Yellow
Write-Host "  ext/Lidarr.Plugin.Common/scripts/test.ps1" -ForegroundColor Cyan
Write-Host ""

foreach ($v in $violations) {
    $relPath = $v.File.Replace($repoPath.Path, "").TrimStart("\", "/")

    if ($CI) {
        # GitHub Actions annotation
        Write-Host "::warning file=$relPath,line=$($v.Line)::Raw 'dotnet test' should use unified runner"
    }

    Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
    Write-Host "File: $relPath" -ForegroundColor White
    Write-Host "Line: $($v.Line)" -ForegroundColor White
    Write-Host ""
    Write-Host $v.Context -ForegroundColor Gray
    Write-Host ""

    if ($Fix) {
        Write-Host (Get-SuggestedFix -Content $v.Content) -ForegroundColor Cyan
        Write-Host ""
    }
}

Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

# Summary
Write-Host "Summary:" -ForegroundColor White
Write-Host "  Violations: $($violations.Count)" -ForegroundColor $(if ($violations.Count -gt 0) { "Yellow" } else { "Green" })
if ($expiredExemptions.Count -gt 0) {
    Write-Host "  Expired exemptions: $($expiredExemptions.Count)" -ForegroundColor Red
}
Write-Host ""

if ($Fix) {
    Write-Host "To fix: Replace raw 'dotnet test' calls with the unified runner." -ForegroundColor Cyan
    Write-Host "See: ext/Lidarr.Plugin.Common/docs/UNIFIED_RUNNER_ADOPTION.md" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Allowlist format (.github/test-runner-allowlist.json):" -ForegroundColor DarkGray
Write-Host '  { "patterns": [{ "file": "*.yml", "expiresOn": "2025-03-01", "owner": "user", "reason": "..." }] }' -ForegroundColor DarkGray
Write-Host ""

$hasFailure = ($violations.Count -gt 0) -or ($expiredExemptions.Count -gt 0)

if ($CI -and $hasFailure) {
    Write-Host "[FAIL] Workflow lint failed - migrate to unified test runner" -ForegroundColor Red
    exit 1
} elseif ($hasFailure) {
    Write-Host "[WARN] Run with -CI to fail on violations" -ForegroundColor Yellow
    exit 0
} else {
    exit 0
}

#endregion
