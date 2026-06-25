#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Contract tests for Common-owned xUnit trait policy and CI test filter.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot '../lib/test-trait-policy.psm1'
$passed = 0
$failed = 0
$testRoot = $null

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Details = ''
    )

    if ($Condition) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
        return
    }

    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    if ($Details) {
        Write-Host "         $Details" -ForegroundColor DarkGray
    }
    $script:failed++
}

function New-TestFile {
    param(
        [string]$RepoPath,
        [string]$RelativePath,
        [string]$Content
    )

    $path = Join-Path $RepoPath $RelativePath
    $dir = Split-Path $path -Parent
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -Path $path -Value $Content -NoNewline
    return $path
}

try {
    Write-Host '=================================================' -ForegroundColor Cyan
    Write-Host 'Test-TestTraitPolicy: CI Trait Contract' -ForegroundColor Cyan
    Write-Host '=================================================' -ForegroundColor Cyan

    Import-Module $modulePath -Force

    $filter = Get-LocalCiDeterministicFilter
    $expectedFilter = 'State!=Quarantined&((Area=E2E/Hermetic)|(Area!=Live&Area!=E2E/Live&Category!=Benchmark&Category!=Integration&Category!=Live&Category!=LiveIntegration&Category!=Docker&Category!=DockerE2E&Category!=ReleaseE2E&Category!=Runtime&Category!=Slow&Category!=Stress&Category!=Performance&Category!=Perf))'

    Assert-True 'Filter matches the reviewed deterministic CI contract' ($filter -eq $expectedFilter) $filter
    Assert-True 'Quarantine exclusion wraps the full OR expression' ($filter.StartsWith('State!=Quarantined&((')) $filter
    Assert-True 'Hermetic E2E tests remain explicitly included' ($filter -match '\(Area=E2E/Hermetic\)') $filter
    Assert-True 'Live, Docker, release, benchmark, and runtime opt-in lanes are excluded' (
        $filter -match 'Area!=E2E/Live' -and
        $filter -match 'Category!=Benchmark' -and
        $filter -match 'Category!=LiveIntegration' -and
        $filter -match 'Category!=Docker' -and
        $filter -match 'Category!=DockerE2E' -and
        $filter -match 'Category!=ReleaseE2E' -and
        $filter -match 'Category!=Runtime'
    ) $filter

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "trait-policy-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/ValidTraits.cs' -Content @'
using Xunit;

public sealed class ValidTraits
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Unit_test_runs_in_default_ci() { }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Area", "E2E/Hermetic")]
    public void Hermetic_integration_is_ci_visible() { }

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "DockerE2E")]
    public void Docker_tests_are_intentionally_opt_in() { }
}
'@ | Out-Null

    $validResult = Test-TestTraitPolicy -Path $testRoot
    Assert-True 'Known traits pass policy' ($validResult.Success -eq $true) (($validResult.Violations | Out-String))

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/InvalidTraits.cs' -Content @'
using Xunit;

public sealed class InvalidTraits
{
    [Fact]
    [Trait("Category", "Mystery")]
    public void Unknown_category_is_rejected() { }

    [Fact]
    [Trait("Area", "SecretLane")]
    public void Unknown_area_is_rejected() { }

    [Fact]
    [Trait("Category", "Docker")]
    public void Docker_tests_must_use_the_canonical_dockere2e_opt_in_trait() { }

    [Fact]
    [Trait("Area", "E2E/Hermetic")]
    [Trait("Category", "Runtime")]
    public void Hermetic_tests_must_not_bypass_runtime_opt_in_traits() { }

    [Fact]
    [Trait("State", "Maybe")]
    public void Unknown_state_is_rejected() { }
}
'@ | Out-Null

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/ComposedInvalidTraits.cs' -Content @'
using Xunit;

[Trait("Area", "E2E/Hermetic")]
public sealed class ComposedInvalidTraits
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void Method_level_runtime_cannot_bypass_class_level_hermetic_area() { }

    [Fact, Trait("Area", "E2E/Hermetic"), Trait("Category", "Benchmark")]
    public void Combined_attribute_syntax_is_still_policy_checked() { }

    [Fact]
    [Trait(
        "Category",
        "Runtime")]
    public void Multiline_trait_syntax_is_attached_to_the_test_target() { }
}
'@ | Out-Null

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/BracketInStringAttr.cs' -Content @'
using Xunit;

public sealed class BracketInStringAttr
{
    [Theory]
    [InlineData("[")]
    [Trait("Area", "E2E/Hermetic")]
    [Trait("Category", "Runtime")]
    public void Bracket_in_inline_data_string_does_not_break_trait_grouping(string token) { }
}
'@ | Out-Null

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/VerbatimBracketAttr.cs' -Content @'
using Xunit;

public sealed class VerbatimBracketAttr
{
    [Theory]
    [InlineData(@"X:\share\")]
    [Trait("Area", "E2E/Hermetic")]
    [Trait("Category", "Live")]
    public void Verbatim_string_trailing_backslash_does_not_break_grouping(string path) { }
}
'@ | Out-Null

    New-TestFile -RepoPath $testRoot -RelativePath 'tests/Fake.Tests/CommentBracketAttr.cs' -Content @'
using Xunit;

public sealed class CommentBracketAttr
{
    [Fact] // TODO: handle [edge case
    [Trait("Area", "E2E/Hermetic")]
    [Trait("Category", "Runtime")]
    public void Comment_with_open_bracket_does_not_break_grouping() { }
}
'@ | Out-Null

    $invalidResult = Test-TestTraitPolicy -Path $testRoot
    $violationText = ($invalidResult.Violations | ForEach-Object { "$($_.Code)|$($_.File):$($_.Line)|$($_.Message)" }) -join "`n"

    Assert-True 'Unknown category is reported' ($violationText -match 'UnknownCategory.*Mystery') $violationText
    Assert-True 'Unknown area is reported' ($violationText -match 'UnknownArea.*SecretLane') $violationText
    Assert-True 'Docker category requires DockerE2E companion trait' ($violationText -match 'DockerWithoutDockerE2E') $violationText
    Assert-True 'Hermetic E2E cannot bypass excluded opt-in categories' ($violationText -match 'HermeticWithExcludedCategory.*Runtime') $violationText
    Assert-True 'Class-level hermetic traits combine with method-level opt-in categories' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/ComposedInvalidTraits\.cs:7.*Runtime') $violationText
    Assert-True 'Combined xUnit attribute syntax is policy checked' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/ComposedInvalidTraits\.cs:10.*Benchmark') $violationText
    Assert-True 'Multiline xUnit trait syntax is attached to class and method targets' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/ComposedInvalidTraits\.cs:14.*Runtime') $violationText
    Assert-True 'Unknown state is reported' ($violationText -match 'UnknownState.*Maybe') $violationText
    Assert-True 'Bracket inside a quoted attribute argument does not break trait grouping' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/BracketInStringAttr\.cs:\d+.*Runtime') $violationText
    Assert-True 'Bracket in a verbatim string (trailing backslash) does not break trait grouping' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/VerbatimBracketAttr\.cs:\d+.*Live') $violationText
    Assert-True 'Bracket inside a comment does not break trait grouping' ($violationText -match 'HermeticWithExcludedCategory\|tests/Fake\.Tests/CommentBracketAttr\.cs:\d+.*Runtime') $violationText

    Write-Host "`n=================================================" -ForegroundColor Cyan
    $total = $passed + $failed
    Write-Host "Results: $passed/$total passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
    Write-Host '=================================================' -ForegroundColor Cyan

    if ($failed -gt 0) {
        exit 1
    }

    exit 0
}
finally {
    if ($testRoot -and (Test-Path $testRoot)) {
        Remove-Item -Path $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
