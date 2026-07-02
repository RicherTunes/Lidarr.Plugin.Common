// <copyright file="DiagnosticErrorCodes.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Diagnostics;

/// <summary>
/// Canonical error code constants for <see cref="DiagnosticHealthResult.ErrorCode"/>.
/// All streaming plugins should use these constants instead of defining local error
/// code strings to ensure ecosystem-wide parity.
/// </summary>
public static class DiagnosticErrorCodes
{
    /// <summary>Authentication failed (expired token, invalid credentials, revoked session).</summary>
    public const string AuthFailed = "AUTH_FAILED";

    /// <summary>Network connectivity failure (DNS, TCP, TLS handshake).</summary>
    public const string ConnectionFailed = "CONNECTION_FAILED";

    /// <summary>Server rate-limited the request (HTTP 429 or equivalent).</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Request timed out before a response was received.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>Region or geo-restriction prevents access.</summary>
    public const string RegionBlocked = "REGION_BLOCKED";

    /// <summary>Settings or input validation failed.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Requested resource (track, album, catalog item) not found.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Requested provider model was not found or is unsupported.</summary>
    public const string ModelNotFound = "MODEL_NOT_FOUND";

    /// <summary>Provider initialization failed before a request could be sent.</summary>
    public const string ProviderInitFailed = "PROVIDER_INIT_FAILED";

    /// <summary>Server returned an unexpected error (5xx).</summary>
    public const string ServerError = "SERVER_ERROR";
}
