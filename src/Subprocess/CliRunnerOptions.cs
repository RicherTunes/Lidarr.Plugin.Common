using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Configuration options for CLI command execution.
/// </summary>
public sealed record CliRunnerOptions
{
    /// <summary>
    /// Working directory for the process. Null uses current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables to set for the process. Null inherits parent.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Maximum execution time before timeout. Null means no timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Time to wait for graceful shutdown after cancellation before force kill.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to throw CliException on non-zero exit code.
    /// Default: true.
    /// </summary>
    public bool ThrowOnNonZeroExitCode { get; init; } = true;

    /// <summary>
    /// Text to pipe to stdin. Null means no stdin input.
    /// Use this to pass sensitive data (like prompts) without exposing them in command-line arguments.
    /// </summary>
    public string? StandardInput { get; init; }
}
