using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.Abstractions.Hosting
{
    /// <summary>
    /// Wraps a plugin instance together with its AssemblyLoadContext for convenient cleanup.
    /// </summary>
    public sealed class PluginHandle : IAsyncDisposable
    {
        public PluginHandle(IPlugin plugin, PluginLoadContext loadContext)
        {
            Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            LoadContext = loadContext ?? throw new ArgumentNullException(nameof(loadContext));
        }

        public IPlugin Plugin { get; }

        public PluginLoadContext LoadContext { get; }

        public async ValueTask DisposeAsync()
        {
            await Plugin.DisposeAsync().ConfigureAwait(false);
            LoadContext.Unload();
        }
    }
}
