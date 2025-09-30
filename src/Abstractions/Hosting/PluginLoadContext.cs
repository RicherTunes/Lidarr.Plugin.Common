using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Lidarr.Plugin.Abstractions.Hosting
{
    /// <summary>
    /// AssemblyLoadContext implementation that isolates a plugin and its dependency graph
    /// while sharing contract assemblies with the host.
    /// </summary>
    public sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly HashSet<string> _sharedAssemblies;

        public PluginLoadContext(string mainAssemblyPath, IEnumerable<string>? sharedAssemblies = null, bool isCollectible = true)
            : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible)
        {
            if (string.IsNullOrWhiteSpace(mainAssemblyPath))
            {
                throw new ArgumentException("Plugin assembly path must be provided", nameof(mainAssemblyPath));
            }

            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _sharedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Lidarr.Plugin.Abstractions",
                "Microsoft.Extensions.Logging.Abstractions"
            };

            if (sharedAssemblies is not null)
            {
                foreach (var assemblyName in sharedAssemblies)
                {
                    if (!string.IsNullOrWhiteSpace(assemblyName))
                    {
                        _sharedAssemblies.Add(assemblyName);
                    }
                }
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (_sharedAssemblies.Contains(assemblyName.Name!))
            {
                // Allow the default context to resolve shared contracts so instances can cross boundaries.
                return null;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
