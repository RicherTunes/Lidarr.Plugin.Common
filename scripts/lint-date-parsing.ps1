#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint source files for culture-unsafe date parsing and unguarded Unix-epoch conversions.

.DESCRIPTION
    Scans C# source for two regression-prone date/time patterns:
    - DateTime/DateTimeOffset parsing or Convert.ToDateTime without CultureInfo.InvariantCulture.
    - Raw DateTimeOffset.FromUnixTimeSeconds/Milliseconds calls that can throw on bad input.

    Use Common's TimeParsing helpers or pass CultureInfo.InvariantCulture directly.
#>

param(
    [string]$Path = '.',
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive',
    [string]$AllowlistPath,
    [string]$SourceDir,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:IsCIMode = ($Mode -eq 'ci')
$script:CulturePattern = '(?:DateTime|DateTimeOffset)\s*\.\s*(?:TryParse|Parse|TryParseExact|ParseExact)\s*\(|Convert\s*\.\s*ToDateTime\s*\('
$script:EpochPattern = 'DateTimeOffset\s*\.\s*FromUnixTime(?:Seconds|Milliseconds)\s*\('
$script:AllowComment = '//\s*lint:allow-date'

function Read-Allowlist {
    param([string]$FilePath)

    if (-not $FilePath -or -not (Test-Path -LiteralPath $FilePath)) {
        return @()
    }

    $json = Get-Content -LiteralPath $FilePath -Raw | ConvertFrom-Json
    if (-not $json.entries) {
        return @()
    }

    return @($json.entries)
}

function Test-Allowlisted {
    param(
        [string]$RelativePath,
        [array]$Allowlist
    )

    $normalized = $RelativePath -replace '\\', '/'
    foreach ($entry in $Allowlist) {
        $rawPath = $null
        if ($entry -is [hashtable]) {
            if ($entry.ContainsKey('file')) { $rawPath = $entry['file'] }
            elseif ($entry.ContainsKey('path')) { $rawPath = $entry['path'] }
        }
        else {
            if ($entry.PSObject.Properties.Match('file').Count -gt 0) { $rawPath = $entry.file }
            elseif ($entry.PSObject.Properties.Match('path').Count -gt 0) { $rawPath = $entry.path }
        }

        if (-not $rawPath) {
            continue
        }

        $pattern = $rawPath -replace '\\', '/'
        if ($pattern.Contains('*')) {
            $regex = '^' + [regex]::Escape($pattern).Replace('\*\*', '.*').Replace('\*', '[^/]*') + '$'
            if ($normalized -match $regex) {
                return $entry
            }
        }
        elseif ($normalized -eq $pattern -or $normalized.EndsWith("/$pattern")) {
            return $entry
        }
    }

    return $null
}

function Resolve-SourceDir {
    param(
        [string]$RepoRoot,
        [string]$Explicit
    )

    if ($Explicit) {
        $candidate = if ([System.IO.Path]::IsPathRooted($Explicit)) {
            $Explicit
        } else {
            Join-Path $RepoRoot $Explicit
        }
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }

        throw "SourceDir not found: $candidate"
    }

    $src = Join-Path $RepoRoot 'src'
    if (Test-Path -LiteralPath $src) {
        return $src
    }

    $pluginDir = Get-ChildItem -Path $RepoRoot -Directory -Filter '*.Plugin' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pluginDir) {
        return $pluginDir.FullName
    }

    return $null
}

function Invoke-Scan {
    param(
        [string]$RepoRoot,
        [array]$Allowlist,
        [string]$SourceDir
    )

    $violations = @()
    $suppressed = @()
    $srcPath = Resolve-SourceDir -RepoRoot $RepoRoot -Explicit $SourceDir
    if (-not $srcPath -or -not (Test-Path -LiteralPath $srcPath)) {
        Write-Warning "No source directory found at $RepoRoot (tried 'src' and '*.Plugin')"
        return @{ Violations = $violations; Suppressed = $suppressed }
    }

    $files = Get-ChildItem -Path $srcPath -Filter '*.cs' -Recurse -File
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $lines = @(Get-Content -LiteralPath $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $trimmed = $line.TrimStart()

            if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')) {
                continue
            }

            $rule = $null
            if ($line -match $script:CulturePattern) {
                $window = $line
                for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) {
                    $window += "`n" + $lines[$j]
                }

                if ($window -notmatch 'InvariantCulture') {
                    $rule = 'CULTURE'
                }
            }
            elseif ($line -match $script:EpochPattern) {
                $rule = 'EPOCH'
            }

            if (-not $rule) {
                continue
            }

            $match = @{
                File = $relativePath
                Line = $i + 1
                Content = $line.Trim()
                Rule = $rule
            }

            if ($line -match $script:AllowComment) {
                $suppressed += $match
                continue
            }

            if (Test-Allowlisted -RelativePath $relativePath -Allowlist $Allowlist) {
                $suppressed += $match
                continue
            }

            $violations += $match
        }
    }

    return @{ Violations = $violations; Suppressed = $suppressed }
}

function Invoke-SelfTest {
    $passed = 0
    $failed = 0
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-date-test-$(Get-Random)"
    $srcDir = Join-Path $tempDir 'src'
    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null

    function Assert-Count {
        param(
            [string]$Name,
            [object]$Actual,
            [object]$Expected
        )

        if ($Actual -eq $Expected) {
            Write-Host "  [PASS] $Name" -ForegroundColor Green
            return 1
        }

        Write-Host "  [FAIL] $Name (expected $Expected, got $Actual)" -ForegroundColor Red
        return 0
    }

    try {
        $file = Join-Path $srcDir 'T.cs'

        Set-Content -Path $file -Value 'var d = DateTime.Parse(s);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'Culture-unsafe DateTime.Parse is flagged' $result.Violations.Count 1
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var ok = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'InvariantCulture parse is clean' $result.Violations.Count 0
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var dt = DateTimeOffset.FromUnixTimeSeconds(v);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'Raw FromUnixTimeSeconds is flagged' $result.Violations.Count 1
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var dt = DateTimeOffset.FromUnixTimeMilliseconds(v);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $isEpoch = ($result.Violations.Count -eq 1 -and $result.Violations[0].Rule -eq 'EPOCH')
        $test = Assert-Count 'FromUnixTimeMilliseconds is labelled EPOCH' $isEpoch $true
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'if (TimeParsing.TryFromUnixTimeSeconds(v, out var dt)) { }' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'TimeParsing.TryFromUnixTimeSeconds is clean' $result.Violations.Count 0
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var dt = DateTimeOffset.FromUnixTimeSeconds(v); // lint:allow-date canonical wrapper' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $allowed = ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1)
        $test = Assert-Count 'Inline allow-date suppresses' $allowed $true
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var dt = DateTimeOffset.FromUnixTimeSeconds(v);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @(@{ file = 'src/T.cs'; reason = 'canonical wrapper' })
        $allowed = ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1)
        $test = Assert-Count 'Whole-file allowlist suppresses' $allowed $true
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var d = Convert.ToDateTime(s);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'Convert.ToDateTime is flagged' $result.Violations.Count 1
        $passed += $test
        if (-not $test) { $failed++ }

        Set-Content -Path $file -Value 'var now = DateTime.UtcNow.AddSeconds(expiresIn); var s = now.ToString("o");' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $test = Assert-Count 'UtcNow/AddSeconds/ToString is clean' $result.Violations.Count 0
        $passed += $test
        if (-not $test) { $failed++ }

        $absoluteSourceDir = Join-Path $tempDir 'absolute-source'
        New-Item -ItemType Directory -Path $absoluteSourceDir -Force | Out-Null
        Set-Content -Path (Join-Path $absoluteSourceDir 'Absolute.cs') -Value 'var d = DateTime.Parse(s);' -NoNewline
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @() -SourceDir $absoluteSourceDir
        $test = Assert-Count 'Absolute SourceDir is scanned' $result.Violations.Count 1
        $passed += $test
        if (-not $test) { $failed++ }

        Write-Host ""
        Write-Host "Self-test: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
        return $failed -eq 0
    }
    finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    Write-Host 'Running self-tests...' -ForegroundColor Cyan
    exit $(if (Invoke-SelfTest) { 0 } else { 1 })
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
if (-not $AllowlistPath) {
    $AllowlistPath = Join-Path $resolvedPath '.github/date-parsing-allowlist.json'
}

$allowlist = Read-Allowlist -FilePath $AllowlistPath
$result = Invoke-Scan -RepoRoot $resolvedPath -Allowlist $allowlist -SourceDir $SourceDir

if ($result.Suppressed.Count -gt 0) {
    Write-Host "Suppressed date-parsing sites: $($result.Suppressed.Count)" -ForegroundColor DarkGray
}

if ($result.Violations.Count -eq 0) {
    Write-Host '[OK] No culture-unsafe date parsing or unguarded epoch conversions found.' -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Found $($result.Violations.Count) date-parsing violation(s):" -ForegroundColor $(if ($script:IsCIMode) { 'Red' } else { 'Yellow' })
foreach ($violation in $result.Violations) {
    Write-Host "  [$($violation.Rule)] $($violation.File):$($violation.Line)" -ForegroundColor $(if ($script:IsCIMode) { 'Red' } else { 'Yellow' })
    Write-Host "      $($violation.Content)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host 'CULTURE -> pass CultureInfo.InvariantCulture, or use TimeParsing.TryParseIsoDateInvariant.' -ForegroundColor Cyan
Write-Host 'EPOCH   -> use TimeParsing.TryFromUnixTime{Seconds,Milliseconds}.' -ForegroundColor Cyan
Write-Host "Justified exception: append '// lint:allow-date <reason>' or add a file entry to the allowlist." -ForegroundColor Cyan

exit $(if ($script:IsCIMode) { 1 } else { 0 })
