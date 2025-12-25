// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Validates plugin package contents to ensure they don't include host assemblies.
    /// This prevents accidental bundling of Lidarr framework assemblies that should be
    /// provided by the host runtime.
    /// </summary>
    /// <remarks>
    /// This pattern is extracted from Tidalarr's packaging-closure workflow and generalized
    /// for use across all plugins. Including host assemblies in plugins can cause version
    /// conflicts and runtime failures.
    /// </remarks>
    public static class PackageClosureValidator
    {
        /// <summary>
        /// Default list of Lidarr host assemblies that should never be included in plugins.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultDisallowedAssemblies = new[]
        {
            "Lidarr.Core.dll",
            "Lidarr.Common.dll",
            "Lidarr.Host.dll",
            "Lidarr.Http.dll",
            "Lidarr.Api.V1.dll",
            "NzbDrone.Core.dll",
            "NzbDrone.Common.dll"
        };

        /// <summary>
        /// Default list of assemblies that are allowed even if they match the Lidarr.* pattern.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultAllowedAssemblies = new[]
        {
            "Lidarr.Plugin.Common.dll",
            "Lidarr.Plugin.Abstractions.dll"
        };

        /// <summary>
        /// Validates a plugin package ZIP file to ensure it doesn't contain host assemblies.
        /// </summary>
        /// <param name="zipFilePath">Path to the ZIP file to validate.</param>
        /// <param name="disallowedAssemblies">Optional custom list of disallowed assemblies.</param>
        /// <param name="allowedAssemblies">Optional custom list of allowed assemblies (overrides disallowed patterns).</param>
        /// <returns>Validation result with details about any violations.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="zipFilePath"/> is null or whitespace.</exception>
        /// <exception cref="FileNotFoundException">Thrown when the ZIP file doesn't exist.</exception>
        public static PackageValidationResult ValidatePackage(
            string zipFilePath,
            IEnumerable<string>? disallowedAssemblies = null,
            IEnumerable<string>? allowedAssemblies = null)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath))
            {
                throw new ArgumentException("ZIP file path cannot be null or whitespace.", nameof(zipFilePath));
            }

            if (!File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("ZIP file not found.", zipFilePath);
            }

            var disallowed = (disallowedAssemblies ?? DefaultDisallowedAssemblies).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = (allowedAssemblies ?? DefaultAllowedAssemblies).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var assembliesInPackage = new List<string>();
            var violations = new List<string>();

            using (var archive = ZipFile.OpenRead(zipFilePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var assemblyName = Path.GetFileName(entry.FullName);
                    assembliesInPackage.Add(assemblyName);

                    // Skip if explicitly allowed
                    if (allowed.Contains(assemblyName))
                    {
                        continue;
                    }

                    // Check against disallowed list
                    if (disallowed.Contains(assemblyName))
                    {
                        violations.Add(assemblyName);
                        continue;
                    }

                    // Check for Lidarr.* pattern (except allowed ones)
                    if (assemblyName.StartsWith("Lidarr.", StringComparison.OrdinalIgnoreCase) &&
                        !allowed.Contains(assemblyName))
                    {
                        violations.Add(assemblyName);
                    }
                }
            }

            return new PackageValidationResult(
                isValid: violations.Count == 0,
                assembliesFound: assembliesInPackage,
                violations: violations,
                packagePath: zipFilePath);
        }

        /// <summary>
        /// Validates a directory containing plugin files.
        /// </summary>
        /// <param name="directoryPath">Path to the directory to validate.</param>
        /// <param name="disallowedAssemblies">Optional custom list of disallowed assemblies.</param>
        /// <param name="allowedAssemblies">Optional custom list of allowed assemblies.</param>
        /// <returns>Validation result with details about any violations.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="directoryPath"/> is null or whitespace.</exception>
        /// <exception cref="DirectoryNotFoundException">Thrown when the directory doesn't exist.</exception>
        public static PackageValidationResult ValidateDirectory(
            string directoryPath,
            IEnumerable<string>? disallowedAssemblies = null,
            IEnumerable<string>? allowedAssemblies = null)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("Directory path cannot be null or whitespace.", nameof(directoryPath));
            }

            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
            }

            var disallowed = (disallowedAssemblies ?? DefaultDisallowedAssemblies).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = (allowedAssemblies ?? DefaultAllowedAssemblies).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var assembliesInDirectory = new List<string>();
            var violations = new List<string>();

            foreach (var file in Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var assemblyName = Path.GetFileName(file);
                assembliesInDirectory.Add(assemblyName);

                // Skip if explicitly allowed
                if (allowed.Contains(assemblyName))
                {
                    continue;
                }

                // Check against disallowed list
                if (disallowed.Contains(assemblyName))
                {
                    violations.Add(assemblyName);
                    continue;
                }

                // Check for Lidarr.* pattern (except allowed ones)
                if (assemblyName.StartsWith("Lidarr.", StringComparison.OrdinalIgnoreCase) &&
                    !allowed.Contains(assemblyName))
                {
                    violations.Add(assemblyName);
                }
            }

            return new PackageValidationResult(
                isValid: violations.Count == 0,
                assembliesFound: assembliesInDirectory,
                violations: violations,
                packagePath: directoryPath);
        }
    }

    /// <summary>
    /// Result of a package closure validation.
    /// </summary>
    public sealed class PackageValidationResult
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PackageValidationResult"/> class.
        /// </summary>
        public PackageValidationResult(
            bool isValid,
            IEnumerable<string> assembliesFound,
            IEnumerable<string> violations,
            string packagePath)
        {
            IsValid = isValid;
            AssembliesFound = assembliesFound.ToList().AsReadOnly();
            Violations = violations.ToList().AsReadOnly();
            PackagePath = packagePath;
        }

        /// <summary>
        /// Gets a value indicating whether the package is valid (no disallowed assemblies found).
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Gets the list of all assemblies found in the package.
        /// </summary>
        public IReadOnlyList<string> AssembliesFound { get; }

        /// <summary>
        /// Gets the list of assemblies that violate the closure policy.
        /// </summary>
        public IReadOnlyList<string> Violations { get; }

        /// <summary>
        /// Gets the path to the package that was validated.
        /// </summary>
        public string PackagePath { get; }

        /// <summary>
        /// Gets a human-readable summary of the validation result.
        /// </summary>
        /// <returns>A summary string.</returns>
        public string GetSummary()
        {
            if (IsValid)
            {
                return $"Package closure OK. Assemblies: {string.Join(", ", AssembliesFound)}";
            }

            return $"Package closure FAILED. Disallowed assemblies: {string.Join(", ", Violations)}";
        }

        /// <summary>
        /// Throws an exception if the validation failed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the package contains disallowed assemblies.</exception>
        public void ThrowIfInvalid()
        {
            if (!IsValid)
            {
                throw new InvalidOperationException(
                    $"Package closure validation failed for '{PackagePath}'. " +
                    $"Disallowed host assemblies found: {string.Join(", ", Violations)}. " +
                    $"These assemblies should be provided by the Lidarr runtime, not bundled with the plugin.");
            }
        }
    }
}
