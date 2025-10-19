using Lidarr.Plugin.Abstractions.Contracts;

namespace MyServiceArr;

public sealed class Plugin : IPlugin
{
    public string Id => "${ServiceId}";
    public string Name => "${ServiceId} Plugin";
    public string Description => "Lidarr plugin for ${ServiceId}.";
}

