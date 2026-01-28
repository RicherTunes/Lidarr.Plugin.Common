using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;

namespace Lidarr.Plugin.Common.Subprocess;

/// <summary>
/// CliWrap-based implementation of <see cref="ICliRunner"/>.
/// Handles async output capture, graceful cancellation, and process tree cleanup.
/// </summary>
public sealed class CliRunner : ICliRunner
{
    /// <inheritdoc />
    public async Task<CliResult> ExecuteAsync(
        string command,
        IReadOnlyList<string> arguments,
        CliRunnerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);

        options ??= new CliRunnerOptions();

        // Dual-token pattern: graceful cancellation with forced fallback
        using var forcefulCts = new CancellationTokenSource();

        // When external cancellation triggers, schedule forced cancellation as fallback
        await using var link = cancellationToken.Register(() =>
            forcefulCts.CancelAfter(options.GracefulShutdownTimeout));

        // Apply timeout (includes graceful shutdown window)
        if (options.Timeout.HasValue)
        {
            forcefulCts.CancelAfter(options.Timeout.Value + options.GracefulShutdownTimeout);
        }

        var cmd = BuildCommand(command, arguments, options);

        // Create linked token that cancels when either the external token or forceful CTS cancels
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(forcefulCts.Token, cancellationToken);

        try
        {
            var result = await cmd
                .WithValidation(CommandResultValidation.None) // We handle validation ourselves
                .ExecuteBufferedAsync(linkedCts.Token);

            var cliResult = new CliResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                Duration = result.RunTime
            };

            if (options.ThrowOnNonZeroExitCode && result.ExitCode != 0)
            {
                throw new CliException(
                    command,
                    arguments,
                    result.ExitCode,
                    result.StandardError,
                    $"Command '{command}' failed with exit code {result.ExitCode}");
            }

            return cliResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // External cancellation - propagate
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout - convert to TimeoutException
            throw new TimeoutException(
                $"Command '{command}' timed out after {options.Timeout}");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CliStreamEvent> StreamAsync(
        string command,
        IReadOnlyList<string> arguments,
        CliRunnerOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);

        options ??= new CliRunnerOptions();

        // Dual-token pattern for graceful shutdown
        using var forcefulCts = new CancellationTokenSource();
        await using var link = cancellationToken.Register(() =>
            forcefulCts.CancelAfter(options.GracefulShutdownTimeout));

        if (options.Timeout.HasValue)
        {
            forcefulCts.CancelAfter(options.Timeout.Value + options.GracefulShutdownTimeout);
        }

        var cmd = BuildCommand(command, arguments, options);
        var startTime = DateTimeOffset.UtcNow;
        var stderr = new StringBuilder();

        // Create linked token that cancels when either the external token or forceful CTS cancels
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(forcefulCts.Token, cancellationToken);

        await foreach (var cmdEvent in cmd.ListenAsync(linkedCts.Token))
        {
            switch (cmdEvent)
            {
                case StartedCommandEvent started:
                    yield return new CliStreamEvent.Started(started.ProcessId);
                    break;

                case StandardOutputCommandEvent output:
                    yield return new CliStreamEvent.StandardOutput(output.Text);
                    break;

                case StandardErrorCommandEvent error:
                    stderr.AppendLine(error.Text);
                    yield return new CliStreamEvent.StandardError(error.Text);
                    break;

                case ExitedCommandEvent exited:
                    var duration = DateTimeOffset.UtcNow - startTime;

                    if (options.ThrowOnNonZeroExitCode && exited.ExitCode != 0)
                    {
                        throw new CliException(
                            command,
                            arguments,
                            exited.ExitCode,
                            stderr.ToString(),
                            $"Command '{command}' failed with exit code {exited.ExitCode}");
                    }

                    yield return new CliStreamEvent.Exited(exited.ExitCode, duration);
                    break;
            }
        }
    }

    private static Command BuildCommand(string command, IReadOnlyList<string> arguments, CliRunnerOptions options)
    {
        Command cmd;

        // Windows .cmd/.bat files must be executed via cmd.exe /c
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows) &&
            (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // Build combined args: /c "command" arg1 arg2 ...
            var cmdArgs = new List<string> { "/c", command };
            cmdArgs.AddRange(arguments);
            cmd = Cli.Wrap("cmd.exe").WithArguments(cmdArgs);
        }
        else
        {
            cmd = Cli.Wrap(command).WithArguments(arguments);
        }

        if (options.WorkingDirectory is not null)
        {
            cmd = cmd.WithWorkingDirectory(options.WorkingDirectory);
        }

        if (options.EnvironmentVariables is not null)
        {
            cmd = cmd.WithEnvironmentVariables(options.EnvironmentVariables);
        }

        return cmd;
    }
}
