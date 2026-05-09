using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Redacts sensitive data from strings before logging.
/// Use this to sanitize any content that might contain API keys, tokens, or credentials.
/// </summary>
public static partial class LogRedactor
{
    /// <summary>Replacement text for redacted values.</summary>
    public const string REDACTED = "***REDACTED***";

    // Pre-compiled regex patterns for common API key formats
    // Using source generators for performance (.NET 7+)

    /// <summary>Matches OpenAI API keys (sk-...).</summary>
    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex OpenAiKeyPattern();

    /// <summary>Matches Anthropic API keys (various formats).</summary>
    [GeneratedRegex(@"\b(sk-ant-[A-Za-z0-9-]+|anthropic-[A-Za-z0-9-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AnthropicKeyPattern();

    /// <summary>Matches Google API keys (AIza...).</summary>
    [GeneratedRegex(@"\bAIza[A-Za-z0-9_-]{35,}\b", RegexOptions.Compiled)]
    private static partial Regex GoogleKeyPattern();

    /// <summary>Matches Bearer tokens in headers.</summary>
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    /// <summary>
    /// Matches Authorization and API key header patterns. Consumes the full value
    /// (not just the first whitespace-delimited token) so multi-token schemes like
    /// <c>Basic dXNlcm5hbWU6cGFzc3dvcmQ=</c> are fully redacted, not partially.
    /// Stops at end-of-line or common log-line delimiters (<c>, ; |</c> and <c>}</c>).
    /// </summary>
    [GeneratedRegex(@"(Authorization|X-Api-Key|api[_-]?key)\s*[:=]\s*[^\r\n,;|}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeaderPattern();

    /// <summary>
    /// Matches <c>Cookie:</c> and <c>Set-Cookie:</c> header values. Cookies routinely
    /// carry session identifiers (e.g. <c>JSESSIONID=...</c>, <c>session_id=...</c>)
    /// that are typically below the 32-char generic-token threshold and so escape
    /// <see cref="GenericTokenPattern"/>. Redacts the entire cookie value to be safe.
    /// </summary>
    [GeneratedRegex(@"(Set-Cookie|Cookie)\s*[:=]\s*[^\r\n]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CookieHeaderPattern();

    /// <summary>Matches generic long alphanumeric tokens (potential API keys).</summary>
    [GeneratedRegex(@"\b[A-Za-z0-9]{32,}\b", RegexOptions.Compiled)]
    private static partial Regex GenericTokenPattern();

    /// <summary>
    /// Matches sensitive JSON property values: <c>"key": "value"</c> or <c>"key":"value"</c>.
    /// Catches nested credentials like <c>{"api_key":"sk-abc..."}</c> where the value alone may not
    /// match a structured token pattern (e.g., short opaque tokens, non-prefixed API keys).
    /// </summary>
    [GeneratedRegex(@"""(api[_-]?key|access[_-]?token|refresh[_-]?token|secret|client[_-]?secret|password|authorization|bearer|token|session[_-]?id|x-api-key)""\s*:\s*""([^""\\]|\\.)*""", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex JsonSensitiveValuePattern();

    /// <summary>
    /// Redacts sensitive values from a string.
    /// Call this before logging any content that might contain credentials.
    /// </summary>
    /// <param name="value">The string to redact.</param>
    /// <returns>The string with sensitive values replaced by REDACTED.</returns>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        // Apply patterns in order of specificity (most specific first)
        value = OpenAiKeyPattern().Replace(value, REDACTED);
        value = AnthropicKeyPattern().Replace(value, REDACTED);
        value = GoogleKeyPattern().Replace(value, REDACTED);
        value = BearerTokenPattern().Replace(value, $"Bearer {REDACTED}");
        value = AuthHeaderPattern().Replace(value, match =>
        {
            var colonIndex = match.Value.IndexOfAny(new[] { ':', '=' });
            if (colonIndex > 0)
            {
                var headerName = match.Value[..colonIndex].Trim();
                return $"{headerName}: {REDACTED}";
            }
            return REDACTED;
        });
        value = CookieHeaderPattern().Replace(value, match =>
        {
            var colonIndex = match.Value.IndexOfAny(new[] { ':', '=' });
            if (colonIndex > 0)
            {
                var headerName = match.Value[..colonIndex].Trim();
                return $"{headerName}: {REDACTED}";
            }
            return REDACTED;
        });

        // Nested JSON credentials: redact the *value* of sensitive JSON properties even when the
        // value alone doesn't trip a structured pattern (short tokens, opaque vendor secrets, etc.).
        // Applied before the generic catch-all so the JSON shape is preserved in the output.
        value = JsonSensitiveValuePattern().Replace(value, match =>
        {
            // Preserve the property name; replace just the quoted value.
            var quoteIdx = match.Value.IndexOf(':');
            if (quoteIdx <= 0) return $"\"{REDACTED}\"";
            var keyPart = match.Value[..quoteIdx].TrimEnd();
            return $"{keyPart}: \"{REDACTED}\"";
        });

        // Generic catch-all: any remaining long opaque alphanumeric string is likely a token
        // (service-specific tokens that don't match the structured prefixes above).
        // Applied LAST so structured patterns claim their matches first.
        // Trade-off: also matches non-hyphenated UUIDs (32 hex), Git SHAs (40 hex), and content hashes (64 hex).
        // This is acceptable noise — those values are not security-sensitive but redacting them is benign.
        // Threshold of 32 chars avoids matching common short identifiers and test fixtures.
        value = GenericTokenPattern().Replace(value, REDACTED);

        return value;
    }

    /// <summary>
    /// Returns a redacted single-line representation of an exception suitable for logging in
    /// token-handling code paths. Walks the inner-exception chain and applies <see cref="Redact"/>
    /// to each <c>Message</c>. The full <c>StackTrace</c> is intentionally omitted because it can
    /// contain bound parameter values (URLs with embedded auth, etc.) that vary by framework
    /// and are not covered by structured redaction patterns.
    /// </summary>
    /// <param name="ex">The exception to render. Null returns empty string.</param>
    /// <returns>A redacted, exception-type-prefixed message chain.</returns>
    public static string RedactException(Exception? ex)
    {
        if (ex is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            if (depth > 0) sb.Append(" --> ");
            sb.Append(current.GetType().Name).Append(": ").Append(Redact(current.Message));
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Checks if a parameter name indicates sensitive data.
    /// Use this to decide whether to redact a parameter value.
    /// </summary>
    /// <param name="name">Parameter name to check.</param>
    /// <returns>True if the parameter likely contains sensitive data.</returns>
    public static bool IsSensitiveParameter(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var lower = name.ToLowerInvariant();

        // Exact matches
        if (SensitiveExactNames.Contains(lower))
            return true;

        // Contains matches
        foreach (var term in SensitiveContainsTerms)
        {
            if (lower.Contains(term))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a redacted copy of a dictionary, masking values for sensitive keys.
    /// </summary>
    public static IDictionary<string, object> RedactDictionary(IDictionary<string, object>? source)
    {
        if (source == null)
            return new Dictionary<string, object>();

        var result = new Dictionary<string, object>(source.Count);
        foreach (var kvp in source)
        {
            if (IsSensitiveParameter(kvp.Key))
            {
                result[kvp.Key] = REDACTED;
            }
            else if (kvp.Value is string s)
            {
                result[kvp.Key] = Redact(s);
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    // Parameter names that exactly match sensitive data
    private static readonly HashSet<string> SensitiveExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "api-key",
        "authorization",
        "x-api-key",
        "secret", "client_secret", "clientsecret",
        "password", "pwd",
        "token", "access_token", "accesstoken", "refresh_token", "refreshtoken",
        "bearer",
        "credential", "credentials",
        "session", "sessionid", "session_id",
        "cookie",
        "signature", "sig", "request_sig"
    };

    // Terms that indicate sensitive data when contained in parameter name
    private static readonly string[] SensitiveContainsTerms =
    {
        "secret",
        "password",
        "token",
        "auth",
        "credential",
        "key",
        "apikey"
    };
}
