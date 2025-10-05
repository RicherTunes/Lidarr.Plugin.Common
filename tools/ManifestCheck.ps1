# snippet:manifest-ci
# snippet-skip-compile
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Project file '$ProjectPath' not found."
}

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest file '$ManifestPath' not found."
}

[xml]$project = Get-Content -LiteralPath $ProjectPath
# Use a real XmlNamespaceManager for robust MSBuild parsing
$nsmgr = New-Object System.Xml.XmlNamespaceManager($project.NameTable)
$nsmgr.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')

$versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:Version', $nsmgr)
if (-not $versionNode) {
    $versionNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:AssemblyVersion', $nsmgr)
}
if (-not $versionNode) {
    throw "Unable to resolve Version from '$ProjectPath'."
}
$projectVersion = $versionNode.InnerText.Trim()

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

if (-not $manifest.version) {
    throw "Manifest at '$ManifestPath' is missing 'version'."
}

$packageReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:PackageReference[@Include='$AbstractionsPackage']", $nsmgr)
$projectReference = $project.SelectSingleNode("//msb:Project/msb:ItemGroup/msb:ProjectReference[contains(@Include, 'Lidarr.Plugin.Abstractions.csproj') or contains(@Include, 'Abstractions')]", $nsmgr)

$packageMajor = $null
$packageVersion = $null
$usingProjectRef = $false

if ($packageReference) {
    $packageVersion = $packageReference.Version
    if (-not $packageVersion) {
        throw "PackageReference to $AbstractionsPackage must specify Version."
    }
    $packageMajor = ($packageVersion -split '\.')[0]
}
elseif ($projectReference) {
    # Fall back to manifest.commonVersion for in-repo abstractions; emit MAN001 warning (or error in Strict)
    $usingProjectRef = $true
}
else {
    throw "Project '$ProjectPath' must reference $AbstractionsPackage either as a PackageReference or ProjectReference."
}

$errors = @()
$warnings = @()

if ($manifest.version -ne $projectVersion) {
    $errors += "Manifest version '$($manifest.version)' does not match project Version '$projectVersion'."
}

if (-not $manifest.apiVersion) {
    $errors += "Manifest missing 'apiVersion'."
}
elseif ($manifest.apiVersion -notmatch '^\d+\.x$') {
    $errors += "apiVersion must be in 'major.x' form (e.g. '1.x')."
}
else {
    $apiMajor = ($manifest.apiVersion -split '\.')[0]
    if ($packageMajor) {
        if ($apiMajor -ne $packageMajor) {
            $errors += "apiVersion major $apiMajor does not match $AbstractionsPackage major $packageMajor."
        }
    } elseif ($usingProjectRef) {
        if (-not $manifest.commonVersion) {
            $msg = "MAN001: Using in-repo $AbstractionsPackage via ProjectReference; 'manifest.commonVersion' is required to validate apiVersion."
            if ($Strict) { $errors += $msg } else { $warnings += $msg }
        } else {
            $commonMajor = ($manifest.commonVersion -split '\.')[0]
            if ($apiMajor -ne $commonMajor) {
                $msg = "MAN001: apiVersion major $apiMajor does not match manifest.commonVersion major $commonMajor (ProjectReference in use)."
                if ($Strict) { $errors += $msg } else { $warnings += $msg }
            } else {
                $warnings += "MAN001: ProjectReference to in-repo abstractions detected; using manifest.commonVersion '$($manifest.commonVersion)' for validation."
            }
        }
    }
}

if (-not $manifest.minHostVersion) {
    $warnings += "minHostVersion is not set; host compatibility cannot be enforced."
}

if ($manifest.targets) {
    $tfmNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFrameworks', $nsmgr)
    if (-not $tfmNode) {
        $tfmNode = $project.SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFramework', $nsmgr)
    }
    $projectTfms = if ($tfmNode) { $tfmNode.InnerText.Split(';') | ForEach-Object { $_.Trim() } } else { @() }

    $missing = @()
    foreach ($target in $manifest.targets) {
        if ($projectTfms -notcontains $target) {
            $missing += $target
        }
    }
    if ($missing.Count -gt 0) {
        $errors += "Project is missing TargetFramework(s): $($missing -join ', ') referenced in manifest.targets."
    }
}

if ($errors.Count -gt 0) {
    foreach ($error in $errors) {
        Write-Error $error
    }
    throw "Manifest validation failed."
}

foreach ($warning in $warnings) {
    Write-Warning $warning
}

Write-Host "Manifest validation succeeded for '$ManifestPath'." -ForegroundColor Green
# end-snippet
