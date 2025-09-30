using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Lidarr.Plugin.Common.Hosting.Settings;

/// <summary>
/// Utility methods for translating between strongly typed settings objects and the
/// dictionary-based representation required by <see cref="Lidarr.Plugin.Abstractions.Contracts.ISettingsProvider"/>.
/// </summary>
internal static class SettingsBinder
{
    public static IReadOnlyDictionary<string, object?> ToDictionary<TSettings>(TSettings settings)
        where TSettings : class
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in GetWritableProperties(settings.GetType()))
        {
            var value = property.GetValue(settings);
            if (value is null)
            {
                continue;
            }

            map[property.Name] = value;
        }

        return map;
    }

    public static void Populate<TSettings>(IDictionary<string, object?> values, TSettings target)
        where TSettings : class
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        foreach (var property in GetWritableProperties(target.GetType()))
        {
            if (!TryGetValue(values, property.Name, out var incoming))
            {
                continue;
            }

            var converted = ConvertValue(incoming, property.PropertyType);
            property.SetValue(target, converted);
        }
    }

    public static TSettings Clone<TSettings>(TSettings source)
        where TSettings : class, new()
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var clone = new TSettings();
        var dictionary = ToDictionary(source);
        Populate(dictionary, clone);
        return clone;
    }

    private static IEnumerable<PropertyInfo> GetWritableProperties(Type type)
        => type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

    private static bool TryGetValue(IDictionary<string, object?> values, string key, out object? value)
    {
        if (values.TryGetValue(key, out value))
        {
            return true;
        }

        // Case-insensitive lookup for consumers that use different casing
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static object? ConvertValue(object? value, Type destinationType)
    {
        destinationType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;

        if (value is null)
        {
            return destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null;
        }

        if (destinationType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is JsonElement json)
        {
            value = ConvertJsonElement(json, destinationType);
            if (value is null)
            {
                return null;
            }

            if (destinationType.IsInstanceOfType(value))
            {
                return value;
            }
        }

        if (destinationType.IsEnum)
        {
            return ConvertToEnum(value, destinationType);
        }

        if (destinationType == typeof(Guid))
        {
            return Guid.Parse(value.ToString()!);
        }

        if (destinationType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        }

        if (destinationType == typeof(DateTime))
        {
            return DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (destinationType == typeof(string))
        {
            return value.ToString();
        }

        return Convert.ChangeType(value, destinationType, CultureInfo.InvariantCulture);
    }

    private static object? ConvertJsonElement(JsonElement element, Type destinationType)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                var str = element.GetString();
                if (destinationType == typeof(Guid))
                {
                    return Guid.TryParse(str, out var guid) ? guid : Guid.Empty;
                }

                if (destinationType == typeof(DateTime))
                {
                    return DateTime.Parse(str!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }

                if (destinationType == typeof(TimeSpan))
                {
                    return TimeSpan.Parse(str!, CultureInfo.InvariantCulture);
                }

                if (destinationType.IsEnum && !string.IsNullOrEmpty(str))
                {
                    return Enum.Parse(destinationType, str!, ignoreCase: true);
                }

                return str;
            case JsonValueKind.Number:
                if (destinationType == typeof(int))
                {
                    return element.GetInt32();
                }

                if (destinationType == typeof(long))
                {
                    return element.GetInt64();
                }

                if (destinationType == typeof(float))
                {
                    return element.GetSingle();
                }

                if (destinationType == typeof(double))
                {
                    return element.GetDouble();
                }

                if (destinationType == typeof(decimal))
                {
                    return element.GetDecimal();
                }

                return element.GetDouble();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetBoolean();
            default:
                return element.GetRawText();
        }
    }

    private static object ConvertToEnum(object value, Type enumType)
    {
        if (value is string s)
        {
            return Enum.Parse(enumType, s, ignoreCase: true);
        }

        if (value is JsonElement json && json.ValueKind == JsonValueKind.String)
        {
            return Enum.Parse(enumType, json.GetString()!, ignoreCase: true);
        }

        var numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return Enum.ToObject(enumType, numeric);
    }
}
