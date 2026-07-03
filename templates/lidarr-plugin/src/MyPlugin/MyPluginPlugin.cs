using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Hosting;

namespace MyPlugin;

public sealed class MyPluginPlugin : StreamingPlugin<MyPluginModule, MyPluginSettings>
{
    protected override IEnumerable<SettingDefinition> DescribeSettings()
    {
        return new[]
        {
            new SettingDefinition
            {
                Key = nameof(MyPluginSettings.ApiKey),
                DisplayName = "API Key",
                Description = "API key for the upstream service.",
                DataType = SettingDataType.Password,
                IsRequired = true
            },
            new SettingDefinition
            {
                Key = nameof(MyPluginSettings.BaseUrl),
                DisplayName = "API Base URL",
                Description = "Base URL for the upstream service API.",
                DataType = SettingDataType.String,
                IsRequired = true,
                DefaultValue = "https://api.example"
            }
        };
    }

    protected override PluginValidationResult ValidateSettings(MyPluginSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            errors.Add("API key is required.");
        }

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add("Base URL must be an absolute HTTP or HTTPS URL.");
        }

        return errors.Count == 0
            ? PluginValidationResult.Success()
            : PluginValidationResult.Failure(errors);
    }
}
