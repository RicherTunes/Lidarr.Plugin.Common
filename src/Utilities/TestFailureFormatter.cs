using System;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Formats a consistent user-facing message for plugin Test() catch arms.
/// </summary>
/// <remarks>
/// Wave 87 consolidation. Waves 73 (tidalarr), 74 (qobuzarr indexer), and 75
/// (qobuzarr download client) independently grew the same message shape:
///
///   "Test failed (HttpRequestException): connection refused.
///    Full details in Lidarr logs."
///
/// This helper centralizes that shape so future plugins (and any new Test()
/// branches) get the same wording for free. The exception type is surfaced
/// because it lets users self-triage at a glance — HttpRequestException
/// signals network, FormatException signals data-shape, AuthenticationException
/// signals credentials. The "Full details in Lidarr logs" pointer reminds
/// operators where to find the stack trace.
/// </remarks>
public static class TestFailureFormatter
{
    /// <summary>
    /// Format an exception caught in a Test() implementation into a single
    /// user-facing line that includes the exception type, message, and a
    /// pointer to the full log.
    /// </summary>
    /// <param name="exception">The caught exception. Must not be null.</param>
    /// <param name="prefix">
    /// Optional prefix. Defaults to "Test failed". Pass a more specific phrase
    /// (e.g. "Connection test failed") to disambiguate when multiple Test
    /// surfaces exist in the same plugin.
    /// </param>
    /// <returns>A formatted string suitable for ValidationFailure messages.</returns>
    public static string Format(Exception exception, string prefix = "Test failed")
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        return $"{prefix} ({exception.GetType().Name}): {exception.Message}. Full details in Lidarr logs.";
    }
}
