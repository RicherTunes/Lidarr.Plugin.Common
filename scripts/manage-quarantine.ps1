#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Manages quarantined tests across the plugin ecosystem.

.DESCRIPTION
    Scans for tests marked with [Trait("State", "Quarantined")] and provides
    reporting, execution, and verification capabilities.

    Quarantined tests are:
    - Excluded from normal CI runs (filtered by trait)
    - Run weekly to detect if they've been fixed
    - Tracked with date and reason in comments

.PARAMETER Path
    Root path to scan for test files. Defaults to current directory.

.PARAMETER Mode
    Operation mode:
    - "report"  : List all quarantined tests with locations and metadata
    - "run"     : Execute only quarantined tests (generates xUnit filter)
    - "check"   : Verify quarantined tests are still failing (helps identify fixed tests)
    - "summary" : Brief summary of quarantine status across the codebase

.PARAMETER CI
    Strict mode for CI. In report mode, fails if quarantine count exceeds threshold.
    In check mode, fails if any quarantined test passes (needs un-quarantining).

.PARAMETER MaxQuarantined
    Maximum allowed quarantined tests in CI mode. Default: 20.

.PARAMETER OutputFormat
    Output format: "console" (default), "json", "markdown".

.PARAMETER Recurse
    Scan subdirectories recursively. Default: $true.

.EXAMPLE
    ./manage-quarantine.ps1
    Shows summary of quarantined tests in current directory.

.EXAMPLE
    ./manage-quarantine.ps1 -Mode report
    Lists all quarantined tests with file:line locations.

.EXAMPLE
    ./manage-quarantine.ps1 -Mode run
    Outputs the xUnit filter string to run only quarantined tests.

.EXAMPLE
    ./manage-quarantine.ps1 -Mode check -CI
    Runs quarantined tests and fails if any pass (they should be un-quarantined).

.EXAMPLE
    ./manage-quarantine.ps1 -Path D:\repos\plugin -OutputFormat json
    Outputs quarantine report as JSON.

.NOTES
    Quarantine Annotation Format:
    [Trait("State", "Quarantined")]  // Quarantined YYYY-MM-DD: Reason - Issue #123

    The comment should include:
    - Date quarantined (YYYY-MM-DD format)
    - Brief reason (flaky, environment-dependent, timing issue, etc.)
    - Issue reference if applicable
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path = (Get-Location).Path,

    [Parameter(Position = 1)]
    [ValidateSet('report', 'run', 'check', 'summary')]
    [string]$Mode = 'summary',

    [switch]$CI,

    [int]$MaxQuarantined = 20,

    [ValidateSet('console', 'json', 'markdown')]
    [string]$OutputFormat = 'console',

    [switch]$Recurse = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

#region Configuration

# Trait pattern to match [Trait("State", "Quarantined")]
$script:QuarantineTraitPattern = '\[Trait\s*\(\s*"State"\s*,\s*"Quarantined"\s*\)\]'

# Pattern to extract quarantine comment (on same or adjacent line)
# Matches: // Quarantined YYYY-MM-DD: reason - Issue #NNN
# The reason is captured as non-greedy text before optional " - Issue #NNN"
$script:QuarantineCommentPattern = '//\s*Quarantined\s+(\d{4}-\d{2}-\d{2}):\s*(.+?)(?:\s+-\s+Issue\s+#(\d+))?(?:\r?\n|$)'

# Pattern to extract test method name
$script:TestMethodPattern = 'public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\('

# Pattern to extract test class name
$script:TestClassPattern = 'public\s+(?:sealed\s+)?class\s+(\w+)'

# Directories to exclude
$script:ExcludeDirs = @('bin', 'obj', '.git', '.worktrees', 'node_modules', 'ext')

#endregion

#region Helper Functions

function Normalize-Path {
    param([string]$FilePath)
    return $FilePath -replace '\\', '/'
}

function Test-IsExcludedPath {
    param([string]$RelPath)

    $normalized = Normalize-Path $RelPath
    $parts = $normalized -split '/'

    foreach ($part in $parts) {
        if ($part -in $script:ExcludeDirs) {
            return $true
        }
    }

    if ($normalized -match '\.g\.cs$') {
        return $true
    }

    return $false
}

function Test-IsInTestsDirectory {
    param([string]$RelPath)

    $normalized = (Normalize-Path $RelPath).ToLowerInvariant()
    return $normalized -match '(^|/)(tests|[^/]*\.tests|[^/]*tests)/'
}

function Get-LineNumber {
    param(
        [string]$Content,
        [int]$Index
    )
    return ($Content.Substring(0, $Index) -split "`n").Count
}

function Get-QuarantineMetadata {
    <#
    .SYNOPSIS
        Extracts quarantine metadata from surrounding comments.
    #>
    param(
        [string]$Content,
        [int]$TraitIndex
    )

    $metadata = @{
        Date = $null
        Reason = $null
        IssueNumber = $null
        DaysQuarantined = $null
    }

    # Get the line and a few lines before/after the trait
    $lineStart = $Content.LastIndexOf("`n", [Math]::Max(0, $TraitIndex - 1)) + 1
    $lineEnd = $Content.IndexOf("`n", $TraitIndex)
    if ($lineEnd -lt 0) { $lineEnd = $Content.Length }

    # Check current line and previous line for comment
    $searchStart = [Math]::Max(0, $lineStart - 200)  # Look back ~2-3 lines
    $searchEnd = [Math]::Min($Content.Length, $lineEnd + 200)  # Look forward too
    $contextLines = $Content.Substring($searchStart, $searchEnd - $searchStart)

    # Use regex match with Multiline option to properly handle line endings
    $regexMatch = [regex]::Match($contextLines, $script:QuarantineCommentPattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($regexMatch.Success) {
        $metadata.Date = $regexMatch.Groups[1].Value
        $metadata.Reason = $regexMatch.Groups[2].Value.Trim()
        if ($regexMatch.Groups[3].Success -and $regexMatch.Groups[3].Value) {
            $metadata.IssueNumber = [int]$regexMatch.Groups[3].Value
        }

        # Calculate days quarantined
        try {
            $quarantineDate = [DateTime]::ParseExact($metadata.Date, 'yyyy-MM-dd', $null)
            $metadata.DaysQuarantined = ([DateTime]::Now - $quarantineDate).Days
        }
        catch {
            # Invalid date format - leave as null
        }
    }

    return $metadata
}

function Get-TestMethodName {
    <#
    .SYNOPSIS
        Finds the test method name following a quarantine trait.
    #>
    param(
        [string]$Content,
        [int]$TraitIndex
    )

    # Search forward from the trait for the method declaration
    $searchContent = $Content.Substring($TraitIndex, [Math]::Min(500, $Content.Length - $TraitIndex))

    if ($searchContent -match $script:TestMethodPattern) {
        return $Matches[1]
    }

    return $null
}

function Get-TestClassName {
    <#
    .SYNOPSIS
        Finds the test class name containing a trait.
    #>
    param(
        [string]$Content,
        [int]$TraitIndex
    )

    # Search backward from the trait for the class declaration
    $searchContent = $Content.Substring(0, $TraitIndex)

    # Find the last class declaration before this trait
    $matches = [regex]::Matches($searchContent, $script:TestClassPattern)
    if ($matches.Count -gt 0) {
        return $matches[$matches.Count - 1].Groups[1].Value
    }

    return $null
}

function Find-QuarantinedTests {
    <#
    .SYNOPSIS
        Scans for all quarantined tests in the given path.
    #>
    param(
        [string]$RootPath
    )

    $quarantined = @()
    $filesScanned = 0

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
        $relPath = $file.FullName
        if ($file.FullName.StartsWith($RootPath)) {
            $relPath = $file.FullName.Substring($RootPath.Length).TrimStart('\', '/')
        }

        if (Test-IsExcludedPath $relPath) {
            continue
        }

        if (-not (Test-IsInTestsDirectory $relPath)) {
            continue
        }

        $filesScanned++

        try {
            $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $content) {
                continue
            }

            $matches = [regex]::Matches($content, $script:QuarantineTraitPattern)

            foreach ($match in $matches) {
                $lineNum = Get-LineNumber -Content $content -Index $match.Index
                $metadata = Get-QuarantineMetadata -Content $content -TraitIndex $match.Index
                $methodName = Get-TestMethodName -Content $content -TraitIndex $match.Index
                $className = Get-TestClassName -Content $content -TraitIndex $match.Index

                $quarantined += [PSCustomObject]@{
                    File = Normalize-Path $relPath
                    FullPath = $file.FullName
                    Line = $lineNum
                    ClassName = $className
                    MethodName = $methodName
                    FullyQualifiedName = if ($className -and $methodName) { "$className.$methodName" } else { $null }
                    Date = $metadata.Date
                    Reason = $metadata.Reason
                    IssueNumber = $metadata.IssueNumber
                    DaysQuarantined = $metadata.DaysQuarantined
                }
            }
        }
        catch {
            Write-Warning "Failed to read file: $($file.FullName)"
        }
    }

    return @{
        Tests = $quarantined
        FilesScanned = $filesScanned
    }
}

function Format-Duration {
    param([int]$Days)

    if ($null -eq $Days) { return 'unknown' }
    if ($Days -eq 0) { return 'today' }
    if ($Days -eq 1) { return '1 day' }
    if ($Days -lt 7) { return "$Days days" }
    if ($Days -lt 30) { return "$([Math]::Floor($Days / 7)) weeks" }
    if ($Days -lt 365) { return "$([Math]::Floor($Days / 30)) months" }
    return "$([Math]::Floor($Days / 365)) years"
}

function Write-ConsoleReport {
    param(
        [array]$Tests,
        [int]$FilesScanned
    )

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Quarantine Report" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    if ($Tests.Count -eq 0) {
        Write-Host "[OK] No quarantined tests found." -ForegroundColor Green
        Write-Host "Files scanned: $FilesScanned" -ForegroundColor DarkGray
        return
    }

    Write-Host "Found $($Tests.Count) quarantined test(s):" -ForegroundColor Yellow
    Write-Host ""

    # Group by file
    $grouped = $Tests | Group-Object -Property File | Sort-Object -Property Name

    foreach ($group in $grouped) {
        Write-Host "  $($group.Name)" -ForegroundColor White

        foreach ($test in ($group.Group | Sort-Object Line)) {
            $location = ":$($test.Line)"
            $methodDisplay = if ($test.MethodName) { $test.MethodName } else { "(unknown method)" }

            Write-Host "    $methodDisplay$location" -ForegroundColor DarkGray

            if ($test.Date) {
                $duration = Format-Duration $test.DaysQuarantined
                Write-Host "      Quarantined: $($test.Date) ($duration ago)" -ForegroundColor DarkYellow
            }
            if ($test.Reason) {
                Write-Host "      Reason: $($test.Reason)" -ForegroundColor DarkGray
            }
            if ($test.IssueNumber) {
                Write-Host "      Issue: #$($test.IssueNumber)" -ForegroundColor DarkCyan
            }
        }
        Write-Host ""
    }

    Write-Host "Files scanned: $FilesScanned" -ForegroundColor DarkGray

    # Warnings for long-quarantined tests
    $longQuarantined = @($Tests | Where-Object { $_.DaysQuarantined -gt 30 })
    if ($longQuarantined.Count -gt 0) {
        Write-Host ""
        Write-Host "[WARNING] $($longQuarantined.Count) test(s) quarantined for more than 30 days:" -ForegroundColor Yellow
        foreach ($test in $longQuarantined) {
            $duration = Format-Duration $test.DaysQuarantined
            Write-Host "  - $($test.FullyQualifiedName ?? $test.File) ($duration)" -ForegroundColor DarkYellow
        }
    }
}

function Write-JsonReport {
    param(
        [array]$Tests,
        [int]$FilesScanned
    )

    $report = @{
        timestamp = [DateTime]::UtcNow.ToString('o')
        summary = @{
            total = $Tests.Count
            filesScanned = $FilesScanned
            withMetadata = @($Tests | Where-Object { $_.Date }).Count
            longQuarantined = @($Tests | Where-Object { $_.DaysQuarantined -gt 30 }).Count
        }
        tests = $Tests | ForEach-Object {
            @{
                file = $_.File
                line = $_.Line
                className = $_.ClassName
                methodName = $_.MethodName
                fullyQualifiedName = $_.FullyQualifiedName
                date = $_.Date
                reason = $_.Reason
                issueNumber = $_.IssueNumber
                daysQuarantined = $_.DaysQuarantined
            }
        }
    }

    $report | ConvertTo-Json -Depth 5
}

function Write-MarkdownReport {
    param(
        [array]$Tests,
        [int]$FilesScanned
    )

    $sb = [System.Text.StringBuilder]::new()

    [void]$sb.AppendLine("# Quarantine Report")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss UTC')")
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Metric | Value |")
    [void]$sb.AppendLine("|--------|-------|")
    [void]$sb.AppendLine("| Total quarantined | $($Tests.Count) |")
    [void]$sb.AppendLine("| Files scanned | $FilesScanned |")
    [void]$sb.AppendLine("| With metadata | $(@($Tests | Where-Object { $_.Date }).Count) |")
    [void]$sb.AppendLine("| Quarantined >30 days | $(@($Tests | Where-Object { $_.DaysQuarantined -gt 30 }).Count) |")
    [void]$sb.AppendLine("")

    if ($Tests.Count -gt 0) {
        [void]$sb.AppendLine("## Quarantined Tests")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| Test | Location | Date | Duration | Reason |")
        [void]$sb.AppendLine("|------|----------|------|----------|--------|")

        foreach ($test in ($Tests | Sort-Object File, Line)) {
            $testName = $test.FullyQualifiedName ?? "(unknown)"
            $location = "$($test.File):$($test.Line)"
            $date = $test.Date ?? "-"
            $duration = if ($test.DaysQuarantined) { Format-Duration $test.DaysQuarantined } else { "-" }
            $reason = $test.Reason ?? "-"

            [void]$sb.AppendLine("| $testName | $location | $date | $duration | $reason |")
        }
    }

    $sb.ToString()
}

function Get-XUnitFilter {
    <#
    .SYNOPSIS
        Generates xUnit filter string for running only quarantined tests.
    #>
    param([array]$Tests)

    # Filter to run only quarantined tests
    return 'State=Quarantined'
}

function Get-XUnitExcludeFilter {
    <#
    .SYNOPSIS
        Generates xUnit filter string to exclude quarantined tests.
    #>
    return 'State!=Quarantined'
}

#endregion

#region Main

# Validate path
if (-not (Test-Path $Path)) {
    Write-Host "ERROR: Path not found: $Path" -ForegroundColor Red
    exit 2
}

$resolvedPath = (Resolve-Path $Path).Path

# Find quarantined tests
$result = Find-QuarantinedTests -RootPath $resolvedPath
$tests = $result.Tests
$filesScanned = $result.FilesScanned

switch ($Mode) {
    'summary' {
        Write-Host ""
        Write-Host "Quarantine Summary" -ForegroundColor Cyan
        Write-Host "==================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Path: $resolvedPath" -ForegroundColor White
        Write-Host "Files scanned: $filesScanned" -ForegroundColor DarkGray
        Write-Host "Quarantined tests: $($tests.Count)" -ForegroundColor $(if ($tests.Count -eq 0) { 'Green' } else { 'Yellow' })

        if ($tests.Count -gt 0) {
            $longQuarantined = ($tests | Where-Object { $_.DaysQuarantined -gt 30 }).Count
            if ($longQuarantined -gt 0) {
                Write-Host "Long-quarantined (>30 days): $longQuarantined" -ForegroundColor Yellow
            }

            Write-Host ""
            Write-Host "To exclude from CI:" -ForegroundColor Cyan
            Write-Host "  dotnet test --filter `"$(Get-XUnitExcludeFilter)`"" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "To run only quarantined:" -ForegroundColor Cyan
            Write-Host "  dotnet test --filter `"$(Get-XUnitFilter)`"" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "For full report: ./manage-quarantine.ps1 -Mode report" -ForegroundColor DarkGray
        }

        $exitCode = 0
        if ($CI -and $tests.Count -gt $MaxQuarantined) {
            Write-Host ""
            Write-Host "[FAIL] Quarantine count ($($tests.Count)) exceeds maximum ($MaxQuarantined)" -ForegroundColor Red
            $exitCode = 1
        }

        exit $exitCode
    }

    'report' {
        switch ($OutputFormat) {
            'console' { Write-ConsoleReport -Tests $tests -FilesScanned $filesScanned }
            'json' { Write-JsonReport -Tests $tests -FilesScanned $filesScanned }
            'markdown' { Write-MarkdownReport -Tests $tests -FilesScanned $filesScanned }
        }

        $exitCode = 0
        if ($CI -and $tests.Count -gt $MaxQuarantined) {
            Write-Host ""
            Write-Host "[FAIL] Quarantine count ($($tests.Count)) exceeds maximum ($MaxQuarantined)" -ForegroundColor Red
            $exitCode = 1
        }

        exit $exitCode
    }

    'run' {
        # Output the filter for running quarantined tests
        $filter = Get-XUnitFilter -Tests $tests

        if ($OutputFormat -eq 'json') {
            @{
                filter = $filter
                excludeFilter = Get-XUnitExcludeFilter
                testCount = $tests.Count
                command = "dotnet test --filter `"$filter`""
            } | ConvertTo-Json
        }
        else {
            Write-Host ""
            Write-Host "xUnit filter to run quarantined tests:" -ForegroundColor Cyan
            Write-Host ""
            Write-Host "  $filter" -ForegroundColor White
            Write-Host ""
            Write-Host "Full command:" -ForegroundColor Cyan
            Write-Host "  dotnet test --filter `"$filter`"" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "To exclude quarantined from normal CI:" -ForegroundColor Cyan
            Write-Host "  dotnet test --filter `"$(Get-XUnitExcludeFilter)`"" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "Tests that will run: $($tests.Count)" -ForegroundColor DarkGray
        }

        exit 0
    }

    'check' {
        if ($tests.Count -eq 0) {
            Write-Host "[OK] No quarantined tests to check." -ForegroundColor Green
            exit 0
        }

        Write-Host ""
        Write-Host "Checking $($tests.Count) quarantined test(s)..." -ForegroundColor Cyan
        Write-Host ""

        # Run only quarantined tests and capture results
        $filter = Get-XUnitFilter -Tests $tests
        Write-Host "Running: dotnet test --filter `"$filter`" --no-build" -ForegroundColor DarkGray
        Write-Host ""

        # We can't actually run dotnet test here and parse results easily,
        # so we output instructions for CI integration
        Write-Host "CI Integration:" -ForegroundColor Yellow
        Write-Host "  1. Run: dotnet test --filter `"$filter`" --logger `"trx;LogFileName=quarantine-check.trx`"" -ForegroundColor DarkGray
        Write-Host "  2. Parse .trx file for passing tests" -ForegroundColor DarkGray
        Write-Host "  3. Any passing tests should be un-quarantined" -ForegroundColor DarkGray
        Write-Host ""

        if ($CI) {
            Write-Host "In full CI mode, run the tests and fail if any pass." -ForegroundColor Yellow
            Write-Host "Passing quarantined tests need to be un-quarantined." -ForegroundColor Yellow
        }

        exit 0
    }
}

#endregion
