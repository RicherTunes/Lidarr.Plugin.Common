using System;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Services exposed by the host to a plugin instance.
    /// </summary>
    public interface IPluginContext
    {
        /// <summary>
        /// Host semantic version (e.g. Lidarr version).
        /// </summary>
        Version HostVersion { get; }

        /// <summary>
        /// Logger factory shared between host and plugin. Always shared from the default AssemblyLoadContext.
        /// </summary>
        ILoggerFactory LoggerFactory { get; }

        /// <summary>
        /// Optional service provider for host supplied services (must only expose types coming from Abstractions).
        /// </summary>
        IServiceProvider? Services { get; }
    }
}
