// <copyright file="ClaudeCodeProvider.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Subprocess;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// LLM provider that wraps Claude Code CLI for completions.
/// Uses CLI's own OAuth authentication (user runs 'claude login' separately).
/// </summary>
public class ClaudeCodeProvider : ILlmProvider
{
    /// <summary>
    /// The unique provider identifier for Claude Code CLI.
    /// </summary>
    public const string ProviderIdConst = "claude-code";

    private readonly ICliRunner _cliRunner;
    private readonly ClaudeCodeDetector _detector;
    private readonly ClaudeCodeSettings _settings;
    private readonly ILogger<ClaudeCodeProvider>? _logger;
    private string? _cachedCliPath;

    /// <inheritdoc />
    public string ProviderId => ProviderIdConst;

    /// <inheritdoc />
    public string DisplayName => "Claude Code CLI";

    /// <inheritdoc />
    public LlmProviderCapabilities Capabilities => new()
    {
        Flags = LlmCapabilityFlags.TextCompletion | LlmCapabilityFlags.SystemPrompt | LlmCapabilityFlags.ExtendedThinking,
        MaxContextTokens = 200_000,
        MaxOutputTokens = 8_192,
        SupportedModels = ["sonnet", "opus", "haiku"],
        UsesOpenAiCompatibleApi = false,
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeProvider"/> class.
    /// </summary>
    /// <param name="cliRunner">The CLI runner for subprocess execution.</param>
    /// <param name="detector">The detector for finding Claude CLI installation.</param>
    /// <param name="settings">Optional settings for timeout and model configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ClaudeCodeProvider(
        ICliRunner cliRunner,
        ClaudeCodeDetector detector,
        ClaudeCodeSettings? settings = null,
        ILogger<ClaudeCodeProvider>? logger = null)
    {
        _cliRunner = cliRunner ?? throw new ArgumentNullException(nameof(cliRunner));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _settings = settings ?? new ClaudeCodeSettings();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        // Step 1: Find CLI
        var cliPath = await _detector.FindClaudeCliAsync(cancellationToken).ConfigureAwait(false);
        if (cliPath == null)
        {
            return ProviderHealthResult.Unhealthy(
                "Claude Code CLI not installed. Install via: irm https://installer.anthropic.com/claude/install.ps1 | iex");
        }

        _cachedCliPath = cliPath;

        // Step 2: Verify version (fast, no auth needed)
        try
        {
            var versionResult = await _cliRunner.ExecuteAsync(
                cliPath,
                ["--version"],
                new CliRunnerOptions
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    ThrowOnNonZeroExitCode = false,
                },
                cancellationToken).ConfigureAwait(false);

            if (!versionResult.IsSuccess)
            {
                return ProviderHealthResult.Unhealthy($"CLI error: {versionResult.StandardError}");
            }
        }
        catch (TimeoutException)
        {
            return ProviderHealthResult.Unhealthy("CLI version check timed out");
        }
        catch (OperationCanceledException)
        {
            return ProviderHealthResult.Unhealthy("Health check cancelled");
        }

        // Step 3: Verify authentication via minimal prompt
        try
        {
            var authResult = await _cliRunner.ExecuteAsync(
                cliPath,
                ["-p", "Reply with only the word OK", "--output-format", "json", "--max-turns", "1"],
                new CliRunnerOptions
                {
                    Timeout = _settings.HealthCheckTimeout,
                    ThrowOnNonZeroExitCode = false,
                },
                cancellationToken).ConfigureAwait(false);

            if (!authResult.IsSuccess)
            {
                var stderr = authResult.StandardError.ToLowerInvariant();
                if (stderr.Contains("authentication") || stderr.Contains("login") || stderr.Contains("unauthorized"))
                {
                    return ProviderHealthResult.Unhealthy("Not authenticated. Run 'claude login' to authenticate.");
                }

                return ProviderHealthResult.Unhealthy($"CLI error: {authResult.StandardError}");
            }

            return ProviderHealthResult.Healthy();
        }
        catch (TimeoutException)
        {
            return ProviderHealthResult.Degraded("Auth check timed out - CLI may be overloaded");
        }
        catch (OperationCanceledException)
        {
            return ProviderHealthResult.Unhealthy("Health check cancelled");
        }
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        // Ensure CLI path is known
        var cliPath = _cachedCliPath ?? await _detector.FindClaudeCliAsync(cancellationToken).ConfigureAwait(false);
        if (cliPath == null)
        {
            throw new ProviderException(ProviderId, LlmErrorCode.ProviderUnavailable, "Claude Code CLI not found");
        }

        // Build arguments
        var args = new List<string>
        {
            "-p", request.Prompt,
            "--output-format", "json",
            "--max-turns", "1",
        };

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            args.AddRange(["--append-system-prompt", request.SystemPrompt]);
        }

        // Use request model if specified, otherwise use settings model
        var model = request.Model ?? _settings.Model;
        if (!string.IsNullOrEmpty(model))
        {
            args.AddRange(["--model", model]);
        }

        var options = new CliRunnerOptions
        {
            Timeout = request.Timeout ?? _settings.DefaultTimeout,
            GracefulShutdownTimeout = _settings.GracefulShutdownTimeout,
            ThrowOnNonZeroExitCode = false,
        };

        try
        {
            var result = await _cliRunner.ExecuteAsync(cliPath, args, options, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw MapCliError(result);
            }

            return ClaudeCodeResponseParser.ParseJsonResponse(result.StandardOutput);
        }
        catch (TimeoutException)
        {
            throw new ProviderException(ProviderId, LlmErrorCode.Timeout, "CLI request timed out");
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (LlmProviderException)
        {
            throw; // Already mapped
        }
        catch (Exception ex)
        {
            throw new ProviderException(ProviderId, LlmErrorCode.InvalidRequest, $"Unexpected error: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <returns>
    /// Always returns <c>null</c> because streaming is not supported in v1.
    /// Could be implemented with --output-format stream-json in v2.
    /// </returns>
    public IAsyncEnumerable<LlmStreamChunk>? StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        // Streaming not supported in v1
        // Could implement with --output-format stream-json in v2
        return null;
    }

    private LlmProviderException MapCliError(CliResult result)
    {
        var stderr = result.StandardError.ToLowerInvariant();

        if (stderr.Contains("authentication") || stderr.Contains("login") || stderr.Contains("unauthorized"))
        {
            return new AuthenticationException(
                ProviderId,
                LlmErrorCode.AuthenticationFailed,
                "Not authenticated. Run 'claude login' to authenticate.");
        }

        if (stderr.Contains("rate") || stderr.Contains("quota") || stderr.Contains("limit"))
        {
            return new RateLimitException(
                ProviderId,
                LlmErrorCode.RateLimited,
                "Rate limited by Claude");
        }

        if (result.ExitCode == 127)
        {
            return new ProviderException(
                ProviderId,
                LlmErrorCode.ProviderUnavailable,
                "Claude CLI not found");
        }

        return new ProviderException(
            ProviderId,
            LlmErrorCode.InvalidRequest,
            $"CLI error (exit {result.ExitCode}): {result.StandardError}");
    }
}
