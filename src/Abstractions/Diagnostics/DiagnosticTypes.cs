// <copyright file="DiagnosticTypes.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Diagnostics;

/// <summary>
/// Canonical diagnostic-type identifiers for <see cref="DiagnosticHealthResult"/> health checks.
/// These strings are a stable contract surfaced in diagnostic results and logs, so all streaming
/// plugins should reference these constants instead of re-declaring per-plugin copies (which drift).
/// The set is the union of the values qobuz/tidal/apple currently re-declare in their
/// <c>*HealthDiagnostics</c> classes; a service-specific check that doesn't map to one of these may
/// add its own, but the shared ones live here so a rename happens in exactly one place.
/// </summary>
public static class DiagnosticTypes
{
    /// <summary>Validates stored credentials / session against the service's auth endpoint.</summary>
    public const string AuthValidate = "auth_validate";

    /// <summary>Basic network reachability / connectivity probe to the service.</summary>
    public const string Connectivity = "connectivity";

    /// <summary>Probes that an audio stream URL can be resolved/opened for a known track.</summary>
    public const string StreamProbe = "stream_probe";

    /// <summary>Verifies catalog / metadata access (search or lookup) succeeds.</summary>
    public const string CatalogAccess = "catalog_access";
}
