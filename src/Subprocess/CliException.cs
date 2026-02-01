using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Exception thrown when CLI execution fails.
/// </summary>
public sealed class CliException : Exception
{
    /// <summary>The command that was executed.</summary>
    public string Command { get; }

    /// <summary>The arguments passed to the command.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Exit code if process exited, null if terminated.</summary>
    public int? ExitCode { get; }

    /// <summary>Captured standard error output.</summary>
    public string StandardError { get; }

    public CliException(string command, IReadOnlyList<string> arguments, int? exitCode, string standardError, string message)
        : base(message)
    {
        Command = command;
        Arguments = arguments;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public CliException(string command, IReadOnlyList<string> arguments, int? exitCode, string standardError, string message, Exception innerException)
        : base(message, innerException)
    {
        Command = command;
        Arguments = arguments;
        ExitCode = exitCode;
        StandardError = standardError;
    }
}
