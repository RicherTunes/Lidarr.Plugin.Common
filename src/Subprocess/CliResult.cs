using System;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Result of a CLI command execution.
/// </summary>
public sealed record CliResult
{
    /// <summary>Process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured standard error.</summary>
    public required string StandardError { get; init; }

    /// <summary>Total execution duration.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Whether the process exited with code 0.</summary>
    public bool IsSuccess => ExitCode == 0;
}
