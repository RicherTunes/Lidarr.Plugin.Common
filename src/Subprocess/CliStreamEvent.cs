using System;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Events emitted during streaming CLI execution.
/// </summary>
public abstract record CliStreamEvent
{
    private CliStreamEvent() { }

    /// <summary>Process has started.</summary>
    public sealed record Started(int ProcessId) : CliStreamEvent;

    /// <summary>Line received on stdout.</summary>
    public sealed record StandardOutput(string Text) : CliStreamEvent;

    /// <summary>Line received on stderr.</summary>
    public sealed record StandardError(string Text) : CliStreamEvent;

    /// <summary>Process has exited.</summary>
    public sealed record Exited(int ExitCode, TimeSpan Duration) : CliStreamEvent;
}
