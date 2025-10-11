using Lidarr.Plugin.Abstractions.Settings;

namespace MyPlugin.Auth;

public sealed class MyPluginOAuthSettings
{
    [Setting("clientId", DisplayName = "Client ID")] 
    public string ClientId { get; set; } = string.Empty;

    [Setting("clientSecret", DisplayName = "Client Secret", IsSecret = true)] 
    public string ClientSecret { get; set; } = string.Empty;

    [Setting("redirectUri", DisplayName = "Redirect URI")] 
    public string RedirectUri { get; set; } = "http://localhost:53682/callback";
}

