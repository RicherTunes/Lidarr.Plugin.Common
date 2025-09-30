using System;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.TestKit.Assertions;

/// <summary>
/// Assertion helpers tailored for streaming plugin scenarios. They throw <see cref="PluginAssertionException"/>
/// to keep failures framework-agnostic.
/// </summary>
public static class PluginAssertions
{
    public static void AssertSuccess(DownloadResult? result, string? because = null)
    {
        if (result is null)
        {
            throw new PluginAssertionException("Download result was null.");
        }

        if (!result.Success)
        {
            var message = "Expected download to succeed";
            if (!string.IsNullOrEmpty(because))
            {
                message += $" because {because}";
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                message += $", but failed with '{result.ErrorMessage}'.";
            }
            else
            {
                message += ".";
            }

            throw new PluginAssertionException(message);
        }
    }

    public static void AssertFailure(DownloadResult? result, string expectedMessageFragment)
    {
        if (result is null)
        {
            throw new PluginAssertionException("Download result was null.");
        }

        if (result.Success)
        {
            throw new PluginAssertionException("Expected download to fail, but it succeeded.");
        }

        if (!string.IsNullOrWhiteSpace(expectedMessageFragment) && (result.ErrorMessage is null || !result.ErrorMessage.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PluginAssertionException($"Expected error message to contain '{expectedMessageFragment}', but was '{result.ErrorMessage}'.");
        }
    }

    public static void AssertImplicitFallback(StreamingQuality requested, StreamingQuality actual)
    {
        if (requested is null)
        {
            throw new ArgumentNullException(nameof(requested));
        }

        if (actual is null)
        {
            throw new PluginAssertionException("Actual quality was null.");
        }

        if (string.Equals(requested.Id, actual.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginAssertionException("Fallback was expected, but the same quality identifier was returned.");
        }

        if (actual.GetTier() > requested.GetTier())
        {
            throw new PluginAssertionException("Fallback yielded a higher quality than requested; ensure tier downgrade happened.");
        }
    }
}

/// <summary>Thrown when a plugin-specific assertion fails.</summary>
public sealed class PluginAssertionException : Exception
{
    public PluginAssertionException(string message) : base(message)
    {
    }
}
