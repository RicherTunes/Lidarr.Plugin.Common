using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Lidarr.Plugin.Common.Tests.PackageValidation;

/// <summary>
/// Shared validation utilities for plugin package correctness.
/// Use this in plugin test suites to ensure packaging meets Lidarr plugin requirements.
/// 
/// ECOSYSTEM PACKAGING POLICY:
/// - HOST-PROVIDED (do not ship):
///   - Lidarr.Plugin.Abstractions.dll
///   - Microsoft.Extensions.DependencyInjection.Abstractions.dll
///   - Microsoft.Extensions.Logging.Abstractions.dll
/// - MERGE into plugin DLL (internalized):
///   - Lidarr.Plugin.Common.dll, Polly*, TagLibSharp*, MS.Ext.DI (impl), etc.
/// - DELETE (host provides):
///   - FluentValidation.dll
///   - Lidarr.Core.dll, Lidarr.Common.dll, Lidarr.Host.dll, etc.
/// </summary>
public static class PluginPackageValidator
{
    /// <summary>
    /// Assemblies that must NOT be shipped in plugin packages.
    /// Shipping these can break multi-plugin cohabitation by loading multiple copies of the same assembly identity.
    /// </summary>
    public static readonly string[] ExpectedHostProvidedAssemblies =
    [
        "Lidarr.Plugin.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll"
    ];

    /// <summary>
    /// Assemblies that must NOT be in the package (host provides them).
    /// Shipping these causes type-identity conflicts at runtime.
    /// Finding these is an ERROR.
    /// </summary>
    public static readonly string[] DisallowedHostAssemblies =
    [
        "FluentValidation.dll",
        "Lidarr.Plugin.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Lidarr.Core.dll",
        "Lidarr.Common.dll",
        "Lidarr.Host.dll",
        "Lidarr.Api.V1.dll",
        "Lidarr.Http.dll",
        "Lidarr.SignalR.dll",
        "NzbDrone.Core.dll",
        "NzbDrone.Common.dll",
        "NzbDrone.Host.dll",
        "NzbDrone.Api.dll"
    ];

    /// <summary>
    /// Validates a plugin package zip file for correctness.
    /// </summary>
    /// <param name="packagePath">Path to the .zip package</param>
    /// <param name="pluginAssemblyName">Expected plugin assembly name (e.g., "Lidarr.Plugin.Tidalarr.dll")</param>
    /// <param name="strictMode">If true, missing type-identity assemblies are errors. If false, warnings. If null, defaults to IsCI.</param>
    /// <returns>Validation result with any errors</returns>
    public static PackageValidationResult ValidatePackage(string packagePath, string pluginAssemblyName, bool? strictMode = null)
    {
        // Default to strict mode in CI environments, but allow explicit override
        bool isStrict = strictMode ?? IsCI;
        
        var result = new PackageValidationResult();

        if (!File.Exists(packagePath))
        {
            result.AddError($"Package not found: {packagePath}");
            return result;
        }

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var dlls = archive.Entries
                .Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check plugin assembly exists
            if (!dlls.Contains(pluginAssemblyName))
            {
                result.AddError($"Plugin assembly '{pluginAssemblyName}' not found in package");
            }

            // Check for disallowed host assemblies
            var foundDisallowed = dlls.Intersect(DisallowedHostAssemblies, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var dll in foundDisallowed)
            {
                result.AddError($"Host assembly '{dll}' should not be in package (host provides it)");
            }

            // Host-provided assemblies should not be shipped (see DisallowedHostAssemblies).
            // Keep a separate list for documentation/searchability.
            _ = ExpectedHostProvidedAssemblies;

            // Check for bloat - should only have a few DLLs
            if (dlls.Count > 10)
            {
                result.AddWarning($"Package contains {dlls.Count} DLLs - may have excessive dependencies. Expected ~5-6 for a well-merged plugin.");
            }

            result.AssemblyCount = dlls.Count;
            result.Assemblies = [.. dlls];
        }
        catch (Exception ex)
        {
            result.AddError($"Failed to read package: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Returns true if running in CI environment.
    /// </summary>
    public static bool IsCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ||
        Environment.GetEnvironmentVariable("TF_BUILD") == "True";
}

/// <summary>
/// Result of package validation.
/// </summary>
public class PackageValidationResult
{
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public bool IsValid => _errors.Count == 0;
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public int AssemblyCount { get; set; }
    public string[] Assemblies { get; set; } = [];

    public void AddError(string message) => _errors.Add(message);
    public void AddWarning(string message) => _warnings.Add(message);

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Package Validation: {(IsValid ? "PASSED" : "FAILED")}");
        sb.AppendLine($"Assemblies: {AssemblyCount}");
        
        if (_errors.Count > 0)
        {
            sb.AppendLine("Errors:");
            foreach (var e in _errors) sb.AppendLine($"  - {e}");
        }
        
        if (_warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var w in _warnings) sb.AppendLine($"  - {w}");
        }
        
        return sb.ToString();
    }
}
