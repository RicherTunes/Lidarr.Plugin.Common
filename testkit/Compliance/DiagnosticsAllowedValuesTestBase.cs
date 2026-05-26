using System.Collections.Generic;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Diagnostics;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Abstract base class for plugin diagnostics allowed-values tests.
/// Validates that every <see cref="DiagnosticHealthResult"/> produced by the plugin's
/// health-diagnostics class uses only well-known, registered string tokens —
/// preventing "stringly-typed" drift over time.
/// </summary>
/// <remarks>
/// Usage — override the four abstract members and optionally add extra facts:
/// <code>
/// public class AppleMusicHealthDiagnosticsAllowedValuesTests
///     : DiagnosticsAllowedValuesTestBase
/// {
///     protected override IReadOnlySet&lt;string&gt; AllowedErrorCodes { get; } =
///         new HashSet&lt;string&gt;(StringComparer.Ordinal) { AppleMusicHealthDiagnostics.ErrorCodes.AuthFailed, ... };
///
///     protected override IReadOnlySet&lt;string&gt; AllowedDiagnosticTypes { get; } = ...;
///     protected override IReadOnlySet&lt;string&gt; AllowedCapabilities { get; } = ...;
///
///     protected override Task&lt;IEnumerable&lt;DiagnosticHealthResult&gt;&gt; GetHealthResultsAsync() =&gt; ...;
/// }
/// </code>
/// </remarks>
public abstract class DiagnosticsAllowedValuesTestBase
{
    /// <summary>
    /// The complete set of error-code strings the plugin's diagnostics class is
    /// permitted to emit.  Any non-<see langword="null"/>
    /// <see cref="DiagnosticHealthResult.ErrorCode"/> must appear in this set.
    /// </summary>
    protected abstract IReadOnlySet<string> AllowedErrorCodes { get; }

    /// <summary>
    /// The complete set of diagnostic-type strings the plugin's diagnostics class is
    /// permitted to emit.  Any non-<see langword="null"/>
    /// <see cref="DiagnosticHealthResult.DiagnosticType"/> must appear in this set.
    /// </summary>
    protected abstract IReadOnlySet<string> AllowedDiagnosticTypes { get; }

    /// <summary>
    /// The complete set of capability strings the plugin's diagnostics class is
    /// permitted to emit.  Any non-<see langword="null"/>
    /// <see cref="DiagnosticHealthResult.Capability"/> must appear in this set.
    /// </summary>
    protected abstract IReadOnlySet<string> AllowedCapabilities { get; }

    /// <summary>
    /// Invokes all the plugin's diagnostic-check methods and returns their results.
    /// The base <see cref="AllResults_HaveAllowedValues"/> fact runs
    /// <see cref="AssertAllowed"/> against every element.
    /// </summary>
    protected abstract Task<IEnumerable<DiagnosticHealthResult>> GetHealthResultsAsync();

    /// <summary>
    /// Asserts that a single <see cref="DiagnosticHealthResult"/> uses only the
    /// tokens declared in <see cref="AllowedErrorCodes"/>,
    /// <see cref="AllowedDiagnosticTypes"/>, and <see cref="AllowedCapabilities"/>.
    /// Call this from per-scenario facts in the subclass for richer output.
    /// </summary>
    protected void AssertAllowed(DiagnosticHealthResult result, string context)
    {
        if (result.ErrorCode is not null)
        {
            Assert.True(
                AllowedErrorCodes.Contains(result.ErrorCode),
                $"ErrorCode '{result.ErrorCode}' from {context} is not a registered value. "
                + $"Allowed: [{string.Join(", ", AllowedErrorCodes)}]");
        }

        if (result.DiagnosticType is not null)
        {
            Assert.True(
                AllowedDiagnosticTypes.Contains(result.DiagnosticType),
                $"DiagnosticType '{result.DiagnosticType}' from {context} is not a registered value. "
                + $"Allowed: [{string.Join(", ", AllowedDiagnosticTypes)}]");
        }

        if (result.Capability is not null)
        {
            Assert.True(
                AllowedCapabilities.Contains(result.Capability),
                $"Capability '{result.Capability}' from {context} is not a registered value. "
                + $"Allowed: [{string.Join(", ", AllowedCapabilities)}]");
        }
    }

    /// <summary>
    /// Drives the full result set from <see cref="GetHealthResultsAsync"/> and
    /// asserts that every result passes <see cref="AssertAllowed"/>.
    /// </summary>
    [Fact]
    public async Task AllResults_HaveAllowedValues()
    {
        var results = await GetHealthResultsAsync();
        var index = 0;
        foreach (var result in results)
        {
            AssertAllowed(result, $"GetHealthResultsAsync()[{index}]");
            index++;
        }
    }
}
