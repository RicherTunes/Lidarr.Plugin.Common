using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Lidarr.Plugin.Common.TestKit.Data;

/// <summary>
/// Helper for retrieving embedded JSON payloads shipped with the TestKit.
/// </summary>
public static class EmbeddedJson
{
    public static JsonDocument Open(string relativePath)
    {
        var assembly = typeof(EmbeddedJson).Assembly;
        var resourceName = ResolveResourceName(assembly, relativePath);
        if (resourceName is null)
        {
            throw new FileNotFoundException($"Embedded JSON '{relativePath}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException($"Resource '{resourceName}' missing from assembly.");
        return JsonDocument.Parse(stream);
    }

    public static string ReadAsString(string relativePath)
    {
        var assembly = typeof(EmbeddedJson).Assembly;
        var resourceName = ResolveResourceName(assembly, relativePath);
        if (resourceName is null)
        {
            throw new FileNotFoundException($"Embedded JSON '{relativePath}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException($"Resource '{resourceName}' missing from assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ResolveResourceName(Assembly assembly, string relativePath)
    {
        var normalized = relativePath.Replace('/', '.').Replace('\\', '.');
        return assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(normalized, System.StringComparison.OrdinalIgnoreCase));
    }
}
