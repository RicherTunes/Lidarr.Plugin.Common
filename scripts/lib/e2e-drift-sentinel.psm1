# e2e-drift-sentinel.psm1 - Stub-vs-live drift detection for E2E testing
# Runs minimal live probes and validates fields that stubs depend on
# Supports error-mode (no creds) and success-mode (with creds) probes
# Warning-first mode: alerts on drift without failing (configurable)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Import helpers for API calls
$helpersPath = Join-Path $PSScriptRoot "e2e-helpers.psm1"
if (Test-Path $helpersPath) {
    Import-Module $helpersPath -Force
}

#region Provider Field Expectations

# Expectations version - increment when changing field contracts
# Used for tracking/debugging when expectations were last updated
$script:ExpectationsVersion = "1.0.0"
$script:ExpectationsLastUpdated = "2025-01-27"

# Define the fields that stubs depend on for each provider
# These are the minimum fields required for hermetic E2E to be representative
$script:ProviderFieldExpectations = @{
    "qobuz" = @{
        Name = "Qobuz"
        Version = "1.0.0"

        # Error mode: validates error response structure (no creds needed)
        AuthEndpoint = "https://www.qobuz.com/api.json/0.2/user/login"
        AuthMethod = "POST"
        AuthBodyError = @{
            email = "drift-sentinel@test.invalid"
            password = "invalid"
            app_id = "000000000"
        }
        ExpectedErrorFields = @("status", "message", "code")

        # Success mode: validates authenticated response structure (creds required)
        # These fields are what stubs need to return for realistic hermetic tests
        ExpectedAuthSuccessFields = @{
            Required = @("user", "user_auth_token")
            User = @("id", "login", "country_code")
        }

        # Search endpoint
        SearchEndpoint = "https://www.qobuz.com/api.json/0.2/album/search"
        SearchParams = @{ query = "Miles Davis"; limit = 5 }

        # Success payload fields (validated in at-least-one mode for optional fields)
        ExpectedSearchFields = @{
            Root = @{ Required = @("albums"); Optional = @() }
            Albums = @{ Required = @("items", "total"); Optional = @("offset", "limit") }
            # For album items: required fields must be present, optional can be missing
            AlbumItem = @{
                Required = @("id", "title", "artist", "duration", "tracks_count")
                # These may be absent for some catalog items - validate as "at least one has it"
                AtLeastOne = @("maximum_bit_depth", "maximum_sampling_rate", "hires", "streamable")
            }
            Artist = @{ Required = @("id", "name"); Optional = @("image") }
        }
    }

    "tidal" = @{
        Name = "Tidal"
        Version = "1.0.0"

        # Tidal uses OAuth
        AuthEndpoint = "https://auth.tidal.com/v1/oauth2/token"
        AuthMethod = "POST"
        AuthBodyError = @{
            grant_type = "client_credentials"
            client_id = "invalid"
            client_secret = "invalid"
        }
        ExpectedErrorFields = @("error", "error_description")
        ExpectedAuthSuccessFields = @{
            Required = @("access_token", "token_type", "expires_in")
        }

        # Search requires auth
        SearchEndpoint = "https://api.tidal.com/v1/search"
        SearchParams = @{ query = "Miles Davis"; limit = 5; countryCode = "US" }
        ExpectedSearchErrorFields = @("status", "subStatus", "userMessage")
        ExpectedSearchFields = @{
            Root = @{ Required = @("albums", "artists", "tracks"); Optional = @("playlists", "videos") }
            Albums = @{ Required = @("items", "totalNumberOfItems"); Optional = @("offset", "limit") }
            AlbumItem = @{
                Required = @("id", "title", "artists", "numberOfTracks", "duration")
                AtLeastOne = @("audioQuality", "audioModes", "explicit")
            }
            Artist = @{ Required = @("id", "name"); Optional = @("picture") }
        }
    }
}

#endregion

#region Rate Limiting and Backoff

$script:RequestState = @{
    LastRequestTime = [datetime]::MinValue
    BackoffMs = 1000
    MaxBackoffMs = 30000
    RateLimitHits = 0
}

<#
.SYNOPSIS
    Makes an HTTP request with exponential backoff and rate limit handling.
.PARAMETER Uri
    Request URI.
.PARAMETER Method
    HTTP method.
.PARAMETER Body
    Request body (will be JSON-encoded for POST).
.PARAMETER Headers
    Optional headers hashtable.
.PARAMETER TimeoutSeconds
    Request timeout.
.PARAMETER MaxRetries
    Maximum retry attempts on transient errors.
.OUTPUTS
    PSCustomObject with Success, Response, StatusCode, IsRateLimited, Error properties.
#>
function Invoke-RateLimitedRequest {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,

        [string]$Method = "GET",

        [hashtable]$Body = $null,

        [hashtable]$Headers = @{},

        [int]$TimeoutSeconds = 30,

        [int]$MaxRetries = 3
    )

    $result = [PSCustomObject]@{
        Success = $false
        Response = $null
        StatusCode = 0
        IsRateLimited = $false
        IsInconclusive = $false
        Error = $null
    }

    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
        # Respect backoff
        $timeSinceLastRequest = ([datetime]::Now - $script:RequestState.LastRequestTime).TotalMilliseconds
        if ($timeSinceLastRequest -lt $script:RequestState.BackoffMs) {
            $waitMs = $script:RequestState.BackoffMs - $timeSinceLastRequest
            Start-Sleep -Milliseconds $waitMs
        }

        $script:RequestState.LastRequestTime = [datetime]::Now

        try {
            $requestParams = @{
                Uri = $Uri
                Method = $Method
                TimeoutSec = $TimeoutSeconds
                ErrorAction = "Stop"
            }

            if ($Headers.Count -gt 0) {
                $requestParams.Headers = $Headers
            }

            if ($Body -and $Method -eq "POST") {
                $requestParams.Body = ($Body | ConvertTo-Json -Compress)
                $requestParams.ContentType = "application/json"
            }

            $response = Invoke-RestMethod @requestParams
            $result.Response = $response
            $result.StatusCode = 200
            $result.Success = $true

            # Reset backoff on success
            $script:RequestState.BackoffMs = 1000
            return $result
        }
        catch {
            $statusCode = 0
            $errorBody = $null

            if ($_.Exception.Response) {
                $statusCode = [int]$_.Exception.Response.StatusCode
                $result.StatusCode = $statusCode

                try {
                    $stream = $_.Exception.Response.GetResponseStream()
                    $reader = New-Object System.IO.StreamReader($stream)
                    $errorBody = $reader.ReadToEnd()
                    $reader.Close()

                    $result.Response = $errorBody | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if (-not $result.Response) {
                        $result.Response = $errorBody
                    }
                }
                catch {
                    $result.Response = $errorBody
                }
            }

            # Handle rate limiting (429)
            if ($statusCode -eq 429) {
                $script:RequestState.RateLimitHits++
                $result.IsRateLimited = $true

                # Exponential backoff
                $script:RequestState.BackoffMs = [Math]::Min(
                    $script:RequestState.BackoffMs * 2,
                    $script:RequestState.MaxBackoffMs
                )

                # Check for Retry-After header
                $retryAfter = $_.Exception.Response.Headers["Retry-After"]
                if ($retryAfter) {
                    $waitSeconds = [int]$retryAfter
                    Write-Host "    Rate limited (429). Retry-After: ${waitSeconds}s" -ForegroundColor Yellow
                    Start-Sleep -Seconds $waitSeconds
                }
                else {
                    Write-Host "    Rate limited (429). Backing off: $($script:RequestState.BackoffMs)ms" -ForegroundColor Yellow
                    Start-Sleep -Milliseconds $script:RequestState.BackoffMs
                }

                if ($attempt -lt $MaxRetries) {
                    continue
                }

                # Max retries exceeded for rate limit - mark as inconclusive
                $result.IsInconclusive = $true
                $result.Error = "Rate limited after $MaxRetries attempts"
                return $result
            }

            # For auth errors (401/403), this is expected in error-mode probes
            if ($statusCode -in @(400, 401, 403)) {
                $result.Success = $true  # Expected failure
                return $result
            }

            # Transient errors - retry with backoff
            if ($statusCode -in @(500, 502, 503, 504) -and $attempt -lt $MaxRetries) {
                $script:RequestState.BackoffMs = [Math]::Min(
                    $script:RequestState.BackoffMs * 2,
                    $script:RequestState.MaxBackoffMs
                )
                Write-Host "    Transient error ($statusCode). Retrying in $($script:RequestState.BackoffMs)ms..." -ForegroundColor Yellow
                Start-Sleep -Milliseconds $script:RequestState.BackoffMs
                continue
            }

            $result.Error = $_.Exception.Message
            return $result
        }
    }

    return $result
}

#endregion

#region Field Validation Helpers

<#
.SYNOPSIS
    Validates that required fields are present in an object.
.PARAMETER Object
    Object to validate.
.PARAMETER RequiredFields
    Array of required field names.
.PARAMETER ObjectName
    Name for error messages.
.OUTPUTS
    Array of missing field names (empty if all present).
#>
function Get-MissingRequiredFields {
    param(
        [object]$Object,
        [string[]]$RequiredFields,
        [string]$ObjectName = "object"
    )

    if (-not $Object -or -not $Object.PSObject) {
        return $RequiredFields
    }

    $actualFields = @($Object.PSObject.Properties.Name)
    $missing = @()

    foreach ($field in $RequiredFields) {
        if ($field -notin $actualFields) {
            $missing += "$ObjectName.$field"
        }
    }

    return $missing
}

<#
.SYNOPSIS
    Validates that at least one item in an array has the specified fields.
.PARAMETER Items
    Array of items to check.
.PARAMETER AtLeastOneFields
    Fields where at least one item should have the field.
.PARAMETER ObjectName
    Name for error messages.
.OUTPUTS
    Array of fields that no item has (empty if at least one has each).
#>
function Get-MissingAtLeastOneFields {
    param(
        [array]$Items,
        [string[]]$AtLeastOneFields,
        [string]$ObjectName = "item"
    )

    if (-not $Items -or $Items.Count -eq 0) {
        return $AtLeastOneFields | ForEach-Object { "$ObjectName.$_" }
    }

    $missing = @()

    foreach ($field in $AtLeastOneFields) {
        $anyHasField = $false
        foreach ($item in $Items) {
            if ($item.PSObject.Properties.Name -contains $field) {
                $anyHasField = $true
                break
            }
        }
        if (-not $anyHasField) {
            $missing += "$ObjectName.$field (no item has this)"
        }
    }

    return $missing
}

#endregion

#region Drift Detection Functions

<#
.SYNOPSIS
    Probes a provider's auth endpoint in error mode (no creds needed).
.PARAMETER Provider
    Provider name (qobuz, tidal).
.PARAMETER TimeoutSeconds
    HTTP request timeout.
.OUTPUTS
    PSCustomObject with Success, DriftDetected, Fields, and Details properties.
#>
function Test-AuthEndpointDrift {
    param(
        [Parameter(Mandatory)]
        [string]$Provider,

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $false
        DriftDetected = $false
        Provider = $Provider
        Endpoint = "auth-error"
        Mode = "error"
        ExpectedFields = @()
        ActualFields = @()
        MissingFields = @()
        Details = $null
        Error = $null
        IsInconclusive = $false
    }

    $providerLower = $Provider.ToLower()
    if (-not $script:ProviderFieldExpectations.ContainsKey($providerLower)) {
        $result.Error = "Unknown provider: $Provider"
        return $result
    }

    $config = $script:ProviderFieldExpectations[$providerLower]
    $result.ExpectedFields = $config.ExpectedErrorFields

    $requestResult = Invoke-RateLimitedRequest `
        -Uri $config.AuthEndpoint `
        -Method $config.AuthMethod `
        -Body $config.AuthBodyError `
        -TimeoutSeconds $TimeoutSeconds

    if ($requestResult.IsInconclusive) {
        $result.IsInconclusive = $true
        $result.Error = $requestResult.Error
        return $result
    }

    if (-not $requestResult.Success -and -not $requestResult.Response) {
        $result.Error = $requestResult.Error
        return $result
    }

    $response = $requestResult.Response

    # Extract actual fields
    $actualFields = @()
    if ($response -and $response.PSObject) {
        $actualFields = @($response.PSObject.Properties.Name)
    }
    $result.ActualFields = $actualFields

    # Check for missing fields
    $missingFields = Get-MissingRequiredFields -Object $response -RequiredFields $config.ExpectedErrorFields -ObjectName "error"
    $result.MissingFields = $missingFields

    if ($missingFields.Count -gt 0) {
        $result.DriftDetected = $true
        $result.Details = "Missing: $($missingFields -join ', ')"
    }
    else {
        $result.Details = "All expected error fields present"
    }

    $result.Success = $true
    return $result
}

<#
.SYNOPSIS
    Probes a provider with real credentials to validate success payload schema.
.PARAMETER Provider
    Provider name (qobuz, tidal).
.PARAMETER Credentials
    Hashtable with provider-specific credentials.
.PARAMETER TimeoutSeconds
    HTTP request timeout.
.OUTPUTS
    PSCustomObject with Success, DriftDetected, and detailed field analysis.
#>
function Test-SuccessPayloadDrift {
    param(
        [Parameter(Mandatory)]
        [string]$Provider,

        [Parameter(Mandatory)]
        [hashtable]$Credentials,

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $false
        DriftDetected = $false
        Provider = $Provider
        Endpoint = "search-success"
        Mode = "success"
        ExpectedFields = @{}
        ActualFields = @{}
        MissingFields = @()
        AtLeastOneMissing = @()
        Details = $null
        Error = $null
        IsInconclusive = $false
        SkippedReason = $null
    }

    $providerLower = $Provider.ToLower()
    if (-not $script:ProviderFieldExpectations.ContainsKey($providerLower)) {
        $result.Error = "Unknown provider: $Provider"
        return $result
    }

    $config = $script:ProviderFieldExpectations[$providerLower]
    $result.ExpectedFields = $config.ExpectedSearchFields

    # Build authenticated request based on provider
    $headers = @{}
    $uri = $config.SearchEndpoint
    $params = $config.SearchParams.Clone()

    switch ($providerLower) {
        "qobuz" {
            if (-not $Credentials.AppId) {
                $result.SkippedReason = "Missing AppId credential"
                return $result
            }
            $params["app_id"] = $Credentials.AppId
            if ($Credentials.AuthToken) {
                $headers["X-User-Auth-Token"] = $Credentials.AuthToken
            }
        }
        "tidal" {
            if (-not $Credentials.AccessToken) {
                $result.SkippedReason = "Missing AccessToken credential"
                return $result
            }
            $headers["Authorization"] = "Bearer $($Credentials.AccessToken)"
            if ($Credentials.Market) {
                $params["countryCode"] = $Credentials.Market
            }
        }
    }

    # Build query string
    $queryString = ($params.GetEnumerator() | ForEach-Object { "$($_.Key)=$([uri]::EscapeDataString($_.Value))" }) -join "&"
    $fullUri = "$uri?$queryString"

    $requestResult = Invoke-RateLimitedRequest `
        -Uri $fullUri `
        -Method "GET" `
        -Headers $headers `
        -TimeoutSeconds $TimeoutSeconds

    if ($requestResult.IsInconclusive) {
        $result.IsInconclusive = $true
        $result.Error = $requestResult.Error
        return $result
    }

    if (-not $requestResult.Success) {
        $result.Error = "Search failed: $($requestResult.Error) (HTTP $($requestResult.StatusCode))"
        return $result
    }

    $response = $requestResult.Response
    if (-not $response) {
        $result.Error = "Empty response from search endpoint"
        return $result
    }

    $allMissing = @()
    $atLeastOneMissing = @()
    $actualFieldsMap = @{}

    # Validate root fields
    $rootExpected = $config.ExpectedSearchFields.Root
    if ($rootExpected) {
        $actualFieldsMap["Root"] = @($response.PSObject.Properties.Name)
        $missing = Get-MissingRequiredFields -Object $response -RequiredFields $rootExpected.Required -ObjectName "Root"
        $allMissing += $missing
    }

    # Validate albums container
    $albumsContainer = $null
    switch ($providerLower) {
        "qobuz" { $albumsContainer = $response.albums }
        "tidal" { $albumsContainer = $response.albums }
    }

    if ($albumsContainer) {
        $albumsExpected = $config.ExpectedSearchFields.Albums
        if ($albumsExpected) {
            $actualFieldsMap["Albums"] = @($albumsContainer.PSObject.Properties.Name)
            $missing = Get-MissingRequiredFields -Object $albumsContainer -RequiredFields $albumsExpected.Required -ObjectName "Albums"
            $allMissing += $missing
        }

        # Validate album items
        $items = $albumsContainer.items
        if ($items -and $items.Count -gt 0) {
            $albumItemExpected = $config.ExpectedSearchFields.AlbumItem
            if ($albumItemExpected) {
                $actualFieldsMap["AlbumItem"] = @($items[0].PSObject.Properties.Name)

                # Required fields - check first item
                $missing = Get-MissingRequiredFields -Object $items[0] -RequiredFields $albumItemExpected.Required -ObjectName "AlbumItem"
                $allMissing += $missing

                # AtLeastOne fields - check across all items
                if ($albumItemExpected.AtLeastOne) {
                    $atLeastMissing = Get-MissingAtLeastOneFields -Items $items -AtLeastOneFields $albumItemExpected.AtLeastOne -ObjectName "AlbumItem"
                    $atLeastOneMissing += $atLeastMissing
                }
            }

            # Validate artist in first album
            $artist = $items[0].artist
            if (-not $artist -and $items[0].artists) {
                $artist = $items[0].artists[0]
            }
            if ($artist) {
                $artistExpected = $config.ExpectedSearchFields.Artist
                if ($artistExpected) {
                    $actualFieldsMap["Artist"] = @($artist.PSObject.Properties.Name)
                    $missing = Get-MissingRequiredFields -Object $artist -RequiredFields $artistExpected.Required -ObjectName "Artist"
                    $allMissing += $missing
                }
            }
        }
    }

    $result.ActualFields = $actualFieldsMap
    $result.MissingFields = $allMissing
    $result.AtLeastOneMissing = $atLeastOneMissing

    # Determine drift
    if ($allMissing.Count -gt 0) {
        $result.DriftDetected = $true
        $result.Details = "Missing required: $($allMissing -join ', ')"
    }
    elseif ($atLeastOneMissing.Count -gt 0) {
        $result.DriftDetected = $true
        $result.Details = "Missing at-least-one: $($atLeastOneMissing -join ', ')"
    }
    else {
        $result.Details = "All expected success fields present"
    }

    $result.Success = $true
    return $result
}

<#
.SYNOPSIS
    Runs drift sentinel for all configured providers.
.PARAMETER Providers
    Array of provider names to check (default: all configured).
.PARAMETER FailOnDrift
    If true, returns failure when drift detected. If false, only warns.
.PARAMETER Credentials
    Hashtable of provider credentials for success-mode probes.
    Format: @{ qobuz = @{ AppId = "..."; AuthToken = "..." }; tidal = @{ AccessToken = "..." } }
.PARAMETER IncludeSuccessMode
    If true and credentials available, also run success-payload validation.
.PARAMETER TimeoutSeconds
    HTTP request timeout per endpoint.
.OUTPUTS
    PSCustomObject with Success, DriftCount, Warnings, and Results properties.
#>
function Invoke-DriftSentinel {
    param(
        [string[]]$Providers = @("qobuz", "tidal"),

        [switch]$FailOnDrift,

        [hashtable]$Credentials = @{},

        [switch]$IncludeSuccessMode,

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $true
        DriftCount = 0
        ErrorCount = 0
        InconclusiveCount = 0
        SkippedCount = 0
        Warnings = @()
        Results = @()
        ExpectationsVersion = $script:ExpectationsVersion
    }

    Write-Host "Drift Sentinel v$($script:ExpectationsVersion) (updated: $($script:ExpectationsLastUpdated))" -ForegroundColor DarkGray

    foreach ($provider in $Providers) {
        Write-Host "`n--- Drift Sentinel: $provider ---" -ForegroundColor Cyan

        # Error mode: always run (no creds needed)
        Write-Host "  [Error mode] Probing auth endpoint..." -ForegroundColor DarkGray
        $authResult = Test-AuthEndpointDrift -Provider $provider -TimeoutSeconds $TimeoutSeconds
        $result.Results += $authResult

        if ($authResult.IsInconclusive) {
            Write-Host "  INCONCLUSIVE (rate limited): $($authResult.Error)" -ForegroundColor Yellow
            $result.InconclusiveCount++
        }
        elseif ($authResult.Error) {
            Write-Host "  ERROR: $($authResult.Error)" -ForegroundColor Yellow
            $result.ErrorCount++
        }
        elseif ($authResult.DriftDetected) {
            Write-Host "  DRIFT DETECTED: $($authResult.Details)" -ForegroundColor Yellow
            $result.DriftCount++
            $result.Warnings += "[$provider/auth-error] $($authResult.Details)"
        }
        else {
            Write-Host "  OK: $($authResult.Details)" -ForegroundColor Green
        }

        # Success mode: only run if credentials available
        if ($IncludeSuccessMode) {
            $providerCreds = $Credentials[$provider.ToLower()]
            if ($providerCreds -and $providerCreds.Count -gt 0) {
                Write-Host "  [Success mode] Probing search endpoint..." -ForegroundColor DarkGray
                $successResult = Test-SuccessPayloadDrift -Provider $provider -Credentials $providerCreds -TimeoutSeconds $TimeoutSeconds
                $result.Results += $successResult

                if ($successResult.SkippedReason) {
                    Write-Host "  SKIPPED: $($successResult.SkippedReason)" -ForegroundColor DarkGray
                    $result.SkippedCount++
                }
                elseif ($successResult.IsInconclusive) {
                    Write-Host "  INCONCLUSIVE (rate limited): $($successResult.Error)" -ForegroundColor Yellow
                    $result.InconclusiveCount++
                }
                elseif ($successResult.Error) {
                    Write-Host "  ERROR: $($successResult.Error)" -ForegroundColor Yellow
                    $result.ErrorCount++
                }
                elseif ($successResult.DriftDetected) {
                    Write-Host "  DRIFT DETECTED: $($successResult.Details)" -ForegroundColor Yellow
                    $result.DriftCount++
                    $result.Warnings += "[$provider/search-success] $($successResult.Details)"
                }
                else {
                    Write-Host "  OK: $($successResult.Details)" -ForegroundColor Green
                }
            }
            else {
                Write-Host "  [Success mode] SKIPPED: No credentials for $provider" -ForegroundColor DarkGray
                $result.SkippedCount++
            }
        }
    }

    # Summary
    Write-Host "`n--- Drift Sentinel Summary ---" -ForegroundColor Cyan
    Write-Host "  Expectations version: $($script:ExpectationsVersion)" -ForegroundColor DarkGray
    Write-Host "  Providers checked: $($Providers.Count)" -ForegroundColor DarkGray
    Write-Host "  Drift detected: $($result.DriftCount)" -ForegroundColor $(if ($result.DriftCount -gt 0) { "Yellow" } else { "Green" })
    Write-Host "  Errors: $($result.ErrorCount)" -ForegroundColor $(if ($result.ErrorCount -gt 0) { "Yellow" } else { "DarkGray" })
    Write-Host "  Inconclusive (rate limited): $($result.InconclusiveCount)" -ForegroundColor $(if ($result.InconclusiveCount -gt 0) { "Yellow" } else { "DarkGray" })
    Write-Host "  Skipped: $($result.SkippedCount)" -ForegroundColor DarkGray

    if ($result.Warnings.Count -gt 0) {
        Write-Host "`n  Warnings:" -ForegroundColor Yellow
        foreach ($warning in $result.Warnings) {
            Write-Host "    - $warning" -ForegroundColor Yellow
        }
    }

    # Determine success based on mode
    if ($FailOnDrift -and $result.DriftCount -gt 0) {
        $result.Success = $false
        Write-Host "`n  DRIFT SENTINEL FAILED: $($result.DriftCount) drift(s) detected" -ForegroundColor Red
    }
    elseif ($result.DriftCount -gt 0) {
        Write-Host "`n  DRIFT SENTINEL WARNING: $($result.DriftCount) drift(s) detected (not failing - warning mode)" -ForegroundColor Yellow
    }
    else {
        Write-Host "`n  DRIFT SENTINEL PASSED: No drift detected" -ForegroundColor Green
    }

    return $result
}

<#
.SYNOPSIS
    Gets the list of supported providers for drift detection.
.OUTPUTS
    Array of provider names.
#>
function Get-DriftSentinelProviders {
    return @($script:ProviderFieldExpectations.Keys)
}

<#
.SYNOPSIS
    Gets the field expectations for a specific provider.
.PARAMETER Provider
    Provider name.
.OUTPUTS
    Hashtable with field expectations and version info.
#>
function Get-ProviderFieldExpectations {
    param(
        [Parameter(Mandatory)]
        [string]$Provider
    )

    $providerLower = $Provider.ToLower()
    if ($script:ProviderFieldExpectations.ContainsKey($providerLower)) {
        $config = $script:ProviderFieldExpectations[$providerLower].Clone()
        $config["_expectationsVersion"] = $script:ExpectationsVersion
        $config["_lastUpdated"] = $script:ExpectationsLastUpdated
        return $config
    }

    return $null
}

<#
.SYNOPSIS
    Gets the current expectations version.
.OUTPUTS
    PSCustomObject with Version and LastUpdated properties.
#>
function Get-DriftSentinelVersion {
    return [PSCustomObject]@{
        Version = $script:ExpectationsVersion
        LastUpdated = $script:ExpectationsLastUpdated
    }
}

#endregion

Export-ModuleMember -Function @(
    'Test-AuthEndpointDrift',
    'Test-SuccessPayloadDrift',
    'Invoke-DriftSentinel',
    'Get-DriftSentinelProviders',
    'Get-ProviderFieldExpectations',
    'Get-DriftSentinelVersion'
)
