using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Security.Llm
{
    /// <summary>
    /// Hardened JSON (de)serialization wrapper for LLM-derived payloads.
    /// Prevents deserialization attacks via depth caps, suspicious-pattern guarding
    /// and bounded-size enforcement.
    /// </summary>
    /// <remarks>
    /// LLM responses are untrusted input regardless of provider — this helper
    /// enforces conservative defaults (<see cref="DefaultMaxDepth"/> = 10,
    /// <see cref="MaxJsonSize"/> = 10 MiB) suitable for chat completion JSON.
    /// </remarks>
    public static class LlmJsonSerializer
    {
        /// <summary>Default max nesting depth (10).</summary>
        public const int DefaultMaxDepth = 10;

        /// <summary>Strict max nesting depth (5).</summary>
        public const int StrictMaxDepth = 5;

        /// <summary>Hard upper bound on nesting (rejected during heuristic check).</summary>
        public const int HardMaxNestingDepth = 20;

        /// <summary>Maximum allowed JSON size (10 MiB).</summary>
        public const int MaxJsonSize = 10 * 1024 * 1024;

        private static readonly JsonSerializerOptions DefaultOptions = new()
        {
            MaxDepth = DefaultMaxDepth,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

        private static readonly JsonSerializerOptions StrictOptions = new()
        {
            MaxDepth = StrictMaxDepth,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>Suspicious patterns rejected by <see cref="ValidateJsonContent"/>.</summary>
        public static readonly string[] DefaultSuspiciousPatterns =
        {
            "__proto__",
            "constructor",
            "$type",
            "$id",
            "$ref",
            "__defineGetter__",
            "__defineSetter__",
            "__lookupGetter__",
            "__lookupSetter__",
            "function(",
            "eval(",
            "settimeout(",
            "setinterval(",
            "<script",
            "javascript:",
            "data:text/html",
            "vbscript:",
            "onclick",
            "onerror",
            "onload"
        };

        /// <summary>Deserializes a JSON string into <typeparamref name="T"/> with hardening.</summary>
        public static T Deserialize<T>(string json, bool strict = false) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json), "JSON content cannot be null or empty");

            if (json.Length > MaxJsonSize)
                throw new InvalidOperationException($"JSON content exceeds maximum allowed size of {MaxJsonSize} bytes");

            ValidateJsonContent(json);

            try
            {
                var options = strict ? StrictOptions : DefaultOptions;
                return JsonSerializer.Deserialize<T>(json, options)
                    ?? throw new InvalidOperationException("Deserialization returned null");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to deserialize JSON: {ex.Message}", ex);
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidOperationException($"Deserialization not supported for type: {ex.Message}", ex);
            }
        }

        /// <summary>Async stream variant of <see cref="Deserialize{T}"/>.</summary>
        public static async Task<T> DeserializeAsync<T>(Stream stream, bool strict = false) where T : class
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            if (stream.CanSeek && stream.Length > MaxJsonSize)
                throw new InvalidOperationException($"JSON stream exceeds maximum allowed size of {MaxJsonSize} bytes");

            try
            {
                var options = strict ? StrictOptions : DefaultOptions;
                return await JsonSerializer.DeserializeAsync<T>(stream, options).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Deserialization returned null");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to deserialize JSON stream: {ex.Message}", ex);
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidOperationException($"Deserialization not supported for type: {ex.Message}", ex);
            }
        }

        /// <summary>Serializes <paramref name="obj"/> to JSON with size enforcement.</summary>
        public static string Serialize<T>(T? obj, bool strict = false) where T : class
        {
            if (obj is null) return "null";

            try
            {
                var options = strict ? StrictOptions : DefaultOptions;
                var json = JsonSerializer.Serialize(obj, options);

                if (json.Length > MaxJsonSize)
                    throw new InvalidOperationException($"Serialized JSON exceeds maximum allowed size of {MaxJsonSize} bytes");

                return json;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to serialize object: {ex.Message}", ex);
            }
        }

        /// <summary>Async stream variant of <see cref="Serialize"/>.</summary>
        public static async Task SerializeAsync<T>(Stream stream, T? obj, bool strict = false) where T : class
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            if (obj is null)
            {
                var nullBytes = Encoding.UTF8.GetBytes("null");
                await stream.WriteAsync(nullBytes, 0, nullBytes.Length).ConfigureAwait(false);
                return;
            }

            try
            {
                var options = strict ? StrictOptions : DefaultOptions;
                await JsonSerializer.SerializeAsync(stream, obj, options).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to serialize object to stream: {ex.Message}", ex);
            }
        }

        /// <summary>Try-pattern wrapper around <see cref="Deserialize{T}"/>.</summary>
        public static bool TryDeserialize<T>(string json, out T? result, out string? error) where T : class
        {
            result = null;
            error = null;
            try
            {
                result = Deserialize<T>(json);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Parse JSON to <see cref="JsonDocument"/> with depth + size + heuristic checks.</summary>
        public static JsonDocument ParseDocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json));

            if (json.Length > MaxJsonSize)
                throw new InvalidOperationException($"JSON content exceeds maximum allowed size of {MaxJsonSize} bytes");

            ValidateJsonContent(json);

            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = DefaultMaxDepth,
                AllowTrailingCommas = false
            };

            return JsonDocument.Parse(json, options);
        }

        /// <summary>
        /// Parse JSON in a relaxed mode intended for provider responses. Skips heuristic
        /// content checks (so legitimate strings like "&lt;script&gt;" pass through), but
        /// preserves size and nesting-depth protections.
        /// </summary>
        public static JsonDocument ParseDocumentRelaxed(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json));

            if (json.Length > MaxJsonSize)
                throw new InvalidOperationException($"JSON content exceeds maximum allowed size of {MaxJsonSize} bytes");

            EnforceNestingDepth(json);

            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = DefaultMaxDepth,
                AllowTrailingCommas = false
            };

            return JsonDocument.Parse(json, options);
        }

        /// <summary>Validate JSON content for suspicious patterns and excessive nesting.</summary>
        public static void ValidateJsonContent(string json)
        {
            var lowerJson = json.ToLowerInvariant();
            foreach (var pattern in DefaultSuspiciousPatterns)
            {
                if (lowerJson.Contains(pattern.ToLowerInvariant()))
                {
                    throw new InvalidOperationException($"Potentially malicious JSON content detected: contains '{pattern}'");
                }
            }

            EnforceNestingDepth(json);

            // Excessive single-array-size indicator (rough heuristic).
            if (System.Text.RegularExpressions.Regex.IsMatch(json, @"\[\s*\d{7,}\s*\]"))
            {
                throw new InvalidOperationException("JSON contains potentially excessive array size");
            }
        }

        private static void EnforceNestingDepth(string json)
        {
            int maxNestingDepth = 0;
            int currentDepth = 0;
            foreach (char c in json)
            {
                if (c == '{' || c == '[')
                {
                    currentDepth++;
                    if (currentDepth > maxNestingDepth) maxNestingDepth = currentDepth;
                }
                else if (c == '}' || c == ']')
                {
                    currentDepth--;
                }
            }

            if (maxNestingDepth > HardMaxNestingDepth)
            {
                throw new InvalidOperationException($"JSON nesting depth exceeds safe limit: {maxNestingDepth}");
            }
        }

        /// <summary>Build custom serializer options bounded by <see cref="HardMaxNestingDepth"/>.</summary>
        public static JsonSerializerOptions CreateOptions(
            int maxDepth = DefaultMaxDepth,
            bool caseInsensitive = true,
            bool writeIndented = false)
        {
            return new JsonSerializerOptions
            {
                MaxDepth = Math.Min(maxDepth, HardMaxNestingDepth),
                PropertyNameCaseInsensitive = caseInsensitive,
                AllowTrailingCommas = false,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = writeIndented,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
        }
    }
}
