using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lidarr.Plugin.Common.Security.Llm;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>Resolves retry hints from complete response bodies using the Common compatibility profile.</summary>
/// <remarks>This library profile does not establish a provider wire schema.</remarks>
public static class RetryBodyHintResolver
{
    private static readonly Regex LegacyRetryAfterPattern = new(
        @"[""']?retry[-_]?after[""']?\s*[:=]\s*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Resolves a retry hint from a complete buffered response body.</summary>
    public static TimeSpan? Resolve(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > LlmJsonSerializer.MaxJsonSize)
            return null;

        var first = FirstNonWhitespace(responseBody);
        try
        {
            using var document = JsonDocument.Parse(
                responseBody,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = LlmJsonSerializer.DefaultMaxDepth,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var found = 0;
            TimeSpan? resolved = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("retry_after" or "retry-after"))
                    continue;

                found++;
                if (found > 1 || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetDouble(out var seconds))
                    return null;

                resolved = ConvertSeconds(seconds);
                if (resolved is null)
                    return null;
            }

            return resolved;
        }
        catch (JsonException)
        {
            return first is '{' or '[' or '"' ? null : ResolveLegacy(responseBody);
        }
    }

    internal static TimeSpan? ConvertSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static TimeSpan? ResolveLegacy(string responseBody)
    {
        var match = LegacyRetryAfterPattern.Match(responseBody);
        if (!match.Success
            || !double.TryParse(
                match.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds))
            return null;

        return ConvertSeconds(seconds);
    }

    private static char FirstNonWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
                return character;
        }

        return '\0';
    }
}
