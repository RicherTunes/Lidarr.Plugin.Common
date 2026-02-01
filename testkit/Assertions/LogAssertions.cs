using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Security;
using Lidarr.Plugin.Common.TestKit.Hosting;

namespace Lidarr.Plugin.Common.TestKit.Assertions;

/// <summary>
/// Assertion helpers for validating that logs do not contain sensitive information.
/// Uses the centralized Sanitize class to detect secrets and provides a contract-based
/// approach rather than testing for specific redaction strings.
/// </summary>
public static class LogAssertions
{
    /// <summary>
    /// Maximum characters to scan in exception text to prevent performance issues with large logs.
    /// </summary>
    private const int MaxExceptionScanLength = 50000; // 50KB limit

    /// <summary>
    /// Default safe URL hosts that are allowed to have query parameters in logs.
    /// These are test/local URLs that cannot leak secrets externally.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSafeHosts = new[]
    {
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]",
        "0.0.0.0",
        "host.docker.internal",
        "testserver",
        "test.local",
        "example.com",
        "example.org",
        "example.net"
    };
    /// <summary>
    /// Asserts that no known secrets appear in any log entries.
    /// This is a contract-based test: it verifies "no secrets appear in logs"
    /// rather than testing for specific redaction formats.
    /// </summary>
    /// <param name="sink">The test log sink containing captured log entries.</param>
    /// <param name="secrets">Known secret values that must not appear in logs (e.g., API keys, tokens, passwords).</param>
    /// <exception cref="LogAssertionException">Thrown when a secret is found in logs.</exception>
    public static void AssertNoSecretsInLogs(TestLogSink sink, params string[] secrets)
    {
        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        if (secrets is null || secrets.Length == 0)
        {
            return; // Nothing to check
        }

        var entries = sink.Snapshot();
        var violations = new List<SecretViolation>();

        foreach (var entry in entries)
        {
            foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
            {
                // Check the message
                if (!string.IsNullOrEmpty(entry.Message) &&
                    entry.Message.Contains(secret, StringComparison.Ordinal))
                {
                    violations.Add(new SecretViolation(
                        entry.Category,
                        entry.Level.ToString(),
                        MaskSecret(secret),
                        "Message"));
                }

                // Check the exception message if present
                if (entry.Exception?.Message != null &&
                    entry.Exception.Message.Contains(secret, StringComparison.Ordinal))
                {
                    violations.Add(new SecretViolation(
                        entry.Category,
                        entry.Level.ToString(),
                        MaskSecret(secret),
                        "Exception.Message"));
                }

                // Check the full exception string (includes inner exceptions and stack traces)
                // Apply size cap to prevent performance issues with large exception chains
                var exceptionText = entry.Exception?.ToString();
                if (exceptionText != null)
                {
                    var textToScan = exceptionText.Length > MaxExceptionScanLength
                        ? exceptionText.Substring(0, MaxExceptionScanLength)
                        : exceptionText;

                    if (textToScan.Contains(secret, StringComparison.Ordinal))
                    {
                        violations.Add(new SecretViolation(
                            entry.Category,
                            entry.Level.ToString(),
                            MaskSecret(secret),
                            "Exception.ToString()"));
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            var details = string.Join("\n", violations.Select(v =>
                $"  - Category: {v.Category}, Level: {v.Level}, Secret: {v.MaskedSecret}, Location: {v.Location}"));

            throw new LogAssertionException(
                $"Found {violations.Count} secret(s) leaked in log entries:\n{details}\n\n" +
                "Ensure all sensitive data is sanitized before logging using Sanitize.SafeErrorMessage() or Sanitize.RedactUrls().");
        }
    }

    /// <summary>
    /// Asserts that log entries do not contain URL query parameters (which may contain secrets).
    /// URLs should be logged using Sanitize.RedactUrls() or Sanitize.UrlHostOnly().
    /// Checks both message text and exception details.
    /// </summary>
    /// <param name="sink">The test log sink containing captured log entries.</param>
    /// <param name="safeHosts">Optional list of hosts to exclude from checking. If null, uses <see cref="DefaultSafeHosts"/>. Pass empty array to check all hosts.</param>
    /// <exception cref="LogAssertionException">Thrown when unredacted URLs are found.</exception>
    public static void AssertNoUnredactedUrls(TestLogSink sink, IEnumerable<string>? safeHosts = null)
    {
        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        var entries = sink.Snapshot();
        var violations = new List<string>();

        // Use default safe hosts if not specified
        var allowedHosts = safeHosts ?? DefaultSafeHosts;
        var safeHostSet = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);

        // Pattern: URLs with query strings - matches http(s)://host/path?query
        // We check for ? followed by non-whitespace to detect query parameters
        var urlPattern = new System.Text.RegularExpressions.Regex(
            @"https?://[^\s""'<>]+\?[^\s""'<>]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var entry in entries)
        {
            // Check message
            CheckTextForUnredactedUrls(entry.Message, entry.Category, "Message", urlPattern, safeHostSet, violations);

            // Check exception message
            CheckTextForUnredactedUrls(entry.Exception?.Message, entry.Category, "Exception.Message", urlPattern, safeHostSet, violations);

            // Check full exception string (includes inner exceptions)
            CheckTextForUnredactedUrls(entry.Exception?.ToString(), entry.Category, "Exception.ToString()", urlPattern, safeHostSet, violations);
        }

        if (violations.Count > 0)
        {
            throw new LogAssertionException(
                $"Found {violations.Count} unredacted URL(s) with query parameters in logs:\n" +
                string.Join("\n", violations.Select(v => $"  - {v}")) +
                "\n\nUse Sanitize.RedactUrls() or Sanitize.UrlHostOnly() before logging URLs." +
                "\nIf these are test URLs, add the host to safeHosts parameter or DefaultSafeHosts.");
        }
    }

    private static void CheckTextForUnredactedUrls(
        string? text,
        string category,
        string location,
        System.Text.RegularExpressions.Regex urlPattern,
        HashSet<string> safeHosts,
        List<string> violations)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Apply size cap for exception text to prevent performance issues
        var textToScan = text.Length > MaxExceptionScanLength
            ? text.Substring(0, MaxExceptionScanLength)
            : text;

        var matches = urlPattern.Matches(textToScan);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            // Skip if it ends with [REDACTED] (already sanitized)
            if (match.Value.EndsWith("[REDACTED]"))
            {
                continue;
            }

            // Extract host from URL and check against safe hosts
            if (TryExtractHost(match.Value, out var host) && safeHosts.Contains(host))
            {
                continue; // Skip safe hosts
            }

            violations.Add($"Category: {category}, Location: {location}, URL: {Sanitize.RedactUrls(match.Value)}");
        }
    }

    /// <summary>
    /// Extracts the host portion from a URL string.
    /// </summary>
    private static bool TryExtractHost(string url, out string host)
    {
        host = string.Empty;
        try
        {
            // Simple extraction without Uri parsing (handles malformed URLs better)
            var startIdx = url.IndexOf("://", StringComparison.Ordinal);
            if (startIdx < 0)
            {
                return false;
            }

            startIdx += 3; // Skip "://"
            var endIdx = url.IndexOfAny(new[] { '/', ':', '?' }, startIdx);
            host = endIdx < 0 ? url.Substring(startIdx) : url.Substring(startIdx, endIdx - startIdx);

            // Handle IPv6 addresses in brackets
            if (host.StartsWith('['))
            {
                var bracketEnd = host.IndexOf(']');
                if (bracketEnd > 0)
                {
                    host = host.Substring(0, bracketEnd + 1);
                }
            }

            return !string.IsNullOrEmpty(host);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asserts that no Bearer tokens appear in log entries.
    /// Checks both message text and exception details.
    /// </summary>
    /// <param name="sink">The test log sink containing captured log entries.</param>
    /// <exception cref="LogAssertionException">Thrown when Bearer tokens are found.</exception>
    public static void AssertNoBearerTokensInLogs(TestLogSink sink)
    {
        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        var entries = sink.Snapshot();
        var bearerPattern = new System.Text.RegularExpressions.Regex(
            @"Bearer\s+[A-Za-z0-9\-_\.]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var violations = new List<string>();

        foreach (var entry in entries)
        {
            // Check message
            CheckTextForBearerTokens(entry.Message, entry.Category, entry.Level.ToString(), "Message", bearerPattern, violations);

            // Check exception message
            CheckTextForBearerTokens(entry.Exception?.Message, entry.Category, entry.Level.ToString(), "Exception.Message", bearerPattern, violations);

            // Check full exception string (includes inner exceptions)
            CheckTextForBearerTokens(entry.Exception?.ToString(), entry.Category, entry.Level.ToString(), "Exception.ToString()", bearerPattern, violations);
        }

        if (violations.Count > 0)
        {
            throw new LogAssertionException(
                $"Found {violations.Count} unredacted Bearer token(s) in logs:\n" +
                string.Join("\n", violations.Select(v => $"  - {v}")) +
                "\n\nUse Sanitize.SafeErrorMessage() to redact Bearer tokens before logging.");
        }
    }

    private static void CheckTextForBearerTokens(
        string? text,
        string category,
        string level,
        string location,
        System.Text.RegularExpressions.Regex bearerPattern,
        List<string> violations)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Apply size cap for exception text to prevent performance issues
        var textToScan = text.Length > MaxExceptionScanLength
            ? text.Substring(0, MaxExceptionScanLength)
            : text;

        var match = bearerPattern.Match(textToScan);
        if (match.Success && !match.Value.Contains("[REDACTED]"))
        {
            violations.Add($"Category: {category}, Level: {level}, Location: {location}");
        }
    }

    /// <summary>
    /// Performs all standard log security assertions.
    /// </summary>
    /// <param name="sink">The test log sink containing captured log entries.</param>
    /// <param name="knownSecrets">Known secret values that must not appear in logs.</param>
    public static void AssertSecureLogs(TestLogSink sink, params string[] knownSecrets)
    {
        AssertSecureLogs(sink, safeHosts: null, knownSecrets);
    }

    /// <summary>
    /// Performs all standard log security assertions with customizable safe hosts.
    /// </summary>
    /// <param name="sink">The test log sink containing captured log entries.</param>
    /// <param name="safeHosts">Hosts to exclude from URL checks. If null, uses <see cref="DefaultSafeHosts"/>.</param>
    /// <param name="knownSecrets">Known secret values that must not appear in logs.</param>
    public static void AssertSecureLogs(TestLogSink sink, IEnumerable<string>? safeHosts, params string[] knownSecrets)
    {
        AssertNoSecretsInLogs(sink, knownSecrets);
        AssertNoUnredactedUrls(sink, safeHosts);
        AssertNoBearerTokensInLogs(sink);
    }

    /// <summary>
    /// Masks a secret for error reporting (shows first and last 2 chars).
    /// </summary>
    private static string MaskSecret(string secret)
    {
        if (secret.Length <= 6)
        {
            return new string('*', secret.Length);
        }

        return $"{secret[..2]}...{secret[^2..]}";
    }

    private record SecretViolation(string Category, string Level, string MaskedSecret, string Location);
}

/// <summary>Thrown when a log assertion fails.</summary>
public sealed class LogAssertionException : Exception
{
    public LogAssertionException(string message) : base(message)
    {
    }
}
