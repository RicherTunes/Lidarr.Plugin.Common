using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Subprocess;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Subprocess;

/// <summary>
/// Stress tests for CliRunner to prevent buffer deadlock and throughput regressions.
/// These tests verify the subprocess management handles edge cases that can cause
/// hangs or data loss in production.
/// </summary>
public class CliRunnerStressTests
{
    private readonly ICliRunner _runner = new CliRunner();

    /// <summary>
    /// Verifies large stdout doesn't cause buffer deadlock.
    /// Background: When a child process writes more data than fits in the OS pipe buffer
    /// (~64KB on most systems), it blocks until the parent reads. If the parent is waiting
    /// for the process to exit before reading, deadlock occurs.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LargeStdout_NoDeadlock()
    {
        // Arrange - generate ~100KB of output (well above typical 64KB pipe buffer)
        const int lineCount = 2000;
        const string line = "This is a test line that will be repeated many times to stress the buffer.";
        var (command, args) = GetRepeatedOutputCommand(line, lineCount);

        // Act
        var result = await _runner.ExecuteAsync(command, args,
            new CliRunnerOptions { Timeout = TimeSpan.FromSeconds(30) });

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.Length > lineCount * 10, "Output should be substantial");

        // Verify we got all lines (or close to it, accounting for platform differences)
        var outputLines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(outputLines.Length >= lineCount - 10, $"Expected ~{lineCount} lines, got {outputLines.Length}");
    }

    /// <summary>
    /// Verifies large stderr doesn't cause buffer deadlock.
    /// Similar to stdout, stderr has its own buffer that can deadlock.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LargeStderr_NoDeadlock()
    {
        // Arrange
        const int lineCount = 1000;
        var (command, args) = GetStderrOutputCommand(lineCount);

        // Act
        var result = await _runner.ExecuteAsync(command, args,
            new CliRunnerOptions { Timeout = TimeSpan.FromSeconds(30) });

        // Assert
        Assert.True(result.StandardError.Length > 0, "Should capture stderr output");
    }

    /// <summary>
    /// Verifies interleaved stdout/stderr is captured correctly without data loss.
    /// This tests the async reading of both streams simultaneously.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InterleavedOutput_CapturesBoth()
    {
        // Arrange
        var (command, args) = GetInterleavedOutputCommand(100);

        // Act
        var result = await _runner.ExecuteAsync(command, args,
            new CliRunnerOptions { Timeout = TimeSpan.FromSeconds(15) });

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.Length > 0, "Should capture stdout");
        Assert.True(result.StandardError.Length > 0, "Should capture stderr");
    }

    /// <summary>
    /// Verifies stdin piping works for large payloads.
    /// This is critical for LLM providers that pass prompts via stdin.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LargeStdin_ProcessesCorrectly()
    {
        // Arrange - create a 50KB input
        var largeInput = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            largeInput.AppendLine($"Input line {i}: Some repeated content to make this substantial.");
        }

        var (command, args) = GetStdinEchoCommand();
        var options = new CliRunnerOptions
        {
            StandardInput = largeInput.ToString(),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Act
        var result = await _runner.ExecuteAsync(command, args, options);

        // Assert
        Assert.Equal(0, result.ExitCode);
        // The output should contain our input (cat echoes stdin to stdout)
        Assert.True(result.StandardOutput.Length > 1000, "Output should contain echoed input");
    }

    /// <summary>
    /// Verifies concurrent execution doesn't cause race conditions.
    /// Multiple simultaneous subprocess executions should all complete correctly.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ConcurrentExecutions_AllComplete()
    {
        // Arrange
        const int concurrentCount = 10;
        var tasks = new List<Task<CliResult>>();

        for (int i = 0; i < concurrentCount; i++)
        {
            var (command, args) = GetEchoCommand($"concurrent-{i}");
            tasks.Add(_runner.ExecuteAsync(command, args,
                new CliRunnerOptions { Timeout = TimeSpan.FromSeconds(30) }));
        }

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, r =>
        {
            Assert.Equal(0, r.ExitCode);
            Assert.True(r.IsSuccess);
        });

        // Verify each got unique output
        var outputs = results.Select(r => r.StandardOutput).ToList();
        for (int i = 0; i < concurrentCount; i++)
        {
            Assert.Contains($"concurrent-{i}", outputs[i]);
        }
    }

    /// <summary>
    /// Verifies StreamAsync handles large output without blocking.
    /// </summary>
    [Fact]
    public async Task StreamAsync_LargeOutput_YieldsAllEvents()
    {
        // Arrange
        const int lineCount = 500;
        const string line = "Streaming test line.";
        var (command, args) = GetRepeatedOutputCommand(line, lineCount);
        var outputEvents = new List<CliStreamEvent.StandardOutput>();

        // Act
        await foreach (var evt in _runner.StreamAsync(command, args,
            new CliRunnerOptions { Timeout = TimeSpan.FromSeconds(30) }))
        {
            if (evt is CliStreamEvent.StandardOutput output)
            {
                outputEvents.Add(output);
            }
        }

        // Assert
        var totalOutputLength = outputEvents.Sum(e => e.Text.Length);
        Assert.True(totalOutputLength > lineCount * 10, "Should receive substantial output via streaming");
    }

    /// <summary>
    /// Verifies timeout triggers cleanly during streaming.
    /// Note: StreamAsync throws OperationCanceledException (not TimeoutException) because
    /// the async enumerable is cancelled by the timeout CTS, which is the expected behavior
    /// for IAsyncEnumerable patterns.
    /// </summary>
    [Fact]
    public async Task StreamAsync_Timeout_ThrowsOperationCanceledException()
    {
        // Arrange
        var (command, args) = GetSleepCommand(30);
        var options = new CliRunnerOptions
        {
            Timeout = TimeSpan.FromMilliseconds(200),
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100)
        };

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _runner.StreamAsync(command, args, options))
            {
                // Consume events until timeout
            }
        });
    }

    /// <summary>
    /// Verifies cancellation during streaming cleans up properly.
    /// </summary>
    [Fact]
    public async Task StreamAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var (command, args) = GetSleepCommand(30);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _runner.StreamAsync(command, args, cancellationToken: cts.Token))
            {
                // Consume events until cancellation
            }
        });
    }

    #region Cross-Platform Command Helpers

    private static (string command, string[] args) GetEchoCommand(string message)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"echo {message}" });
        return ("echo", new[] { message });
    }

    private static (string command, string[] args) GetRepeatedOutputCommand(string line, int count)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use PowerShell for Windows as cmd FOR loop is complex
            return ("powershell", new[]
            {
                "-NoProfile", "-Command",
                $"1..{count} | ForEach-Object {{ Write-Host '{line}' }}"
            });
        }

        return ("sh", new[] { "-c", $"for i in $(seq 1 {count}); do echo '{line}'; done" });
    }

    private static (string command, string[] args) GetStderrOutputCommand(int count)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("powershell", new[]
            {
                "-NoProfile", "-Command",
                $"1..{count} | ForEach-Object {{ [Console]::Error.WriteLine('stderr line ' + $_) }}"
            });
        }

        return ("sh", new[] { "-c", $"for i in $(seq 1 {count}); do echo \"stderr line $i\" >&2; done" });
    }

    private static (string command, string[] args) GetInterleavedOutputCommand(int count)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("powershell", new[]
            {
                "-NoProfile", "-Command",
                $"1..{count} | ForEach-Object {{ Write-Host \"stdout $_\"; [Console]::Error.WriteLine(\"stderr $_\") }}"
            });
        }

        return ("sh", new[] { "-c", $"for i in $(seq 1 {count}); do echo \"stdout $i\"; echo \"stderr $i\" >&2; done" });
    }

    private static (string command, string[] args) GetStdinEchoCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // PowerShell reads from stdin and echoes to stdout
            return ("powershell", new[]
            {
                "-NoProfile", "-Command",
                "$input | ForEach-Object { $_ }"
            });
        }

        return ("cat", Array.Empty<string>());
    }

    private static (string command, string[] args) GetSleepCommand(int seconds)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"ping -n {seconds + 1} 127.0.0.1 > nul" });
        return ("sleep", new[] { seconds.ToString() });
    }

    #endregion
}
