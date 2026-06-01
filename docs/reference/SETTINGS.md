# Settings Reference

Centralize your plugin settings so users know which fields must be configured and which defaults are safe.

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

## BaseStreamingSettings properties

`BaseStreamingSettings` (namespace `Lidarr.Plugin.Common.Base`) is the recommended base class for streaming-service plugin settings. It provides common configuration with sensible defaults.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BaseUrl` | `string` | — | Base URL for the streaming service API |
| `Email` | `string` | — | User email for authentication |
| `Password` | `string` | — | User password for authentication (mark as sensitive) |
| `AuthToken` | `string` | — | Authentication token for token-based auth services |
| `UserId` | `string` | — | User ID for services that require it |
| `CountryCode` | `string` | `"US"` | ISO 3166-1 alpha-2 country code for content availability |
| `Locale` | `string` | `"en-US"` | BCP 47 locale tag |
| `SearchLimit` | `int` | `100` | Max search results per query (1–1000) |
| `IncludeSingles` | `bool` | `false` | Include singles and EPs in results |
| `IncludeCompilations` | `bool` | `false` | Include compilation albums in results |
| `ApiRateLimit` | `int` | `60` | Max API requests per minute (1–1000) |
| `SearchCacheDuration` | `int` | `5` | Cache TTL in minutes (0–1440) |
| `ConnectionTimeout` | `int` | `30` | Connection timeout in seconds (5–300) |
| `OrganizeByArtist` | `bool` | `true` | Organize downloads by artist/album folder structure |
| `EarlyReleaseDayLimit` | `int` | `0` | Include albums up to N days before official release (0–90) |

Computed accessors: `CacheDuration` (`TimeSpan`), `RequestTimeout` (`TimeSpan`), `RateLimitWindow` (`TimeSpan` = 1 min).

Override `IsValid(out string errorMessage)` in derived classes to add service-specific validation.

Source: [`src/Base/BaseStreamingSettings.cs`](../../src/Base/BaseStreamingSettings.cs).

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


