using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Settings;

namespace MyPlugin;

public sealed class MyPluginSettings : ISettings
{
    [Setting("apiKey", DisplayName = "API Key", IsSecret = true)]
    public string ApiKey { get; set; } = string.Empty;

    [Setting("baseUrl", DisplayName = "Base URL")]
    public string BaseUrl { get; set; } = "https://api.example";
}

