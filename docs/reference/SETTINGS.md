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

## Bridge implementation

The easiest way to satisfy `ISettingsProvider` is to inherit from `StreamingPlugin<TModule, TSettings>`. The bridge exposes your strongly typed settings to DI as a singleton while presenting the host with the dictionary contract.

```csharp
public sealed class QobuzPlugin : StreamingPlugin<QobuzModule, QobuzSettings>
{
    protected override IEnumerable<SettingDefinition> DescribeSettings()
    {
        yield return new SettingDefinition
        {
            Key = nameof(QobuzSettings.AppId),
            DisplayName = "Application ID",
            DataType = SettingDataType.String,
            IsRequired = true,
            Description = "API identifier issued by Qobuz"
        };
    }

    protected override PluginValidationResult ValidateSettings(QobuzSettings settings)
        => string.IsNullOrWhiteSpace(settings.AppId)
            ? PluginValidationResult.Failure(new[] { "AppId is required." })
            : PluginValidationResult.Success();
}
```

The bridge:

- Loads `plugin.json` and keeps the manifest handy.
- Maps public writable properties on `TSettings` to dictionary keys.
- Handles conversion from JSON primitives (`JsonElement`) to the correct CLR type.
- Applies new settings in-place when the host calls `Apply`, so existing services pick up changes.
- Provides hooks (`ConfigureDefaults`, `ValidateSettings`, `OnSettingsApplied`) for custom behaviour.

> Prefer flat settings objects. Nested objects are currently unsupported and require custom logic.

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


