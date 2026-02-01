using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// Executes CLI commands with timeout, cancellation, and streaming support.
/// </summary>
public interface ICliRunner
{
    /// <summary>
    /// Execute a command and return buffered output.
    /// </summary>
    /// <param name="command">The command/executable to run.</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="options">Execution options (timeout, working directory, etc.).</param>
    /// <param name="cancellationToken">Token to cancel execution.</param>
    /// <returns>Result containing exit code, stdout, stderr, and duration.</returns>
    /// <exception cref="CliException">Thrown when ThrowOnNonZeroExitCode is true and exit code is non-zero.</exception>
    /// <exception cref="TimeoutException">Thrown when execution exceeds timeout.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    Task<CliResult> ExecuteAsync(
        string command,
        IReadOnlyList<string> arguments,
        CliRunnerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a command with streaming output.
    /// </summary>
    /// <param name="command">The command/executable to run.</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="options">Execution options (timeout, working directory, etc.).</param>
    /// <param name="cancellationToken">Token to cancel execution.</param>
    /// <returns>Stream of events: Started, StandardOutput, StandardError, Exited.</returns>
    IAsyncEnumerable<CliStreamEvent> StreamAsync(
        string command,
        IReadOnlyList<string> arguments,
        CliRunnerOptions? options = null,
        CancellationToken cancellationToken = default);
}
