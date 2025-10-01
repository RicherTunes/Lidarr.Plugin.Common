# snippet:manifest-ci
# snippet-skip-compile
param(
    [Parameter(Mandatory = True)]
    [string],

    [Parameter(Mandatory = True)]
    [string],

    [string] = "Lidarr.Plugin.Abstractions"
)

Continue = 'Stop'

if (!(Test-Path )) {
    throw "Project file '' not found."
}

if (!(Test-Path )) {
    throw "Manifest file '' not found."
}

[xml] = Get-Content 
 = @{ msb = 'http://schemas.microsoft.com/developer/msbuild/2003' }

 = .SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:Version', )
if (-not ) {
     = .SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:AssemblyVersion', )
}
if (-not ) {
    throw "Unable to resolve Version from ''."
}
 = .InnerText.Trim()

 = Get-Content  -Raw | ConvertFrom-Json

if (-not .version) {
    throw "Manifest at '' is missing 'version'."
}

 = .SelectSingleNode("//msb:Project/msb:ItemGroup/msb:PackageReference[@Include='']", )
if (-not ) {
    throw "Project '' must reference ."
}
 = .Version
if (-not ) {
    throw "PackageReference to  must specify Version."
}
 = ( -split '\.')[0]

 = @()
 = @()

if (.version -ne ) {
     += "Manifest version '' does not match project Version ''."
}

if (-not .apiVersion) {
     += "Manifest missing 'apiVersion'."
} elseif (.apiVersion -notmatch '^\d+\.x$') {
     += "apiVersion must be in 'major.x' form (e.g. '1.x')."
} else {
     = (.apiVersion -split '\.')[0]
    if ( -ne ) {
         += "apiVersion major  does not match  major ."
    }
}

if (-not .minHostVersion) {
     += "minHostVersion is not set; host compatibility cannot be enforced."
}

if (.targets) {
     = .SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFrameworks', )
    if (-not ) {
         = .SelectSingleNode('//msb:Project/msb:PropertyGroup/msb:TargetFramework', )
         = if () { .InnerText } else { '' }
    } else {
         = .InnerText
    }

     = @()
    foreach ( in .targets) {
        if (-not ( -split ';' | Where-Object { .Trim() -eq  })) {
             += 
        }
    }
    if (.Count -gt 0) {
         += "Project is missing TargetFramework(s):  referenced in manifest.targets."
    }
}

if (.Count -gt 0) {
    foreach ( in ) {
        Write-Error 
    }
    throw "Manifest validation failed."
}

foreach ( in ) {
    Write-Warning 
}

Write-Host "Manifest validation succeeded for ''." -ForegroundColor Green
# end-snippet

