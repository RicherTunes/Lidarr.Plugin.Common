// <copyright file="ClaudeCodeCapabilitiesTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Lidarr.Plugin.Common.Subprocess;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

/// <summary>
/// Unit tests for <see cref="ClaudeCodeCapabilities"/> and <see cref="CapabilitySet"/>.
/// Tests --help parsing, capability detection, and argument building.
/// </summary>
public class ClaudeCodeCapabilitiesTests : IDisposable
{
    private readonly Mock<ICliRunner> _mockCliRunner;
    private readonly ClaudeCodeCapabilities _capabilities;

    public ClaudeCodeCapabilitiesTests()
    {
        _mockCliRunner = new Mock<ICliRunner>();
        _capabilities = new ClaudeCodeCapabilities(_mockCliRunner.Object);
    }

    public void Dispose()
    {
        // Clear cache between tests to ensure isolation
        ClaudeCodeCapabilities.InvalidateCache();
    }

    #region Constructor

    [Fact]
    public void Constructor_NullCliRunner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeCapabilities(null!));
    }

    #endregion

    #region GetCapabilitiesAsync - Help Parsing

    [Fact]
    public async Task GetCapabilitiesAsync_FullFeaturedHelp_DetectsAllCapabilities()
    {
        // Arrange
        var helpOutput = """
            Usage: claude [options] [prompt]

            Options:
              --allowedTools <tools>      Restrict tool access
              --allowed-tools <tools>     Alias for --allowedTools
              --max-turns <n>             Maximum conversation turns
              --output-format <fmt>       Output format (text, json, stream-json)
              --append-system-prompt <p>  Append to system prompt
              --model <model>             Model to use (sonnet, opus, haiku)
              --no-session-persistence    Don't persist session
              --verbose, -v               Verbose output
              --quiet, -q                 Quiet mode
              --input <file>              Read from stdin or file
            """;

        SetupHelpOutput(helpOutput);

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert
        Assert.True(caps.SupportsAllowedTools);
        Assert.True(caps.SupportsMaxTurns);
        Assert.True(caps.SupportsOutputFormat);
        Assert.True(caps.SupportsAppendSystemPrompt);
        Assert.True(caps.SupportsModel);
        Assert.True(caps.SupportsNoSessionPersistence);
        Assert.True(caps.SupportsStdin);
        Assert.True(caps.SupportsVerbose);
        Assert.True(caps.SupportsQuiet);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_MinimalHelp_DetectsOnlyBasicCapabilities()
    {
        // Arrange - Older CLI version with fewer features
        var helpOutput = """
            Usage: claude [options] [prompt]

            Options:
              --output-format <fmt>       Output format
              --append-system-prompt <p>  System prompt
              --model <model>             Model selection
            """;

        SetupHelpOutput(helpOutput);

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert
        Assert.False(caps.SupportsAllowedTools);
        Assert.False(caps.SupportsMaxTurns);
        Assert.True(caps.SupportsOutputFormat);
        Assert.True(caps.SupportsAppendSystemPrompt);
        Assert.True(caps.SupportsModel);
        Assert.False(caps.SupportsNoSessionPersistence);
        Assert.False(caps.SupportsStdin);
        Assert.False(caps.SupportsVerbose);
        Assert.False(caps.SupportsQuiet);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_EmptyHelp_ReturnsSafeDefaults()
    {
        // Arrange
        SetupHelpOutput("");

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert - Should match SafeDefaults
        var defaults = CapabilitySet.SafeDefaults;
        Assert.Equal(defaults.SupportsAllowedTools, caps.SupportsAllowedTools);
        Assert.Equal(defaults.SupportsMaxTurns, caps.SupportsMaxTurns);
        Assert.Equal(defaults.SupportsOutputFormat, caps.SupportsOutputFormat);
        Assert.Equal(defaults.SupportsAppendSystemPrompt, caps.SupportsAppendSystemPrompt);
        Assert.Equal(defaults.SupportsModel, caps.SupportsModel);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_HelpError_ReturnsSafeDefaults()
    {
        // Arrange
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Help command failed"));

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert - Should return safe defaults on error
        Assert.False(caps.SupportsAllowedTools); // Don't assume on error
        Assert.False(caps.SupportsMaxTurns);
        Assert.True(caps.SupportsOutputFormat); // Required for JSON parsing
        Assert.True(caps.SupportsAppendSystemPrompt);
        Assert.True(caps.SupportsModel);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_HelpTimeout_ReturnsSafeDefaults()
    {
        // Arrange
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Help timed out"));

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert
        Assert.False(caps.SupportsAllowedTools);
        Assert.False(caps.SupportsMaxTurns);
    }

    [Theory]
    [InlineData("--allowedTools", true)]
    [InlineData("--allowed-tools", true)]
    [InlineData("--allowedtools", true)] // Case insensitive
    [InlineData("--ALLOWEDTOOLS", true)]
    [InlineData("--toolsallowed", false)] // Wrong order
    public async Task GetCapabilitiesAsync_AllowedToolsVariants_DetectedCorrectly(string flag, bool expected)
    {
        // Arrange
        SetupHelpOutput($"Options:\n  {flag} <tools>  Restrict tools");

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert
        Assert.Equal(expected, caps.SupportsAllowedTools);
    }

    [Theory]
    [InlineData("--no-session-persistence", true)]
    [InlineData("--nosessionpersistence", true)]
    [InlineData("--session-persistence", false)] // Different flag
    public async Task GetCapabilitiesAsync_SessionPersistenceVariants_DetectedCorrectly(string flag, bool expected)
    {
        // Arrange
        SetupHelpOutput($"Options:\n  {flag}  Session control");

        // Act
        var caps = await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert
        Assert.Equal(expected, caps.SupportsNoSessionPersistence);
    }

    #endregion

    #region Caching

    [Fact]
    public async Task GetCapabilitiesAsync_SamePath_UsesCachedResult()
    {
        // Arrange
        SetupHelpOutput("--max-turns");

        // Act - Call twice
        await _capabilities.GetCapabilitiesAsync("/mock/claude");
        await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert - Help command only called once (cached)
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DifferentPaths_ProbesBoth()
    {
        // Arrange
        SetupHelpOutput("--max-turns");

        // Act - Call with different paths
        await _capabilities.GetCapabilitiesAsync("/mock/claude1");
        await _capabilities.GetCapabilitiesAsync("/mock/claude2");

        // Assert - Help called twice (different paths)
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidateCache_ClearsCachedCapabilities()
    {
        // Arrange
        SetupHelpOutput("--max-turns");
        await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Act - Invalidate and call again
        ClaudeCodeCapabilities.InvalidateCache();
        await _capabilities.GetCapabilitiesAsync("/mock/claude");

        // Assert - Help called twice (cache was cleared)
        _mockCliRunner.Verify(r => r.ExecuteAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
            It.IsAny<CliRunnerOptions>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #endregion

    #region CapabilitySet.SafeDefaults

    [Fact]
    public void SafeDefaults_HasConservativeSettings()
    {
        var defaults = CapabilitySet.SafeDefaults;

        // Conservative: don't assume newer flags exist
        Assert.False(defaults.SupportsAllowedTools);
        Assert.False(defaults.SupportsMaxTurns);
        Assert.False(defaults.SupportsNoSessionPersistence);
        Assert.False(defaults.SupportsStdin);
        Assert.False(defaults.SupportsVerbose);
        Assert.False(defaults.SupportsQuiet);

        // Assume basic flags available (required for operation)
        Assert.True(defaults.SupportsOutputFormat);
        Assert.True(defaults.SupportsAppendSystemPrompt);
        Assert.True(defaults.SupportsModel);
    }

    #endregion

    #region CapabilitySet.BuildArguments

    [Fact]
    public void BuildArguments_MinimalCapabilities_BuildsBasicArgs()
    {
        // Arrange - Only output format available
        var caps = new CapabilitySet
        {
            SupportsOutputFormat = true,
            SupportsAppendSystemPrompt = false,
            SupportsModel = false,
            SupportsMaxTurns = false,
            SupportsAllowedTools = false,
            SupportsNoSessionPersistence = false
        };

        // Act
        var args = caps.BuildArguments("Hello");

        // Assert
        Assert.Contains("-p", args);
        Assert.Contains("Hello", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);
        Assert.DoesNotContain("--max-turns", args);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public void BuildArguments_FullCapabilities_BuildsAllSafetyArgs()
    {
        // Arrange - Full featured CLI
        var caps = new CapabilitySet
        {
            SupportsOutputFormat = true,
            SupportsAppendSystemPrompt = true,
            SupportsModel = true,
            SupportsMaxTurns = true,
            SupportsAllowedTools = true,
            SupportsNoSessionPersistence = true
        };

        // Act
        var args = caps.BuildArguments("Test prompt", "System prompt", "opus");

        // Assert - Required args
        Assert.Contains("-p", args);
        Assert.Contains("Test prompt", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);

        // Safety args
        Assert.Contains("--max-turns", args);
        Assert.Contains("1", args);
        Assert.Contains("--allowedTools", args);
        Assert.Contains("--no-session-persistence", args);

        // Optional args
        Assert.Contains("--append-system-prompt", args);
        Assert.Contains("System prompt", args);
        Assert.Contains("--model", args);
        Assert.Contains("opus", args);
    }

    [Fact]
    public void BuildArguments_NoSystemPrompt_OmitsSystemPromptArg()
    {
        // Arrange
        var caps = new CapabilitySet { SupportsAppendSystemPrompt = true };

        // Act
        var args = caps.BuildArguments("Hello", null);

        // Assert
        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Fact]
    public void BuildArguments_EmptySystemPrompt_OmitsSystemPromptArg()
    {
        // Arrange
        var caps = new CapabilitySet { SupportsAppendSystemPrompt = true };

        // Act
        var args = caps.BuildArguments("Hello", "");

        // Assert
        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Fact]
    public void BuildArguments_NoModel_OmitsModelArg()
    {
        // Arrange
        var caps = new CapabilitySet { SupportsModel = true };

        // Act
        var args = caps.BuildArguments("Hello", null, null);

        // Assert
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void BuildArguments_EmptyModel_OmitsModelArg()
    {
        // Arrange
        var caps = new CapabilitySet { SupportsModel = true };

        // Act
        var args = caps.BuildArguments("Hello", null, "");

        // Assert
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void BuildArguments_AllowedToolsDisabled_PassesEmptyString()
    {
        // Arrange - When allowedTools is supported, pass empty to disable all tools
        var caps = new CapabilitySet { SupportsAllowedTools = true };

        // Act
        var args = caps.BuildArguments("Hello");

        // Assert - Empty string disables all tool access
        var allowedToolsIndex = args.IndexOf("--allowedTools");
        Assert.True(allowedToolsIndex >= 0);
        Assert.Equal(string.Empty, args[allowedToolsIndex + 1]);
    }

    [Fact]
    public void BuildArguments_OutputFormatUnsupported_OmitsOutputFormat()
    {
        // Arrange - Hypothetical ancient CLI
        var caps = new CapabilitySet { SupportsOutputFormat = false };

        // Act
        var args = caps.BuildArguments("Hello");

        // Assert
        Assert.DoesNotContain("--output-format", args);
    }

    [Theory]
    [InlineData("sonnet")]
    [InlineData("opus")]
    [InlineData("haiku")]
    public void BuildArguments_SupportedModels_IncludesModel(string model)
    {
        // Arrange
        var caps = new CapabilitySet { SupportsModel = true };

        // Act
        var args = caps.BuildArguments("Hello", null, model);

        // Assert
        Assert.Contains("--model", args);
        Assert.Contains(model, args);
    }

    [Fact]
    public void BuildArguments_PromptWithSpecialCharacters_PreservesPrompt()
    {
        // Arrange
        var caps = new CapabilitySet();
        var prompt = "Hello \"world\" with 'quotes' and $special chars";

        // Act
        var args = caps.BuildArguments(prompt);

        // Assert - Prompt is passed as-is (CLI handles escaping)
        Assert.Contains(prompt, args);
    }

    #endregion

    #region Helper Methods

    private void SetupHelpOutput(string helpText)
    {
        _mockCliRunner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(a => a.Contains("--help")),
                It.IsAny<CliRunnerOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult
            {
                ExitCode = 0,
                StandardOutput = helpText,
                StandardError = "",
                Duration = TimeSpan.FromMilliseconds(50)
            });
    }

    #endregion
}
