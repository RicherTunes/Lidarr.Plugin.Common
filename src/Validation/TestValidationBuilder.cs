using System.Collections.Generic;
using FluentValidation.Results;

namespace Lidarr.Plugin.Common.Validation;

/// <summary>
/// Accumulate-then-build helper for plugin <c>Test()</c> pipelines.
/// Collects ALL required-field failures before returning so the user sees every
/// missing field in a single Lidarr "Test" click instead of one failure at a time.
/// This directly fixes the early-return-after-first-missing-field pattern from PR #130 finding #12.
/// </summary>
/// <remarks>
/// Typical usage in an overridden <c>Test(List&lt;ValidationFailure&gt; failures)</c>:
/// <code>
/// var builder = new TestValidationBuilder()
///     .RequireNonEmpty("ConfigPath", Settings.ConfigPath,
///         "Config path is required.")
///     .RequireNonEmpty("DownloadPath", Settings.DownloadPath,
///         "Download path is required.");
///
/// failures.AddRange(builder.Build());
/// if (builder.HasFailures) return;
///
/// // smoke HTTP call here …
/// </code>
/// </remarks>
public sealed class TestValidationBuilder
{
    private readonly List<ValidationFailure> _failures = new();

    /// <summary>
    /// Adds a <see cref="ValidationFailure"/> if <paramref name="value"/> is null, empty, or whitespace.
    /// Returns <c>this</c> for fluent chaining. Does NOT return early — every call is always evaluated.
    /// </summary>
    /// <param name="fieldName">The settings property name shown in the Lidarr UI error.</param>
    /// <param name="value">The string value to check.</param>
    /// <param name="message">
    /// Human-readable message. Defaults to "<paramref name="fieldName"/> is required." when null.
    /// </param>
    public TestValidationBuilder RequireNonEmpty(string fieldName, string? value, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _failures.Add(new ValidationFailure(
                fieldName,
                message ?? $"{fieldName} is required."));
        }

        return this;
    }

    /// <summary>
    /// Appends an already-constructed <see cref="ValidationFailure"/>.
    /// Useful for failures whose message requires additional context unavailable at chain time.
    /// </summary>
    public TestValidationBuilder AddFailure(ValidationFailure failure)
    {
        _failures.Add(failure);
        return this;
    }

    /// <summary>
    /// <c>true</c> if any failures have been accumulated so far.
    /// Use this to decide whether to skip the smoke HTTP call:
    /// <code>
    /// if (builder.HasFailures) return;
    /// </code>
    /// </summary>
    public bool HasFailures => _failures.Count > 0;

    /// <summary>
    /// Returns the accumulated failure list. Callers should pass this to
    /// <c>failures.AddRange(builder.Build())</c> before checking <see cref="HasFailures"/>.
    /// The list is a snapshot; subsequent calls to <see cref="RequireNonEmpty"/> or
    /// <see cref="AddFailure"/> will not retroactively affect the returned collection.
    /// </summary>
    public IReadOnlyList<ValidationFailure> Build() => _failures.AsReadOnly();
}
