using System;
using System.Reflection;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Creates defensive snapshots of mutable host settings objects.
/// </summary>
public static class SettingsSnapshot
{
    /// <summary>
    /// Returns a new <typeparamref name="TSettings"/> with every public read-write
    /// non-indexer instance property copied from <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Call this with the concrete settings type. Inherited public properties are included,
    /// but properties only present on a derived runtime type are not included if
    /// <typeparamref name="TSettings"/> is a base type. Reference-typed property values are
    /// copied by reference; use a custom snapshotter for settings that require deep-copy
    /// semantics.
    /// </remarks>
    public static TSettings Copy<TSettings>(TSettings source)
        where TSettings : class, new()
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var snapshot = new TSettings();
        foreach (var property in typeof(TSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod?.IsPublic == true &&
                property.SetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            {
                property.SetValue(snapshot, property.GetValue(source));
            }
        }

        return snapshot;
    }
}
