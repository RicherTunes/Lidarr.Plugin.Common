namespace MyPlugin.Auth;

/// <summary>
/// OAuth2/PKCE client settings for MyPlugin. Plain POCO — see the remarks on
/// <see cref="MyPlugin.MyPluginSettings"/> for why no attribute/marker interface is used.
/// </summary>
public sealed class MyPluginOAuthSettings
{
    /// <summary>OAuth2 client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret. Treat as a secret (mask in logs/UI).</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Redirect URI registered with the OAuth2 provider for the local PKCE callback.</summary>
    public string RedirectUri { get; set; } = "http://localhost:53682/callback";
}

