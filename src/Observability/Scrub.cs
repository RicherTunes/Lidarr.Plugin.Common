using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Centralized scrubbing helpers for preventing secrets from reaching logs.
///
/// Three surfaces are covered:
/// <list type="bullet">
///   <item><see cref="Secret"/> — truncate an opaque token to its leading visible chars.</item>
///   <item><see cref="Headers"/> — redact values of known sensitive header names.</item>
///   <item><see cref="Url"/> — replace values of known sensitive URL query parameters.</item>
/// </list>
///
/// These helpers are intentionally thin: they do not depend on
/// <see cref="PluginLogContext"/> and produce strings callers can interpolate into
/// any logger without friction.
/// </summary>
public static class Scrub
{
    // ------------------------------------------------------------------ //
    // Secret
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a redacted representation of a secret token that preserves
    /// only the leading <paramref name="leadingVisible"/> characters:
    /// <c>"sk-abc...xyz" → "sk-***"</c>.
    ///
    /// <para>When <paramref name="value"/> is null, empty, or shorter than
    /// <paramref name="leadingVisible"/> the entire value is replaced with
    /// <c>"***"</c>.</para>
    /// </summary>
    /// <param name="value">The secret to scrub. Null is treated as empty.</param>
    /// <param name="leadingVisible">
    ///   Number of characters to keep. Defaults to 3.
    ///   Must be non-negative.
    /// </param>
    public static string Secret(string? value, int leadingVisible = 3)
    {
        if (leadingVisible < 0) throw new ArgumentOutOfRangeException(nameof(leadingVisible), "Must be non-negative.");

        if (string.IsNullOrEmpty(value) || value.Length <= leadingVisible)
            return "***";

        return value[..leadingVisible] + "***";
    }

    // ------------------------------------------------------------------ //
    // Headers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a copy of <paramref name="headers"/> where the values of
    /// known sensitive header names are replaced with <c>"***"</c>.
    ///
    /// Comparison is case-insensitive. The set of sensitive headers includes:
    /// <c>Authorization</c>, <c>X-API-Key</c>, <c>X-Auth-Token</c>,
    /// <c>X-Access-Token</c>, <c>Music-User-Token</c>, <c>X-Adp-Token</c>, <c>X-Adp-Signature</c>,
    /// <c>Cookie</c>, <c>Set-Cookie</c>, <c>Proxy-Authorization</c>.
    /// </summary>
    public static IDictionary<string, string> Headers(IDictionary<string, string> headers)
    {
        if (headers is null) throw new ArgumentNullException(nameof(headers));

        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            result[key] = IsSensitiveHeader(key) ? "***" : value;
        }
        return result;
    }

    // ------------------------------------------------------------------ //
    // URL
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a copy of <paramref name="url"/> where the values of known
    /// sensitive query parameters are replaced with <c>"***"</c>.
    ///
    /// Known sensitive parameter names (case-insensitive):
    /// <c>api_key</c>, <c>apikey</c>, <c>api-key</c>, <c>token</c>,
    /// <c>access_token</c>, <c>refresh_token</c>, <c>key</c>, <c>secret</c>,
    /// <c>client_secret</c>, <c>password</c>, <c>pwd</c>, <c>authorization</c>,
    /// <c>bearer</c>, <c>auth</c>, <c>sig</c>, <c>signature</c>, <c>request_sig</c>.
    ///
    /// The URL scheme, host, path, and non-sensitive parameters are preserved.
    /// </summary>
    public static string Url(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url ?? string.Empty;

        // Replace sensitive parameter values using regex.
        // Pattern: capture (name=)(value) where name is in the sensitive list.
        // Works for ?name=val&name2=val2 and semicolon-separated params.
        return SensitiveQueryParamPattern.Replace(url, m =>
        {
            // m.Groups[1] = "name=", m.Groups[2] = the value
            return m.Groups[1].Value + "***";
        });
    }

    /// <summary>
    /// Defensive full-strip variant of <see cref="Url"/>: drops the entire query string
    /// regardless of parameter name. Use when the URL may contain caller-controlled tokens
    /// the selective <see cref="Url"/> regex doesn't recognize (e.g. third-party APIs that
    /// use non-canonical token parameter names). Returns the scheme + host + path only.
    ///
    /// <para>Returns <see cref="string.Empty"/> for null/empty input; returns the original
    /// string unchanged when <c>new Uri(url)</c> throws <see cref="UriFormatException"/> —
    /// log redaction must not crash a log statement.</para>
    /// </summary>
    public static string UrlAndStripQuery(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        try
        {
            return new Uri(url).GetLeftPart(UriPartial.Path);
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    // ------------------------------------------------------------------ //
    // Private helpers
    // ------------------------------------------------------------------ //

    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "X-API-Key",
        "X-Auth-Token",
        "X-Access-Token",
        "Music-User-Token",
        "X-Adp-Token",
        "X-Adp-Signature",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
    };

    private static bool IsSensitiveHeader(string name)
        => SensitiveHeaderNames.Contains(name);

    // Matches: (param_name=)(value) for known sensitive names, terminated by & ; # end-of-string.
    private static readonly Regex SensitiveQueryParamPattern = new(
        @"(?i)((?:api[_-]?key|apikey|token|access[_-]?token|refresh[_-]?token|key|secret|client[_-]?secret|password|pwd|authorization|bearer|auth|request[_-]?sig|signature|sig)=)([^&;#\s]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
