using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// Reflection loader that resolves an <b>out-of-tree</b> <see cref="IExternalDownloadHandler"/>
    /// at runtime via <see cref="Assembly.LoadFrom(string)"/>, configured by explicit arguments or
    /// environment variables.
    ///
    /// <para>
    /// <b>Common ships NO handler implementation — not even a mock.</b> No key derivation, no CDM,
    /// no license-challenge and no decrypt logic live in this library (see
    /// <see cref="IExternalDownloadHandler"/>). The concrete decryptor is supplied out-of-tree by
    /// the user; this loader merely locates and casts it. <see cref="TryLoad(string, string, ILogger, string, string)"/>
    /// returns <c>null</c> whenever no handler is configured (the default), so a plugin with no
    /// external downloader simply runs without one.
    /// </para>
    /// </summary>
    public static class ExternalDownloadHandlerLoader
    {
        /// <summary>
        /// Attempts to load and instantiate an <see cref="IExternalDownloadHandler"/> from an
        /// assembly on disk.
        ///
        /// <para>
        /// Resolution order for each input: the explicit argument if non-empty, otherwise the
        /// corresponding environment variable. When the resulting assembly path or type name is
        /// missing, the method returns <c>null</c> (no handler configured). It also returns
        /// <c>null</c> — and logs — when the assembly/type cannot be loaded, when instantiation
        /// fails, or when the resolved type does not implement <see cref="IExternalDownloadHandler"/>.
        /// The method never throws.
        /// </para>
        /// </summary>
        /// <param name="assemblyPath">
        /// Path to the assembly containing the handler. When null/empty, falls back to the
        /// <paramref name="assemblyPathEnvVar"/> environment variable.
        /// </param>
        /// <param name="typeName">
        /// Fully-qualified type name of the handler. When null/empty, falls back to the
        /// <paramref name="typeNameEnvVar"/> environment variable.
        /// </param>
        /// <param name="logger">Optional logger for diagnostics. No logging occurs when null.</param>
        /// <param name="assemblyPathEnvVar">
        /// Environment variable consulted when <paramref name="assemblyPath"/> is null/empty.
        /// Plugins pass their own (e.g. <c>AMAZONMUSICARR_EXTERNAL_DOWNLOADER_PATH</c>).
        /// </param>
        /// <param name="typeNameEnvVar">
        /// Environment variable consulted when <paramref name="typeName"/> is null/empty
        /// (e.g. <c>AMAZONMUSICARR_EXTERNAL_DOWNLOADER_TYPE</c>).
        /// </param>
        /// <returns>The loaded handler, or <c>null</c> when none is configured or loading fails.</returns>
        public static IExternalDownloadHandler? TryLoad(
            string? assemblyPath = null,
            string? typeName = null,
            ILogger? logger = null,
            string? assemblyPathEnvVar = null,
            string? typeNameEnvVar = null)
        {
            try
            {
                var resolvedPath = FirstNonEmpty(assemblyPath, ReadEnv(assemblyPathEnvVar));
                var resolvedType = FirstNonEmpty(typeName, ReadEnv(typeNameEnvVar));

                // No handler configured: this is the normal default, not an error.
                if (string.IsNullOrWhiteSpace(resolvedPath) || string.IsNullOrWhiteSpace(resolvedType))
                {
                    return null;
                }

                var fullPath = Path.GetFullPath(resolvedPath!);
                var assembly = Assembly.LoadFrom(fullPath);
                var type = assembly.GetType(resolvedType!, throwOnError: true, ignoreCase: false);

                var instance = Activator.CreateInstance(type!);
                if (instance is IExternalDownloadHandler handler)
                {
                    return handler;
                }

                logger?.LogWarning(
                    "Type {TypeName} from {AssemblyPath} does not implement IExternalDownloadHandler.",
                    resolvedType,
                    fullPath);
                return null;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load external download handler.");
                return null;
            }
        }

        private static string? ReadEnv(string? name)
            => string.IsNullOrWhiteSpace(name) ? null : Environment.GetEnvironmentVariable(name!);

        private static string? FirstNonEmpty(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);
    }
}
