using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lidarr.Plugin.Abstractions.Manifest
{
    /// <summary>
    /// Immutable description of a Lidarr streaming plugin as declared in plugin.json.
    /// The host uses this to evaluate compatibility before loading the plugin AssemblyLoadContext.
    /// </summary>
    public sealed class PluginManifest
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        /// <summary>
        /// A unique identifier for the plugin (stable across versions).
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Human friendly plugin name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Semantic version of the plugin package.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        /// <summary>
        /// Supported Lidarr.Plugin.Abstractions major version expressed as "1.x".
        /// </summary>
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; init; } = string.Empty;

        /// <summary>
        /// Version of Lidarr.Plugin.Common that ships with the plugin (diagnostic only).
        /// </summary>
        [JsonPropertyName("commonVersion")]
        public string? CommonVersion { get; init; }

        /// <summary>
        /// Minimum host version required (optional).
        /// </summary>
        [JsonPropertyName("minHostVersion")]
        public string? MinHostVersion { get; init; }

        /// <summary>
        /// Optional friendly description for UIs.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        /// <summary>
        /// Plugin author attribution.
        /// </summary>
        [JsonPropertyName("author")]
        public string? Author { get; init; }

        /// <summary>
        /// Optional list of features/capabilities implemented by the plugin.
        /// </summary>
        [JsonPropertyName("capabilities")]
        public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Required settings keys that the host must provide.
        /// </summary>
        [JsonPropertyName("requiredSettings")]
        public IReadOnlyList<string> RequiredSettings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Optional assembly name that contains the plugin entry-point.
        /// </summary>
        [JsonPropertyName("entryAssembly")]
        public string? EntryAssembly { get; init; }

        /// <summary>
        /// Loads and parses plugin.json from disk.
        /// </summary>
        public static PluginManifest Load(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                throw new ArgumentException("Manifest path must be provided", nameof(manifestPath));
            }

            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);
            if (manifest is null)
            {
                throw new InvalidOperationException($"Failed to parse manifest '{manifestPath}'.");
            }

            return manifest.EnsureValid();
        }

        /// <summary>
        /// Parses plugin.json from a JSON payload.
        /// </summary>
        public static PluginManifest FromJson(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);
            if (manifest is null)
            {
                throw new InvalidOperationException("Failed to parse plugin manifest.");
            }

            return manifest.EnsureValid();
        }

        /// <summary>
        /// Serialises the manifest back to JSON (useful for tooling/tests).
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, SerializerOptions);
        }

        /// <summary>
        /// Validates the manifest fields and returns the original instance when valid.
        /// Throws <see cref="InvalidOperationException"/> when validation fails.
        /// </summary>
        private PluginManifest EnsureValid()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Id)) errors.Add("Manifest 'id' is required.");
            if (string.IsNullOrWhiteSpace(Name)) errors.Add("Manifest 'name' is required.");
            if (string.IsNullOrWhiteSpace(Version)) errors.Add("Manifest 'version' is required.");
            if (string.IsNullOrWhiteSpace(ApiVersion)) errors.Add("Manifest 'apiVersion' is required.");

            if (!TryParseVersion(Version, out _)) errors.Add($"Manifest version '{Version}' is not a valid SemVer value.");
            if (!IsSupportedApiVersion(ApiVersion)) errors.Add($"apiVersion '{ApiVersion}' must use 'major.x' format (e.g. '1.x').");
            if (!string.IsNullOrWhiteSpace(MinHostVersion) && !TryParseVersion(MinHostVersion, out _))
            {
                errors.Add($"minHostVersion '{MinHostVersion}' is not a valid SemVer value.");
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", errors));
            }

            return this;
        }

        private static bool TryParseVersion(string value, out Version? version)
        {
            var normalised = NormaliseSemVer(value);
            if (string.IsNullOrWhiteSpace(normalised))
            {
                version = null;
                return false;
            }

            if (System.Version.TryParse(normalised!, out version))
            {
                return true;
            }

            version = null;
            return false;
        }
        private static string NormaliseSemVer(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var main = value.Split('+')[0];
            var dashIndex = main.IndexOf('-');
            if (dashIndex >= 0)
            {
                main = main.Substring(0, dashIndex);
            }

            var segments = main.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length switch
            {
                1 => string.Join('.', segments[0], "0", "0"),
                2 => string.Join('.', segments[0], segments[1], "0"),
                _ => string.Join('.', segments.Take(3))
            };
        }

        private static bool IsSupportedApiVersion(string apiVersion)
        {
            if (string.IsNullOrWhiteSpace(apiVersion)) return false;
            var parts = apiVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[1].Equals("x", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks whether the manifest is compatible with the provided host + abstractions versions.
        /// </summary>
        public PluginCompatibilityResult EvaluateCompatibility(Version hostVersion, Version abstractionsVersion)
        {
            if (hostVersion is null) throw new ArgumentNullException(nameof(hostVersion));
            if (abstractionsVersion is null) throw new ArgumentNullException(nameof(abstractionsVersion));

            var hostIsCompatible = string.IsNullOrWhiteSpace(MinHostVersion) ||
                (TryParseVersion(MinHostVersion, out var minHost) && minHost is not null && hostVersion >= minHost);

            if (!hostIsCompatible)
            {
                return PluginCompatibilityResult.Incompatible($"Host version {hostVersion} is lower than required {MinHostVersion}.");
            }

            if (!int.TryParse(ApiVersion.Split('.')[0], out var requiredMajor))
            {
                return PluginCompatibilityResult.Incompatible($"apiVersion '{ApiVersion}' is invalid.");
            }

            if (abstractionsVersion.Major != requiredMajor)
            {
                return PluginCompatibilityResult.Incompatible($"Plugin targets abstractions major {requiredMajor} but host provides {abstractionsVersion.Major}.");
            }

            return PluginCompatibilityResult.Compatible();
        }
    }
}
