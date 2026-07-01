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
/// - SHIP: the plugin DLL and manifest assets only.
/// - MERGE into plugin DLL (internalized via ILRepack): Common, Abstractions, TagLibSharp,
///   FluentValidation, Microsoft.Extensions.* implementation/abstraction sidecars, Polly, etc.
/// - DO NOT SHIP: host assemblies or merged/internalized dependency sidecars listed in
///   scripts/parity-spec.json versionContract.forbiddenPackageContents.
/// </summary>
public static class PluginPackageValidator
{
    /// <summary>
    /// Type-identity sidecars expected in plugin packages.
    /// Current ALC/ILRepack policy internalizes these, so this remains empty by design.
    /// </summary>
    public static readonly string[] TypeIdentityAssemblies = [];

    /// <summary>
    /// Assemblies required to be present in plugin packages.
    /// Only the main plugin assembly is strictly required - type-identity assemblies
    /// are validated separately with warnings.
    /// </summary>
    public static readonly string[] RequiredPluginAssemblies = [];

    /// <summary>
    /// Assemblies that must NOT be in the package.
    /// Shipping these causes type-identity conflicts at runtime or bypasses ILRepack internalization.
    /// Finding these is an ERROR.
    /// </summary>
    public static readonly string[] DisallowedHostAssemblies =
    [
        "FluentValidation.dll",
        "NLog.dll",
        "System.Text.Json.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.Logging.dll",
        "Microsoft.Extensions.Configuration.dll",
        "Microsoft.Extensions.Caching.Memory.dll",
        "Microsoft.Extensions.Http.dll",
        "Lidarr.Plugin.Abstractions.dll",
        "Lidarr.Plugin.Common.dll",
        "Lidarr.Core.dll",
        "Lidarr.Common.dll",
        "Lidarr.Http.dll",
        "Lidarr.Api.V1.dll",
        "Lidarr.Host.dll",
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

            foreach (var required in RequiredPluginAssemblies)
            {
                if (!dlls.Contains(required))
                {
                    if (isStrict)
                    {
                        result.AddError($"Required assembly '{required}' missing from package");
                    }
                    else
                    {
                        result.AddWarning($"Required assembly '{required}' missing from package");
                    }
                }
            }

            // Check for type-identity assemblies (error in CI, warning locally)
            foreach (var typeIdentity in TypeIdentityAssemblies)
            {
                if (!dlls.Contains(typeIdentity))
                {
                    if (isStrict)
                    {
                        result.AddError($"Type-identity assembly '{typeIdentity}' missing - will cause runtime method signature mismatches");
                    }
                    else
                    {
                        result.AddWarning($"Type-identity assembly '{typeIdentity}' missing - may cause method signature mismatches");
                    }
                }
            }

            // Check for disallowed host assemblies
            var foundDisallowed = dlls.Intersect(DisallowedHostAssemblies, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var dll in foundDisallowed)
            {
                result.AddError($"Disallowed assembly '{dll}' should not be in package (merge/internalize it or let the host provide it)");
            }

            // Check for bloat - should only have a few DLLs
            if (dlls.Count > 10)
            {
                result.AddWarning($"Package contains {dlls.Count} DLLs - may have excessive dependencies. Expected the plugin DLL plus manifest assets for a well-merged plugin.");
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
