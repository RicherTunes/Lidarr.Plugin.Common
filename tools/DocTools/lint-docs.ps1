Param([switch]$PassThru)

Write-Host "Running snippet verifier..." -ForegroundColor Cyan
$exitCode = dotnet run --project "$(Join-Path $PSScriptRoot 'SnippetVerifier/SnippetVerifier.csproj')"
if ($exitCode -ne 0) {
    Write-Error "Snippet verification failed"
    exit $exitCode
}

Write-Host "(placeholder) Run markdownlint / vale / cspell here" -ForegroundColor Yellow
if ($PassThru) {
    Write-Output @{ SnippetVerifier = $exitCode }
}
