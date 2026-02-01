// <copyright file="ClaudeCodeProvider.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Observability;
using Lidarr.Plugin.Common.Streaming;
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

    /// <summary>
    /// The plugin name for logging purposes.
    /// </summary>
    private const string PluginName = "Common";

    private readonly ICliRunner _cliRunner;
    private readonly ClaudeCodeDetector _detector;
    private readonly ClaudeCodeCapabilities _capabilities;
    private readonly ClaudeCodeSettings _settings;
    private readonly ILogger<ClaudeCodeProvider>? _logger;
    private string? _cachedCliPath;
    private CapabilitySet? _cachedCapabilities;

    /// <inheritdoc />
    public string ProviderId => ProviderIdConst;

    /// <inheritdoc />
    public string DisplayName => "Claude Code CLI";

    /// <inheritdoc />
    public LlmProviderCapabilities Capabilities => new()
    {
        Flags = LlmCapabilityFlags.TextCompletion | LlmCapabilityFlags.SystemPrompt | LlmCapabilityFlags.ExtendedThinking | LlmCapabilityFlags.Streaming,
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
        _capabilities = new ClaudeCodeCapabilities(cliRunner);
        _settings = settings ?? new ClaudeCodeSettings();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Step 1: Find CLI
        var cliPath = await _detector.FindClaudeCliAsync(cancellationToken).ConfigureAwait(false);
        if (cliPath == null)
        {
            _logger?.LogHealthCheckFail(PluginName, ProviderId, "CLI not installed");
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
                _logger?.LogHealthCheckFail(PluginName, ProviderId, $"Version check failed: {versionResult.StandardError}");
                return ProviderHealthResult.Unhealthy($"CLI error: {versionResult.StandardError}");
            }
        }
        catch (TimeoutException)
        {
            _logger?.LogHealthCheckFail(PluginName, ProviderId, "Version check timed out");
            return ProviderHealthResult.Unhealthy("CLI version check timed out");
        }
        catch (OperationCanceledException)
        {
            _logger?.LogHealthCheckFail(PluginName, ProviderId, "Health check cancelled");
            return ProviderHealthResult.Unhealthy("Health check cancelled");
        }

        // Step 3: Probe capabilities (what flags does this CLI version support?)
        try
        {
            _cachedCapabilities = await _capabilities.GetCapabilitiesAsync(cliPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cachedCapabilities = CapabilitySet.SafeDefaults;
        }

        // Step 4: Verify authentication via minimal prompt using detected capabilities
        try
        {
            var args = _cachedCapabilities.BuildArguments("Reply with only the word OK");

            var authResult = await _cliRunner.ExecuteAsync(
                cliPath,
                args,
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
                    _logger?.LogAuthFail(PluginName, ProviderId, "health-check", "Not authenticated");
                    return ProviderHealthResult.Unhealthy("Not authenticated. Run 'claude login' to authenticate.");
                }

                _logger?.LogHealthCheckFail(PluginName, ProviderId, $"Auth check failed: {authResult.StandardError}");
                return ProviderHealthResult.Unhealthy($"CLI error: {authResult.StandardError}");
            }

            _logger?.LogHealthCheckPass(PluginName, ProviderId, sw.ElapsedMilliseconds);
            return ProviderHealthResult.Healthy();
        }
        catch (TimeoutException)
        {
            _logger?.LogHealthCheckFail(PluginName, ProviderId, "Auth check timed out");
            return ProviderHealthResult.Degraded("Auth check timed out - CLI may be overloaded");
        }
        catch (OperationCanceledException)
        {
            _logger?.LogHealthCheckFail(PluginName, ProviderId, "Health check cancelled");
            return ProviderHealthResult.Unhealthy("Health check cancelled");
        }
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8];
        var sw = Stopwatch.StartNew();

        // Ensure CLI path is known
        var cliPath = _cachedCliPath ?? await _detector.FindClaudeCliAsync(cancellationToken).ConfigureAwait(false);
        if (cliPath == null)
        {
            _logger?.LogRequestError(PluginName, ProviderId, "CompleteAsync", correlationId, "CLI_NOT_FOUND", "Claude Code CLI not found");
            throw new ProviderException(ProviderId, LlmErrorCode.ProviderUnavailable, "Claude Code CLI not found");
        }

        // Ensure capabilities are probed
        var caps = _cachedCapabilities ?? await _capabilities.GetCapabilitiesAsync(cliPath, cancellationToken).ConfigureAwait(false);
        _cachedCapabilities = caps;

        // Use request model if specified, otherwise use settings model
        var model = request.Model ?? _settings.Model;

        // Build arguments using detected capabilities (safe conditional flags)
        var args = caps.BuildArguments(request.Prompt, request.SystemPrompt, model);

        var options = new CliRunnerOptions
        {
            Timeout = request.Timeout ?? _settings.DefaultTimeout,
            GracefulShutdownTimeout = _settings.GracefulShutdownTimeout,
            ThrowOnNonZeroExitCode = false,
        };

        _logger?.LogRequestStart(PluginName, ProviderId, "CompleteAsync", correlationId, model);

        try
        {
            _logger?.LogDebug(
                "Executing Claude CLI with {ArgCount} args (prompt length: {PromptLength})",
                args.Count,
                request.Prompt.Length);

            var result = await _cliRunner.ExecuteAsync(cliPath, args, options, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var error = MapCliError(result, correlationId);
                throw error;
            }

            var response = ClaudeCodeResponseParser.ParseJsonResponse(result.StandardOutput);
            _logger?.LogRequestComplete(
                PluginName, ProviderId, "CompleteAsync", correlationId, sw.ElapsedMilliseconds,
                response.Usage?.InputTokens, response.Usage?.OutputTokens);

            return response;
        }
        catch (TimeoutException)
        {
            _logger?.LogRequestError(PluginName, ProviderId, "CompleteAsync", correlationId, "TIMEOUT", "CLI request timed out");
            throw new ProviderException(ProviderId, LlmErrorCode.Timeout, "CLI request timed out");
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Request cancelled for correlation {CorrelationId}", correlationId);
            throw; // Let cancellation propagate
        }
        catch (LlmProviderException)
        {
            throw; // Already mapped and logged
        }
        catch (Exception ex)
        {
            _logger?.LogRequestError(PluginName, ProviderId, "CompleteAsync", correlationId, "UNKNOWN", ex.Message, ex);
            throw new ProviderException(ProviderId, LlmErrorCode.Unknown, $"Unexpected error: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<LlmStreamChunk>? StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        // Return the streaming implementation
        // This method is sync but returns an async enumerable that will execute lazily
        return StreamAsyncCore(request, cancellationToken);
    }

    private async IAsyncEnumerable<LlmStreamChunk> StreamAsyncCore(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8];
        var sw = Stopwatch.StartNew();

        // Ensure CLI path is known
        var cliPath = _cachedCliPath ?? await _detector.FindClaudeCliAsync(cancellationToken).ConfigureAwait(false);
        if (cliPath == null)
        {
            _logger?.LogRequestError(PluginName, ProviderId, "StreamAsync", correlationId, "CLI_NOT_FOUND", "Claude Code CLI not found");
            throw new ProviderException(ProviderId, LlmErrorCode.ProviderUnavailable, "Claude Code CLI not found");
        }

        // Ensure capabilities are probed
        var caps = _cachedCapabilities ?? await _capabilities.GetCapabilitiesAsync(cliPath, cancellationToken).ConfigureAwait(false);
        _cachedCapabilities = caps;

        // Check if streaming is supported
        var model = request.Model ?? _settings.Model;
        var args = caps.BuildStreamingArguments(request.Prompt, request.SystemPrompt, model);

        if (args == null)
        {
            // Streaming not supported by this CLI version - fall back to non-streaming
            _logger?.LogDebug("Streaming not supported by CLI, falling back to non-streaming");
            var response = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            yield return new LlmStreamChunk
            {
                ContentDelta = response.Content,
                IsComplete = true,
                FinalUsage = response.Usage,
            };
            yield break;
        }

        // Configure timeout policy for streaming
        var timeoutPolicy = StreamingTimeoutPolicy.ForClaudeCode();
        if (request.Timeout.HasValue)
        {
            timeoutPolicy = timeoutPolicy with { TotalStreamTimeout = request.Timeout.Value };
        }

        var options = new CliRunnerOptions
        {
            Timeout = timeoutPolicy.TotalStreamTimeout,
            GracefulShutdownTimeout = _settings.GracefulShutdownTimeout,
            ThrowOnNonZeroExitCode = false,
        };

        _logger?.LogRequestStart(PluginName, ProviderId, "StreamAsync", correlationId, model);

        // Use a wrapper to handle exceptions outside the yield block
        var chunks = StreamWithErrorHandlingAsync(
            cliPath, args, options, timeoutPolicy, cancellationToken, correlationId, sw);

        await foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<LlmStreamChunk> StreamWithErrorHandlingAsync(
        string cliPath,
        List<string> args,
        CliRunnerOptions options,
        StreamingTimeoutPolicy timeoutPolicy,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string correlationId,
        Stopwatch sw)
    {
        await using var streamCancellation = new StreamingCancellation(timeoutPolicy, cancellationToken);

        // Create rate-limited logger for unknown events to prevent log spam
        var eventLogger = new RateLimitedEventLogger(
            msg => _logger?.LogWarning("Stream event drift: {Message}", msg),
            maxUniqueEvents: 3);

        // Create parser with tolerant mode - log unknown event types instead of failing
        var parser = new ClaudeCodeStreamParser(unknownEventLogger: eventLogger.CreateLoggerAction());

        var hasYielded = false;
        var chunkCount = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        TimeSpan? timeToFirstChunk = null;
        Exception? caughtException = null;

        var enumerator = parser.ParseAsync(
            _cliRunner.StreamAsync(cliPath, args, options, streamCancellation.Token),
            streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);

        try
        {
            while (true)
            {
                LlmStreamChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    chunk = enumerator.Current;
                }
                catch (OperationCanceledException) when (streamCancellation.CancellationReason != StreamingCancellationReason.ExternalCancellation)
                {
                    var reason = streamCancellation.CancellationReason ?? StreamingCancellationReason.TotalStreamTimeout;
                    var diagnostics = StreamingDiagnostics.Cancelled(
                        decoderId: "claude-code",
                        providerId: ProviderId,
                        reason: reason,
                        elapsed: sw.Elapsed,
                        chunkCount: chunkCount);

                    var timeoutEx = streamCancellation.CreateTimeoutException();
                    _logger?.LogRequestError(PluginName, ProviderId, "StreamAsync", correlationId, "TIMEOUT", timeoutEx.Message);
                    caughtException = new ProviderException(ProviderId, LlmErrorCode.Timeout, timeoutEx.Message, timeoutEx)
                    {
                        Data = { ["Diagnostics"] = diagnostics },
                    };
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug("Stream cancelled for correlation {CorrelationId}", correlationId);
                    throw;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("streaming error"))
                {
                    _logger?.LogRequestError(PluginName, ProviderId, "StreamAsync", correlationId, "STREAM_ERROR", ex.Message);
                    caughtException = new ProviderException(ProviderId, LlmErrorCode.InvalidRequest, ex.Message, ex);
                    break;
                }
                catch (LlmProviderException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogRequestError(PluginName, ProviderId, "StreamAsync", correlationId, "UNKNOWN", ex.Message, ex);
                    caughtException = new ProviderException(ProviderId, LlmErrorCode.Unknown, $"Streaming error: {ex.Message}", ex);
                    break;
                }

                chunkCount++;

                // Only reset timeout on meaningful chunks (content or reasoning)
                // Metadata-only chunks (keepalives, structural events) should not reset the timer
                var hasMeaningfulContent = !string.IsNullOrEmpty(chunk.ContentDelta)
                    || !string.IsNullOrEmpty(chunk.ReasoningDelta);

                if (hasMeaningfulContent)
                {
                    streamCancellation.OnChunkReceived();

                    // Track time to first meaningful chunk
                    timeToFirstChunk ??= sw.Elapsed;
                }

                hasYielded = true;

                if (chunk.FinalUsage != null)
                {
                    totalInputTokens = chunk.FinalUsage.InputTokens;
                    totalOutputTokens = chunk.FinalUsage.OutputTokens;
                }

                yield return chunk;

                if (chunk.IsComplete)
                {
                    break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // Log suppressed unknown events summary (if any)
        eventLogger.LogSuppressedSummary();

        if (caughtException != null)
        {
            throw caughtException;
        }

        if (!hasYielded)
        {
            yield return new LlmStreamChunk { IsComplete = true };
        }
        else
        {
            // Log completion with diagnostics summary
            _logger?.LogRequestComplete(
                PluginName, ProviderId, "StreamAsync", correlationId, sw.ElapsedMilliseconds,
                totalInputTokens > 0 ? totalInputTokens : null,
                totalOutputTokens > 0 ? totalOutputTokens : null);

            // Log streaming diagnostics summary (single line, no payload)
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug(
                    "Stream diagnostics: chunks={ChunkCount}, ttfc={TimeToFirstChunkMs}ms, " +
                    "elapsed={ElapsedMs}ms, tokens={InputTokens}/{OutputTokens}",
                    chunkCount,
                    timeToFirstChunk?.TotalMilliseconds ?? 0,
                    sw.ElapsedMilliseconds,
                    totalInputTokens,
                    totalOutputTokens);
            }
        }
    }

    private LlmProviderException MapCliError(CliResult result, string? correlationId = null)
    {
        var stderr = result.StandardError.ToLowerInvariant();
        var corrId = correlationId ?? "unknown";

        if (stderr.Contains("authentication") || stderr.Contains("login") || stderr.Contains("unauthorized"))
        {
            _logger?.LogAuthFail(PluginName, ProviderId, corrId, "CLI reported authentication failure");
            return new AuthenticationException(
                ProviderId,
                LlmErrorCode.AuthenticationFailed,
                "Not authenticated. Run 'claude login' to authenticate.");
        }

        if (stderr.Contains("rate") || stderr.Contains("quota") || stderr.Contains("limit"))
        {
            _logger?.LogRateLimited(PluginName, ProviderId, corrId);
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
