# e2e-helpers.psm1 - Common E2E helper library
# Provides: Wait-Until, Assert-Eventually, Get-RecentLogs, Assert-NoSecretsInText
# Eliminates fixed sleeps and duplicated polling/redaction logic across gates

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Import sanitization for secret detection
$sanitizePath = Join-Path $PSScriptRoot "e2e-sanitize.psm1"
if (Test-Path $sanitizePath) {
    Import-Module $sanitizePath -Force
}

#region Polling Helpers

<#
.SYNOPSIS
    Polls a condition until it returns $true or timeout expires.
.PARAMETER ScriptBlock
    Condition to evaluate. Should return $true when satisfied.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.PARAMETER IntervalMs
    Polling interval in milliseconds.
.PARAMETER Description
    Human-readable description for logging.
.OUTPUTS
    PSCustomObject with Success, ElapsedMs, Attempts, and LastResult properties.
#>
function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock,

        [int]$TimeoutSeconds = 60,

        [int]$IntervalMs = 1000,

        [string]$Description = "condition"
    )

    $result = [PSCustomObject]@{
        Success = $false
        ElapsedMs = 0
        Attempts = 0
        LastResult = $null
        LastError = $null
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $timeoutMs = $TimeoutSeconds * 1000

    while ($stopwatch.ElapsedMilliseconds -lt $timeoutMs) {
        $result.Attempts++

        try {
            $conditionResult = & $ScriptBlock
            $result.LastResult = $conditionResult

            if ($conditionResult -eq $true) {
                $stopwatch.Stop()
                $result.Success = $true
                $result.ElapsedMs = $stopwatch.ElapsedMilliseconds
                return $result
            }
        }
        catch {
            $result.LastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds $IntervalMs
    }

    $stopwatch.Stop()
    $result.ElapsedMs = $stopwatch.ElapsedMilliseconds
    return $result
}

<#
.SYNOPSIS
    Asserts a condition becomes true within timeout, with detailed failure info.
.PARAMETER ScriptBlock
    Condition to evaluate.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.PARAMETER IntervalMs
    Polling interval.
.PARAMETER FailureMessage
    Message to include in exception if condition never becomes true.
.PARAMETER Description
    Human-readable description for logging.
.OUTPUTS
    The last result from the condition (if truthy), otherwise throws.
#>
function Assert-Eventually {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock,

        [int]$TimeoutSeconds = 60,

        [int]$IntervalMs = 1000,

        [string]$FailureMessage = "Condition not satisfied within timeout",

        [string]$Description = "condition"
    )

    $waitResult = Wait-Until -ScriptBlock $ScriptBlock -TimeoutSeconds $TimeoutSeconds -IntervalMs $IntervalMs -Description $Description

    if (-not $waitResult.Success) {
        $details = "Timeout: ${TimeoutSeconds}s, Attempts: $($waitResult.Attempts), Elapsed: $($waitResult.ElapsedMs)ms"
        if ($waitResult.LastError) {
            $details += ", Last error: $($waitResult.LastError)"
        }
        if ($waitResult.LastResult) {
            $details += ", Last result: $($waitResult.LastResult)"
        }
        throw "$FailureMessage ($details)"
    }

    return $waitResult.LastResult
}

#endregion

#region Lidarr API Polling Helpers

<#
.SYNOPSIS
    Polls Lidarr queue until a condition is met.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER Condition
    ScriptBlock that receives queue records array and returns $true when satisfied.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.PARAMETER IntervalMs
    Polling interval.
.PARAMETER Description
    Human-readable description for logging.
.OUTPUTS
    PSCustomObject with Success, Records, ElapsedMs, and Error properties.
#>
function Wait-LidarrQueueCondition {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [scriptblock]$Condition,

        [int]$TimeoutSeconds = 60,

        [int]$IntervalMs = 3000,

        [string]$Description = "queue condition"
    )

    $result = [PSCustomObject]@{
        Success = $false
        Records = @()
        ElapsedMs = 0
        Error = $null
    }

    $waitResult = Wait-Until -ScriptBlock {
        try {
            $queue = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/queue?page=1&pageSize=100" -Headers $Headers -TimeoutSec 30 -ErrorAction Stop

            $records = @()
            if ($queue.PSObject.Properties.Name -contains 'records') {
                $records = @($queue.records)
            }
            elseif ($queue -is [array]) {
                $records = @($queue)
            }

            # Pass records to condition
            $script:_lastRecords = $records
            return (& $Condition $records)
        }
        catch {
            return $false
        }
    } -TimeoutSeconds $TimeoutSeconds -IntervalMs $IntervalMs -Description $Description

    $result.ElapsedMs = $waitResult.ElapsedMs

    if ($waitResult.Success) {
        $result.Success = $true
        $result.Records = $script:_lastRecords
    }
    else {
        $result.Error = "Queue condition '$Description' not satisfied within ${TimeoutSeconds}s"
        if ($waitResult.LastError) {
            $result.Error += ": $($waitResult.LastError)"
        }
    }

    return $result
}

<#
.SYNOPSIS
    Polls Lidarr history until a condition is met.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER Condition
    ScriptBlock that receives history records array and returns $true when satisfied.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.PARAMETER IntervalMs
    Polling interval.
.OUTPUTS
    PSCustomObject with Success, Records, ElapsedMs, and Error properties.
#>
function Wait-LidarrHistoryCondition {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [scriptblock]$Condition,

        [int]$TimeoutSeconds = 60,

        [int]$IntervalMs = 3000,

        [string]$Description = "history condition"
    )

    $result = [PSCustomObject]@{
        Success = $false
        Records = @()
        ElapsedMs = 0
        Error = $null
    }

    $waitResult = Wait-Until -ScriptBlock {
        try {
            $history = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/history?page=1&pageSize=100" -Headers $Headers -TimeoutSec 30 -ErrorAction Stop

            $records = @()
            if ($history.PSObject.Properties.Name -contains 'records') {
                $records = @($history.records)
            }
            elseif ($history -is [array]) {
                $records = @($history)
            }

            $script:_lastHistoryRecords = $records
            return (& $Condition $records)
        }
        catch {
            return $false
        }
    } -TimeoutSeconds $TimeoutSeconds -IntervalMs $IntervalMs -Description $Description

    $result.ElapsedMs = $waitResult.ElapsedMs

    if ($waitResult.Success) {
        $result.Success = $true
        $result.Records = $script:_lastHistoryRecords
    }
    else {
        $result.Error = "History condition '$Description' not satisfied within ${TimeoutSeconds}s"
    }

    return $result
}

<#
.SYNOPSIS
    Waits for Lidarr API to become available after container start/restart.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.OUTPUTS
    PSCustomObject with Success, Version, ElapsedMs, and Error properties.
#>
function Wait-LidarrApiReady {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [int]$TimeoutSeconds = 120
    )

    $result = [PSCustomObject]@{
        Success = $false
        Version = $null
        ElapsedMs = 0
        Error = $null
    }

    $waitResult = Wait-Until -ScriptBlock {
        try {
            $status = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/system/status" -Headers $Headers -TimeoutSec 5 -ErrorAction Stop
            if ($status -and $status.version) {
                $script:_apiVersion = $status.version
                return $true
            }
            return $false
        }
        catch {
            return $false
        }
    } -TimeoutSeconds $TimeoutSeconds -IntervalMs 2000 -Description "Lidarr API ready"

    $result.ElapsedMs = $waitResult.ElapsedMs

    if ($waitResult.Success) {
        $result.Success = $true
        $result.Version = $script:_apiVersion
    }
    else {
        $result.Error = "Lidarr API not ready within ${TimeoutSeconds}s"
    }

    return $result
}

#endregion

#region Log Helpers

<#
.SYNOPSIS
    Gets recent container logs with optional filtering.
.PARAMETER ContainerName
    Name of the container.
.PARAMETER TailLines
    Number of recent lines to fetch.
.PARAMETER Pattern
    Optional regex pattern to filter logs.
.OUTPUTS
    Array of log lines (filtered if pattern provided).
#>
function Get-RecentLogs {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [int]$TailLines = 500,

        [string]$Pattern = $null
    )

    $logs = & docker logs $ContainerName --tail $TailLines 2>&1
    if ($LASTEXITCODE -ne 0) {
        return @()
    }

    $lines = @($logs -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if (-not [string]::IsNullOrWhiteSpace($Pattern)) {
        $lines = @($lines | Where-Object { $_ -match $Pattern })
    }

    return $lines
}

<#
.SYNOPSIS
    Waits for a log pattern to appear in container logs.
.PARAMETER ContainerName
    Name of the container.
.PARAMETER Pattern
    Regex pattern to search for.
.PARAMETER TimeoutSeconds
    Maximum time to wait.
.PARAMETER TailLines
    Number of recent lines to check each poll.
.OUTPUTS
    PSCustomObject with Success, MatchingLines, ElapsedMs, and Error properties.
#>
function Wait-LogPattern {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [int]$TimeoutSeconds = 60,

        [int]$TailLines = 500
    )

    $result = [PSCustomObject]@{
        Success = $false
        MatchingLines = @()
        ElapsedMs = 0
        Error = $null
    }

    $waitResult = Wait-Until -ScriptBlock {
        $lines = Get-RecentLogs -ContainerName $ContainerName -TailLines $TailLines -Pattern $Pattern
        if ($lines.Count -gt 0) {
            $script:_matchingLogLines = $lines
            return $true
        }
        return $false
    } -TimeoutSeconds $TimeoutSeconds -IntervalMs 2000 -Description "log pattern '$Pattern'"

    $result.ElapsedMs = $waitResult.ElapsedMs

    if ($waitResult.Success) {
        $result.Success = $true
        $result.MatchingLines = $script:_matchingLogLines
    }
    else {
        $result.Error = "Log pattern '$Pattern' not found within ${TimeoutSeconds}s"
    }

    return $result
}

#endregion

#region Secret Detection Helpers

# Patterns that indicate secrets leaked in text
$script:SecretLeakPatterns = @(
    # Bearer tokens
    @{ Name = "Bearer token"; Pattern = '(?i)bearer\s+[A-Za-z0-9_-]{20,}' }
    # API keys in headers
    @{ Name = "API key header"; Pattern = '(?i)x-api-key:\s*[A-Za-z0-9_-]{20,}' }
    @{ Name = "Authorization header"; Pattern = '(?i)authorization:\s*[^\s\[\]]{20,}' }
    # Query params with secrets (not redacted)
    @{ Name = "Query param secret"; Pattern = '(?i)[?&](access_token|api_key|apikey|token|auth|secret|password|refresh_token|client_secret)=(?!\[REDACTED\])[^\s&]{8,}' }
    # OAuth codes
    @{ Name = "OAuth code"; Pattern = '(?i)[?&]code=(?!\[REDACTED\])[A-Za-z0-9_-]{20,}' }
    # JWT tokens
    @{ Name = "JWT token"; Pattern = '(?i)eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}' }
    # Hex strings that look like API keys (32+ chars)
    @{ Name = "Hex API key"; Pattern = '(?i)(?<=[=:/"''])[a-f0-9]{32,}(?=[&\s"''/?]|$)' }
    # Base64 tokens (long, no obvious redaction)
    @{ Name = "Base64 token"; Pattern = '(?i)(?<=[=:/"''])[A-Za-z0-9+/]{50,}={0,2}(?=[&\s"''/?]|$)' }
)

# Patterns that indicate properly redacted secrets
$script:RedactedPatterns = @(
    '\[REDACTED\]',
    '\*{3,}',
    '<HIDDEN>',
    '\[HIDDEN\]',
    '\[PRIVATE-IP\]',
    '\[LOCALHOST\]',
    '\[INTERNAL-HOST\]'
)

<#
.SYNOPSIS
    Checks text for leaked secrets.
.PARAMETER Text
    Text to check (log lines, error messages, etc.).
.PARAMETER AllowedPatterns
    Patterns that are allowed (e.g., known test data).
.OUTPUTS
    PSCustomObject with HasLeaks, LeakDetails, and CheckedPatterns properties.
#>
function Test-TextForSecrets {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text,

        [string[]]$AllowedPatterns = @()
    )

    $result = [PSCustomObject]@{
        HasLeaks = $false
        LeakDetails = @()
        CheckedPatterns = $script:SecretLeakPatterns.Count
    }

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $result
    }

    foreach ($patternDef in $script:SecretLeakPatterns) {
        $matches = [regex]::Matches($Text, $patternDef.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

        foreach ($match in $matches) {
            $matchValue = $match.Value
            $isAllowed = $false

            # Check if matches an allowed pattern
            foreach ($allowedPattern in $AllowedPatterns) {
                if ($matchValue -match $allowedPattern) {
                    $isAllowed = $true
                    break
                }
            }

            # Check if actually redacted
            foreach ($redactedPattern in $script:RedactedPatterns) {
                if ($matchValue -match $redactedPattern) {
                    $isAllowed = $true
                    break
                }
            }

            if (-not $isAllowed) {
                $result.HasLeaks = $true
                $result.LeakDetails += [PSCustomObject]@{
                    PatternName = $patternDef.Name
                    Pattern = $patternDef.Pattern
                    Match = if ($matchValue.Length -gt 40) { $matchValue.Substring(0, 40) + "..." } else { $matchValue }
                }
            }
        }
    }

    return $result
}

<#
.SYNOPSIS
    Asserts text contains no leaked secrets, throws if found.
.PARAMETER Text
    Text to check.
.PARAMETER Context
    Context description for error message.
.PARAMETER AllowedPatterns
    Patterns that are allowed.
#>
function Assert-NoSecretsInText {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text,

        [string]$Context = "text",

        [string[]]$AllowedPatterns = @()
    )

    $checkResult = Test-TextForSecrets -Text $Text -AllowedPatterns $AllowedPatterns

    if ($checkResult.HasLeaks) {
        $leakSummary = ($checkResult.LeakDetails | ForEach-Object { "$($_.PatternName): $($_.Match)" }) -join "; "
        throw "Secret leak detected in $Context : $leakSummary"
    }
}

<#
.SYNOPSIS
    Checks container logs for leaked secrets.
.PARAMETER ContainerName
    Name of the container.
.PARAMETER TailLines
    Number of recent lines to check.
.PARAMETER AllowedPatterns
    Patterns that are allowed.
.OUTPUTS
    PSCustomObject with HasLeaks, LeakDetails, and LinesChecked properties.
#>
function Test-ContainerLogsForSecrets {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [int]$TailLines = 500,

        [string[]]$AllowedPatterns = @()
    )

    $result = [PSCustomObject]@{
        HasLeaks = $false
        LeakDetails = @()
        LinesChecked = 0
    }

    $logs = Get-RecentLogs -ContainerName $ContainerName -TailLines $TailLines
    $result.LinesChecked = $logs.Count

    $logText = $logs -join "`n"
    $secretCheck = Test-TextForSecrets -Text $logText -AllowedPatterns $AllowedPatterns

    $result.HasLeaks = $secretCheck.HasLeaks
    $result.LeakDetails = $secretCheck.LeakDetails

    return $result
}

#endregion

Export-ModuleMember -Function @(
    # Polling helpers
    'Wait-Until',
    'Assert-Eventually',
    # Lidarr API helpers
    'Wait-LidarrQueueCondition',
    'Wait-LidarrHistoryCondition',
    'Wait-LidarrApiReady',
    # Log helpers
    'Get-RecentLogs',
    'Wait-LogPattern',
    # Secret detection
    'Test-TextForSecrets',
    'Assert-NoSecretsInText',
    'Test-ContainerLogsForSecrets'
)
