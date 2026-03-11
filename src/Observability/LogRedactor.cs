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

    /// <summary>Matches Authorization and API key header patterns.</summary>
    [GeneratedRegex(@"(Authorization|X-Api-Key|api[_-]?key)\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeaderPattern();

    /// <summary>Matches generic long alphanumeric tokens (potential API keys).</summary>
    [GeneratedRegex(@"\b[A-Za-z0-9]{32,}\b", RegexOptions.Compiled)]
    private static partial Regex GenericTokenPattern();

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

        return value;
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
