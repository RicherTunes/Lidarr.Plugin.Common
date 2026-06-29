#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate plugin Gitea CI secret-scan wiring.

.DESCRIPTION
    Fails closed when a plugin's primary Gitea workflow omits the secret-scan
    job, moves the gitleaks command outside that job, drops checksum
    verification, or lets verify run without depending on secret-scan.
#>

[CmdletBinding()]
param(
    [string]$RepoPath = '.',
    [switch]$CI
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Add-Violation {
    param([string]$Message)

    $script:violations.Add($Message) | Out-Null
}

function Get-WorkflowJobBlock {
    param(
        [string]$Workflow,
        [string]$JobName
    )

    $header = "  ${JobName}:"
    $lines = $Workflow.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n")
    $start = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimEnd() -eq $header) {
            $start = $i
            break
        }
    }

    if ($start -lt 0) {
        return $null
    }

    $block = [System.Collections.Generic.List[string]]::new()
    $block.Add($lines[$start]) | Out-Null

    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^  [A-Za-z0-9_-]+:\s*$') {
            break
        }

        $block.Add($lines[$i]) | Out-Null
    }

    return ($block -join "`n")
}

function ConvertTo-NeedToken {
    param([string]$Value)

    return $Value.Trim().Trim('"', "'")
}

function Get-WorkflowJobNeeds {
    param([string]$JobBlock)

    $needs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lines = $JobBlock.Replace("`r`n", "`n").Replace("`r", "`n").Split("`n")

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^\s+needs:\s*(?<rest>.*)$') {
            continue
        }

        $rest = $Matches['rest'].Trim()
        if ($rest -match '^\[(?<items>[^\]]+)\]\s*$') {
            foreach ($item in $Matches['items'].Split(',')) {
                $token = ConvertTo-NeedToken $item
                if ($token) { $needs.Add($token) | Out-Null }
            }
            continue
        }

        if ($rest -match '^[A-Za-z0-9_-]+$') {
            $needs.Add((ConvertTo-NeedToken $rest)) | Out-Null
            continue
        }

        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '^\s+-\s*(?<need>[A-Za-z0-9_-]+)\s*$') {
                $needs.Add((ConvertTo-NeedToken $Matches['need'])) | Out-Null
                continue
            }

            if ($lines[$j].Trim()) {
                break
            }
        }
    }

    return $needs
}

try {
    $resolvedRepoPath = (Resolve-Path -LiteralPath $RepoPath).Path
}
catch {
    Write-Error "RepoPath not found: $RepoPath"
    exit 1
}

$workflowPath = Join-Path $resolvedRepoPath '.gitea/workflows/ci.yml'
$script:violations = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $workflowPath)) {
    Add-Violation ".gitea/workflows/ci.yml not found; plugin Gitea CI is not fail-closed."
}
else {
    $workflow = Get-Content -LiteralPath $workflowPath -Raw

    $secretScan = Get-WorkflowJobBlock -Workflow $workflow -JobName 'secret-scan'
    if (-not $secretScan) {
        Add-Violation "secret-scan job is missing from .gitea/workflows/ci.yml."
    }
    else {
        if ($secretScan -notmatch 'sha256sum\s+-c\s+-') {
            Add-Violation "secret-scan job must verify the downloaded gitleaks archive with sha256sum -c -."
        }

        if ($secretScan -notmatch [regex]::Escape('/tmp/gitleaks detect --source . --no-banner --redact --exit-code 1')) {
            Add-Violation "secret-scan job must run gitleaks detect with --redact and --exit-code 1 inside the secret-scan job."
        }
    }

    $verify = Get-WorkflowJobBlock -Workflow $workflow -JobName 'verify'
    if (-not $verify) {
        Add-Violation "verify job is missing from .gitea/workflows/ci.yml."
    }
    else {
        $needs = Get-WorkflowJobNeeds -JobBlock $verify
        foreach ($requiredNeed in @('lint', 'secret-scan')) {
            if (-not $needs.Contains($requiredNeed)) {
                Add-Violation "verify job needs must include '$requiredNeed' so failed policy gates block verification."
            }
        }
    }
}

if ($script:violations.Count -gt 0) {
    Write-Host "Gitea secret-scan workflow guard failed:" -ForegroundColor Red
    foreach ($violation in $script:violations) {
        Write-Host "  - $violation" -ForegroundColor Red
    }
    exit 1
}

Write-Host "[OK] Gitea secret-scan workflow is fail-closed." -ForegroundColor Green
exit 0
