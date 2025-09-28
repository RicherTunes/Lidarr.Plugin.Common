param()

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path $scriptRoot -Parent

$sourcePath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, '..', 'Lidarr', '_output', 'net6.0'))
if (-not (Test-Path $sourcePath)) {
    Write-Error "Host assemblies not found at $sourcePath. Run build/publish on Lidarr first."
    exit 1
}

$targetPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, 'artifacts', 'host-assemblies'))
if (Test-Path $targetPath) {
    Remove-Item -LiteralPath $targetPath -Recurse -Force
}
New-Item -ItemType Directory -Path $targetPath | Out-Null

$assemblies = Get-ChildItem -Path $sourcePath -Filter *.dll -File -Recurse
if (-not $assemblies) {
    Write-Error "No DLLs found under $sourcePath."
    exit 1
}

$copied = @()
foreach ($assembly in $assemblies) {
    $relative = $assembly.FullName.Substring($sourcePath.Length).TrimStart([char[]]@('\','/'))
    $destination = Join-Path $targetPath $relative
    $destinationDir = Split-Path $destination -Parent
    if (-not (Test-Path $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $assembly.FullName -Destination $destination -Force
    $copied += $destination
}

$failures = @()
foreach ($assemblyPath in $copied) {
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).FileVersion
    try {
        $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
    }
    catch {
        Write-Error ("Failed to read AssemblyVersion from {0}: {1}" -f $assemblyPath, $_)
        exit 1
    }

    if ([string]::IsNullOrWhiteSpace($fileVersion) -or $fileVersion -ne $assemblyVersion) {
        $failures += [pscustomobject]@{
            Path = $assemblyPath
            FileVersion = $fileVersion
            AssemblyVersion = $assemblyVersion
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Error 'Version mismatches detected:'
    foreach ($failure in $failures) {
        Write-Error ("{0} => FileVersion {1}, AssemblyVersion {2}" -f $failure.Path, $failure.FileVersion, $failure.AssemblyVersion)
    }
    exit 1
}

Write-Host ("Validated {0} host assemblies; FileVersion matches AssemblyVersion." -f $copied.Count)
