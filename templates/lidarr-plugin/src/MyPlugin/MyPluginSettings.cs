namespace MyPlugin;

/// <summary>
/// Strongly typed settings for MyPlugin.
///
/// This is a plain POCO by design: Common's <c>SettingsBinder</c> (used internally by
/// <c>StreamingPlugin</c>/<c>ISettingsProvider</c> implementations) round-trips settings via
/// reflection over public writable properties — no marker interface or attribute is required.
/// To surface these fields in the host UI, describe them explicitly from your
/// <see cref="Lidarr.Plugin.Abstractions.Contracts.ISettingsProvider.Describe"/> implementation
/// using <see cref="Lidarr.Plugin.Abstractions.Contracts.SettingDefinition"/>, e.g.:
/// <c>new SettingDefinition { Key = nameof(ApiKey), DisplayName = "API Key", DataType = SettingDataType.Password, IsRequired = true }</c>.
/// </summary>
public sealed class MyPluginSettings
{
    /// <summary>API key for the upstream service. Treat as a secret (mask in logs/UI).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL for the upstream service's API.</summary>
    public string BaseUrl { get; set; } = "https://api.example";
}

