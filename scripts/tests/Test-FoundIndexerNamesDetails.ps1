$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Found Indexer Names Details" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$gatesModule = Join-Path $repoRoot 'scripts/lib/e2e-gates.psm1'

if (-not (Test-Path $gatesModule)) {
    throw "Module not found: $gatesModule"
}

Import-Module $gatesModule -Force

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Assert-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

Write-Host "`nTest Group: Dedup + Trim + Sort" -ForegroundColor Yellow
$releases = @(
    @{ indexer = 'Qobuzarr' },
    @{ indexer = ' qobuzarr ' },
    @{ indexer = 'Tidalarr' },
    @{ indexer = $null },
    @{ indexer = '   ' }
)
$s = Get-FoundIndexerNamesDetails -Releases $releases -MaxNames 10
Assert-Equal "foundIndexerNameCount counts unique names (case-insensitive)" $s.foundIndexerNameCount 2
Assert-Equal "foundIndexerNamesCapped is false when <= MaxNames" $s.foundIndexerNamesCapped $false
Assert-Equal "foundIndexerNames length matches unique count when uncapped" $s.foundIndexerNames.Count 2
Assert-Equal "foundIndexerNames[0] is Qobuzarr (sorted invariant)" $s.foundIndexerNames[0] 'Qobuzarr'
Assert-Equal "foundIndexerNames[1] is Tidalarr (sorted invariant)" $s.foundIndexerNames[1] 'Tidalarr'

Write-Host "`nTest Group: Cap to 10" -ForegroundColor Yellow
$many = 1..15 | ForEach-Object { @{ indexer = ('Idx{0:00}' -f $_) } }
$many += @{ indexer = 'idx01' } # duplicate by case
$s2 = Get-FoundIndexerNamesDetails -Releases $many -MaxNames 10
Assert-Equal "foundIndexerNameCount includes all unique names" $s2.foundIndexerNameCount 15
Assert-Equal "foundIndexerNamesCapped is true when > MaxNames" $s2.foundIndexerNamesCapped $true
Assert-Equal "foundIndexerNames length is capped to MaxNames" $s2.foundIndexerNames.Count 10
Assert-Equal "first capped name is Idx01" $s2.foundIndexerNames[0] 'Idx01'
Assert-Equal "last capped name is Idx10" $s2.foundIndexerNames[9] 'Idx10'

Write-Host "`nTest Group: Accepts string inputs" -ForegroundColor Yellow
$names = @('Zeta', 'alpha', 'Alpha', '  beta  ')
$s3 = Get-FoundIndexerNamesDetails -Releases $names -MaxNames 10
Assert-Equal "string inputs are deduped + trimmed" $s3.foundIndexerNameCount 3
Assert-Equal "string inputs are sorted invariant" ($s3.foundIndexerNames -join ',') 'alpha,beta,Zeta'

Write-Host "`nTest Group: MaxNames guard (<=0 coerces to 1)" -ForegroundColor Yellow
$s4 = Get-FoundIndexerNamesDetails -Releases @('A','B') -MaxNames 0
Assert-Equal "MaxNames coerces to 1 (capped list length)" $s4.foundIndexerNames.Count 1
Assert-Equal "capped flag true when MaxNames coerces to 1 and unique>1" $s4.foundIndexerNamesCapped $true

Write-Host "`nSummary: $passed passed, $failed failed" -ForegroundColor Cyan
if ($failed -gt 0) {
    throw "$failed assertions failed"
}

