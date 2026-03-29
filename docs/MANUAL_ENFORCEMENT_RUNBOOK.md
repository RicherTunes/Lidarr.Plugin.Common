# Manual Enforcement Runbook

**Use until:** CI billing is restored for Tidalarr, Qobuzarr, AppleMusicarr (issue #459)

**Shell:** All commands below use **PowerShell** (`pwsh`). Bash equivalents noted where they differ.

## Before merging ANY PR in billing-blocked repos

### Required checks (run locally, paste output in PR)

1. **Build**
   ```pwsh
   dotnet build -m:1
   ```
   Expected: 0 errors

2. **Full test suite**
   ```pwsh
   dotnet test --blame-hang-timeout 30s -m:1
   ```
   Expected: 0 new failures, count matches baseline

3. **Runtime sandbox**
   ```pwsh
   dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s -m:1
   ```
   Expected: all pass (Tidalarr 10, Qobuzarr 11, AppleMusicarr 10)

4. **No net6.0 regressions**
   ```pwsh
   # PowerShell
   Get-ChildItem -Recurse -Include *.csproj,*.props,*.ps1,*.sh,*.yml |
     Where-Object { $_.FullName -notmatch 'ext[/\\]|obj[/\\]|\.git[/\\]' } |
     Select-String 'net6\.0' |
     ForEach-Object { $_.Path + ':' + $_.LineNumber }
   ```
   ```bash
   # Bash alternative
   grep -rn "net6\.0" --include="*.csproj" --include="*.props" --include="*.ps1" --include="*.sh" --include="*.yml" . | grep -v "ext/" | grep -v "obj/" | grep -v ".git/"
   ```
   Expected: 0 matches (Common is allowed 2 in tooling)

5. **Single IPlugin** (plugin repos only)
   ```pwsh
   # PowerShell
   Get-ChildItem -Path src -Recurse -Filter *.cs |
     Select-String 'class\s+\w+.*:\s*.*IPlugin' |
     Where-Object { $_.Line -notmatch 'internal|abstract|//' }
   ```
   ```bash
   # Bash alternative
   grep -rn "class.*:.*IPlugin" src/ --include="*.cs" | grep -v "internal\|abstract\|//"
   ```
   Expected: exactly 1 match

### If Common submodule changed

6. **Common SHA is a tagged release**
   ```pwsh
   Push-Location ext/Lidarr.Plugin.Common
   git describe --tags --exact-match HEAD
   Pop-Location
   ```
   Expected: v1.7.x tag

7. **Promotion matrix**
   Run full matrix per `ext/Lidarr.Plugin.Common/docs/ECOSYSTEM_PROMOTION_CHECKLIST.md`

### Evidence format

Paste in PR comment:
```
Build: 0 errors
Tests: XXX passed, X failed (pre-existing), X skipped
Runtime: XX/XX pass
net6.0: 0 matches
IPlugin: 1 match
```

## Before promoting a Common release

Run the full promotion matrix:

```pwsh
# Common compliance
Push-Location D:\Alex\github\lidarr.plugin.common
dotnet test tests\Lidarr.Plugin.Common.Tests.csproj --filter "FullyQualifiedName~Bridge|FullyQualifiedName~Compliance" --blame-hang-timeout 30s
Pop-Location

# Each plugin runtime
foreach ($repo in @('tidalarr', 'qobuzarr', 'applemusicarr', 'brainarr')) {
    Write-Host "=== $repo ===" -ForegroundColor Cyan
    Push-Location "D:\Alex\github\$repo"
    dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s -m:1
    Pop-Location
}
```

Expected: 107/107 (68 + 10 + 11 + 10 + 8)
