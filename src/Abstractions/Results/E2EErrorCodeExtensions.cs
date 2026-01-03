using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Extension methods for E2E error code handling.
    /// </summary>
    public static class E2EErrorCodeExtensions
    {
        /// <summary>
        /// Standard metadata key for E2E error codes.
        /// The E2E runner checks this key first before falling back to pattern matching.
        /// </summary>
        public const string MetadataKey = "e2eErrorCode";

        /// <summary>
        /// Converts an E2E error code enum to its manifest string representation.
        /// </summary>
        /// <param name="code">The E2E error code.</param>
        /// <returns>The manifest string (e.g., "E2E_AUTH_MISSING").</returns>
        public static string ToManifestString(this E2EErrorCode code)
        {
            return code switch
            {
                E2EErrorCode.None => string.Empty,
                E2EErrorCode.AuthMissing => "E2E_AUTH_MISSING",
                E2EErrorCode.ConfigInvalid => "E2E_CONFIG_INVALID",
                E2EErrorCode.ApiTimeout => "E2E_API_TIMEOUT",
                E2EErrorCode.DockerUnavailable => "E2E_DOCKER_UNAVAILABLE",
                E2EErrorCode.NoReleasesAttributed => "E2E_NO_RELEASES_ATTRIBUTED",
                E2EErrorCode.QueueNotFound => "E2E_QUEUE_NOT_FOUND",
                E2EErrorCode.ZeroAudioFiles => "E2E_ZERO_AUDIO_FILES",
                E2EErrorCode.MetadataMissing => "E2E_METADATA_MISSING",
                E2EErrorCode.ImportFailed => "E2E_IMPORT_FAILED",
                E2EErrorCode.ComponentAmbiguous => "E2E_COMPONENT_AMBIGUOUS",
                E2EErrorCode.LoadFailure => "E2E_LOAD_FAILURE",
                E2EErrorCode.RateLimited => "E2E_RATE_LIMITED",
                E2EErrorCode.ProviderUnavailable => "E2E_PROVIDER_UNAVAILABLE",
                E2EErrorCode.Cancelled => "E2E_CANCELLED",
                _ => $"E2E_UNKNOWN_{(int)code}"
            };
        }

        /// <summary>
        /// Maps a <see cref="PluginErrorCode"/> to its corresponding <see cref="E2EErrorCode"/>.
        /// </summary>
        /// <param name="code">The plugin error code.</param>
        /// <returns>The corresponding E2E error code, or <see cref="E2EErrorCode.None"/> if no mapping exists.</returns>
        public static E2EErrorCode ToE2EErrorCode(this PluginErrorCode code)
        {
            return code switch
            {
                PluginErrorCode.None => E2EErrorCode.None,
                PluginErrorCode.Unknown => E2EErrorCode.None,
                PluginErrorCode.ValidationFailed => E2EErrorCode.ConfigInvalid,
                PluginErrorCode.NotFound => E2EErrorCode.None,
                PluginErrorCode.Unauthorized => E2EErrorCode.AuthMissing,
                PluginErrorCode.AuthenticationExpired => E2EErrorCode.AuthMissing,
                PluginErrorCode.RateLimited => E2EErrorCode.RateLimited,
                PluginErrorCode.Timeout => E2EErrorCode.ApiTimeout,
                PluginErrorCode.Cancelled => E2EErrorCode.Cancelled,
                PluginErrorCode.ProviderUnavailable => E2EErrorCode.ProviderUnavailable,
                PluginErrorCode.Unsupported => E2EErrorCode.None,
                PluginErrorCode.QuotaExceeded => E2EErrorCode.RateLimited,
                PluginErrorCode.Conflict => E2EErrorCode.None,
                PluginErrorCode.ParsingFailed => E2EErrorCode.None,
                PluginErrorCode.NetworkFailure => E2EErrorCode.ProviderUnavailable,
                _ => E2EErrorCode.None
            };
        }

        /// <summary>
        /// Creates a new <see cref="PluginError"/> with the E2E error code added to metadata.
        /// </summary>
        /// <param name="error">The original plugin error.</param>
        /// <param name="e2eCode">The E2E error code to add.</param>
        /// <returns>A new PluginError with the E2E code in metadata.</returns>
        public static PluginError WithE2EErrorCode(this PluginError error, E2EErrorCode e2eCode)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            if (e2eCode == E2EErrorCode.None)
            {
                return error;
            }

            return error.WithMetadata(new Dictionary<string, string>
            {
                [MetadataKey] = e2eCode.ToManifestString()
            });
        }

        /// <summary>
        /// Creates a new <see cref="PluginError"/> with the E2E error code derived from the plugin error code.
        /// </summary>
        /// <param name="error">The original plugin error.</param>
        /// <returns>A new PluginError with the derived E2E code in metadata.</returns>
        public static PluginError WithDerivedE2EErrorCode(this PluginError error)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            var e2eCode = error.Code.ToE2EErrorCode();
            return error.WithE2EErrorCode(e2eCode);
        }

        /// <summary>
        /// Gets the E2E error code from plugin error metadata, if present.
        /// </summary>
        /// <param name="error">The plugin error.</param>
        /// <returns>The E2E error code string, or null if not present.</returns>
        public static string? GetE2EErrorCode(this PluginError error)
        {
            if (error?.Metadata is null)
            {
                return null;
            }

            return error.Metadata.TryGetValue(MetadataKey, out var code) ? code : null;
        }

        /// <summary>
        /// Creates a PluginError with both the plugin error code and derived E2E error code.
        /// </summary>
        /// <param name="code">The plugin error code.</param>
        /// <param name="message">Optional error message.</param>
        /// <param name="exception">Optional underlying exception.</param>
        /// <param name="metadata">Optional additional metadata.</param>
        /// <returns>A PluginError with E2E code in metadata.</returns>
        public static PluginError CreateWithE2ECode(
            PluginErrorCode code,
            string? message = null,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var error = new PluginError(code, message, exception, metadata);
            return error.WithDerivedE2EErrorCode();
        }

        /// <summary>
        /// Creates a PluginError with an explicit E2E error code.
        /// </summary>
        /// <param name="code">The plugin error code.</param>
        /// <param name="e2eCode">The explicit E2E error code.</param>
        /// <param name="message">Optional error message.</param>
        /// <param name="exception">Optional underlying exception.</param>
        /// <param name="metadata">Optional additional metadata.</param>
        /// <returns>A PluginError with explicit E2E code in metadata.</returns>
        public static PluginError CreateWithE2ECode(
            PluginErrorCode code,
            E2EErrorCode e2eCode,
            string? message = null,
            Exception? exception = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var error = new PluginError(code, message, exception, metadata);
            return error.WithE2EErrorCode(e2eCode);
        }
    }
}
