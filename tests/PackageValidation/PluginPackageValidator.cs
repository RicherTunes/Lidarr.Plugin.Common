using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Lidarr.Plugin.Common.Tests.PackageValidation;

/// <summary>
/// Specifies the plugin's packaging policy based on how it interacts with FluentValidation.
/// </summary>
public enum PluginPackagingPolicy
{
    /// <summary>
    /// Plugin does NOT override Test(List&lt;ValidationFailure&gt;) methods.
    /// FluentValidation.dll MUST be shipped for type-identity matching.
    /// Used by: Qobuzarr, Tidalarr (streaming download clients/indexers).
    /// </summary>
    ShipsFluentValidation,

    /// <summary>
    /// Plugin DOES override Test(List&lt;ValidationFailure&gt;) methods.
    /// FluentValidation.dll must NOT be shipped - causes "Method 'Test' does not have an implementation" at runtime.
    /// Used by: AppleMusicarr, Brainarr (ImportLists that override Test).
    /// </summary>
    ForbidsFluentValidation
}

/// <summary>
/// Shared validation utilities for plugin package correctness.
/// </summary>
public static class PluginPackageValidator
{
    public static readonly string[] CoreTypeIdentityAssemblies =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Lidarr.Plugin.Abstractions.dll"
    ];

    public static readonly string[] TypeIdentityAssemblies =
    [
        "FluentValidation.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Lidarr.Plugin.Abstractions.dll"
    ];

    public static readonly string[] RequiredPluginAssemblies = [];

    public static readonly string[] DisallowedHostAssemblies =
    [
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

    public static PackageValidationResult ValidatePackage(string packagePath, string pluginAssemblyName, bool? strictMode = null)
    {
        return ValidatePackage(packagePath, pluginAssemblyName, PluginPackagingPolicy.ShipsFluentValidation, strictMode);
    }

    public static PackageValidationResult ValidatePackage(
        string packagePath,
        string pluginAssemblyName,
        PluginPackagingPolicy policy,
        bool? strictMode = null)
    {
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

            if (!dlls.Contains(pluginAssemblyName))
                result.AddError($"Plugin assembly '{pluginAssemblyName}' not found in package");

            foreach (var required in RequiredPluginAssemblies)
            {
                if (!dlls.Contains(required))
                {
                    if (isStrict)
                        result.AddError($"Required assembly '{required}' missing from package");
                    else
                        result.AddWarning($"Required assembly '{required}' missing from package");
                }
            }

            foreach (var typeIdentity in CoreTypeIdentityAssemblies)
            {
                if (!dlls.Contains(typeIdentity))
                {
                    if (isStrict)
                        result.AddError($"Type-identity assembly '{typeIdentity}' missing");
                    else
                        result.AddWarning($"Type-identity assembly '{typeIdentity}' missing");
                }
            }

            bool hasFluentValidation = dlls.Contains("FluentValidation.dll");
            switch (policy)
            {
                case PluginPackagingPolicy.ShipsFluentValidation:
                    if (!hasFluentValidation)
                    {
                        if (isStrict)
                            result.AddError("FluentValidation.dll missing - required for Test() method signature matching");
                        else
                            result.AddWarning("FluentValidation.dll missing");
                    }
                    break;

                case PluginPackagingPolicy.ForbidsFluentValidation:
                    if (hasFluentValidation)
                    {
                        result.AddError(
                            "FluentValidation.dll must NOT be shipped - plugin overrides Test() method. " +
                            "Shipping it causes 'Method does not have an implementation' at runtime.");
                    }
                    break;
            }

            var foundDisallowed = dlls.Intersect(DisallowedHostAssemblies, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var dll in foundDisallowed)
                result.AddError($"Host assembly '{dll}' should not be in package");

            if (dlls.Count > 10)
                result.AddWarning($"Package contains {dlls.Count} DLLs - may have excessive dependencies.");

            result.AssemblyCount = dlls.Count;
            result.Assemblies = [.. dlls];
            result.Policy = policy;
        }
        catch (Exception ex)
        {
            result.AddError($"Failed to read package: {ex.Message}");
        }

        return result;
    }

    public static bool IsCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ||
        Environment.GetEnvironmentVariable("TF_BUILD") == "True";
}

public class PackageValidationResult
{
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public bool IsValid => _errors.Count == 0;
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public int AssemblyCount { get; set; }
    public string[] Assemblies { get; set; } = [];
    public PluginPackagingPolicy Policy { get; set; }

    public void AddError(string message) => _errors.Add(message);
    public void AddWarning(string message) => _warnings.Add(message);

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Package Validation: {(IsValid ? "PASSED" : "FAILED")}");
        sb.AppendLine($"Policy: {Policy}");
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
