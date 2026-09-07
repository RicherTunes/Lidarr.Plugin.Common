Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LocalCiTrxSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TrxPath)

    $resolved = (Resolve-Path -LiteralPath $TrxPath -ErrorAction Stop).Path
    [xml]$trx = Get-Content -LiteralPath $resolved -Raw -ErrorAction Stop
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) { throw "TRX does not contain result counters: $resolved" }
    $notExecutedResults = @($trx.SelectNodes('//*[local-name()="UnitTestResult"][@outcome="NotExecuted"]')).Count
    [pscustomobject]@{
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Skipped = [Math]::Max([int]$counters.notExecuted, $notExecutedResults)
    }
}

function Publish-LocalCiTrxEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TrxPath,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [Parameter(Mandatory)][string]$ProjectName
    )

    $source = (Resolve-Path -LiteralPath $TrxPath -ErrorAction Stop).Path
    if (Test-Path -LiteralPath $DestinationDirectory -PathType Leaf) {
        throw "Local CI results destination is a file: $DestinationDirectory"
    }
    New-Item -ItemType Directory -Path $DestinationDirectory -Force -ErrorAction Stop | Out-Null
    $destinationRoot = (Resolve-Path -LiteralPath $DestinationDirectory -ErrorAction Stop).Path
    $safeProject = ($ProjectName -replace '[^A-Za-z0-9_.-]', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($safeProject)) { $safeProject = 'tests' }
    $fileName = '{0}-{1}-{2}.trx' -f $safeProject, ([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')), ([guid]::NewGuid().ToString('N'))
    $destination = Join-Path $destinationRoot $fileName
    Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
    $published = Get-Item -LiteralPath $destination -ErrorAction Stop
    if ($published.Length -ne (Get-Item -LiteralPath $source).Length) {
        throw "Published TRX length differs from source: $destination"
    }
    return $published.FullName
}

Export-ModuleMember -Function Get-LocalCiTrxSummary, Publish-LocalCiTrxEvidence
