using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PluginBridgeSample
{
    // snippet:settings-dot-notation
    // snippet-skip-compile
    public static class DotNotation
    {
        public static void Apply(IDictionary<string, object?> values, object target)
        {
            foreach (var (path, raw) in values)
            {
                var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    continue;
                }

                object current = target;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    var containerProperty = FindProperty(current, segments[i]);
                    var next = containerProperty.GetValue(current);
                    if (next is null)
                    {
                        next = Activator.CreateInstance(containerProperty.PropertyType)
                            ?? throw new InvalidOperationException($"Cannot create '{containerProperty.PropertyType}'.");
                        containerProperty.SetValue(current, next);
                    }

                    current = next;
                }

                var leafProperty = FindProperty(current, segments[^1]);
                var converted = ConvertValue(raw, leafProperty.PropertyType);
                leafProperty.SetValue(current, converted);
            }
        }

        private static PropertyInfo FindProperty(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                throw new InvalidOperationException($"Unknown property '{name}' on '{target.GetType().Name}'.");
            }

            return property;
        }

        private static object? ConvertValue(object? raw, Type targetType)
        {
            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (raw is null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, raw.ToString()!, ignoreCase: true);
            }

            if (targetType == typeof(TimeSpan))
            {
                return TimeSpan.Parse(raw.ToString()!, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(Guid))
            {
                return Guid.Parse(raw.ToString()!);
            }

            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }
    }
    // end-snippet
}
    // snippet:settings-roundtrip
    // snippet-skip-compile
    public sealed class StreamingCredentials
    {
        public string ApiKey { get; set; } = string.Empty;
        public OAuthSettings OAuth { get; set; } = new();
        public RetrySettings Retry { get; set; } = new();
    }

    public sealed class OAuthSettings
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public StreamingScope Scope { get; set; } = StreamingScope.Read;
    }

    public sealed class RetrySettings
    {
        public int Attempts { get; set; } = 3;
        public TimeSpan Backoff { get; set; } = TimeSpan.FromSeconds(2);
    }

    public enum StreamingScope
    {
        Read,
        ReadWrite
    }
    // end-snippet
}
