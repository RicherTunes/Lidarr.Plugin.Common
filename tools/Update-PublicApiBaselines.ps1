param(
    [string[]] $Tfms = @("net8.0","net6.0"),
    [switch] $SkipPack
)

$project = Join-Path $PSScriptRoot '..\src\Abstractions\Lidarr.Plugin.Abstractions.csproj'
Write-Host "🔧 Updating Public API baselines in $project"

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

Write-Host "🧹 Cleaning..."
dotnet clean $project | Out-Null

foreach ($tfm in $Tfms) {
    Write-Host "🧩 ${tfm}: build using source baselines"
    dotnet build $project -c Release -p:PublicApiUseSourcesForCodeFix=true -f $tfm | Out-Null

    Write-Host "🛠️  ${tfm}: applying RS0016/RS0017 fixes"
    dotnet format $project analyzers --diagnostics RS0016,RS0017 -p:TargetFramework=$tfm -v minimal | Out-Null

    $dir = Join-Path $PSScriptRoot "..\src\Abstractions\PublicAPI\$tfm"
    $unshipped = Join-Path $dir 'PublicAPI.Unshipped.txt'
    $shipped   = Join-Path $dir 'PublicAPI.Shipped.txt'

    if (-not (Test-Path $shipped)) {
        throw "Missing shipped baseline for ${tfm} at $shipped"
    }
    if (-not (Test-Path $unshipped)) {
        Set-Content -Path $unshipped -Value "#nullable enable`r`n" -Encoding UTF8
    }

    $lines = Get-Content $unshipped
    $meaningful = $lines | Where-Object { $_.Trim().Length -gt 0 -and $_ -ne '#nullable enable' }

    if ($meaningful.Count -gt 0) {
        Write-Host "📦 ${tfm}: promoting unshipped entries to shipped"
        Add-Content -Path $shipped -Value (($meaningful -join "`r`n") + "`r`n")
        Set-Content -Path $unshipped -Value "#nullable enable`r`n" -Encoding UTF8
    }
    else {
        Write-Host " ${tfm}: no unshipped entries"
    }
}

if (-not $SkipPack.IsPresent) {
    Write-Host "✅ Validating build after baseline update"
    dotnet build (Join-Path $PSScriptRoot '..\src\Lidarr.Plugin.Common.csproj') -c Release -warnaserror | Out-Null
}

Write-Host "🎯 Baselines refreshed."
