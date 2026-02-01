#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates xUnit test trait categories across plugin test files.

.DESCRIPTION
    Scans all *.cs files in tests/ directories and validates that [Trait("Category", "...")]
    attributes use only approved category names. Reports unknown categories and exits with
    non-zero status for CI integration.

    This prevents flaky tests caused by unknown categories or missing "Slow" tags on
    timing-sensitive tests.

    NOTE: This script only validates Category traits. State traits (e.g., "State=Quarantined")
    are managed separately and are NOT flagged by this linter. State traits describe test
    health status, while Category traits describe test behavior.

.PARAMETER Path
    Root path to scan for test files. Defaults to current directory.

.PARAMETER Recurse
    Scan subdirectories recursively. Default: $true.

.PARAMETER AllowedCategories
    Additional allowed categories beyond the built-in list (array).

.PARAMETER CI
    Strict mode for CI: fails on any unknown category. Without this flag,
    warnings are shown but exit code is 0 unless critical issues found.

.PARAMETER Fix
    Suggests adding unknown categories to an allowlist configuration.

.PARAMETER Verbose
    Show detailed output including all files scanned.

.EXAMPLE
    ./lint-test-categories.ps1
    Scans current directory with default approved categories.

.EXAMPLE
    ./lint-test-categories.ps1 -Path D:\repos\myproject -CI
    Scans specified path in strict CI mode.

.EXAMPLE
    ./lint-test-categories.ps1 -AllowedCategories @("CustomCategory", "MyTests")
    Adds custom categories to the approved list.

.EXAMPLE
    ./lint-test-categories.ps1 -Fix
    Shows suggestions for adding unknown categories to allowlist.

.NOTES
    Approved Categories (built-in):
    - Integration     : External service tests
    - Packaging       : ILRepack/merged assembly tests
    - LibraryLinking  : Assembly isolation tests
    - Benchmark       : Performance measurement tests
    - Slow            : Tests taking >5s or with timing dependencies

    State Traits (separate from categories):
    - State=Quarantined : Temporarily disabled tests (managed by manage-quarantine.ps1)

    State traits are orthogonal to categories. A test can have both:
    [Trait("Category", "Integration")]
    [Trait("State", "Quarantined")]
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path = (Get-Location).Path,

    [switch]$Recurse = $true,

    [string[]]$AllowedCategories = @(),

    [switch]$CI,

    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

#region Configuration

# Built-in approved categories with descriptions
$script:ApprovedCategories = @{
    'Integration'     = 'External service tests'
    'Packaging'       = 'ILRepack/merged assembly tests'
    'LibraryLinking'  = 'Assembly isolation tests'
    'Benchmark'       = 'Performance measurement tests'
    'Slow'            = 'Tests taking >5s or with timing dependencies'
}

# Directories to exclude from scanning
$script:ExcludeDirs = @('bin', 'obj', '.git', '.worktrees', 'node_modules', 'ext')

# Regex pattern to match [Trait("Category", "value")]
$script:TraitPattern = '\[Trait\s*\(\s*"Category"\s*,\s*"([^"]+)"\s*\)\]'

# Regex pattern to match [Trait("State", "value")] - for informational reporting only
$script:StateTraitPattern = '\[Trait\s*\(\s*"State"\s*,\s*"([^"]+)"\s*\)\]'

# Valid State trait values (not enforced, just documented)
$script:ValidStateTraits = @{
    'Quarantined' = 'Temporarily disabled tests (flaky, broken, environment-dependent)'
}

#endregion

#region Helper Functions

function Normalize-Path {
    <#
    .SYNOPSIS
        Normalizes path separators to forward slashes for consistent output.
    #>
    param([string]$FilePath)
    return $FilePath -replace '\\', '/'
}

function Test-IsExcludedPath {
    <#
    .SYNOPSIS
        Checks if a path should be excluded from scanning.
    #>
    param([string]$RelPath)

    $normalized = Normalize-Path $RelPath
    $parts = $normalized -split '/'

    foreach ($part in $parts) {
        if ($part -in $script:ExcludeDirs) {
            return $true
        }
    }

    # Exclude generated files
    if ($normalized -match '\.g\.cs$') {
        return $true
    }

    return $false
}

function Test-IsInTestsDirectory {
    <#
    .SYNOPSIS
        Checks if a file is within a tests/ or *.Tests/ directory.
    .DESCRIPTION
        Matches common test directory patterns:
        - tests/           : Standard test directory
        - *.Tests/         : .NET convention (e.g., MyProject.Tests/)
        - *Tests/          : Alternative naming (e.g., UnitTests/)
    #>
    param([string]$RelPath)

    $normalized = (Normalize-Path $RelPath).ToLowerInvariant()

    # Match: tests/, *.Tests/, or *Tests/ directory patterns
    return $normalized -match '(^|/)(tests|[^/]*\.tests|[^/]*tests)/'
}

function Get-LineNumber {
    <#
    .SYNOPSIS
        Gets the line number for a match index in content.
    #>
    param(
        [string]$Content,
        [int]$Index
    )

    return ($Content.Substring(0, $Index) -split "`n").Count
}

function Find-CategoryViolations {
    <#
    .SYNOPSIS
        Scans files for trait category violations and State traits.
    #>
    param(
        [string]$RootPath,
        [hashtable]$AllAllowed
    )

    $violations = @()
    $allCategories = @{}
    $allStateTraits = @{}
    $filesScanned = 0

    # Get all .cs files
    $searchParams = @{
        Path = $RootPath
        Filter = '*.cs'
        File = $true
        ErrorAction = 'SilentlyContinue'
    }
    if ($Recurse) {
        $searchParams['Recurse'] = $true
    }

    $files = Get-ChildItem @searchParams

    foreach ($file in $files) {
        # Get relative path
        $relPath = $file.FullName
        if ($file.FullName.StartsWith($RootPath)) {
            $relPath = $file.FullName.Substring($RootPath.Length).TrimStart('\', '/')
        }

        # Skip excluded directories
        if (Test-IsExcludedPath $relPath) {
            continue
        }

        # Only scan files in tests/ directories
        if (-not (Test-IsInTestsDirectory $relPath)) {
            continue
        }

        $filesScanned++

        try {
            $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $content) {
                continue
            }

            # Find all Trait("Category", "...") matches
            $matches = [regex]::Matches($content, $script:TraitPattern)

            foreach ($match in $matches) {
                $category = $match.Groups[1].Value
                $lineNum = Get-LineNumber -Content $content -Index $match.Index

                # Track all categories found
                if (-not $allCategories.ContainsKey($category)) {
                    $allCategories[$category] = @()
                }
                $allCategories[$category] += [PSCustomObject]@{
                    File = Normalize-Path $relPath
                    Line = $lineNum
                    FullPath = $file.FullName
                }

                # Check if category is allowed
                if (-not $AllAllowed.ContainsKey($category)) {
                    $violations += [PSCustomObject]@{
                        Category = $category
                        File = Normalize-Path $relPath
                        Line = $lineNum
                        FullPath = $file.FullName
                    }
                }
            }

            # Find all Trait("State", "...") matches (informational only)
            $stateMatches = [regex]::Matches($content, $script:StateTraitPattern)

            foreach ($match in $stateMatches) {
                $state = $match.Groups[1].Value
                $lineNum = Get-LineNumber -Content $content -Index $match.Index

                # Track all state traits found
                if (-not $allStateTraits.ContainsKey($state)) {
                    $allStateTraits[$state] = @()
                }
                $allStateTraits[$state] += [PSCustomObject]@{
                    File = Normalize-Path $relPath
                    Line = $lineNum
                    FullPath = $file.FullName
                }
            }
        }
        catch {
            Write-Warning "Failed to read file: $($file.FullName)"
        }
    }

    return @{
        Violations = $violations
        AllCategories = $allCategories
        AllStateTraits = $allStateTraits
        FilesScanned = $filesScanned
    }
}

function Show-FixSuggestions {
    <#
    .SYNOPSIS
        Shows suggestions for adding unknown categories to an allowlist.
    #>
    param(
        [array]$Violations
    )

    $uniqueCategories = $Violations | Select-Object -ExpandProperty Category -Unique | Sort-Object

    Write-Host ""
    Write-Host "=== Fix Suggestions ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "To allow these categories, add them to the -AllowedCategories parameter:" -ForegroundColor Yellow
    Write-Host ""

    $categoryList = ($uniqueCategories | ForEach-Object { "'$_'" }) -join ', '
    Write-Host "  ./lint-test-categories.ps1 -AllowedCategories @($categoryList)" -ForegroundColor White
    Write-Host ""

    Write-Host "Or create a configuration file '.test-categories.json' in your repo root:" -ForegroundColor Yellow
    Write-Host ""

    $configJson = @{
        allowedCategories = @($uniqueCategories)
        description = "Additional approved test categories for this repository"
    } | ConvertTo-Json -Depth 2

    Write-Host $configJson -ForegroundColor DarkGray
    Write-Host ""

    Write-Host "Category descriptions to document:" -ForegroundColor Yellow
    foreach ($cat in $uniqueCategories) {
        Write-Host "  - $cat : [Add description here]" -ForegroundColor DarkGray
    }
}

function Get-ConfigFileCategories {
    <#
    .SYNOPSIS
        Loads additional categories from a config file if present.
    #>
    param([string]$RootPath)

    $configPath = Join-Path $RootPath '.test-categories.json'
    if (Test-Path $configPath) {
        try {
            $config = Get-Content $configPath -Raw | ConvertFrom-Json
            if ($config.allowedCategories) {
                return @($config.allowedCategories)
            }
        }
        catch {
            Write-Warning "Failed to parse config file: $configPath"
        }
    }
    return @()
}

#endregion

#region Main

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Category Linter" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Validate path
if (-not (Test-Path $Path)) {
    Write-Host "ERROR: Path not found: $Path" -ForegroundColor Red
    exit 2
}

$resolvedPath = (Resolve-Path $Path).Path
Write-Host "Scanning: $resolvedPath" -ForegroundColor White
if ($CI) {
    Write-Host "Mode: CI (strict)" -ForegroundColor Yellow
}
Write-Host ""

# Build allowed categories hashtable
$allAllowed = @{}
foreach ($key in $script:ApprovedCategories.Keys) {
    $allAllowed[$key] = $script:ApprovedCategories[$key]
}

# Add categories from config file
$configCategories = Get-ConfigFileCategories -RootPath $resolvedPath
foreach ($cat in $configCategories) {
    if (-not $allAllowed.ContainsKey($cat)) {
        $allAllowed[$cat] = 'From .test-categories.json'
    }
}

# Add command-line allowed categories
foreach ($cat in $AllowedCategories) {
    if (-not $allAllowed.ContainsKey($cat)) {
        $allAllowed[$cat] = 'Command-line override'
    }
}

Write-Host "Approved categories:" -ForegroundColor Cyan
foreach ($key in ($allAllowed.Keys | Sort-Object)) {
    Write-Host "  - $key : $($allAllowed[$key])" -ForegroundColor DarkGray
}
Write-Host ""

# Find violations
$result = Find-CategoryViolations -RootPath $resolvedPath -AllAllowed $allAllowed

Write-Host "Files scanned: $($result.FilesScanned)" -ForegroundColor White
Write-Host "Categories found: $($result.AllCategories.Count)" -ForegroundColor White
Write-Host ""

# Report violations
$violations = $result.Violations
$exitCode = 0

if ($violations.Count -eq 0) {
    Write-Host "[PASS] All test categories are approved." -ForegroundColor Green
}
else {
    # Group violations by category
    $grouped = $violations | Group-Object -Property Category | Sort-Object -Property Name

    Write-Host "[FAIL] Found $($violations.Count) usage(s) of $($grouped.Count) unknown category(ies):" -ForegroundColor Red
    Write-Host ""

    foreach ($group in $grouped) {
        Write-Host "  Unknown category: '$($group.Name)'" -ForegroundColor Yellow
        $occurrences = $group.Group | Sort-Object File, Line

        # Show first few occurrences
        $shown = 0
        $maxShow = 5
        foreach ($v in $occurrences) {
            if ($shown -ge $maxShow) {
                $remaining = $occurrences.Count - $maxShow
                Write-Host "    ... and $remaining more occurrence(s)" -ForegroundColor DarkGray
                break
            }
            Write-Host "    - $($v.File):$($v.Line)" -ForegroundColor DarkGray
            $shown++
        }
        Write-Host ""
    }

    if ($CI) {
        $exitCode = 1
        Write-Host "CI mode: Failing due to unknown categories." -ForegroundColor Red
    }
    else {
        Write-Host "Note: Use -CI flag to fail on unknown categories." -ForegroundColor Yellow
    }

    if ($Fix) {
        Show-FixSuggestions -Violations $violations
    }
    else {
        Write-Host ""
        Write-Host "Tip: Use -Fix flag to see suggestions for resolving violations." -ForegroundColor Cyan
    }
}

# Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$approvedUsed = @()
$unknownUsed = @()

foreach ($cat in ($result.AllCategories.Keys | Sort-Object)) {
    $count = $result.AllCategories[$cat].Count
    if ($allAllowed.ContainsKey($cat)) {
        $approvedUsed += "  $cat ($count)"
    }
    else {
        $unknownUsed += "  $cat ($count)"
    }
}

if ($approvedUsed.Count -gt 0) {
    Write-Host ""
    Write-Host "Approved categories in use:" -ForegroundColor Green
    $approvedUsed | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
}

if ($unknownUsed.Count -gt 0) {
    Write-Host ""
    Write-Host "Unknown categories in use:" -ForegroundColor Red
    $unknownUsed | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}

# Report State traits (informational, not validated)
if ($result.AllStateTraits.Count -gt 0) {
    Write-Host ""
    Write-Host "State traits found (not validated by this linter):" -ForegroundColor Cyan
    foreach ($state in ($result.AllStateTraits.Keys | Sort-Object)) {
        $count = $result.AllStateTraits[$state].Count
        $description = if ($script:ValidStateTraits.ContainsKey($state)) {
            $script:ValidStateTraits[$state]
        } else {
            'Unknown state trait'
        }
        $color = if ($state -eq 'Quarantined') { 'Yellow' } else { 'DarkGray' }
        Write-Host "  State=$state ($count) - $description" -ForegroundColor $color
    }

    # Warn if there are quarantined tests
    if ($result.AllStateTraits.ContainsKey('Quarantined')) {
        $quarantinedCount = $result.AllStateTraits['Quarantined'].Count
        Write-Host ""
        Write-Host "  Note: $quarantinedCount test(s) are quarantined. Use manage-quarantine.ps1 for details." -ForegroundColor Yellow
    }
}

Write-Host ""
if ($exitCode -eq 0) {
    Write-Host "Result: PASSED" -ForegroundColor Green
}
else {
    Write-Host "Result: FAILED" -ForegroundColor Red
}

exit $exitCode

#endregion
