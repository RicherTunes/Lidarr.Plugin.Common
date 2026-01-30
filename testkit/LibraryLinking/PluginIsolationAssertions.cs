using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.LibraryLinking
{
    /// <summary>
    /// Shared assertions for verifying plugin library linking and isolation.
    /// Use these utilities across all streaming plugins (Brainarr, Qobuzarr, Tidalarr)
    /// to ensure consistent testing of ILRepack merging and service isolation.
    /// </summary>
    public static class PluginIsolationAssertions
    {
        #region Ready-to-Use Assertion Methods

        /// <summary>
        /// Asserts that no Lidarr.Plugin.Common types are publicly exported from a merged assembly.
        /// After ILRepack with Internalize=true, all Common types should be internal.
        /// </summary>
        /// <param name="pluginAssembly">The merged plugin assembly to check.</param>
        /// <param name="because">Optional reason for the assertion.</param>
        /// <exception cref="PluginIsolationException">Thrown when public Common types are found.</exception>
        public static void AssertNoPublicCommonTypesInMergedAssembly(Assembly pluginAssembly, string? because = null)
        {
            var exposedTypes = GetExposedCommonTypes(pluginAssembly);
            if (exposedTypes.Count > 0)
            {
                var typeList = string.Join("\n", exposedTypes.Select(t => $"  - {t.FullName}"));
                var message = $"Found {exposedTypes.Count} Lidarr.Plugin.Common type(s) publicly exported from merged assembly";
                if (!string.IsNullOrEmpty(because))
                {
                    message += $" ({because})";
                }
                message += $":\n{typeList}\n\nAfter ILRepack with Internalize=true, these types should be internal.";
                throw new PluginIsolationException(message);
            }
        }

        /// <summary>
        /// Asserts that no Polly types are publicly exported from a merged assembly.
        /// </summary>
        /// <param name="pluginAssembly">The merged plugin assembly to check.</param>
        /// <exception cref="PluginIsolationException">Thrown when public Polly types are found.</exception>
        public static void AssertNoPublicPollyTypesInMergedAssembly(Assembly pluginAssembly)
        {
            var exposedTypes = GetExposedPollyTypes(pluginAssembly);
            if (exposedTypes.Count > 0)
            {
                var typeList = string.Join("\n", exposedTypes.Select(t => $"  - {t.FullName}"));
                throw new PluginIsolationException(
                    $"Found {exposedTypes.Count} Polly type(s) publicly exported from merged assembly:\n{typeList}\n\n" +
                    "After ILRepack with Internalize=true, these types should be internal.");
            }
        }

        /// <summary>
        /// Asserts that no TagLibSharp types are publicly exported from a merged assembly.
        /// </summary>
        /// <param name="pluginAssembly">The merged plugin assembly to check.</param>
        /// <exception cref="PluginIsolationException">Thrown when public TagLib types are found.</exception>
        public static void AssertNoPublicTagLibTypesInMergedAssembly(Assembly pluginAssembly)
        {
            var exposedTypes = GetExposedTagLibTypes(pluginAssembly);
            if (exposedTypes.Count > 0)
            {
                var typeList = string.Join("\n", exposedTypes.Select(t => $"  - {t.FullName}"));
                throw new PluginIsolationException(
                    $"Found {exposedTypes.Count} TagLibSharp type(s) publicly exported from merged assembly:\n{typeList}\n\n" +
                    "After ILRepack with Internalize=true, these types should be internal.");
            }
        }

        /// <summary>
        /// Asserts that all expected assemblies have been merged (no separate DLL files).
        /// </summary>
        /// <param name="pluginDirectory">Directory containing the plugin.</param>
        /// <exception cref="PluginIsolationException">Thrown when unmerged assembly files are found.</exception>
        public static void AssertAllAssembliesMerged(string pluginDirectory)
        {
            var unmerged = GetUnmergedAssemblyFiles(pluginDirectory);
            if (unmerged.Count > 0)
            {
                var fileList = string.Join("\n", unmerged.Select(f => $"  - {Path.GetFileName(f)}"));
                throw new PluginIsolationException(
                    $"Found {unmerged.Count} assembly file(s) that should have been merged:\n{fileList}\n\n" +
                    "These assemblies should be embedded in the main plugin DLL via ILRepack.");
            }
        }

        /// <summary>
        /// Performs all standard ILRepack internalization assertions.
        /// </summary>
        /// <param name="pluginAssembly">The merged plugin assembly to check.</param>
        /// <param name="pluginDirectory">Directory containing the plugin.</param>
        public static void AssertProperlyInternalized(Assembly pluginAssembly, string pluginDirectory)
        {
            AssertNoPublicCommonTypesInMergedAssembly(pluginAssembly);
            AssertNoPublicPollyTypesInMergedAssembly(pluginAssembly);
            AssertNoPublicTagLibTypesInMergedAssembly(pluginAssembly);
            AssertAllAssembliesMerged(pluginDirectory);
        }

        #endregion

        #region Manifest Consistency Validation

        /// <summary>
        /// Validates that plugin.json and manifest.json are consistent with each other.
        /// This prevents drift between the two sources of plugin metadata.
        /// </summary>
        /// <param name="pluginDirectory">Directory containing the plugin.</param>
        /// <returns>Validation result with any consistency issues found.</returns>
        public static ManifestConsistencyResult ValidateManifestConsistency(string pluginDirectory)
        {
            var result = new ManifestConsistencyResult { PluginDirectory = pluginDirectory };

            var pluginJsonPath = Path.Combine(pluginDirectory, "plugin.json");
            var manifestJsonPath = Path.Combine(pluginDirectory, "manifest.json");

            // Check if files exist
            if (!File.Exists(pluginJsonPath))
            {
                result.Issues.Add("plugin.json not found");
                return result;
            }

            // manifest.json is optional but if it exists, it should be consistent
            if (!File.Exists(manifestJsonPath))
            {
                result.ManifestExists = false;
                return result;
            }

            result.ManifestExists = true;

            try
            {
                var pluginJson = JsonDocument.Parse(File.ReadAllText(pluginJsonPath));
                var manifestJson = JsonDocument.Parse(File.ReadAllText(manifestJsonPath));

                var pluginRoot = pluginJson.RootElement;
                var manifestRoot = manifestJson.RootElement;

                // Compare version fields
                var pluginVersion = GetJsonString(pluginRoot, "version");
                var manifestVersion = GetJsonString(manifestRoot, "version") ?? GetJsonString(manifestRoot, "Version");

                if (!string.IsNullOrEmpty(pluginVersion) && !string.IsNullOrEmpty(manifestVersion))
                {
                    if (!string.Equals(pluginVersion, manifestVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add($"Version mismatch: plugin.json='{pluginVersion}' vs manifest.json='{manifestVersion}'");
                    }
                }

                // Compare name fields
                var pluginName = GetJsonString(pluginRoot, "name");
                var manifestName = GetJsonString(manifestRoot, "name") ?? GetJsonString(manifestRoot, "Name");

                if (!string.IsNullOrEmpty(pluginName) && !string.IsNullOrEmpty(manifestName))
                {
                    if (!string.Equals(pluginName, manifestName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add($"Name mismatch: plugin.json='{pluginName}' vs manifest.json='{manifestName}'");
                    }
                }

                // Compare id/guid fields
                var pluginId = GetJsonString(pluginRoot, "id") ?? GetJsonString(pluginRoot, "guid");
                var manifestId = GetJsonString(manifestRoot, "id") ?? GetJsonString(manifestRoot, "guid") ?? GetJsonString(manifestRoot, "Guid");

                if (!string.IsNullOrEmpty(pluginId) && !string.IsNullOrEmpty(manifestId))
                {
                    if (!string.Equals(pluginId, manifestId, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add($"ID/GUID mismatch: plugin.json='{pluginId}' vs manifest.json='{manifestId}'");
                    }
                }

                result.PluginJsonContent = pluginJson;
                result.ManifestJsonContent = manifestJson;
            }
            catch (JsonException ex)
            {
                result.Issues.Add($"JSON parse error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Asserts that plugin.json and manifest.json are consistent.
        /// </summary>
        /// <param name="pluginDirectory">Directory containing the plugin.</param>
        /// <exception cref="PluginIsolationException">Thrown when inconsistencies are found.</exception>
        public static void AssertManifestConsistency(string pluginDirectory)
        {
            var result = ValidateManifestConsistency(pluginDirectory);
            if (!result.IsConsistent)
            {
                throw new PluginIsolationException(
                    $"Plugin manifest inconsistency detected:\n" +
                    string.Join("\n", result.Issues.Select(i => $"  - {i}")) +
                    "\n\nUse plugin.json as the canonical source and generate manifest.json from it during build.");
            }
        }

        private static string? GetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        #endregion

        /// <summary>
        /// Verifies that the Common library types are not publicly exposed.
        /// After ILRepack merging, these types should be internalized.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <returns>List of improperly exposed types (empty if correct)</returns>
        public static IReadOnlyList<Type> GetExposedCommonTypes(Assembly pluginAssembly)
        {
            if (pluginAssembly == null)
                throw new ArgumentNullException(nameof(pluginAssembly));

            return pluginAssembly.GetExportedTypes()
                .Where(t => t.Namespace?.StartsWith("Lidarr.Plugin.Common", StringComparison.Ordinal) == true)
                .ToList();
        }

        /// <summary>
        /// Verifies that Polly types are not publicly exposed.
        /// After ILRepack merging, resilience policy types should be internalized.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <returns>List of improperly exposed types (empty if correct)</returns>
        public static IReadOnlyList<Type> GetExposedPollyTypes(Assembly pluginAssembly)
        {
            if (pluginAssembly == null)
                throw new ArgumentNullException(nameof(pluginAssembly));

            return pluginAssembly.GetExportedTypes()
                .Where(t => t.Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true)
                .ToList();
        }

        /// <summary>
        /// Verifies that TagLibSharp types are not publicly exposed.
        /// After ILRepack merging, audio tagging types should be internalized.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <returns>List of improperly exposed types (empty if correct)</returns>
        public static IReadOnlyList<Type> GetExposedTagLibTypes(Assembly pluginAssembly)
        {
            if (pluginAssembly == null)
                throw new ArgumentNullException(nameof(pluginAssembly));

            return pluginAssembly.GetExportedTypes()
                .Where(t => t.Namespace?.StartsWith("TagLib", StringComparison.Ordinal) == true)
                .ToList();
        }

        /// <summary>
        /// Gets external assembly references that should have been merged.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <returns>List of assembly references that should be merged but aren't</returns>
        public static IReadOnlyList<AssemblyName> GetUnmergedReferences(Assembly pluginAssembly)
        {
            if (pluginAssembly == null)
                throw new ArgumentNullException(nameof(pluginAssembly));

            var mergeTargets = new[]
            {
                "Lidarr.Plugin.Common",
                "Lidarr.Plugin.Abstractions",
                "Polly",
                "Polly.Core",
                "Polly.Extensions.Http"
            };

            return pluginAssembly.GetReferencedAssemblies()
                .Where(a => mergeTargets.Any(t => a.Name?.Equals(t, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();
        }

        /// <summary>
        /// Checks if separate merged assembly files exist in the plugin directory.
        /// These should be embedded in the main DLL after ILRepack.
        /// </summary>
        /// <param name="pluginDirectory">Directory containing the plugin</param>
        /// <returns>List of files that exist but shouldn't</returns>
        public static IReadOnlyList<string> GetUnmergedAssemblyFiles(string pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory))
                throw new ArgumentNullException(nameof(pluginDirectory));

            var mergedAssemblyNames = new[]
            {
                "Lidarr.Plugin.Common.dll",
                "Lidarr.Plugin.Abstractions.dll",
                "Polly.dll",
                "Polly.Core.dll",
                "Polly.Extensions.Http.dll",
                "TagLibSharp.dll",
                "TagLibSharp-Lidarr.dll"
            };

            return mergedAssemblyNames
                .Select(name => Path.Combine(pluginDirectory, name))
                .Where(File.Exists)
                .ToList();
        }

        /// <summary>
        /// Verifies that the plugin manifest exists and contains required fields.
        /// </summary>
        /// <param name="pluginDirectory">Directory containing the plugin</param>
        /// <returns>Validation result with any issues found</returns>
        public static ManifestValidationResult ValidateManifest(string pluginDirectory)
        {
            var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
            var result = new ManifestValidationResult { ManifestPath = manifestPath };

            if (!File.Exists(manifestPath))
            {
                result.Issues.Add("plugin.json not found");
                return result;
            }

            var content = File.ReadAllText(manifestPath);

            if (!content.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
                result.Issues.Add("Missing 'id' field");

            if (!content.Contains("\"version\"", StringComparison.OrdinalIgnoreCase))
                result.Issues.Add("Missing 'version' field");

            if (!content.Contains("\"name\"", StringComparison.OrdinalIgnoreCase))
                result.Issues.Add("Missing 'name' field");

            result.ManifestContent = content;
            return result;
        }

        /// <summary>
        /// Verifies the plugin can be loaded concurrently without issues.
        /// </summary>
        /// <param name="pluginAssemblyPath">Path to the plugin assembly</param>
        /// <param name="concurrency">Number of concurrent load attempts</param>
        /// <returns>True if all loads succeeded</returns>
        public static async Task<bool> VerifyConcurrentLoadingAsync(string pluginAssemblyPath, int concurrency = 5)
        {
            if (!File.Exists(pluginAssemblyPath))
                return false;

            var loadTasks = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(() =>
                {
                    try
                    {
                        var assembly = Assembly.LoadFrom(pluginAssemblyPath);
                        return assembly != null;
                    }
                    catch
                    {
                        return false;
                    }
                }));

            var results = await Task.WhenAll(loadTasks);
            return results.All(r => r);
        }

        /// <summary>
        /// Verifies that all public types in the plugin are properly namespaced.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <param name="expectedNamespacePrefix">Expected namespace prefix (e.g., "Brainarr", "Lidarr.Plugin.Qobuzarr")</param>
        /// <returns>List of improperly namespaced types</returns>
        public static IReadOnlyList<Type> GetImproperlyNamespacedTypes(Assembly pluginAssembly, string expectedNamespacePrefix)
        {
            if (pluginAssembly == null)
                throw new ArgumentNullException(nameof(pluginAssembly));

            var ignoredPrefixes = new[] { "System", "Microsoft", "Polly", "TagLib", "Lidarr.Plugin.Common" };

            return pluginAssembly.GetExportedTypes()
                .Where(t => !ignoredPrefixes.Any(prefix =>
                    t.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true))
                .Where(t => t.Namespace?.StartsWith(expectedNamespacePrefix, StringComparison.Ordinal) != true)
                .ToList();
        }

        /// <summary>
        /// Verifies that the target framework is compatible with Lidarr.
        /// </summary>
        /// <param name="pluginAssembly">The plugin assembly to check</param>
        /// <returns>True if targeting a compatible framework</returns>
        public static bool IsTargetFrameworkCompatible(Assembly pluginAssembly)
        {
            if (pluginAssembly == null)
                return false;

            var targetFramework = pluginAssembly
                .GetCustomAttributes<System.Runtime.Versioning.TargetFrameworkAttribute>()
                .FirstOrDefault();

            if (targetFramework == null)
                return false;

            // Lidarr supports net6.0 and net8.0
            return targetFramework.FrameworkName.Contains("net6") ||
                   targetFramework.FrameworkName.Contains("net8") ||
                   targetFramework.FrameworkName.Contains(".NETCoreApp,Version=v6") ||
                   targetFramework.FrameworkName.Contains(".NETCoreApp,Version=v8");
        }
    }

    /// <summary>
    /// Result of plugin manifest validation.
    /// </summary>
    public class ManifestValidationResult
    {
        /// <summary>
        /// Path to the manifest file.
        /// </summary>
        public string ManifestPath { get; set; } = string.Empty;

        /// <summary>
        /// Raw content of the manifest if it exists.
        /// </summary>
        public string? ManifestContent { get; set; }

        /// <summary>
        /// List of validation issues found.
        /// </summary>
        public List<string> Issues { get; } = new();

        /// <summary>
        /// Whether the manifest is valid (no issues).
        /// </summary>
        public bool IsValid => Issues.Count == 0 && ManifestContent != null;
    }

    /// <summary>
    /// Result of plugin manifest consistency validation between plugin.json and manifest.json.
    /// </summary>
    public class ManifestConsistencyResult
    {
        /// <summary>
        /// Path to the plugin directory.
        /// </summary>
        public string PluginDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Whether manifest.json exists (it's optional).
        /// </summary>
        public bool ManifestExists { get; set; }

        /// <summary>
        /// Parsed plugin.json content.
        /// </summary>
        public JsonDocument? PluginJsonContent { get; set; }

        /// <summary>
        /// Parsed manifest.json content.
        /// </summary>
        public JsonDocument? ManifestJsonContent { get; set; }

        /// <summary>
        /// List of consistency issues found.
        /// </summary>
        public List<string> Issues { get; } = new();

        /// <summary>
        /// Whether the manifests are consistent (no issues).
        /// </summary>
        public bool IsConsistent => Issues.Count == 0;
    }

    /// <summary>Thrown when a plugin isolation assertion fails.</summary>
    public sealed class PluginIsolationException : Exception
    {
        public PluginIsolationException(string message) : base(message)
        {
        }
    }
}
