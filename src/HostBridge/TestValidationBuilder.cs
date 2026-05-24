using System.Collections.Generic;
using FluentValidation.Results;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Accumulating builder for the "Test()" required-field-check pattern that every Lidarr
/// indexer / download client implements.
///
/// <para>The default shape is:</para>
/// <code>
///   protected override void Test(List&lt;ValidationFailure&gt; failures)
///   {
///       if (string.IsNullOrWhiteSpace(Settings.Field1)) { failures.Add(...); return; }
///       if (string.IsNullOrWhiteSpace(Settings.Field2)) { failures.Add(...); return; }
///       // ...
///   }
/// </code>
///
/// <para>The early-return after the first failure is ergonomically wrong: a first-run user
/// who left ALL fields blank gets the validation message one-at-a-time, fixing each then
/// clicking Test again. PR #130 review #1 finding #12 caught this in apple and tidal both.</para>
///
/// <para>With this builder:</para>
/// <code>
///   protected override void Test(List&lt;ValidationFailure&gt; failures)
///   {
///       new TestValidationBuilder()
///           .RequireNonEmpty(nameof(Settings.Field1), Settings.Field1, "Field1 is required.")
///           .RequireNonEmpty(nameof(Settings.Field2), Settings.Field2, "Field2 is required.")
///           .RequireIf(!Settings.ProbeOnly, nameof(Settings.UserToken), Settings.UserToken,
///                      "UserToken is required when ProbeOnly is unchecked.")
///           .ApplyTo(failures);
///       if (failures.Count > 0) return;
///
///       // ... actual smoke-probe call ...
///   }
/// </code>
///
/// <para>Result: ONE round-trip surfaces every missing field at once.</para>
///
/// <para>Lifted as Wave B item 7 from
/// <c>memory/project_apple_bridge_unification_plan.md</c>.</para>
/// </summary>
public sealed class TestValidationBuilder
{
    private readonly List<ValidationFailure> _failures = new();

    /// <summary>True if any check has failed so far.</summary>
    public bool HasFailures => _failures.Count > 0;

    /// <summary>
    /// Require <paramref name="value"/> to be non-empty after whitespace trim. Failure adds
    /// a <see cref="ValidationFailure"/> targeted at <paramref name="propertyName"/>.
    /// Returns <c>this</c> for chaining.
    /// </summary>
    public TestValidationBuilder RequireNonEmpty(string propertyName, string? value, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _failures.Add(new ValidationFailure(propertyName, errorMessage));
        }
        return this;
    }

    /// <summary>
    /// Conditional non-empty check: only validates when <paramref name="condition"/> is true.
    /// Common usage: "UserToken is required when ProbeOnly is unchecked" expresses as
    /// <c>RequireIf(!Settings.ProbeOnly, ...)</c>.
    /// </summary>
    public TestValidationBuilder RequireIf(bool condition, string propertyName, string? value, string errorMessage)
    {
        if (condition && string.IsNullOrWhiteSpace(value))
        {
            _failures.Add(new ValidationFailure(propertyName, errorMessage));
        }
        return this;
    }

    /// <summary>
    /// Return the accumulated failures as a list. Each call returns a fresh list — the
    /// builder retains its internal state and can be reused.
    /// </summary>
    public IReadOnlyList<ValidationFailure> ToFailures() => _failures.AsReadOnly();

    /// <summary>
    /// Append the accumulated failures to an existing list (e.g. the
    /// <c>List&lt;ValidationFailure&gt;</c> that Lidarr passes to <c>Test()</c>). Preserves
    /// any failures the host pre-populated.
    /// </summary>
    public void ApplyTo(List<ValidationFailure> failures)
    {
        if (failures is null) return;
        failures.AddRange(_failures);
    }
}
