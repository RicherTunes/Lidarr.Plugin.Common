using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Observability;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Providers.OpenAi;

/// <summary>Shared template for API-key providers using the OpenAI Chat Completions wire format.</summary>
public abstract class OpenAiChatProviderBase : ILlmProvider
{
    private static readonly JsonSerializerOptions WireJsonOptions = CreateWireJsonOptions();
    private static readonly Regex EscapedSurrogatePair = new(@"\\u(?<high>D[89ABab][0-9A-Fa-f]{2})\\u(?<low>D[C-Fc-f][0-9A-Fa-f]{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request/transport deadline fired (caller cancellation still propagates as a
            // raw cancellation): surface an unhealthy result instead of leaking a raw
            // TaskCanceled/OperationCanceled exception to health callers. The original provider
            // normalized timeouts in its SendAsync before the health catch filter ran; this
            // extraction moved that normalization out of the health path, so it is restored here.
            return ProviderHealthResult.Unhealthy("Request timed out.", stopwatch.Elapsed, ProviderId, "apiKey", _model, LlmErrorCode.Timeout.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ProviderHealthResult.Unhealthy(SanitizeText(exception.Message), stopwatch.Elapsed, ProviderId, "apiKey", _model);
        }
    }

    public virtual async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = BeginCompletionScope();
        OnCompletionRequestStarting();
        string? reason = null;
        bool circuitOpen;
        try { circuitOpen = _authCircuit?.IsOpen(ProviderId, _apiKey, out reason) == true; }
        catch (LlmProviderException exception) { throw SanitizeException(exception); }
        catch (Exception exception) { throw SanitizeException(LlmErrorMapper.MapException(ProviderId, exception)); }
        if (circuitOpen)
            throw new AuthenticationException(ProviderId, LlmErrorCode.AuthenticationFailed, SanitizeText("Auth circuit open: " + reason));

        try
        {
            var response = await SendAsync(BuildRequestBody(request), ResolveRequestTimeout(request), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != (int)HttpStatusCode.OK)
            {
                throw MapSafeHttpError(response.StatusCode, Truncate(response.Body), response.RetryAfter, response.TransportException);
            }

            var result = ParseCompletion(response.Body ?? string.Empty);
            if (string.IsNullOrWhiteSpace(result.Content)) throw InvalidResponse("The provider completion did not contain content.");
            _authCircuit?.RecordSuccess(ProviderId, _apiKey);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LlmProviderException exception)
        {
            var safe = SanitizeException(exception);
            RecordAuthFailureOnce(safe);
            throw safe;
        }
        catch (Exception exception)
        {
            throw SanitizeException(LlmErrorMapper.MapException(ProviderId, exception));
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
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return new LlmResponse { Content = content };
            if (choices.GetArrayLength() == 0) return new LlmResponse { Content = string.Empty };
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) return new LlmResponse { Content = content };
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new LlmResponse { Content = string.Empty };
            if (message.ValueKind != JsonValueKind.Object)
                return new LlmResponse { Content = content };
            if (!message.TryGetProperty("content", out var text)) return new LlmResponse { Content = string.Empty };
            if (text.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new LlmResponse { Content = string.Empty };
            if (text.ValueKind != JsonValueKind.String) return new LlmResponse { Content = content };
            LlmUsage? usage = null;
            JsonElement usageElement;
            if (document.RootElement.TryGetProperty("usage", out usageElement) && usageElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return new LlmResponse { Content = content };
            if (document.RootElement.TryGetProperty("usage", out usageElement) && usageElement.ValueKind == JsonValueKind.Object) usage = new LlmUsage
            {
                InputTokens = usageElement.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
                OutputTokens = usageElement.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
            };
            return new LlmResponse { Content = text.GetString()!, FinishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null, Usage = usage };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return new LlmResponse { Content = content };
        }
    }
    protected virtual LlmStreamChunk TransformStreamChunk(LlmStreamChunk chunk) => chunk;
    protected virtual string NormalizeModel(string model) => model;

    protected virtual object BuildRequestBody(LlmRequest request) => BuildChatBody(request, false);
    protected virtual object BuildStreamingRequestBody(LlmRequest request) => BuildChatBody(request, true);
    protected virtual string SerializeRequestBody(object body)
        => EscapedSurrogatePair.Replace(JsonSerializer.Serialize(body, WireJsonOptions), static match =>
            char.ConvertFromUtf32(char.ConvertToUtf32(
                (char)int.Parse(match.Groups["high"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                (char)int.Parse(match.Groups["low"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
    protected virtual void OnCompletionRequestStarting() { }
    /// <summary>Creates an optional scope lasting for the whole completion operation.</summary>
    protected virtual IDisposable? BeginCompletionScope() => null;

    private async Task<OpenAiChatResponse> SendAsync(object body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {_apiKey}", ["Content-Type"] = "application/json" };
        AddCompletionRequestHeaders(headers);
        return await _transport.SendAsync(new OpenAiChatRequest(ProviderId, ChatCompletionsEndpoint, SerializeRequestBody(body), headers, timeout), cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<LlmStreamChunk> StreamCoreAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = new ResilienceTimeout(ResolveRequestTimeout(request), cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {_apiKey}", ["Accept"] = "text/event-stream" };
        AddStreamingRequestHeaders(headers);
        var response = await OpenStreamAsync(new OpenAiChatRequest(ProviderId, ChatCompletionsEndpoint, SerializeRequestBody(BuildStreamingRequestBody(request)), headers, ResolveRequestTimeout(request)), timeout.Token, cancellationToken).ConfigureAwait(false);
        var emittedMeaningfulContent = false;
        var reachedTerminalState = false;
        Exception? primaryError = null;
        Exception? disposalError = null;
        IAsyncEnumerator<LlmStreamChunk>? enumerator = null;
        try
        {
            if (response.StatusCode != (int)HttpStatusCode.OK)
            {
                primaryError = SanitizeException(MapHttpError(response.StatusCode, Truncate(response.ErrorBody), response.RetryAfter, null));
                reachedTerminalState = true;
            }
            else
            {
                var decoder = new OpenAiStreamDecoder();
                enumerator = decoder.DecodeAsync(response.Content, timeout.Token).GetAsyncEnumerator(timeout.Token);
                while (primaryError is null)
                {
                    var next = await ReadNextStreamChunkAsync(enumerator, cancellationToken).ConfigureAwait(false);
                    if (next.Error is not null) { primaryError = next.Error; reachedTerminalState = true; break; }
                    if (!next.HasChunk) { reachedTerminalState = true; break; }
                    var transformed = next.Chunk!;
                    emittedMeaningfulContent |= !string.IsNullOrWhiteSpace(transformed.ContentDelta) || !string.IsNullOrWhiteSpace(transformed.ReasoningDelta);
                    yield return transformed;
                }
                if (primaryError is null && !emittedMeaningfulContent)
                    primaryError = InvalidResponse("The provider stream ended without completion content.");
            }
        }
        finally
        {
            disposalError = await DisposeStreamResourcesAsync(enumerator, response, cancellationToken).ConfigureAwait(false);
            if (!reachedTerminalState && disposalError is not null)
                ExceptionDispatchInfo.Capture(disposalError).Throw();
        }
        if (primaryError is not null) ExceptionDispatchInfo.Capture(primaryError).Throw();
        if (disposalError is not null) ExceptionDispatchInfo.Capture(disposalError).Throw();
    }

    private object BuildChatBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object?> { ["model"] = string.IsNullOrWhiteSpace(request.Model) ? _model : NormalizeModel(request.Model), ["messages"] = string.IsNullOrEmpty(request.SystemPrompt) ? new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = request.Prompt } } : new[] { new Dictionary<string, string> { ["role"] = "system", ["content"] = request.SystemPrompt }, new Dictionary<string, string> { ["role"] = "user", ["content"] = request.Prompt } } };
        if (SendsTemperature) body["temperature"] = request.Temperature is { } temperature ? (object)temperature : DefaultTemperature;
        body["max_tokens"] = request.MaxTokens ?? 2000;
        body["stream"] = stream;
        if (SupportsJsonResponseFormat && IsJsonModeRequested(request)) body["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
        return body;
    }

    private void RecordAuthFailureOnce(LlmProviderException exception)
    {
        if (exception.ErrorCode is not (LlmErrorCode.AuthenticationFailed or LlmErrorCode.AuthorizationFailed)) return;
        try { _authCircuit?.RecordAuthFailure(ProviderId, _apiKey, exception); }
        catch { /* A bookkeeping callback cannot replace the provider error. */ }
    }

    private ProviderException InvalidResponse(string message) => new(ProviderId, LlmErrorCode.InvalidRequest, message);
    private LlmProviderException MapSafeHttpError(int statusCode, string? body, TimeSpan? retryAfter, Exception? inner)
    {
        return SanitizeException(MapHttpError(statusCode, body, retryAfter, inner));
    }
    private async ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken, CancellationToken callerCancellationToken)
    {
        try { return await _transport.OpenStreamAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested) { throw; }
        catch (LlmProviderException exception) { throw SanitizeException(exception); }
        catch (Exception exception) { throw SanitizeException(LlmErrorMapper.MapException(ProviderId, exception)); }
    }
    private async ValueTask<StreamReadResult> ReadNextStreamChunkAsync(IAsyncEnumerator<LlmStreamChunk> enumerator, CancellationToken callerCancellationToken)
    {
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) return new(false, null, null);
            return new(true, TransformStreamChunk(enumerator.Current), null);
        }
        catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested) { return new(false, null, exception); }
        catch (LlmProviderException exception) { return new(false, null, SanitizeException(exception)); }
        catch (Exception exception) { return new(false, null, SanitizeException(LlmErrorMapper.MapException(ProviderId, exception))); }
    }
    private async ValueTask<Exception?> DisposeStreamResourcesAsync(IAsyncEnumerator<LlmStreamChunk>? enumerator, OpenAiChatStreamResponse response, CancellationToken callerCancellationToken)
    {
        Exception? error = null;
        try { if (enumerator is not null) await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { error = MapStreamException(exception, callerCancellationToken); }
        try { await response.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { error ??= MapStreamException(exception, callerCancellationToken); }
        return error;
    }
    private Exception MapStreamException(Exception exception, CancellationToken callerCancellationToken)
        => exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested
            ? exception
            : exception is LlmProviderException providerException
                ? SanitizeException(providerException)
                : SanitizeException(LlmErrorMapper.MapException(ProviderId, exception));
    private readonly record struct StreamReadResult(bool HasChunk, LlmStreamChunk? Chunk, Exception? Error);
    private LlmProviderException SanitizeException(LlmProviderException exception)
    {
        if (!exception.ToString().Contains(_apiKey, StringComparison.Ordinal)) return exception;
        var message = SanitizeText(exception.Message);
        return exception switch
        {
            RateLimitException => new RateLimitException(ProviderId, exception.ErrorCode, message, exception.RetryAfter),
            AuthenticationException => new AuthenticationException(ProviderId, exception.ErrorCode, message),
            NetworkException => new NetworkException(ProviderId, exception.ErrorCode, message),
            _ => new ProviderException(ProviderId, exception.ErrorCode, message),
        };
    }
    private string SanitizeText(string text) => LogRedactor.Redact(text).Replace(_apiKey, LogRedactor.REDACTED, StringComparison.Ordinal);
    private static string? Truncate(string? body) => string.IsNullOrEmpty(body) || body.Length <= 500 ? body : body[..500];

    private TimeSpan ResolveRequestTimeout(LlmRequest request)
        => request.Timeout is { } requested && requested > TimeSpan.Zero && requested < CompletionTimeout
            ? requested
            : CompletionTimeout;

    private static JsonSerializerOptions CreateWireJsonOptions()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new OpenAiSingleConverter());
        options.Converters.Add(new OpenAiDoubleConverter());
        return options;
    }

    private sealed class OpenAiDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            => writer.WriteRawValue(FormatNumber(value));
    }

    private sealed class OpenAiSingleConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetSingle();
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
            => writer.WriteRawValue(FormatNumber(value));
    }

    private static string FormatNumber<T>(T value) where T : IFormattable
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'e']) < 0 ? text + ".0" : text;
    }
}
