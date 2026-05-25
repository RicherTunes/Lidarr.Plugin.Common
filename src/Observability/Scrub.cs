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
    /// <c>X-Access-Token</c>, <c>Cookie</c>, <c>Set-Cookie</c>, <c>Proxy-Authorization</c>.
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
    /// Recognition delegates to <see cref="LogRedactor.IsSensitiveParameter"/>,
    /// which uses both exact-name matching (<c>apikey</c>, <c>authorization</c>,
    /// <c>signature</c>, <c>sessionid</c>, <c>credential</c>, <c>x-api-key</c>, …)
    /// AND a contains-rule for compound names — anything whose param name
    /// contains <c>secret</c>, <c>password</c>, <c>token</c>, <c>auth</c>,
    /// <c>credential</c>, <c>key</c>, or <c>apikey</c> is redacted. This keeps
    /// URL and structured-property redaction in lockstep so the same value is
    /// recognised across observability surfaces (Wave 17F unification).
    ///
    /// <para>The contains-rule will false-positive-mask param names that legitimately
    /// embed a sensitive term as a substring (e.g. <c>keyboard</c> contains
    /// <c>key</c>). That's intentional: a secret leaking to logs is strictly worse
    /// than a UI param value being masked.</para>
    ///
    /// <para>The URL scheme, host, path, and non-sensitive parameter values are preserved.</para>
    /// </summary>
    public static string Url(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url ?? string.Empty;

        // Match every (separator)(name)=(value) triple in the query/fragment portion.
        // Separator can be `?`, `&`, or `;` (legacy). Callback consults
        // LogRedactor.IsSensitiveParameter so the recognition matches structured-property
        // redaction; the redactor handles exact + contains rules in one place.
        return AnyQueryParamPattern.Replace(url, m =>
        {
            var separator = m.Groups[1].Value;
            var name = m.Groups[2].Value;
            if (LogRedactor.IsSensitiveParameter(name))
            {
                return $"{separator}{name}=***";
            }
            return m.Value;
        });
    }

    // ------------------------------------------------------------------ //
    // URL (conservative — strip entire query+fragment)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns <paramref name="url"/> with its query string AND fragment dropped,
    /// leaving only <c>scheme://host[:port]/path</c>. The conservative sibling
    /// to <see cref="Url"/>.
    ///
    /// <para>Use this when you can't enumerate sensitive parameter names ahead
    /// of time — signed CDN URLs, HLS streams with rotating tokens, vendor
    /// proprietary query schemas. The cost is losing any non-sensitive context
    /// (region, format, expires) from the logged URL; the upside is no leak
    /// risk if a new sensitive param name appears upstream and
    /// <see cref="LogRedactor.IsSensitiveParameter"/> hasn't been updated to
    /// recognise it.</para>
    ///
    /// <para>For known URL structures with stable parameter sets, prefer
    /// <see cref="Url"/> — it preserves debugging-useful context.</para>
    ///
    /// <para>Behaviour:
    /// <list type="bullet">
    ///   <item><c>null</c> or empty → empty string.</item>
    ///   <item>Valid absolute URL → scheme://host/path, query and fragment dropped.</item>
    ///   <item>Unparseable input (relative paths, non-URLs) → returned unchanged.
    ///         Caller can detect this by comparing the result to the input.</item>
    /// </list>
    /// </para>
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
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
    };

    private static bool IsSensitiveHeader(string name)
        => SensitiveHeaderNames.Contains(name);

    // Matches every (separator)(name)=(value) triple in a URL's query/fragment portion.
    // Separator captures `?`, `&`, or `;` (legacy URL param separator). The replacement
    // callback decides whether to redact by consulting LogRedactor.IsSensitiveParameter,
    // so URL scrubbing and structured-property redaction stay in lockstep.
    private static readonly Regex AnyQueryParamPattern = new(
        @"([?&;])([^=&;#\s]+)=([^&;#\s]*)",
        RegexOptions.Compiled);
}
