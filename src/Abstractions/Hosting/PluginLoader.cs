using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;

namespace Lidarr.Plugin.Abstractions.Hosting
{
    /// <summary>
    /// Utility helpers for loading plugins into isolated AssemblyLoadContexts.
    /// </summary>
    public static class PluginLoader
    {
        public static async Task<PluginHandle> LoadAsync(PluginLoadRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            var pluginDirectory = request.PluginDirectory ?? throw new ArgumentException("PluginDirectory must be provided", nameof(request));
            var hostVersion = request.HostVersion ?? throw new ArgumentException("HostVersion must be provided", nameof(request));
            var contractVersion = request.ContractVersion ?? throw new ArgumentException("ContractVersion must be provided", nameof(request));
            var pluginContext = request.PluginContext ?? throw new ArgumentException("PluginContext must be provided", nameof(request));

            var manifest = request.Manifest ?? PluginManifest.Load(Path.Combine(pluginDirectory, "plugin.json"));
            var compatibility = manifest.EvaluateCompatibility(hostVersion, contractVersion);
            if (!compatibility.IsCompatible)
            {
                throw new InvalidOperationException($"Plugin '{manifest.Id}' is incompatible: {compatibility.Message}");
            }

            var assemblyPath = request.GetAssemblyPath(manifest);
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Plugin assembly '{assemblyPath}' not found.", assemblyPath);
            }

            var loadContext = new PluginLoadContext(assemblyPath, request.SharedAssemblies);
            using var _ = loadContext.EnterContextualReflection();

            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginType = assembly
                .GetTypes()
                .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null);

            if (pluginType is null)
            {
                throw new InvalidOperationException($"Plugin assembly '{assembly.FullName}' does not contain a parameterless IPlugin implementation.");
            }

            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;

            await plugin.InitializeAsync(pluginContext, cancellationToken).ConfigureAwait(false);

            return new PluginHandle(plugin, loadContext);
        }
    }
}
