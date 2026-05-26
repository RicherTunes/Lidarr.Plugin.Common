using System;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Packaging;

/// <summary>
/// Abstract <see cref="FactAttribute"/> base for plugin packaging policy tests.
/// Subclasses pass their plugin name to the constructor; the attribute then
/// auto-skips when no package zip is present and
/// <see cref="PackagingTestPaths.IsStrictMode"/> returns <see langword="false"/>.
/// </summary>
/// <remarks>
/// Usage — one-liner subclass per plugin:
/// <code>
/// public sealed class PackagingFactAttribute()
///     : Lidarr.Plugin.Common.TestKit.Packaging.PackagingFactAttribute("AppleMusicarr") { }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public abstract class PackagingFactAttribute : FactAttribute
{
    /// <summary>
    /// Initialises the attribute for the given plugin.
    /// </summary>
    /// <param name="pluginName">
    /// Pascal-cased plugin name (e.g. "AppleMusicarr", "Brainarr", "Tidalarr").
    /// Forwarded to <see cref="PackagingTestPaths.For"/>.
    /// </param>
    /// <param name="envVarName">
    /// Optional override for the plugin-specific env var.
    /// Defaults to <c>{PLUGINNAME}_PACKAGE_PATH</c>.
    /// </param>
    protected PackagingFactAttribute(string pluginName, string? envVarName = null)
    {
        if (PackagingTestPaths.IsStrictMode())
        {
            // Strict / CI mode: let the test run and fail naturally if the zip is missing.
            return;
        }

        var paths = PackagingTestPaths.For(pluginName, envVarName);
        if (paths.TryFindPackagePath() is null)
        {
            Skip = $"{pluginName} package not found. "
                 + $"Build the package (e.g. ./build.ps1 -Package) "
                 + $"or set {pluginName.ToUpperInvariant()}_PACKAGE_PATH to enable packaging tests.";
        }
    }
}
