Set-StrictMode -Version Latest

$script:AllowedCategories = @(
    'AdvancedConfiguration',
    'Benchmark',
    'BugFix',
    'Calibration',
    'Compliance',
    'Concurrency',
    'Configuration',
    'Contract',
    'Coverage',
    'Critical',
    'DependencyInjection',
    'Docker',
    'DockerE2E',
    'E2E',
    'EdgeCase',
    'Enhanced',
    'HallucinationDetection',
    'Hosting',
    'Integration',
    'IterativeStrategy',
    'LibraryLinking',
    'Live',
    'LiveIntegration',
    'Logging',
    'Models',
    'Packaging',
    'Parity',
    'Perf',
    'Performance',
    'PlanCache',
    'Plugin',
    'PromptBuilder',
    'PromptPlanner',
    'PromptRenderer',
    'Registry',
    'ReleaseE2E',
    'Resilience',
    'Runtime',
    'Sampling',
    'Security',
    'ServiceIsolation',
    'Simulations',
    'Slow',
    'Snapshot',
    'StableHash',
    'Streaming',
    'Stress',
    'Tokenization',
    'TopUp',
    'Unit',
    'Utils',
    'Wave2',
    'Wave5'
)

$script:AllowedAreas = @(
    'Characterization',
    'Concurrency',
    'E2E/Hermetic',
    'E2E/Live',
    'LibraryAnalyzer',
    'Live',
    'Orchestrator',
    'PromptBuilder',
    'RuntimeCache',
    'Settings'
)

$script:AllowedStates = @('Quarantined')
$script:TraitPattern = '(?<![A-Za-z0-9_])Trait(?:Attribute)?\s*\(\s*"(?<name>Category|Area|State)"\s*,\s*"(?<value>[^"]+)"\s*\)'

$script:ExcludedDeterministicCategories = @(
    'Benchmark',
    'Integration',
    'Live',
    'LiveIntegration',
    'Docker',
    'DockerE2E',
    'ReleaseE2E',
    'Runtime',
    # Timing-sensitive lanes: kept out of the per-PR deterministic gate (nightly-only, per
    # docs/CI_LANE_STRATEGY.md) so wall-clock-dependent tests don't flake the merge gate.
    'Slow',
    'Stress',
    'Performance',
    'Perf'
)

$script:ExcludedDeterministicAreas = @(
    'Live',
    'E2E/Live'
)

function Get-LocalCiDeterministicFilter {
    <#
    .SYNOPSIS
        Returns the xUnit/VSTest filter for merge-critical deterministic local CI.
    #>

    $excludedAreaFilter = ($script:ExcludedDeterministicAreas | ForEach-Object { "Area!=$_" }) -join '&'
    $excludedCategoryFilter = ($script:ExcludedDeterministicCategories | ForEach-Object { "Category!=$_" }) -join '&'
    return "State!=Quarantined&((Area=E2E/Hermetic)|($excludedAreaFilter&$excludedCategoryFilter))"
}

function Get-TestTraitPolicy {
    <#
    .SYNOPSIS
        Returns the Common-owned test trait vocabulary and deterministic opt-out traits.
    #>

    [PSCustomObject]@{
        AllowedCategories = @($script:AllowedCategories)
        AllowedAreas = @($script:AllowedAreas)
        AllowedStates = @($script:AllowedStates)
        ExcludedDeterministicCategories = @($script:ExcludedDeterministicCategories)
        ExcludedDeterministicAreas = @($script:ExcludedDeterministicAreas)
        DeterministicFilter = Get-LocalCiDeterministicFilter
    }
}

function Normalize-PolicyPath {
    param([string]$Path)
    return $Path -replace '\\', '/'
}

function Test-IsExcludedPolicyPath {
    param([string]$RelativePath)

    $normalized = Normalize-PolicyPath $RelativePath
    $parts = $normalized -split '/'
    foreach ($part in $parts) {
        if ($part -in @('bin', 'obj', '.git', '.worktrees', 'node_modules', 'ext')) {
            return $true
        }
    }

    return $normalized -match '\.g\.cs$'
}

function Test-IsPolicyTestFile {
    param([string]$RelativePath)

    $normalized = (Normalize-PolicyPath $RelativePath).ToLowerInvariant()
    return $normalized -match '(^|/)(tests|[^/]*\.tests|[^/]*tests)/'
}

function Get-PolicyLineNumber {
    param(
        [string]$Content,
        [int]$Index
    )

    return ($Content.Substring(0, $Index) -split "`n").Count
}

function New-PolicyViolation {
    param(
        [string]$Code,
        [string]$File,
        [int]$Line,
        [string]$Message,
        [string]$TraitName = '',
        [string]$TraitValue = ''
    )

    [PSCustomObject]@{
        Code = $Code
        File = $File
        Line = $Line
        Message = $Message
        TraitName = $TraitName
        TraitValue = $TraitValue
    }
}

function Get-PolicyTraitsFromText {
    param(
        [string]$Text,
        [int]$StartLine
    )

    $traits = @()
    $traitMatches = [regex]::Matches($Text, $script:TraitPattern)
    foreach ($match in $traitMatches) {
        $prefix = $Text.Substring(0, $match.Index)
        $lineOffset = ([regex]::Matches($prefix, '\r?\n')).Count
        $traits += [PSCustomObject]@{
            Line = $StartLine + $lineOffset
            Name = $match.Groups['name'].Value
            Value = $match.Groups['value'].Value
        }
    }

    return @($traits)
}

function Get-PolicyTraitsFromLine {
    param(
        [string]$Line,
        [int]$LineNumber
    )

    return @(Get-PolicyTraitsFromText -Text $Line -StartLine $LineNumber)
}

function Get-PolicyDeclarationKind {
    param([string]$Line)

    if ($Line -match '\b(class|record|struct)\s+[A-Za-z_]') {
        return 'Class'
    }

    if ($Line -match '\(' -and $Line -notmatch '^\s*(if|for|foreach|while|switch|catch|using|lock)\b') {
        return 'Member'
    }

    return ''
}

function Get-PolicyClassEndLine {
    param(
        [string[]]$Lines,
        [int]$StartLine
    )

    $depth = 0
    $seenOpenBrace = $false
    for ($i = $StartLine - 1; $i -lt $Lines.Count; $i++) {
        foreach ($char in $Lines[$i].ToCharArray()) {
            if ($char -eq '{') {
                $depth++
                $seenOpenBrace = $true
            } elseif ($char -eq '}' -and $seenOpenBrace) {
                $depth--
                if ($depth -le 0) {
                    return $i + 1
                }
            }
        }
    }

    return $Lines.Count
}

function Remove-PolicyStringLiterals {
    # Drops the CONTENT of C# string/char literals and comments (each literal collapses to an empty
    # ""/'' and comments are removed) so bracket characters inside them never affect attribute-bracket
    # counting or same-line declaration detection. Handles: regular strings ("\..." backslash escapes),
    # VERBATIM strings (@"..." where backslash is literal and "" escapes a quote), char literals
    # ('...'), interpolated strings ($"..."), and line (//) + single-line block (/* */) comments.
    # Not a full C# tokenizer — nested quotes inside interpolation holes and raw string literals
    # ("""...""") remain best-effort — but it covers every construct that appears in test attribute
    # lines (callers only need accurate [ / ] accounting, not byte-for-byte fidelity).
    param([string]$Line)

    $sb = [System.Text.StringBuilder]::new()
    $n = $Line.Length
    $i = 0
    while ($i -lt $n) {
        $ch = $Line[$i]
        $next = if ($i + 1 -lt $n) { $Line[$i + 1] } else { [char]0 }

        if ($ch -eq '/' -and $next -eq '/') { break }                     # line comment: drop the rest
        if ($ch -eq '/' -and $next -eq '*') {                             # block comment (single-line scope)
            $i += 2
            while ($i -lt $n -and -not ($Line[$i] -eq '*' -and ($i + 1 -lt $n) -and $Line[$i + 1] -eq '/')) { $i++ }
            $i += 2
            continue
        }
        if ($ch -eq '@' -and $next -eq '"') {                             # verbatim string: "" escapes, \ is literal
            [void]$sb.Append('""')
            $i += 2
            while ($i -lt $n) {
                if ($Line[$i] -eq '"') {
                    if (($i + 1 -lt $n) -and $Line[$i + 1] -eq '"') { $i += 2; continue }
                    $i++; break
                }
                $i++
            }
            continue
        }
        if ($ch -eq '"') {                                                # regular / interpolated string
            [void]$sb.Append('""')
            $i++
            while ($i -lt $n) {
                if ($Line[$i] -eq '\') { $i += 2; continue }
                if ($Line[$i] -eq '"') { $i++; break }
                $i++
            }
            continue
        }
        if ($ch -eq "'") {                                                # char literal
            [void]$sb.Append("''")
            $i++
            while ($i -lt $n) {
                if ($Line[$i] -eq '\') { $i += 2; continue }
                if ($Line[$i] -eq "'") { $i++; break }
                $i++
            }
            continue
        }

        [void]$sb.Append($ch)
        $i++
    }

    return $sb.ToString()
}

function Get-PolicyAttributeRemainder {
    param([string]$Line)

    # Operate entirely on the string-masked copy so a ']' inside a quoted argument / comment is not
    # mistaken for an attribute's closing bracket. The remainder is used only for declaration-kind
    # detection (class/record/'('), which is insensitive to masked literal content.
    $remaining = (Remove-PolicyStringLiterals $Line).TrimStart()
    while ($remaining.StartsWith('[')) {
        $closeIndex = $remaining.IndexOf(']')
        if ($closeIndex -lt 0) {
            return ''
        }

        $remaining = $remaining.Substring($closeIndex + 1).TrimStart()
    }

    return $remaining
}

function Get-PolicyTraitTargets {
    param([string]$Content)

    $targets = @()
    $pendingAttributeLines = @()
    $pendingAttributeStartLine = 0
    $inAttributeBlock = $false
    $attributeDepth = 0
    $lines = $Content -split '\r?\n'

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($attributeDepth -gt 0 -or $line -match '^\s*\[') {
            $inAttributeBlock = $true
            if ($pendingAttributeLines.Count -eq 0) {
                $pendingAttributeStartLine = $i + 1
            }

            $pendingAttributeLines += $line
            # Count brackets on a string-masked copy so brackets inside quoted attribute arguments
            # (e.g. [InlineData("[")]) do not inflate the depth and swallow following lines.
            $bracketScan = Remove-PolicyStringLiterals $line
            $attributeDepth += ([regex]::Matches($bracketScan, '\[')).Count
            $attributeDepth -= ([regex]::Matches($bracketScan, '\]')).Count
            if ($attributeDepth -gt 0) {
                continue
            }
            if ($attributeDepth -lt 0) {
                $attributeDepth = 0
            }

            $pendingAttributeText = $pendingAttributeLines -join "`n"
            $pendingTraits = @(Get-PolicyTraitsFromText -Text $pendingAttributeText -StartLine $pendingAttributeStartLine)

            $sameLineDeclaration = Get-PolicyAttributeRemainder -Line $line
            $sameLineKind = Get-PolicyDeclarationKind -Line $sameLineDeclaration
            if ($pendingTraits.Count -gt 0 -and $sameLineKind) {
                $targets += [PSCustomObject]@{
                    Kind = $sameLineKind
                    Line = $i + 1
                    EndLine = if ($sameLineKind -eq 'Class') { Get-PolicyClassEndLine -Lines $lines -StartLine ($i + 1) } else { $i + 1 }
                    Traits = @($pendingTraits)
                }
                $pendingAttributeLines = @()
                $pendingAttributeStartLine = 0
                $inAttributeBlock = $false
            }

            continue
        }

        if ($inAttributeBlock) {
            if ($line -match '^\s*$') {
                continue
            }

            $kind = Get-PolicyDeclarationKind -Line $line
            $pendingAttributeText = $pendingAttributeLines -join "`n"
            $pendingTraits = @(Get-PolicyTraitsFromText -Text $pendingAttributeText -StartLine $pendingAttributeStartLine)
            if ($pendingTraits.Count -gt 0 -and $kind) {
                $targets += [PSCustomObject]@{
                    Kind = $kind
                    Line = $i + 1
                    EndLine = if ($kind -eq 'Class') { Get-PolicyClassEndLine -Lines $lines -StartLine ($i + 1) } else { $i + 1 }
                    Traits = @($pendingTraits)
                }
            }

            $pendingAttributeLines = @()
            $pendingAttributeStartLine = 0
            $inAttributeBlock = $false
        }
    }

    return @($targets)
}

function Test-TestTraitPolicy {
    <#
    .SYNOPSIS
        Validates xUnit trait vocabulary and opt-in lane markers for plugin tests.
    #>
    [CmdletBinding()]
    param(
        [string]$Path = (Get-Location).Path,
        [bool]$Recurse = $true
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Path not found: $Path"
    }

    $root = (Resolve-Path -LiteralPath $Path).Path
    $allowedCategories = @{}
    $allowedAreas = @{}
    $allowedStates = @{}
    foreach ($category in $script:AllowedCategories) { $allowedCategories[$category] = $true }
    foreach ($area in $script:AllowedAreas) { $allowedAreas[$area] = $true }
    foreach ($state in $script:AllowedStates) { $allowedStates[$state] = $true }

    $searchParams = @{
        Path = $root
        Filter = '*.cs'
        File = $true
        ErrorAction = 'SilentlyContinue'
    }
    if ($Recurse) {
        $searchParams['Recurse'] = $true
    }

    $files = @(Get-ChildItem @searchParams)
    $violations = @()
    $traitUsages = @()
    $filesScanned = 0

    foreach ($file in $files) {
        $relPath = $file.FullName
        if ($file.FullName.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relPath = $file.FullName.Substring($root.Length).TrimStart('\', '/')
        }
        $relPath = Normalize-PolicyPath $relPath

        if (Test-IsExcludedPolicyPath $relPath) {
            continue
        }

        if (-not (Test-IsPolicyTestFile $relPath)) {
            continue
        }

        $filesScanned++
        $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) {
            continue
        }

        $firstTraitLine = 1
        $traitMatches = [regex]::Matches($content, $script:TraitPattern)
        $isFirstTrait = $true

        foreach ($match in $traitMatches) {
            $name = $match.Groups['name'].Value
            $value = $match.Groups['value'].Value
            $line = Get-PolicyLineNumber -Content $content -Index $match.Index
            if ($isFirstTrait) {
                $firstTraitLine = $line
                $isFirstTrait = $false
            }

            $traitUsages += [PSCustomObject]@{
                File = $relPath
                Line = $line
                Name = $name
                Value = $value
            }

            switch ($name) {
                'Category' {
                    if (-not $allowedCategories.ContainsKey($value)) {
                        $violations += New-PolicyViolation -Code 'UnknownCategory' -File $relPath -Line $line -TraitName $name -TraitValue $value -Message "Unknown Category trait '$value'. Add it to Common policy or use an existing lane."
                    }
                }
                'Area' {
                    if (-not $allowedAreas.ContainsKey($value)) {
                        $violations += New-PolicyViolation -Code 'UnknownArea' -File $relPath -Line $line -TraitName $name -TraitValue $value -Message "Unknown Area trait '$value'. Add it to Common policy or use an existing lane."
                    }
                }
                'State' {
                    if (-not $allowedStates.ContainsKey($value)) {
                        $violations += New-PolicyViolation -Code 'UnknownState' -File $relPath -Line $line -TraitName $name -TraitValue $value -Message "Unknown State trait '$value'. Supported state traits: $($script:AllowedStates -join ', ')."
                    }
                }
            }
        }

        $traitTargets = @(Get-PolicyTraitTargets -Content $content)
        $effectiveTargets = @()
        foreach ($target in $traitTargets) {
            $effectiveTraits = @($target.Traits)
            if ($target.Kind -ne 'Class') {
                $classTraits = @(
                    $traitTargets |
                        Where-Object { $_.Kind -eq 'Class' -and $_.Line -lt $target.Line -and $_.EndLine -ge $target.Line } |
                        Sort-Object Line |
                        ForEach-Object { $_.Traits }
                )
                $effectiveTraits = @($classTraits + $effectiveTraits)
            }

            $effectiveTargets += [PSCustomObject]@{
                Kind = $target.Kind
                Line = $target.Line
                Traits = @($effectiveTraits)
            }
        }

        foreach ($target in $effectiveTargets) {
            $blockTraits = @($target.Traits)
            $blockCategories = @($blockTraits | Where-Object { $_.Name -eq 'Category' } | ForEach-Object { $_.Value })
            $blockAreas = @($blockTraits | Where-Object { $_.Name -eq 'Area' } | ForEach-Object { $_.Value })

            if ($blockCategories -contains 'Docker' -and -not ($blockCategories -contains 'DockerE2E')) {
                $line = [int](@($blockTraits | Where-Object { $_.Name -eq 'Category' -and $_.Value -eq 'Docker' } | Select-Object -First 1).Line)
                $violations += New-PolicyViolation -Code 'DockerWithoutDockerE2E' -File $relPath -Line $line -TraitName 'Category' -TraitValue 'Docker' -Message "Docker tests must also carry Category=DockerE2E so they stay in the explicit Docker lane."
            }

            if ($blockAreas -contains 'E2E/Hermetic') {
                $invalidHermeticCategoryTraits = @(
                    $blockTraits |
                        Where-Object { $_.Name -eq 'Category' -and $_.Value -ne 'Integration' -and $_.Value -in $script:ExcludedDeterministicCategories } |
                        Sort-Object Value -Unique
                )
                foreach ($trait in $invalidHermeticCategoryTraits) {
                    $violations += New-PolicyViolation -Code 'HermeticWithExcludedCategory' -File $relPath -Line ([int]$trait.Line) -TraitName 'Category' -TraitValue $trait.Value -Message "Area=E2E/Hermetic can only bypass Category=Integration. Remove Category=$($trait.Value) or move the test to its opt-in lane."
                }

                $invalidHermeticAreaTraits = @(
                    $blockTraits |
                        Where-Object { $_.Name -eq 'Area' -and $_.Value -ne 'E2E/Hermetic' -and $_.Value -in $script:ExcludedDeterministicAreas } |
                        Sort-Object Value -Unique
                )
                foreach ($trait in $invalidHermeticAreaTraits) {
                    $violations += New-PolicyViolation -Code 'HermeticWithExcludedArea' -File $relPath -Line ([int]$trait.Line) -TraitName 'Area' -TraitValue $trait.Value -Message "Area=E2E/Hermetic cannot be combined with Area=$($trait.Value) because that would bypass the opt-in live lane."
                }
            }
        }

        $hasReleaseCategory = @($traitTargets | Where-Object {
            @($_.Traits | Where-Object { $_.Name -eq 'Category' } | ForEach-Object { $_.Value }) -contains 'ReleaseE2E'
        }).Count -gt 0
        if ((Normalize-PolicyPath $relPath) -match '(^|/)ReleaseE2E/' -and -not $hasReleaseCategory) {
            $violations += New-PolicyViolation -Code 'ReleaseE2EWithoutCategory' -File $relPath -Line $firstTraitLine -TraitName 'Category' -TraitValue 'ReleaseE2E' -Message "ReleaseE2E tests must carry Category=ReleaseE2E so default CI excludes them intentionally."
        }
    }

    [PSCustomObject]@{
        Success = ($violations.Count -eq 0)
        Violations = @($violations)
        TraitUsages = @($traitUsages)
        FilesScanned = $filesScanned
        Policy = Get-TestTraitPolicy
    }
}

Export-ModuleMember -Function Get-LocalCiDeterministicFilter, Get-TestTraitPolicy, Test-TestTraitPolicy
