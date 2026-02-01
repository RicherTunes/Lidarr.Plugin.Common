// <copyright file="ClaudeCodeDetectorTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Lidarr.Plugin.Common.Subprocess;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

/// <summary>
/// Unit tests for <see cref="ClaudeCodeDetector"/>.
/// Tests CLI detection via PATH lookup and filesystem fallbacks.
/// </summary>
/// <remarks>
/// Note: These tests verify the PATH lookup behavior via mocked ICliRunner.
/// The filesystem fallback uses File.Exists directly, so tests may find the
/// actual CLI if it's installed on the test machine. Tests are designed to
/// verify correct behavior regardless of actual installation state.
/// </remarks>
public class ClaudeCodeDetectorTests
{
    private readonly Mock<ICliRunner> _mockCliRunner;
    private readonly ClaudeCodeDetector _detector;

    public ClaudeCodeDetectorTests()
    {
        _mockCliRunner = new Mock<ICliRunner>();
        _detector = new ClaudeCodeDetector(_mockCliRunner.Object);
    }

    #region PATH Detection

    [Fact]
    public async Task FindClaudeCliAsync_FoundInPath_AttemptsPathLookup()
    {
        // Arrange - Mock successful PATH lookup (which/where returns path)
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.Is<string>(c => c == "which" || c == "where"),
                It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "claude"),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "/usr/local/bin/claude\n",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(50)
            });

        // Act
        var result = await _detector.FindClaudeCliAsync();

        // Assert - Verify PATH lookup was attempted with correct parameters
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.Is<string>(c => c == "which" || c == "where"),
            It.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "claude"),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FindClaudeCliAsync_MultiplePathsReturned_AttemptsPathLookup()
    {
        // Arrange - where command on Windows can return multiple paths
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "/usr/local/bin/claude\n/opt/homebrew/bin/claude\n",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(30)
            });

        // Act
        var result = await _detector.FindClaudeCliAsync();

        // Assert - verify PATH lookup was attempted
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.Is<string>(c => c == "which" || c == "where"),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FindClaudeCliAsync_NotInPath_FallsBackToFilesystem()
    {
        // Arrange - PATH lookup fails (command returns non-zero)
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "claude not found",
                Duration = TimeSpan.FromMilliseconds(20)
            });

        // Act
        var result = await _detector.FindClaudeCliAsync();

        // Assert - PATH lookup was attempted
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Result depends on actual filesystem state (CLI may or may not be installed)
        // If result is not null, it should be a valid path
        if (result != null)
        {
            Assert.True(File.Exists(result), $"Returned path should exist: {result}");
        }
    }

    [Fact]
    public async Task FindClaudeCliAsync_EmptyPathOutput_FallsBackToFilesystem()
    {
        // Arrange - PATH lookup succeeds but returns empty output
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        var result = await _detector.FindClaudeCliAsync();

        // Assert - PATH lookup was attempted
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Result depends on filesystem state
        if (result != null)
        {
            Assert.True(File.Exists(result));
        }
    }

    [Fact]
    public async Task FindClaudeCliAsync_WhitespaceOnlyOutput_FallsBackToFilesystem()
    {
        // Arrange
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = "   \n\t  ",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        var result = await _detector.FindClaudeCliAsync();

        // Assert - PATH lookup was attempted (whitespace paths are not valid)
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Whitespace-only output should fall back to filesystem
        if (result != null)
        {
            Assert.True(File.Exists(result));
        }
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task FindClaudeCliAsync_CliRunnerThrowsGenericException_FallsBackToFilesystem()
    {
        // Arrange
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("which command failed"));

        // Act - Should not throw
        var result = await _detector.FindClaudeCliAsync();

        // Assert - Exception is caught, fallback to filesystem
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        if (result != null)
        {
            Assert.True(File.Exists(result));
        }
    }

    [Fact]
    public async Task FindClaudeCliAsync_CliRunnerThrowsCliException_FallsBackToFilesystem()
    {
        // Arrange
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CliException("which", Array.Empty<string>(), 127, "", "which: command not found"));

        // Act - Should not throw
        var result = await _detector.FindClaudeCliAsync();

        // Assert
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        if (result != null)
        {
            Assert.True(File.Exists(result));
        }
    }

    [Fact]
    public async Task FindClaudeCliAsync_CancellationRequested_PropagatesCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert - Cancellation should propagate, not be caught
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _detector.FindClaudeCliAsync(cts.Token));
    }

    [Fact]
    public async Task FindClaudeCliAsync_TimeoutException_FallsBackToFilesystem()
    {
        // Arrange - Timeout during PATH lookup
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PATH lookup timed out"));

        // Act - Should not throw
        var result = await _detector.FindClaudeCliAsync();

        // Assert - Timeout is caught and fallback to filesystem
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);

        if (result != null)
        {
            Assert.True(File.Exists(result));
        }
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullCliRunner_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeDetector(null!));
    }

    #endregion

    #region Options Verification

    [Fact]
    public async Task FindClaudeCliAsync_UsesCorrectTimeout()
    {
        // Arrange
        CliRunnerOptions? capturedOptions = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, _, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        await _detector.FindClaudeCliAsync();

        // Assert - Detector should use 5-second timeout for PATH lookup
        Assert.NotNull(capturedOptions);
        Assert.Equal(TimeSpan.FromSeconds(5), capturedOptions!.Timeout);
        Assert.False(capturedOptions.ThrowOnNonZeroExitCode);
    }

    [Fact]
    public async Task FindClaudeCliAsync_PassesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        CancellationToken capturedToken = default;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, _, _, ct) => capturedToken = ct)
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        await _detector.FindClaudeCliAsync(cts.Token);

        // Assert
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task FindClaudeCliAsync_UsesCorrectArguments()
    {
        // Arrange
        IReadOnlyList<string>? capturedArgs = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (_, args, _, _) => capturedArgs = args)
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        await _detector.FindClaudeCliAsync();

        // Assert - Should search for "claude"
        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs);
        Assert.Equal("claude", capturedArgs[0]);
    }

    [Fact]
    public async Task FindClaudeCliAsync_UsesWhichOrWhereCommand()
    {
        // Arrange
        string? capturedCommand = null;

        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CliRunnerOptions?, CancellationToken>(
                (cmd, _, _, _) => capturedCommand = cmd)
            .ReturnsAsync(new CliResult
            {
                ExitCode = 1,
                StandardOutput = "",
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(10)
            });

        // Act
        await _detector.FindClaudeCliAsync();

        // Assert - Should use which (Unix) or where (Windows)
        Assert.NotNull(capturedCommand);
        Assert.True(capturedCommand == "which" || capturedCommand == "where",
            $"Expected 'which' or 'where', got '{capturedCommand}'");
    }

    #endregion
}
