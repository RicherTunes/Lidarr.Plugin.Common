using System;

namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Non-generic operation result signalling success or failure.
    /// </summary>
    public readonly struct PluginOperationResult
    {
        private PluginOperationResult(bool isSuccess, PluginError? error)
        {
            IsSuccess = isSuccess;
            Error = error;
        }

        /// <summary>
        /// Indicates whether the operation succeeded.
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// Gets the error when <see cref="IsSuccess"/> is false.
        /// </summary>
        public PluginError? Error { get; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static PluginOperationResult Success() => new(true, null);

        /// <summary>
        /// Creates a failing result.
        /// </summary>
        public static PluginOperationResult Failure(PluginError error) => new(false, error ?? throw new ArgumentNullException(nameof(error)));

        /// <summary>
        /// Throws <see cref="PluginOperationException"/> when the result represents a failure.
        /// </summary>
        public void EnsureSuccess()
        {
            if (!IsSuccess)
            {
                throw new PluginOperationException(Error ?? new PluginError(PluginErrorCode.Unknown));
            }
        }

        public void Deconstruct(out bool isSuccess, out PluginError? error)
        {
            isSuccess = IsSuccess;
            error = Error;
        }
    }

    /// <summary>
    /// Operation result carrying a value when successful.
    /// </summary>
    public readonly struct PluginOperationResult<T>
    {
        private PluginOperationResult(bool isSuccess, T? value, PluginError? error)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
        }

        /// <summary>
        /// Indicates whether the operation succeeded.
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// Gets the value when <see cref="IsSuccess"/> is true.
        /// </summary>
        public T? Value { get; }

        /// <summary>
        /// Gets the error when <see cref="IsSuccess"/> is false.
        /// </summary>
        public PluginError? Error { get; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static PluginOperationResult<T> Success(T value) => new(true, value, null);

        /// <summary>
        /// Creates a failing result.
        /// </summary>
        public static PluginOperationResult<T> Failure(PluginError error) => new(false, default, error ?? throw new ArgumentNullException(nameof(error)));

        /// <summary>
        /// Gets the value or throws <see cref="PluginOperationException"/> when the result is a failure.
        /// </summary>
        public T GetValueOrThrow()
        {
            if (!IsSuccess)
            {
                throw new PluginOperationException(Error ?? new PluginError(PluginErrorCode.Unknown));
            }

            return Value!;
        }

        /// <summary>
        /// Gets the value or the provided fallback when the result is a failure.
        /// </summary>
        public T GetValueOrDefault(T fallback = default!) => IsSuccess ? Value! : fallback;

        public void Deconstruct(out bool isSuccess, out T? value, out PluginError? error)
        {
            isSuccess = IsSuccess;
            value = Value;
            error = Error;
        }
    }

    /// <summary>
    /// Exception used when a failed <see cref="PluginOperationResult"/> is escalated.
    /// </summary>
    public sealed class PluginOperationException : Exception
    {
        public PluginOperationException(PluginError error)
            : base(error?.Message ?? "Plugin operation failed.", error?.Exception)
        {
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        /// <summary>
        /// Gets the underlying plugin error.
        /// </summary>
        public PluginError Error { get; }
    }
}
