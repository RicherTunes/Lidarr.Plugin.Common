using System;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.Abstractions.Hosting
{
    /// <summary>
    /// Minimal implementation of <see cref="IPluginContext"/> suitable for tests and simple hosts.
    /// </summary>
    public sealed class DefaultPluginContext : IPluginContext
    {
        public DefaultPluginContext(Version hostVersion, ILoggerFactory loggerFactory, IServiceProvider? services = null)
        {
            HostVersion = hostVersion ?? throw new ArgumentNullException(nameof(hostVersion));
            LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            Services = services;
        }

        public Version HostVersion { get; }

        public ILoggerFactory LoggerFactory { get; }

        public IServiceProvider? Services { get; }
    }
}
