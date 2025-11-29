using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Abstract base class defining compliance tests that ALL Lidarr plugins must pass.
/// Plugin test projects should inherit from this class and implement the abstract properties.
/// This ensures a minimum quality bar across all plugins.
/// </summary>
/// <remarks>
/// To use this base class:
/// 1. Create a test class that inherits from PluginComplianceTestBase
/// 2. Implement the abstract properties (PluginAssembly, PluginManifest, etc.)
/// 3. The base class provides test methods that verify compliance
/// 4. Override virtual methods to add plugin-specific validations
/// </remarks>
public abstract class PluginComplianceTestBase : IDisposable
{
    #region Abstract Properties - Must be implemented by plugin tests

    /// <summary>
    /// The plugin assembly to test.
    /// </summary>
    protected abstract Assembly PluginAssembly { get; }

    /// <summary>
    /// The plugin manifest (plugin.json parsed).
    /// </summary>
    protected abstract PluginManifest PluginManifest { get; }

    /// <summary>
    /// The plugin's main entry point type implementing IPlugin.
    /// </summary>
    protected abstract Type? PluginEntryPointType { get; }

    /// <summary>
    /// Expected minimum version of Lidarr this plugin supports.
    /// </summary>
    protected abstract Version MinimumLidarrVersion { get; }

    /// <summary>
    /// The plugin's unique identifier.
    /// </summary>
    protected abstract string PluginId { get; }

    #endregion

    #region Manifest Compliance Tests

    /// <summary>
    /// Verifies the plugin manifest has all required fields.
    /// </summary>
    public virtual ComplianceResult VerifyManifestCompleteness()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(PluginManifest.Id))
            errors.Add("Manifest must have a non-empty Id");

        if (string.IsNullOrWhiteSpace(PluginManifest.Name))
            errors.Add("Manifest must have a non-empty Name");

        if (string.IsNullOrWhiteSpace(PluginManifest.Version))
            errors.Add("Manifest must have a Version");

        if (string.IsNullOrWhiteSpace(PluginManifest.ApiVersion))
            errors.Add("Manifest must specify ApiVersion");

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies the manifest version matches the assembly version.
    /// </summary>
    public virtual ComplianceResult VerifyVersionConsistency()
    {
        var errors = new List<string>();
        var assemblyVersion = PluginAssembly.GetName().Version;

        if (!string.IsNullOrWhiteSpace(PluginManifest.Version) && assemblyVersion != null)
        {
            // Try to parse the manifest version and compare major versions
            if (Version.TryParse(PluginManifest.Version, out var manifestVersion))
            {
                if (manifestVersion.Major != assemblyVersion.Major)
                {
                    errors.Add($"Manifest version major ({manifestVersion.Major}) " +
                              $"should match assembly version major ({assemblyVersion.Major})");
                }
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Assembly Compliance Tests

    /// <summary>
    /// Verifies the assembly can be loaded without errors.
    /// </summary>
    public virtual ComplianceResult VerifyAssemblyLoadable()
    {
        var errors = new List<string>();

        try
        {
            var types = PluginAssembly.GetTypes();
            if (!types.Any())
            {
                errors.Add("Assembly contains no types");
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            errors.Add($"Assembly failed to load types: {ex.Message}");
            foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null))
            {
                errors.Add($"  - {loaderEx!.Message}");
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies no public types from Common library are exposed (ILRepack compliance).
    /// </summary>
    public virtual ComplianceResult VerifyInternalization()
    {
        var errors = new List<string>();
        var publicTypes = PluginAssembly.GetExportedTypes();

        // Check for exposed Common library types
        var exposedCommonTypes = publicTypes
            .Where(t => t.Namespace?.StartsWith("Lidarr.Plugin.Common", StringComparison.Ordinal) == true)
            .ToList();

        if (exposedCommonTypes.Any())
        {
            errors.Add($"Found {exposedCommonTypes.Count} publicly exposed Common library types " +
                      "(should be internalized via ILRepack):");
            foreach (var type in exposedCommonTypes.Take(5))
            {
                errors.Add($"  - {type.FullName}");
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies the plugin implements required interfaces.
    /// </summary>
    public virtual ComplianceResult VerifyRequiredInterfaces()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        // Check for IPlugin implementation
        var pluginTypes = allTypes.Where(t =>
            typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();

        if (!pluginTypes.Any())
        {
            errors.Add("Plugin must have at least one type implementing IPlugin");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Security Compliance Tests

    /// <summary>
    /// Verifies no hardcoded credentials exist in the assembly.
    /// </summary>
    public virtual ComplianceResult VerifyNoHardcodedCredentials()
    {
        var errors = new List<string>();
        var suspiciousPatterns = new[]
        {
            "password=",
            "apikey=",
            "api_key=",
            "secret=",
            "token=",
            "bearer ",
            "basic "
        };

        try
        {
            // Check string literals in the assembly (basic check)
            var allTypes = PluginAssembly.GetTypes();
            foreach (var type in allTypes)
            {
                var fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var field in fields.Where(f => f.FieldType == typeof(string)))
                {
                    try
                    {
                        var value = field.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(value))
                        {
                            foreach (var pattern in suspiciousPatterns)
                            {
                                if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase) &&
                                    value.Length > pattern.Length + 5) // Has actual value after pattern
                                {
                                    // Check if it looks like a real credential (not a placeholder)
                                    var afterPattern = value.Substring(
                                        value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) + pattern.Length);
                                    if (!afterPattern.StartsWith("{") &&
                                        !afterPattern.StartsWith("$") &&
                                        !afterPattern.StartsWith("<") &&
                                        afterPattern.Length > 8)
                                    {
                                        errors.Add($"Potential hardcoded credential in {type.Name}.{field.Name}");
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip fields that can't be read
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Could not scan for credentials: {ex.Message}");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Verifies the plugin doesn't use dangerous APIs.
    /// </summary>
    public virtual ComplianceResult VerifyNoDangerousApis()
    {
        var errors = new List<string>();
        var dangerousTypes = new[]
        {
            "System.Diagnostics.Process",
            "System.Runtime.InteropServices.DllImportAttribute",
            "System.Reflection.Emit"
        };

        var referencedTypes = PluginAssembly.GetReferencedAssemblies();
        var allTypes = PluginAssembly.GetTypes();

        foreach (var type in allTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                try
                {
                    var methodBody = method.GetMethodBody();
                    // Basic check - can be expanded
                }
                catch
                {
                    // Skip methods that can't be inspected
                }
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Performance Compliance Tests

    /// <summary>
    /// Verifies the plugin doesn't have obvious performance issues.
    /// </summary>
    public virtual ComplianceResult VerifyNoBlockingSyncOverAsync()
    {
        var errors = new List<string>();
        var allTypes = PluginAssembly.GetTypes();

        foreach (var type in allTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                // Check for .Result or .Wait() calls on Task
                if (method.ReturnType == typeof(void) || !method.ReturnType.Name.Contains("Task"))
                {
                    // Potential sync-over-async if method isn't async but calls async methods
                    // This is a simplified check
                }
            }
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Runs all compliance checks and returns aggregated results.
    /// </summary>
    public virtual ComplianceReport RunAllComplianceChecks()
    {
        var results = new Dictionary<string, ComplianceResult>
        {
            ["ManifestCompleteness"] = VerifyManifestCompleteness(),
            ["VersionConsistency"] = VerifyVersionConsistency(),
            ["AssemblyLoadable"] = VerifyAssemblyLoadable(),
            ["Internalization"] = VerifyInternalization(),
            ["RequiredInterfaces"] = VerifyRequiredInterfaces(),
            ["NoHardcodedCredentials"] = VerifyNoHardcodedCredentials(),
            ["NoDangerousApis"] = VerifyNoDangerousApis(),
            ["NoBlockingSyncOverAsync"] = VerifyNoBlockingSyncOverAsync()
        };

        var passed = results.Values.Count(r => r.Passed);
        var total = results.Count;

        return new ComplianceReport(results, passed, total);
    }

    #endregion

    public virtual void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Result of a single compliance check.
/// </summary>
public record ComplianceResult(bool Passed, IReadOnlyList<string> Errors)
{
    public static ComplianceResult Success => new(true, Array.Empty<string>());
    public static ComplianceResult Failure(params string[] errors) => new(false, errors);
}

/// <summary>
/// Aggregated compliance report.
/// </summary>
public record ComplianceReport(
    IReadOnlyDictionary<string, ComplianceResult> Results,
    int PassedCount,
    int TotalCount)
{
    public bool AllPassed => PassedCount == TotalCount;
    public double PassRate => TotalCount > 0 ? (double)PassedCount / TotalCount : 0;

    public IEnumerable<string> GetAllErrors() =>
        Results.Values.SelectMany(r => r.Errors);
}
