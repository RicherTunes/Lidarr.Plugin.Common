[CmdletBinding()]
Param([switch]$PassThru)

$ErrorActionPreference = 'Stop'

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)] [string] $Description,
        [Parameter(Mandatory = $true)] [scriptblock] $Action
    )

    Write-Host $Description -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed: $Description"
    }
}

$results = [ordered]@{}

Invoke-Step -Description 'Running snippet verifier' -Action {
    dotnet run --project "$(Join-Path $PSScriptRoot 'SnippetVerifier/SnippetVerifier.csproj')"
}
$results.SnippetVerifier = $true

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "npx is required. Install Node.js or add npx to PATH."
}

Invoke-Step -Description 'Running markdownlint-cli2' -Action {
    npx --yes markdownlint-cli2 "README.md" "docs/**/*.md"
}
$results.Markdownlint = $true

Invoke-Step -Description 'Running cspell' -Action {
    npx --yes cspell lint --no-must-find-files README.md docs/**/*.md
}
$results.CSpell = $true

$lychee = Get-Command lychee -ErrorAction SilentlyContinue
if ($lychee) {
    Invoke-Step -Description 'Running lychee link checker' -Action {
        lychee --no-progress --max-concurrency 4 README.md docs/**/*.md
    }
    $results.Lychee = $true
} else {
    Write-Warning 'lychee not found on PATH. Skipping link validation. Install via `cargo install lychee` or see docs/dev-guide/TESTING_DOCS.md.'
    $results.Lychee = $false
}

if ($PassThru) {
    Write-Output $results
}
