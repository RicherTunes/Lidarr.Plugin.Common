using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
}
