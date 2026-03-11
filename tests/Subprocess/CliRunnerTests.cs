using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Subprocess;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Subprocess;

public class CliRunnerTests
{
    private readonly ICliRunner _runner = new CliRunner();

    [Fact]
    public async Task ExecuteAsync_EchoCommand_CapturesOutput()
    {
        // Arrange
        var (command, args) = GetEchoCommand("hello world");

        // Act
        var result = await _runner.ExecuteAsync(command, args);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsSuccess);
        Assert.Contains("hello world", result.StandardOutput);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ThrowsCliException()
    {
        // Arrange
        var (command, args) = GetExitCodeCommand(42);
        var options = new CliRunnerOptions { ThrowOnNonZeroExitCode = true };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<CliException>(
            () => _runner.ExecuteAsync(command, args, options));

        Assert.Equal(42, ex.ExitCode);
        Assert.Equal(command, ex.Command);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReturnsResult_WhenThrowDisabled()
    {
        // Arrange
        var (command, args) = GetExitCodeCommand(1);
        var options = new CliRunnerOptions { ThrowOnNonZeroExitCode = false };

        // Act
        var result = await _runner.ExecuteAsync(command, args, options);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ThrowsTimeoutException()
    {
        // Arrange
        var (command, args) = GetSleepCommand(30); // 30 second sleep
        var options = new CliRunnerOptions
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(50)
        };

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.ExecuteAsync(command, args, options));
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var (command, args) = GetSleepCommand(30);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _runner.ExecuteAsync(command, args, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task StreamAsync_EchoCommand_YieldsEvents()
    {
        // Arrange
        var (command, args) = GetEchoCommand("streaming test");
        var events = new List<CliStreamEvent>();

        // Act
        await foreach (var evt in _runner.StreamAsync(command, args))
        {
            events.Add(evt);
        }

        // Assert
        Assert.Contains(events, e => e is CliStreamEvent.Started);
        Assert.Contains(events, e => e is CliStreamEvent.StandardOutput { Text: var t } && t.Contains("streaming"));
        Assert.Contains(events, e => e is CliStreamEvent.Exited { ExitCode: 0 });
    }

    [Fact]
    public async Task ExecuteAsync_WithEnvironmentVariables_PassesToProcess()
    {
        // Arrange
        var (command, args) = GetEnvVarCommand("TEST_VAR");
        var options = new CliRunnerOptions
        {
            EnvironmentVariables = new Dictionary<string, string?> { ["TEST_VAR"] = "test_value" }
        };

        // Act
        var result = await _runner.ExecuteAsync(command, args, options);

        // Assert
        Assert.Contains("test_value", result.StandardOutput);
    }

    // Helper methods for cross-platform command generation
    private static (string command, string[] args) GetEchoCommand(string message)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"echo {message}" });
        return ("echo", new[] { message });
    }

    private static (string command, string[] args) GetExitCodeCommand(int exitCode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"exit {exitCode}" });
        return ("sh", new[] { "-c", $"exit {exitCode}" });
    }

    private static (string command, string[] args) GetSleepCommand(int seconds)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"ping -n {seconds + 1} 127.0.0.1 > nul" });
        return ("sleep", new[] { seconds.ToString() });
    }

    private static (string command, string[] args) GetEnvVarCommand(string varName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("cmd", new[] { "/c", $"echo %{varName}%" });
        return ("sh", new[] { "-c", $"echo ${varName}" });
    }
}
