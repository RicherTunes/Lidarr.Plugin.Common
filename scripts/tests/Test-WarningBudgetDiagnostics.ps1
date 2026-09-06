#!/usr/bin/env pwsh
# Execute the real runner's warning counter without its build/Docker entrypoint.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/local-ci.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join "`n") }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-WarningCountFromOutput' }, $false)
if (-not $function) { throw 'Shared warning counter missing.' }
. ([scriptblock]::Create($function.Extent.Text))
$passed = 0; $failed = 0
function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        if (-not (& $Body)) { throw 'Assertion returned false.' }
        Write-Host "PASS $Name"; $script:passed++
    } catch { Write-Host "FAIL ${Name}: $($_.Exception.Message)"; $script:failed++ }
}
$warning = 'src/A.cs(10,2): warning CS0618: Legacy method is obsolete [Plugin.csproj]'
Check 'legacy callers retain the MSBuild summary occurrence count' {
    (Get-WarningCountFromOutput -OutputLines @($warning, $warning, '  2 Warning(s)')) -eq 2
}
Check 'duplicate diagnostic output in one build counts once with explicit unique mode' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @($warning, $warning, '  1 Warning(s)') -SeenDiagnostics $seen) -eq 1
}
Check 'recompiling the same diagnostic in another test project does not charge twice' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $first = Get-WarningCountFromOutput -OutputLines @($warning, '1 Warning(s)') -SeenDiagnostics $seen
    $second = Get-WarningCountFromOutput -OutputLines @($warning, '1 Warning(s)') -SeenDiagnostics $seen
    $first -eq 1 -and $second -eq 0 -and $seen.Count -eq 1
}
Check 'different locations and different warning messages remain distinct debt' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lines = @($warning, $warning.Replace('(10,2)', '(11,2)'), $warning.Replace('Legacy', 'Another'))
    (Get-WarningCountFromOutput -OutputLines $lines -SeenDiagnostics $seen) -eq 3
}
Check 'unknown diagnostics reported only by a summary are conservatively charged' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @('7 Warning(s)') -SeenDiagnostics $seen) -eq 7
}
Check 'summary remainder is not silently lost when some diagnostics are absent' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @($warning, '4 Warning(s)') -SeenDiagnostics $seen) -eq 4
}
Check 'duplicate output cannot conceal a second unrepresented summary warning' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $first = Get-WarningCountFromOutput -OutputLines @($warning, $warning, '2 Warning(s)') -SeenDiagnostics $seen
    $second = Get-WarningCountFromOutput -OutputLines @($warning, $warning, '2 Warning(s)') -SeenDiagnostics $seen
    $first -eq 2 -and $second -eq 1
}
Check 'ANSI presentation and indentation do not multiply a diagnostic' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @($warning, "`e[33m  $warning`e[0m") -SeenDiagnostics $seen) -eq 1
}
Check 'warnings without codes are still counted rather than discarded' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @('SourceLink.targets: warning : No source control information.') -SeenDiagnostics $seen) -eq 1
}
Check 'empty output is zero in both modes' {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Get-WarningCountFromOutput -OutputLines @()) -eq 0 -and (Get-WarningCountFromOutput -OutputLines @() -SeenDiagnostics $seen) -eq 0
}
Check 'shared runner explicitly validates and reports the optional metric' {
    $source = Get-Content -LiteralPath (Join-Path $root 'scripts/local-ci.ps1') -Raw
    $source -match 'WarningBudgetMetric' -and $source -match 'UniqueDiagnostics' -and $source -match 'Unsupported warning budget metric'
}
Write-Host "Results: $passed passed, $failed failed"
if ($failed) { exit 1 }
