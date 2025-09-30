# Settings Reference

Centralise your plugin settings so users know which fields must be configured and which defaults are safe.

## Abstractions
The host interacts with settings via `ISettingsProvider` and `SettingDefinition`.

```csharp

public sealed class MySettingsProvider : ISettingsProvider
{
    public IReadOnlyCollection<SettingDefinition> Describe() => new[]
    {
        new SettingDefinition
        {
            Key = "ClientId",
            DisplayName = "Client ID",
            Description = "OAuth client identifier provided by the service",
            DataType = SettingDataType.String,
            IsRequired = true
        },
        new SettingDefinition
        {
            Key = "ClientSecret",
            DisplayName = "Client Secret",
            Description = "OAuth client secret",
            DataType = SettingDataType.String,
            IsRequired = true,
            IsSensitive = true
        }
    };

    public IReadOnlyDictionary<string, object?> GetDefaults() => new Dictionary<string, object?>
    {
        ["Locale"] = "en-US",
        ["PreferredQuality"] = StreamingQualityTier.Lossless
    };

    public PluginValidationResult Validate(IDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue("ClientId", out var id) || string.IsNullOrWhiteSpace(id as string))
        {
            return PluginValidationResult.Failure(new[] { "ClientId is required" });
        }

        return PluginValidationResult.Success();
    }

    public PluginValidationResult Apply(IDictionary<string, object?> settings)
        => PluginValidationResult.Success();
}

```

## Recommended keys

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `ClientId` | string | yes | OAuth identifier. |
| `ClientSecret` | string | yes | Mark as sensitive. |
| `BaseUrl` | string | yes | Required for self-hosted services. |
| `CountryCode` | string | no | Default `US`; use for locale-specific APIs. |
| `Locale` | string | no | Default `en-US`. |
| `PreferredQuality` | enum | no | Use `StreamingQualityTier`. |

## Best practices

- Mark secrets with `IsSensitive=true` so the host hides them in UIs/logs.
- Provide defaults for optional settings to simplify onboarding.
- Validate settings and return actionable errors via `PluginValidationResult.Failure`.
- Document each setting in your plugin README and cross-link here to avoid drift.

## Related docs

- [Plugin manifest](MANIFEST.md)
- [Migration guide](../migration/FROM_LEGACY.md)
- [Developer guide → Settings](../dev-guide/DEVELOPER_GUIDE.md#settings)

