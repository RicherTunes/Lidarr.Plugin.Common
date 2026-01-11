# e2e-sanitize.psm1 - Centralized sanitization module for E2E test artifacts
# Single source of truth for all secret/PII redaction patterns

# Module version for baseline tracking
$script:SanitizerVersion = '1.0.0'

#region Pattern Definitions

# Patterns for secrets in error messages/URLs
$script:ErrorPatterns = @(
    # URL query parameters with secrets
    @{ Pattern = '(?i)([\?&](access_token|api_key|apikey|token|auth|secret|password|bearer|refresh_token|client_secret))=([^\s&"'']+)'; Replace = '$1=[REDACTED]' }
    # Authorization headers in error messages
    @{ Pattern = '(?i)(authorization:\s*(bearer|basic)\s+)([^\s"'']+)'; Replace = '$1[REDACTED]' }
    # Hex strings that look like API keys (20-40 chars) in URLs or messages
    @{ Pattern = '(?i)(?<=[=:/"''])[a-f0-9]{20,40}(?=[&\s"''/?]|$)'; Replace = '[REDACTED]' }
    # Base64-encoded tokens (long alphanumeric with +/=)
    @{ Pattern = '(?i)(?<=[=:/"''])[A-Za-z0-9+/]{40,}={0,2}(?=[&\s"''/?]|$)'; Replace = '[REDACTED]' }
    # JWT tokens (xxx.xxx.xxx format)
    @{ Pattern = '(?i)eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+'; Replace = '[JWT-REDACTED]' }
)

# Private/internal endpoint patterns (matched against string VALUES)
$script:PrivateEndpointPatterns = @{
    # RFC1918 IPv4 ranges (private networks)
    'RFC1918_10' = @{ Pattern = '(?<!\d)10\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'; Replace = '[PRIVATE-IP]' }
    'RFC1918_172' = @{ Pattern = '(?<!\d)172\.(?:1[6-9]|2\d|3[01])\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'; Replace = '[PRIVATE-IP]' }
    'RFC1918_192' = @{ Pattern = '(?<!\d)192\.168\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'; Replace = '[PRIVATE-IP]' }
    # Link-local (APIPA)
    'LinkLocal' = @{ Pattern = '(?<!\d)169\.254\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'; Replace = '[PRIVATE-IP]' }
    # Loopback
    'Loopback' = @{ Pattern = '(?<!\d)127\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'; Replace = '[LOCALHOST]' }
    # Docker/internal hostnames
    'DockerInternal' = @{ Pattern = '(?i)host\.docker\.internal'; Replace = '[INTERNAL-HOST]' }
    'Localhost' = @{ Pattern = '(?i)(?<![a-z0-9])localhost(?![a-z0-9])'; Replace = '[LOCALHOST]' }
    # IPv6 private/internal addresses (RFC 3986: IPv6 in URLs must be bracketed)
    'IPv6_Loopback' = @{ Pattern = '\[::1\]'; Replace = '[LOCALHOST]' }
    'IPv6_LinkLocal' = @{ Pattern = '(?i)\[fe80:[0-9a-f:]+\]'; Replace = '[PRIVATE-IPv6]' }
    'IPv6_UniqueLocal' = @{ Pattern = '(?i)\[f[cd][0-9a-f]{2}:[0-9a-f:]+\]'; Replace = '[PRIVATE-IPv6]' }
}

# Query parameters that commonly carry secrets
$script:SecretQueryParams = @(
    'code',
    'code_verifier',
    'refresh_token',
    'access_token',
    'token',
    'apiKey',
    'api_key',
    'secret',
    'password',
    'client_secret'
)

# Sensitive field name patterns (for object traversal)
$script:SensitiveFieldPatterns = @(
    'password',
    'secret',
    'token',
    'key',
    'apikey',
    'api_key',
    'bearer',
    'credential',
    'auth',
    'redirect',
    'accesstoken',
    'access_token',
    'refreshtoken',
    'refresh_token',
    'clientsecret',
    'client_secret',
    'privatekey',
    'private_key',
    'code_verifier',
    'codeverifier',
    'authorization_code',
    'authorizationcode',
    'pkce',
    'configurationurl',
    'configuration_url'
)

# Public identifiers that should NEVER be redacted
# Keep this list narrow per user requirements
$script:PublicIdentifierPatterns = @(
    # ISRC: ISO 3901 standard - 12 chars, CC-XXX-YY-NNNNN format (often without hyphens)
    '(?i)^[A-Z]{2}[A-Z0-9]{3}\d{2}\d{5}$'
    # MusicBrainz UUIDs - standard UUID format
    '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
)

#endregion

#region Public Functions

<#
.SYNOPSIS
    Returns the sanitizer module version for baseline tracking.
.OUTPUTS
    Version string (e.g., "1.0.0").
#>
function Get-SanitizerVersion {
    return $script:SanitizerVersion
}

<#
.SYNOPSIS
    Sanitizes error text by removing secrets, tokens, and private endpoints.
.PARAMETER Text
    The error string to sanitize.
.OUTPUTS
    Sanitized string with secrets replaced by [REDACTED] markers.
.EXAMPLE
    Sanitize-ErrorText "Failed to auth with token=abc123def456"
#>
function Sanitize-ErrorText {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }

    $result = $Text

    # Apply error patterns (secrets in URLs, headers, etc.)
    foreach ($pattern in $script:ErrorPatterns) {
        $result = $result -replace $pattern.Pattern, $pattern.Replace
    }

    # Apply private endpoint patterns
    foreach ($entry in $script:PrivateEndpointPatterns.GetEnumerator()) {
        $result = $result -replace $entry.Value.Pattern, $entry.Value.Replace
    }

    # Redact secret query parameters (keep param name, redact value)
    foreach ($param in $script:SecretQueryParams) {
        $result = $result -replace "(?i)([?&])($param)=([^&]*)", '$1$2=[REDACTED]'
    }

    return $result
}

<#
.SYNOPSIS
    Sanitizes a URL by redacting secrets and private endpoints.
.PARAMETER Url
    The URL to sanitize.
.OUTPUTS
    Sanitized URL string.
.EXAMPLE
    Sanitize-Url "http://192.168.1.1:8686/api?apikey=secret123"
#>
function Sanitize-Url {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Url
    )

    if ([string]::IsNullOrEmpty($Url)) {
        return $Url
    }

    # URLs get the same treatment as error text
    return Sanitize-ErrorText -Text $Url
}

<#
.SYNOPSIS
    Sanitizes a file path, optionally extracting only the basename.
.PARAMETER Path
    The file path to sanitize.
.PARAMETER Mode
    'Basename' (default): Strip directory, keep filename only.
    'None': Return path unchanged (still applies other sanitization).
.OUTPUTS
    Sanitized path string.
.EXAMPLE
    Sanitize-Path "C:\Users\john\Documents\secret.flac"
    # Returns: "secret.flac"
.EXAMPLE
    Sanitize-Path "/home/user/Music/track.flac" -Mode Basename
    # Returns: "track.flac"
#>
function Sanitize-Path {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Path,

        [ValidateSet('Basename', 'None')]
        [string]$Mode = 'Basename'
    )

    if ([string]::IsNullOrEmpty($Path)) {
        return $Path
    }

    if ($Mode -eq 'Basename') {
        return [System.IO.Path]::GetFileName($Path)
    }

    return $Path
}

<#
.SYNOPSIS
    Sanitizes any value type (string, object, array) by walking the structure.
.DESCRIPTION
    - Strings: Applies Sanitize-ErrorText
    - Arrays: Recursively sanitizes each element
    - Objects/Hashtables: Redacts sensitive field values, recursively sanitizes others
    - Primitives: Returns unchanged
.PARAMETER Value
    The value to sanitize (any type).
.OUTPUTS
    Sanitized value (same type as input).
.EXAMPLE
    $config = @{ apiKey = "secret"; name = "test" }
    Sanitize-Any $config
    # Returns: @{ apiKey = "[REDACTED]"; name = "test" }
#>
function Sanitize-Any {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    # Handle arrays
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string] -and $Value -isnot [System.Collections.IDictionary]) {
        $sanitized = @($Value | ForEach-Object { Sanitize-Any -Value $_ })
        return $sanitized
    }

    # Handle hashtables and dictionaries
    if ($Value -is [System.Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $Value.Keys) {
            $fieldValue = $Value[$key]
            if (Test-SensitiveFieldName -Name $key) {
                $result[$key] = '[REDACTED]'
            }
            elseif ($fieldValue -is [string]) {
                # Check if it's a public identifier before sanitizing
                if (Test-PublicIdentifier -Value $fieldValue) {
                    $result[$key] = $fieldValue
                }
                else {
                    $result[$key] = Sanitize-ErrorText -Text $fieldValue
                }
            }
            else {
                $result[$key] = Sanitize-Any -Value $fieldValue
            }
        }
        return $result
    }

    # Handle PSObjects
    if ($Value -is [PSObject] -and $Value.PSObject.Properties.Count -gt 0) {
        $result = @{}
        foreach ($prop in $Value.PSObject.Properties) {
            $fieldValue = $prop.Value
            if (Test-SensitiveFieldName -Name $prop.Name) {
                $result[$prop.Name] = '[REDACTED]'
            }
            elseif ($fieldValue -is [string]) {
                if (Test-PublicIdentifier -Value $fieldValue) {
                    $result[$prop.Name] = $fieldValue
                }
                else {
                    $result[$prop.Name] = Sanitize-ErrorText -Text $fieldValue
                }
            }
            else {
                $result[$prop.Name] = Sanitize-Any -Value $fieldValue
            }
        }
        return [PSCustomObject]$result
    }

    # Handle strings
    if ($Value -is [string]) {
        if (Test-PublicIdentifier -Value $Value) {
            return $Value
        }
        return Sanitize-ErrorText -Text $Value
    }

    # Primitives (numbers, bools, etc.) - return unchanged
    return $Value
}

#endregion

#region Private Helper Functions

<#
.SYNOPSIS
    Tests if a field name matches sensitive patterns.
#>
function Test-SensitiveFieldName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }

    $nameLower = $Name.ToLowerInvariant()
    foreach ($pattern in $script:SensitiveFieldPatterns) {
        if ($nameLower -match $pattern) {
            return $true
        }
    }
    return $false
}

<#
.SYNOPSIS
    Tests if a value is a known public identifier (ISRC, MusicBrainz UUID).
.DESCRIPTION
    Returns $true if the value should be preserved (not redacted).
#>
function Test-PublicIdentifier {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }

    foreach ($pattern in $script:PublicIdentifierPatterns) {
        if ($Value -match $pattern) {
            return $true
        }
    }
    return $false
}

<#
.SYNOPSIS
    Tests if a string value contains a private/internal endpoint.
#>
function Test-PrivateEndpoint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }

    foreach ($entry in $script:PrivateEndpointPatterns.GetEnumerator()) {
        if ($Value -match $entry.Value.Pattern) {
            return $true
        }
    }
    return $false
}

#endregion

#region Backward Compatibility - Thin Wrappers

<#
.SYNOPSIS
    Legacy wrapper for Sanitize-ErrorText.
.DESCRIPTION
    Maintained for backward compatibility with existing code.
    Calls the new Sanitize-ErrorText function.
#>
function Invoke-ErrorSanitization {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ErrorString
    )

    return Sanitize-ErrorText -Text $ErrorString
}

#endregion

# Export public functions
Export-ModuleMember -Function @(
    # Core sanitization API
    'Get-SanitizerVersion',
    'Sanitize-ErrorText',
    'Sanitize-Url',
    'Sanitize-Path',
    'Sanitize-Any',
    # Utility functions
    'Test-PrivateEndpoint',
    # Backward compatibility
    'Invoke-ErrorSanitization'
)
