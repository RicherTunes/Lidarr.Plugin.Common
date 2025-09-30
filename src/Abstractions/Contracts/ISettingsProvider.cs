using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Manages plugin configuration that must remain private to the plugin AssemblyLoadContext.
    /// All interactions use plain dictionaries to avoid leaking plugin-specific types into the host.
    /// </summary>
    public interface ISettingsProvider
    {
        /// <summary>
        /// Returns strongly-typed metadata about the plugin settings for UI consumption.
        /// </summary>
        IReadOnlyCollection<SettingDefinition> Describe();

        /// <summary>
        /// Returns default settings payload.
        /// </summary>
        IReadOnlyDictionary<string, object?> GetDefaults();

        /// <summary>
        /// Validates settings without mutating plugin state.
        /// </summary>
        PluginValidationResult Validate(IDictionary<string, object?> settings);

        /// <summary>
        /// Applies settings to the plugin.
        /// </summary>
        PluginValidationResult Apply(IDictionary<string, object?> settings);
    }

    /// <summary>
    /// Metadata describing an individual plugin setting field.
    /// </summary>
    public sealed class SettingDefinition
    {
        public string Key { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public SettingDataType DataType { get; init; } = SettingDataType.String;
        public bool IsRequired { get; init; }
        public IReadOnlyList<string>? AllowedValues { get; init; }
        public object? DefaultValue { get; init; }
    }

    public enum SettingDataType
    {
        String,
        Boolean,
        Integer,
        Number,
        Enum,
        Password,
        Json
    }
}
