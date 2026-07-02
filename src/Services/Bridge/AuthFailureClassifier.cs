using System;

using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Canonical, precise classification of whether an exception observed at a bridge entry point is an
/// AUTHENTICATION failure (so the <see cref="AuthFailureGate"/> should latch and the caller should
/// degrade to an empty result) versus an ordinary recoverable failure that must propagate.
///
/// <para><b>Why this is precise, not type-based:</b> the canonical search executor signals "every
/// variant failed" with an <see cref="InvalidOperationException"/> — the SAME type tidalarr/amazonmusicarr
/// throw for "not authenticated". A classifier that keyed on the exception <i>type</i> would latch the
/// auth gate on a genuine all-failed/network outage and suppress every subsequent search for 60s. So
/// classification keys on (a) <see cref="AuthGatedException"/>, (b) an explicit 401/403 status supplied
/// by the caller's <c>statusOf</c> extractor, or (c) an explicit auth phrase in the message — never the
/// bare type. The executor's all-failed message ("All N … request(s) failed; surfacing the error …")
/// contains none of those phrases, so it correctly classifies as NOT auth.</para>
/// </summary>
public static class AuthFailureClassifier
{
    // Explicit auth phrases drawn from the four plugins' real "not authenticated" messages
    // (tidal "Not authenticated"; amazon "… is not authenticated …"; apple "… user token is required …";
    // qobuz "Authentication failed", "Credential validation failed", "Invalid user ID or auth token",
    // "No valid authentication method"). Deliberately specific so the executor all-failed / generic
    // network messages never match.
    private static readonly string[] AuthPhrases =
    {
        "not authenticated",
        "unauthenticated",
        "unauthorized",
        "authentication failed",
        "credential validation failed",
        "user token is required",
        "invalid user id or auth token",
        "no valid authentication",
        "the token may have expired",
    };

    /// <summary>
    /// True when <paramref name="ex"/> indicates an authentication failure. <paramref name="statusOf"/>
    /// (optional) extracts an HTTP status from the caller's typed exception so 401/403 can be recognized
    /// even when the message carries no auth phrase.
    /// </summary>
    public static bool IsAuthFailure(Exception? ex, Func<Exception, int?>? statusOf = null)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is AuthGatedException)
        {
            return true;
        }

        var code = statusOf?.Invoke(ex);
        if (code is 401 or 403)
        {
            return true;
        }

        return MessageIndicatesAuth(ex.Message);
    }

    /// <summary>
    /// Maps <paramref name="ex"/> to an <see cref="AuthFailure"/> (suitable for
    /// <see cref="AuthFailureGate.RecordExceptionOutcome"/>) when it is an auth failure, else <c>null</c>.
    /// </summary>
    public static AuthFailure? Classify(Exception? ex, Func<Exception, int?>? statusOf = null)
    {
        if (!IsAuthFailure(ex, statusOf))
        {
            return null;
        }

        var code = (ex as AuthGatedException)?.ErrorCode
                   ?? statusOf?.Invoke(ex!)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new AuthFailure
        {
            ErrorCode = code ?? "auth",
            Message = ex!.Message ?? string.Empty,
        };
    }

    private static bool MessageIndicatesAuth(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var phrase in AuthPhrases)
        {
            if (message!.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
