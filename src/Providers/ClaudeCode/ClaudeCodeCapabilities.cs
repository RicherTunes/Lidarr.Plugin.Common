// <copyright file="ClaudeCodeCapabilities.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Subprocess;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Detects and caches Claude CLI capabilities based on --help output.
/// Ensures we only use flags that the installed version supports.
/// </summary>
public sealed class ClaudeCodeCapabilities
{
    private static readonly ConcurrentDictionary<string, CapabilitySet> Cache = new();
    private readonly ICliRunner _cliRunner;
    private readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeCapabilities"/> class.
    /// </summary>
    /// <param name="cliRunner">CLI runner for executing help command.</param>
    public ClaudeCodeCapabilities(ICliRunner cliRunner)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
    }

    /// <summary>
    /// Gets the detected capabilities for the specified CLI path.
    /// Results are cached per CLI path for the process lifetime.
    /// </summary>
    /// <param name="cliPath">Path to the Claude CLI executable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detected capabilities.</returns>
    public async Task<CapabilitySet> GetCapabilitiesAsync(string cliPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cliPath);

        if (Cache.TryGetValue(cliPath, out var cached))
        {
            return cached;
        }

        var capabilities = await ProbeCapabilitiesAsync(cliPath, cancellationToken).ConfigureAwait(false);
        Cache[cliPath] = capabilities;
        return capabilities;
    }

    /// <summary>
    /// Invalidates the cached capabilities for all CLI paths.
    /// Used primarily for testing.
    /// </summary>
    public static void InvalidateCache()
    {
        Cache.Clear();
    }

    private async Task<CapabilitySet> ProbeCapabilitiesAsync(string cliPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _cliRunner.ExecuteAsync(
                cliPath,
                ["--help"],
                new CliRunnerOptions
                {
                    Timeout = _probeTimeout,
                    ThrowOnNonZeroExitCode = false,
                },
                cancellationToken).ConfigureAwait(false);

            var helpText = result.StandardOutput + result.StandardError;
            return ParseHelpOutput(helpText);
        }
        catch
        {
            // On any error, return safe defaults (minimal capabilities)
            return CapabilitySet.SafeDefaults;
        }
    }

    private static CapabilitySet ParseHelpOutput(string helpText)
    {
        if (string.IsNullOrEmpty(helpText))
        {
            return CapabilitySet.SafeDefaults;
        }

        var text = helpText.ToLowerInvariant();

        // Check for stream-json format support (indicates streaming capability)
        var supportsStreamJson = text.Contains("stream-json");

        return new CapabilitySet
        {
            SupportsAllowedTools = text.Contains("--allowedtools") || text.Contains("--allowed-tools"),
            SupportsMaxTurns = text.Contains("--max-turns"),
            SupportsOutputFormat = text.Contains("--output-format"),
            SupportsStreamJson = supportsStreamJson,
            SupportsAppendSystemPrompt = text.Contains("--append-system-prompt"),
            SupportsModel = text.Contains("--model"),
            SupportsNoSessionPersistence = text.Contains("--no-session-persistence") || text.Contains("--nosessionpersistence"),
            SupportsStdin = text.Contains("stdin") || text.Contains("--input"),
            SupportsVerbose = text.Contains("--verbose") || text.Contains("-v"),
            SupportsQuiet = text.Contains("--quiet") || text.Contains("-q"),
        };
    }
}

/// <summary>
/// Represents the detected capabilities of a Claude CLI installation.
/// </summary>
public sealed class CapabilitySet
{
    /// <summary>
    /// Gets safe defaults when capability detection fails.
    /// Only assumes basic flags that have been stable across versions.
    /// </summary>
    public static CapabilitySet SafeDefaults { get; } = new()
    {
        SupportsAllowedTools = false, // Don't assume - could cause errors
        SupportsMaxTurns = false,
        SupportsOutputFormat = true, // Required for JSON parsing
        SupportsStreamJson = false, // Don't assume streaming support
        SupportsAppendSystemPrompt = true, // Generally available
        SupportsModel = true, // Generally available
        SupportsNoSessionPersistence = false, // Don't assume
        SupportsStdin = false,
        SupportsVerbose = false,
        SupportsQuiet = false,
    };

    /// <summary>
    /// Gets or sets a value indicating whether --allowedTools flag is supported.
    /// </summary>
    public bool SupportsAllowedTools { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --max-turns flag is supported.
    /// </summary>
    public bool SupportsMaxTurns { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --output-format flag is supported.
    /// </summary>
    public bool SupportsOutputFormat { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --output-format stream-json is supported.
    /// This enables streaming responses with NDJSON output.
    /// </summary>
    public bool SupportsStreamJson { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --append-system-prompt flag is supported.
    /// </summary>
    public bool SupportsAppendSystemPrompt { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --model flag is supported.
    /// </summary>
    public bool SupportsModel { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --no-session-persistence flag is supported.
    /// </summary>
    public bool SupportsNoSessionPersistence { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether stdin input is supported.
    /// </summary>
    public bool SupportsStdin { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --verbose flag is supported.
    /// </summary>
    public bool SupportsVerbose { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether --quiet flag is supported.
    /// </summary>
    public bool SupportsQuiet { get; init; }

    /// <summary>
    /// Builds the argument list based on detected capabilities.
    /// </summary>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="systemPrompt">Optional system prompt.</param>
    /// <param name="model">Optional model selection.</param>
    /// <returns>List of CLI arguments.</returns>
    public List<string> BuildArguments(string prompt, string? systemPrompt = null, string? model = null)
    {
        var args = new List<string>();

        // Prompt (always required)
        args.AddRange(["-p", prompt]);

        // Output format (required for JSON parsing)
        if (SupportsOutputFormat)
        {
            args.AddRange(["--output-format", "json"]);
        }

        // Safety: limit to single turn
        if (SupportsMaxTurns)
        {
            args.AddRange(["--max-turns", "1"]);
        }

        // Safety: disable tool access
        if (SupportsAllowedTools)
        {
            args.AddRange(["--allowedTools", string.Empty]);
        }

        // Prevent session state bleeding across requests
        if (SupportsNoSessionPersistence)
        {
            args.Add("--no-session-persistence");
        }

        // System prompt
        if (!string.IsNullOrEmpty(systemPrompt) && SupportsAppendSystemPrompt)
        {
            args.AddRange(["--append-system-prompt", systemPrompt]);
        }

        // Model selection
        if (!string.IsNullOrEmpty(model) && SupportsModel)
        {
            args.AddRange(["--model", model]);
        }

        return args;
    }

    /// <summary>
    /// Builds the argument list for streaming mode.
    /// </summary>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="systemPrompt">Optional system prompt.</param>
    /// <param name="model">Optional model selection.</param>
    /// <returns>List of CLI arguments configured for streaming, or null if streaming not supported.</returns>
    public List<string>? BuildStreamingArguments(string prompt, string? systemPrompt = null, string? model = null)
    {
        // Streaming requires stream-json output format support
        if (!SupportsStreamJson || !SupportsOutputFormat)
        {
            return null;
        }

        var args = new List<string>();

        // Prompt (always required)
        args.AddRange(["-p", prompt]);

        // Output format for streaming
        args.AddRange(["--output-format", "stream-json"]);

        // Safety: limit to single turn
        if (SupportsMaxTurns)
        {
            args.AddRange(["--max-turns", "1"]);
        }

        // Safety: disable tool access
        if (SupportsAllowedTools)
        {
            args.AddRange(["--allowedTools", string.Empty]);
        }

        // Prevent session state bleeding across requests
        if (SupportsNoSessionPersistence)
        {
            args.Add("--no-session-persistence");
        }

        // System prompt
        if (!string.IsNullOrEmpty(systemPrompt) && SupportsAppendSystemPrompt)
        {
            args.AddRange(["--append-system-prompt", systemPrompt]);
        }

        // Model selection
        if (!string.IsNullOrEmpty(model) && SupportsModel)
        {
            args.AddRange(["--model", model]);
        }

        return args;
    }
}
