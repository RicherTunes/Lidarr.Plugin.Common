using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Represents a structured error returned by a plugin operation.
    /// </summary>
    public sealed record PluginError
    {
        public PluginError(PluginErrorCode code, string? message = null, Exception? exception = null, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Code = code;
            Message = message;
            Exception = exception;
            Metadata = metadata ?? new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets the error code describing the failure category.
        /// </summary>
        public PluginErrorCode Code { get; }

        /// <summary>
        /// Gets an optional human-readable message.
        /// </summary>
        public string? Message { get; }

        /// <summary>
        /// Gets the underlying exception (if one was captured).
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Gets optional metadata describing the failure (provider status, request id, etc.).
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>
        /// Factory helper for wrapping an exception with the <see cref="PluginErrorCode.Unknown"/> code.
        /// </summary>
        public static PluginError FromException(Exception exception, string? message = null)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return new PluginError(PluginErrorCode.Unknown, message ?? exception.Message, exception);
        }

        /// <summary>
        /// Creates a copy of this error with extra metadata merged in.
        /// Existing keys are overwritten by the supplied metadata.
        /// </summary>
        public PluginError WithMetadata(IReadOnlyDictionary<string, string> metadata)
        {
            if (metadata is null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            var merged = new Dictionary<string, string>(Metadata);
            foreach (var kvp in metadata)
            {
                merged[kvp.Key] = kvp.Value;
            }

            return new PluginError(Code, Message, Exception, merged);
        }
    }
}
