using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Observability;
using Lidarr.Plugin.Common.Streaming.Decoders;

namespace Lidarr.Plugin.Common.Providers.OpenAi;

/// <summary>Shared template for API-key providers using the OpenAI Chat Completions wire format.</summary>
public abstract class OpenAiChatProviderBase : ILlmProvider
{
    private static readonly JsonSerializerOptions WireJsonOptions = CreateWireJsonOptions();
    private readonly IOpenAiChatTransport _transport;
    private readonly IOpenAiChatAuthCircuit? _authCircuit;
    private readonly string _apiKey;
    private string _model;

    protected OpenAiChatProviderBase(
        IOpenAiChatTransport transport,
        string apiKey,
        string? model,
        string providerId,
        string defaultModel,
        TimeSpan completionTimeout,
        TimeSpan healthTimeout,
        IOpenAiChatAuthCircuit? authCircuit = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("A provider id is required.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(defaultModel)) throw new ArgumentException("A default model is required.", nameof(defaultModel));
        if (completionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(completionTimeout));
        if (healthTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(healthTimeout));

        _apiKey = apiKey;
        ProviderId = providerId;
        _model = NormalizeModel(model ?? defaultModel);
        CompletionTimeout = completionTimeout;
        HealthTimeout = healthTimeout;
        _authCircuit = authCircuit;
    }

    public string ProviderId { get; }
    public abstract string DisplayName { get; }
    public abstract LlmProviderCapabilities Capabilities { get; }
    protected abstract Uri ChatCompletionsEndpoint { get; }
    protected TimeSpan CompletionTimeout { get; }
    protected TimeSpan HealthTimeout { get; }
    protected string CurrentModel => _model;
    protected virtual double DefaultTemperature => 0.7;
    protected virtual bool SendsTemperature => true;
    protected virtual bool SupportsJsonResponseFormat => true;
    protected virtual bool IsJsonModeRequested(LlmRequest request) => request.JsonMode;

    public void UpdateModel(string modelName)
    {
        if (!string.IsNullOrWhiteSpace(modelName)) _model = NormalizeModel(modelName);
    }

    public virtual async Task<ProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await SendAsync(BuildHealthProbeBody(), HealthTimeout, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == (int)HttpStatusCode.OK)
                return ProviderHealthResult.Healthy(stopwatch.Elapsed, ProviderId, "apiKey", _model);
            if (response.TransportException is null)
                return ProviderHealthResult.Unhealthy($"HTTP {response.StatusCode}", stopwatch.Elapsed, ProviderId, "apiKey", _model, response.StatusCode.ToString());
            throw MapSafeHttpError(response.StatusCode, Truncate(response.Body), response.RetryAfter, response.TransportException);
        }
        catch (LlmProviderException exception)
        {
            return ProviderHealthResult.Unhealthy(exception.Message, stopwatch.Elapsed, ProviderId, "apiKey", _model, exception.ErrorCode.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ProviderHealthResult.Unhealthy(exception.Message, stopwatch.Elapsed, ProviderId, "apiKey", _model);
        }
    }

    public virtual async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = BeginCompletionScope();
        OnCompletionRequestStarting();
        if (_authCircuit?.IsOpen(ProviderId, _apiKey, out var reason) == true)
            throw new AuthenticationException(ProviderId, LlmErrorCode.AuthenticationFailed, "Auth circuit open: " + reason);

        try
        {
            var response = await SendAsync(BuildRequestBody(request), ResolveRequestTimeout(request), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != (int)HttpStatusCode.OK)
            {
                throw MapSafeHttpError(response.StatusCode, Truncate(response.Body), response.RetryAfter, response.TransportException);
            }

            var result = ParseCompletion(response.Body ?? string.Empty);
            if (string.IsNullOrEmpty(result.Content)) throw InvalidResponse("The provider completion did not contain content.");
            _authCircuit?.RecordSuccess(ProviderId, _apiKey);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LlmProviderException exception)
        {
            RecordAuthFailureOnce(exception);
            throw;
        }
        catch (Exception exception)
        {
            throw LlmErrorMapper.MapException(ProviderId, exception);
        }
    }

    public virtual IAsyncEnumerable<LlmStreamChunk>? StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        return StreamCoreAsync(request, cancellationToken);
    }

    protected virtual object BuildHealthProbeBody() => new Dictionary<string, object?>
    {
        ["model"] = _model,
        ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = "Reply with OK" } },
        ["max_tokens"] = 5,
    };
    protected virtual void AddCompletionRequestHeaders(IDictionary<string, string> headers) { }
    protected virtual void AddStreamingRequestHeaders(IDictionary<string, string> headers) { }
    protected virtual LlmProviderException MapHttpError(int statusCode, string? body, TimeSpan? retryAfter, Exception? inner)
        => LlmErrorMapper.MapHttpError(ProviderId, statusCode, body, retryAfter, inner);
    protected virtual LlmResponse ParseCompletion(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw InvalidResponse("The provider returned an empty completion response.");
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw InvalidResponse("The provider response did not contain completion content.");
            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(text.GetString()))
                throw InvalidResponse("The provider completion choice did not contain content.");
            LlmUsage? usage = null;
            if (document.RootElement.TryGetProperty("usage", out var usageElement)) usage = new LlmUsage
            {
                InputTokens = usageElement.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
                OutputTokens = usageElement.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
            };
            return new LlmResponse { Content = text.GetString()!, FinishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null, Usage = usage };
        }
        catch (JsonException) { return new LlmResponse { Content = content }; }
    }
    protected virtual LlmStreamChunk TransformStreamChunk(LlmStreamChunk chunk) => chunk;
    protected virtual string NormalizeModel(string model) => model;

    protected virtual object BuildRequestBody(LlmRequest request) => BuildChatBody(request, false);
    protected virtual object BuildStreamingRequestBody(LlmRequest request) => BuildChatBody(request, true);
    protected virtual void OnCompletionRequestStarting() { }
    /// <summary>Creates an optional scope lasting for the whole completion operation.</summary>
    protected virtual IDisposable? BeginCompletionScope() => null;

    private async Task<OpenAiChatResponse> SendAsync(object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {_apiKey}", ["Content-Type"] = "application/json" };
        AddCompletionRequestHeaders(headers);
        return await _transport.SendAsync(new OpenAiChatRequest(ProviderId, ChatCompletionsEndpoint, JsonSerializer.Serialize(body, WireJsonOptions), headers, timeout), cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<LlmStreamChunk> StreamCoreAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {_apiKey}", ["Accept"] = "text/event-stream" };
        AddStreamingRequestHeaders(headers);
        await using var response = await _transport.OpenStreamAsync(new OpenAiChatRequest(ProviderId, ChatCompletionsEndpoint, JsonSerializer.Serialize(BuildStreamingRequestBody(request), WireJsonOptions), headers, ResolveRequestTimeout(request)), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != (int)HttpStatusCode.OK) throw MapHttpError(response.StatusCode, Truncate(response.ErrorBody), response.RetryAfter, null);
        var emittedMeaningfulContent = false;
        var decoder = new OpenAiStreamDecoder();
        await foreach (var chunk in decoder.DecodeAsync(response.Content, cancellationToken).ConfigureAwait(false))
        {
            var transformed = TransformStreamChunk(chunk);
            emittedMeaningfulContent |= !string.IsNullOrEmpty(transformed.ContentDelta) || !string.IsNullOrEmpty(transformed.ReasoningDelta);
            yield return transformed;
        }
        if (!emittedMeaningfulContent) throw InvalidResponse("The provider stream ended without completion content.");
    }

    private object BuildChatBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object?> { ["model"] = string.IsNullOrWhiteSpace(request.Model) ? _model : NormalizeModel(request.Model), ["messages"] = string.IsNullOrEmpty(request.SystemPrompt) ? new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = request.Prompt } } : new[] { new Dictionary<string, string> { ["role"] = "system", ["content"] = request.SystemPrompt }, new Dictionary<string, string> { ["role"] = "user", ["content"] = request.Prompt } } };
        if (SendsTemperature) body["temperature"] = (double?)request.Temperature ?? DefaultTemperature;
        body["max_tokens"] = request.MaxTokens ?? 2000;
        body["stream"] = stream;
        if (SupportsJsonResponseFormat && IsJsonModeRequested(request)) body["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
        return body;
    }

    private void RecordAuthFailureOnce(LlmProviderException exception)
    {
        if (exception.ErrorCode is LlmErrorCode.AuthenticationFailed or LlmErrorCode.AuthorizationFailed) _authCircuit?.RecordAuthFailure(ProviderId, _apiKey, exception);
    }

    private ProviderException InvalidResponse(string message) => new(ProviderId, LlmErrorCode.InvalidRequest, message);
    private LlmProviderException MapSafeHttpError(int statusCode, string? body, TimeSpan? retryAfter, Exception? inner)
    {
        var mapped = MapHttpError(statusCode, body, retryAfter, inner);
        if (!mapped.ToString().Contains(_apiKey, StringComparison.Ordinal)) return mapped;
        var message = LogRedactor.Redact(mapped.Message).Replace(_apiKey, LogRedactor.REDACTED, StringComparison.Ordinal);
        return new ProviderException(ProviderId, mapped.ErrorCode, message);
    }
    private static string? Truncate(string? body) => string.IsNullOrEmpty(body) || body.Length <= 500 ? body : body[..500];

    private TimeSpan ResolveRequestTimeout(LlmRequest request)
        => request.Timeout is { } requested && requested > TimeSpan.Zero && requested < CompletionTimeout
            ? requested
            : CompletionTimeout;

    private static JsonSerializerOptions CreateWireJsonOptions()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new OpenAiDoubleConverter());
        return options;
    }

    private sealed class OpenAiDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            => writer.WriteRawValue(value.ToString("0.0###############", CultureInfo.InvariantCulture));
    }
}
