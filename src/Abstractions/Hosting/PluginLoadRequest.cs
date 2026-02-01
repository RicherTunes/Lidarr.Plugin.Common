using System;
using System.Collections.Generic;
using System.IO;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;

namespace Lidarr.Plugin.Abstractions.Hosting
{
    /// <summary>
    /// Parameters required to load a plugin into an isolated AssemblyLoadContext.
    /// </summary>
    public sealed class PluginLoadRequest
    {
        /// <summary>
        /// Path to the root directory containing plugin.json and the main assembly.
        /// </summary>
        public string? PluginDirectory { get; init; }

        /// <summary>
        /// Filename of the plugin assembly (defaults to manifest entryAssembly or {id}.dll).
        /// </summary>
        public string? PluginAssemblyFile { get; init; }

        /// <summary>
        /// Versions of assemblies that should be shared with the host (defaults to Abstractions + Logging).
        /// </summary>
        public IReadOnlyCollection<string>? SharedAssemblies { get; init; }

        /// <summary>
        /// Host semantic version for compatibility checks.
        /// </summary>
        public Version? HostVersion { get; init; }

        /// <summary>
        /// Version of Lidarr.Plugin.Abstractions loaded in the host.
        /// </summary>
        public Version? ContractVersion { get; init; }

        /// <summary>
        /// Host-provided plugin context implementation.
        /// </summary>
        public IPluginContext? PluginContext { get; init; }

        /// <summary>
        /// Optional manifest override when already parsed.
        /// </summary>
        public PluginManifest? Manifest { get; init; }

        internal string GetAssemblyPath(PluginManifest manifest)
        {
            if (PluginDirectory is null)
            {
                throw new InvalidOperationException("PluginDirectory must be provided.");
            }

            var assemblyName = PluginAssemblyFile
                ?? manifest.EntryAssembly
                ?? manifest.Main
                ?? $"{manifest.Id}.dll";

            return Path.Combine(PluginDirectory, assemblyName);
        }
    }
}
