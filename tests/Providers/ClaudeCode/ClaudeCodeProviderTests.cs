// <copyright file="ClaudeCodeProviderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Lidarr.Plugin.Common.Subprocess;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

/// <summary>
/// Unit tests for <see cref="ClaudeCodeProvider"/>.
/// Tests health check, completion, streaming, and error mapping.
/// </summary>
/// <remarks>
/// These tests use mocked ICliRunner. Since ClaudeCodeDetector uses File.Exists
/// for filesystem fallback, and the actual Claude CLI may be installed on the
/// test machine, the tests are designed to work with any discovered CLI path.
/// Mock setups use flexible matchers to handle the actual system path.
/// </remarks>
public class ClaudeCodeProviderTests : IDisposable
{
    private readonly Mock<ICliRunner> _mockCliRunner;
    private readonly ClaudeCodeDetector _detector;
    private readonly ClaudeCodeProvider _provider;
    private readonly ClaudeCodeSettings _settings;

    public ClaudeCodeProviderTests()
    {
        _mockCliRunner = new Mock<ICliRunner>();
        _detector = new ClaudeCodeDetector(_mockCliRunner.Object);
        _settings = new ClaudeCodeSettings();
        _provider = new ClaudeCodeProvider(_mockCliRunner.Object, _detector, _settings);
    }

    public void Dispose()
    {
        // Clear capability cache between tests to ensure isolation
        ClaudeCodeCapabilities.InvalidateCache();
    }

    #region Capabilities and Metadata

    [Fact]
    public void ProviderId_ReturnsClaudeCode()
    {
        Assert.Equal("claude-code", _provider.ProviderId);
    }

    [Fact]
    public void DisplayName_ReturnsClaudeCodeCli()
    {
        Assert.Equal("Claude Code CLI", _provider.DisplayName);
    }

    [Fact]
    public void Capabilities_HasCorrectFlags()
    {
        var caps = _provider.Capabilities;

        Assert.True(caps.Flags.HasFlag(LlmCapabilityFlags.TextCompletion));
        Assert.True(caps.Flags.HasFlag(LlmCapabilityFlags.SystemPrompt));
        Assert.True(caps.Flags.HasFlag(LlmCapabilityFlags.ExtendedThinking));
        Assert.False(caps.UsesOpenAiCompatibleApi);
    }

    [Fact]
    public void Capabilities_HasCorrectTokenLimits()
    {
        var caps = _provider.Capabilities;

        Assert.Equal(200_000, caps.MaxContextTokens);
        Assert.Equal(8_192, caps.MaxOutputTokens);
    }

    [Fact]
    public void Capabilities_HasSupportedModels()
    {
        var caps = _provider.Capabilities;

        Assert.Contains("sonnet", caps.SupportedModels);
        Assert.Contains("opus", caps.SupportedModels);
        Assert.Contains("haiku", caps.SupportedModels);
    }

    #endregion

    #region Health Check - CLI Not Found

    [Fact]
    public async Task CheckHealthAsync_CliNotFound_AttemptsPathLookup()
    {
        // Arrange - Setup PATH lookup to fail
        // Note: If Claude CLI is actually installed via filesystem,
        // it will still be found. This test verifies PATH lookup is attempted.
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.Is<string>(c => c == "which" || c == "where"),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "not found",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Also setup mocks for any CLI calls that might happen if CLI is found via filesystem
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "--version"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "claude 1.0.0",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = """{"type":"result","result":"OK","session_id":"test","is_error":false,"num_turns":1,"duration_ms":100}""",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(100)
            });

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert - PATH lookup was attempted
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.Is<string>(c => c == "which" || c == "where"),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // If CLI not found via PATH or filesystem, result is unhealthy
        // If found, the test passes (verifying PATH lookup was attempted)
        if (!result.IsHealthy && result.StatusMessage != null)
        {
            Assert.Contains("not installed", result.StatusMessage);
        }
    }

    #endregion

    #region Health Check - Version Check

    [Fact]
    public async Task CheckHealthAsync_VersionCheckFails_ReturnsUnhealthy()
    {
        // Arrange - Setup to find CLI, then fail version check
        SetupAllCliCallsForPath();

        // Override version check to fail
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "--version"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "Command not recognized",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.NotNull(result.StatusMessage);
        Assert.Contains("CLI error", result.StatusMessage);
    }

    [Fact]
    public async Task CheckHealthAsync_VersionCheckTimesOut_ReturnsUnhealthy()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "--version"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Version check timed out"));

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Contains("timed out", result.StatusMessage!);
    }

    #endregion

    #region Health Check - Auth Check

    [Fact]
    public async Task CheckHealthAsync_AuthCheckFails_ReturnsUnhealthyWithLoginMessage()
    {
        // Arrange
        SetupAllCliCallsForPath();
        SetupVersionCheckSuccess();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "Error: authentication failed",
                Duration = TimeSpan.FromSeconds(1)
            });

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Contains("login", result.StatusMessage!);
    }

    [Fact]
    public async Task CheckHealthAsync_AuthCheckTimesOut_ReturnsDegraded()
    {
        // Arrange
        SetupAllCliCallsForPath();
        SetupVersionCheckSuccess();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Auth check timed out"));

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert
        Assert.True(result.IsHealthy); // Degraded is still healthy
        Assert.Contains("Degraded", result.StatusMessage!);
        Assert.Contains("timed out", result.StatusMessage!);
    }

    [Fact]
    public async Task CheckHealthAsync_AuthCheckSucceeds_ReturnsHealthy()
    {
        // Arrange
        SetupAllCliCallsForPath();
        SetupVersionCheckSuccess();
        SetupCapabilityProbeSuccess();
        SetupAuthCheckSuccess();

        // Act
        var result = await _provider.CheckHealthAsync();

        // Assert
        Assert.True(result.IsHealthy);
    }

    #endregion

    #region CompleteAsync - Success

    [Fact]
    public async Task CompleteAsync_Success_ReturnsLlmResponse()
    {
        // Arrange
        SetupAllCliCallsForPath();
        SetupCompletionSuccess();

        // Act
        var response = await _provider.CompleteAsync(new LlmRequest { Prompt = "test" });

        // Assert
        Assert.Equal("Test response", response.Content);
        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(5, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_WithSystemPrompt_PassesAppendSystemPromptArg()
    {
        // Arrange
        SetupAllCliCallsForPath();
        IReadOnlyList<string>? capturedArgs = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(CreateSuccessCliResult());

        // Act
        await _provider.CompleteAsync(new LlmRequest
        {
            Prompt = "test prompt",
            SystemPrompt = "You are a helpful assistant"
        });

        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Contains("--append-system-prompt", capturedArgs);
        Assert.Contains("You are a helpful assistant", capturedArgs);
    }

    [Fact]
    public async Task CompleteAsync_WithModel_PassesModelArg()
    {
        // Arrange
        SetupAllCliCallsForPath();
        IReadOnlyList<string>? capturedArgs = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(CreateSuccessCliResult());

        // Act
        await _provider.CompleteAsync(new LlmRequest
        {
            Prompt = "test",
            Model = "opus"
        });

        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Contains("--model", capturedArgs);
        Assert.Contains("opus", capturedArgs);
    }

    [Fact]
    public async Task CompleteAsync_RequiredArgsPresent()
    {
        // Arrange
        SetupAllCliCallsForPath();
        SetupCapabilityProbeSuccess();
        IReadOnlyList<string>? capturedArgs = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(CreateSuccessCliResult());

        // Act
        await _provider.CompleteAsync(new LlmRequest { Prompt = "hello" });

        // Assert - Required args: -p, --output-format json, --max-turns 1
        Assert.NotNull(capturedArgs);
        Assert.Contains("-p", capturedArgs);
        Assert.Contains("hello", capturedArgs);
        Assert.Contains("--output-format", capturedArgs);
        Assert.Contains("json", capturedArgs);
        Assert.Contains("--max-turns", capturedArgs);
        Assert.Contains("1", capturedArgs);
    }

    #endregion

    #region CompleteAsync - Error Mapping

    [Fact]
    public async Task CompleteAsync_CliNotFound_AttemptsDetection()
    {
        // Arrange - Setup PATH lookup to fail
        // Note: If CLI is found via filesystem, test still passes
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.Is<string>(c => c == "which" || c == "where"),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "not found",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Setup mocks for CLI calls if found via filesystem
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = """{"type":"result","result":"OK","session_id":"test","is_error":false,"num_turns":1,"duration_ms":100}""",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(100)
            });

        // Act - Attempt completion
        try
        {
            var result = await _provider.CompleteAsync(new LlmRequest { Prompt = "test" });
            // If we get here, CLI was found via filesystem and completion succeeded
            Assert.Equal("OK", result.Content);
        }
        catch (ProviderException ex) when (ex.ErrorCode == LlmErrorCode.ProviderUnavailable)
        {
            // Expected when CLI is not installed
            Assert.Contains("not found", ex.Message);
        }

        // Assert - PATH lookup was attempted
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.Is<string>(c => c == "which" || c == "where"),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_AuthError_ThrowsAuthenticationException()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "Error: authentication failed",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<AuthenticationException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }));

        Assert.Contains("authenticate", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task CompleteAsync_RateLimitError_ThrowsRateLimitException()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "Error: rate limit exceeded",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RateLimitException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }));

        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task CompleteAsync_ExitCode127_ThrowsProviderUnavailable()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 127,
                StandardOutput = "",
                StandardError = "command not found",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }));

        Assert.Equal(LlmErrorCode.ProviderUnavailable, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_GenericError_ThrowsProviderException()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "Unknown error occurred",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }));

        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_Timeout_ThrowsProviderExceptionWithTimeoutCode()
    {
        // Arrange
        SetupAllCliCallsForPath();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }));

        Assert.Equal(LlmErrorCode.Timeout, ex.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_Cancellation_PropagatesOperationCanceledException()
    {
        // Arrange
        SetupAllCliCallsForPath();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _provider.CompleteAsync(new LlmRequest { Prompt = "test" }, cts.Token));
    }

    #endregion

    #region StreamAsync

    [Fact]
    public void StreamAsync_ReturnsAsyncEnumerable()
    {
        // Act
        var result = _provider.StreamAsync(new LlmRequest { Prompt = "test" });

        // Assert - v2 supports streaming, returns async enumerable
        Assert.NotNull(result);
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullCliRunner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeCodeProvider(null!, _detector));
    }

    [Fact]
    public void Constructor_NullDetector_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeCodeProvider(_mockCliRunner.Object, null!));
    }

    [Fact]
    public void Constructor_NullSettings_UsesDefaults()
    {
        // Act - should not throw
        var provider = new ClaudeCodeProvider(_mockCliRunner.Object, _detector, null);

        // Assert
        Assert.NotNull(provider);
        Assert.Equal("claude-code", provider.ProviderId);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Sets up the mock to handle PATH lookup, returning a fictional path.
    /// The detector will attempt PATH lookup first, and this mock ensures
    /// a consistent path is returned for subsequent mock setups.
    /// </summary>
    private void SetupAllCliCallsForPath()
    {
        // First, try to find a real CLI path for consistency
        // Mock the PATH lookup to return a consistent path
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.Is<string>(c => c == "which" || c == "where"),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                // Return a path that actually exists on the test system
                // Since Claude is installed, use a plausible path
                StandardOutput = GetMockCliPath() + "\n",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });
    }

    /// <summary>
    /// Gets a CLI path to use for mocking. If Claude CLI is actually installed,
    /// returns a path that will be found. Otherwise returns a mock path.
    /// </summary>
    private static string GetMockCliPath()
    {
        // Check common installation locations
        var possiblePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude"),
            "/usr/local/bin/claude",
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Return a fictional path (tests will still work with mocks)
        return OperatingSystem.IsWindows()
            ? @"C:\mock\claude.exe"
            : "/mock/claude";
    }

    private void SetupVersionCheckSuccess()
    {
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "--version"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "claude-code 1.0.0",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(50)
            });
    }

    private void SetupCapabilityProbeSuccess()
    {
        // Mock --help command for capability probing
        var helpOutput = """
            Usage: claude [options] [prompt]

            Options:
              --allowedTools <tools>      Restrict tool access
              --max-turns <n>             Maximum conversation turns
              --output-format <fmt>       Output format (text, json, stream-json)
              --append-system-prompt <p>  Append to system prompt
              --model <model>             Model to use
              --no-session-persistence    Don't persist session
            """;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "--help"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = helpOutput,
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(50)
            });
    }

    private void SetupAuthCheckSuccess()
    {
        var jsonResponse = """
        {
            "type": "result",
            "result": "OK",
            "session_id": "health-check",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 500
        }
        """;

        // This matches auth check (has -p and --max-turns)
        // But NOT regular completion (which we set up separately)
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a =>
                    a.Contains("-p") &&
                    a.Contains("--max-turns") &&
                    a.Any(x => x.Contains("OK"))),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = jsonResponse,
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(500)
            });
    }

    private void SetupCompletionSuccess()
    {
        var jsonResponse = """
        {
            "type": "result",
            "result": "Test response",
            "session_id": "test-123",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100,
            "usage": {
                "input_tokens": 10,
                "output_tokens": 5
            }
        }
        """;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("-p")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = jsonResponse,
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(100)
            });
    }

    private static CliResult CreateSuccessCliResult()
    {
        return new CliResult
        {
            ExitCode = 0,
            StandardOutput = """
            {
                "type": "result",
                "result": "OK",
                "session_id": "test",
                "is_error": false,
                "num_turns": 1,
                "duration_ms": 100
            }
            """,
            StandardError = "",
            Duration = TimeSpan.FromMilliseconds(100)
        };
    }

    #endregion
}
