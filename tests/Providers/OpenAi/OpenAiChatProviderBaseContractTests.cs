using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Providers.OpenAi;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.OpenAi;

public sealed class OpenAiChatProviderBaseContractTests
{
    private const string Endpoint = "https://chat.example.test/v1/chat/completions";
    private const string OkBody = "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task CompleteAsync_SendsThePinnedDefaultWireBody()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi" });

        Assert.Equal(
            "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":0.7,\"max_tokens\":2000,\"stream\":false}",
            transport.LastCompletionRequest!.JsonBody);
    }

    [Fact]
    public async Task CompleteAsync_UsesRequestTemperatureAndProviderHeaderPrecedence()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport, completionHeaders: new Dictionary<string, string>
        {
            ["Authorization"] = "provider-authorized",
            ["X-Provider"] = "visible-hook",
        });

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", Temperature = 1.0f });

        Assert.Equal(
            "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":1.0,\"max_tokens\":2000,\"stream\":false}",
            transport.LastCompletionRequest!.JsonBody);
        Assert.Equal("provider-authorized", transport.LastCompletionRequest.Headers["Authorization"]);
        Assert.Equal("visible-hook", transport.LastCompletionRequest.Headers["X-Provider"]);
    }

    [Theory]
    [InlineData(0.0f, "0.0")]
    [InlineData(1.0f, "1.0")]
    public async Task CompleteAsync_PreservesIntegralTemperatureDecimalNotation(float temperature, string wireValue)
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", Temperature = temperature });

        Assert.Contains($"\"temperature\":{wireValue}", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(2, 2)]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    public async Task CompleteAsync_RequestTimeoutCannotExtendOrDisableProviderTimeout(int requestSeconds, int expectedSeconds)
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport, completionTimeout: TimeSpan.FromSeconds(5));
        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", Timeout = TimeSpan.FromSeconds(requestSeconds) });
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), transport.LastCompletionRequest!.Timeout);
    }

    [Fact]
    public async Task StreamAsync_RequestTimeoutCannotExtendProviderTimeout()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody), StreamContent = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n" };
        var provider = new TestProvider(transport, completionTimeout: TimeSpan.FromSeconds(5));
        await foreach (var _ in provider.StreamAsync(new LlmRequest { Prompt = "hi", Timeout = TimeSpan.FromSeconds(10) })!) { }
        Assert.Equal(TimeSpan.FromSeconds(5), transport.LastStreamRequest!.Timeout);
    }

    [Fact]
    public async Task StreamAsync_ShortRequestTimeoutCancelsTheWholeStreamAsRecoverableTimeout()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var provider = new TestProvider(new BlockingTransport(), completionTimeout: TimeSpan.FromSeconds(5));
        var stream = provider.StreamAsync(new LlmRequest { Prompt = "hi", Timeout = TimeSpan.FromMilliseconds(30) }, caller.Token);
        var exception = await Assert.ThrowsAsync<NetworkException>(async () => { await foreach (var _ in stream!) { } });
        Assert.Equal(LlmErrorCode.Timeout, exception.ErrorCode);
    }

    [Fact]
    public async Task StreamAsync_ReadTimeoutMapsToRecoverableTimeoutAndDisposesResponse()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var transport = new BlockingReadTransport();
        var provider = new TestProvider(transport, completionTimeout: TimeSpan.FromSeconds(5));
        var stream = provider.StreamAsync(new LlmRequest { Prompt = "hi", Timeout = TimeSpan.FromMilliseconds(30) }, caller.Token);
        var exception = await Assert.ThrowsAsync<NetworkException>(async () => { await foreach (var _ in stream!) { } });
        Assert.Equal(LlmErrorCode.Timeout, exception.ErrorCode);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task StreamAsync_CallerCancellationDuringBlockedReadPropagatesAndDisposesResponse()
    {
        using var caller = new CancellationTokenSource();
        var transport = new BlockingReadTransport();
        var provider = new TestProvider(transport, completionTimeout: TimeSpan.FromSeconds(5));
        var stream = provider.StreamAsync(new LlmRequest { Prompt = "hi", Timeout = TimeSpan.FromSeconds(5) }, caller.Token);

        var enumeration = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream!) { }
        });
        await transport.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1));
        caller.Cancel();

        await enumeration;
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task CheckHealthAsync_BareReturnedNonSuccessPreservesNumericHttpStatus()
    {
        var provider = new TestProvider(new ScriptedTransport { Completion = new(401, "denied") });
        var health = await provider.CheckHealthAsync();
        Assert.False(health.IsHealthy);
        Assert.Equal("401", health.ErrorCode);
        Assert.Equal("HTTP 401", health.StatusMessage);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotExposeTheKnownApiKeyInMappedErrorText()
    {
        const string secret = "short-opaque-test-secret";
        var provider = new TestProvider(new ScriptedTransport { Completion = new(400, $"{{\"message\":\"failed {secret}\"}}") }, apiKey: secret);
        var exception = await Assert.ThrowsAnyAsync<LlmProviderException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckHealthAsync_DoesNotExposeKnownApiKeyFromTransportException()
    {
        const string secret = "short-opaque-test-secret";
        var provider = new TestProvider(new ThrowingTransport(secret), apiKey: secret);
        var health = await provider.CheckHealthAsync();
        Assert.False(health.IsHealthy);
        Assert.DoesNotContain(secret, health.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_DoesNotExposeKnownApiKeyFromTransportException()
    {
        const string secret = "short-opaque-test-secret";
        var provider = new TestProvider(new ThrowingTransport(secret), apiKey: secret);
        var stream = provider.StreamAsync(new LlmRequest { Prompt = "hi" });
        var exception = await Assert.ThrowsAnyAsync<LlmProviderException>(async () => { await foreach (var _ in stream!) { } });
        Assert.Equal(LlmErrorCode.ConnectionFailed, exception.ErrorCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TransportResponse_ExposesBufferedTransportExceptionForErrorMapping()
    {
        var property = typeof(OpenAiChatResponse).GetProperty("TransportException");
        Assert.NotNull(property);
        Assert.Equal(typeof(Exception), property!.PropertyType);
    }

    [Fact]
    public async Task CompleteAsync_PreservesUnicodeAndHtmlInTheLegacyWireBody()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport);

        await provider.CompleteAsync(new LlmRequest { Prompt = "<tag> café" });

        Assert.Contains("<tag> café", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", transport.LastCompletionRequest.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_OmitsTemperatureAndResponseFormatWhenHooksDisableThem()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport, sendsTemperature: false, supportsJson: false);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", Temperature = 0.0f, JsonMode = true });

        Assert.DoesNotContain("temperature", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("response_format", transport.LastCompletionRequest.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_RejectsAnEmptyResponseInsteadOfReturningAQuietEmptySuccess()
    {
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, string.Empty) });

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));

        Assert.Equal(LlmErrorCode.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_RejectsAChoiceLessJsonResponseInsteadOfReturningAQuietEmptySuccess()
    {
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, "{\"choices\":[]}") });

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));

        Assert.Equal(LlmErrorCode.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_UsesTheProviderErrorMapperBeforeTheDefaultMapper()
    {
        var provider = new TestProvider(
            new ScriptedTransport { Completion = new(429, "{\"error\":{\"code\":\"1113\"}}") },
            errorMapper: static (_, _, _, _) => new ProviderException("test", LlmErrorCode.QuotaExceeded, "custom mapper"));

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));

        Assert.Equal(LlmErrorCode.QuotaExceeded, exception.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_ShortCircuitsAnOpenAuthCircuitBeforeTransport()
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport, authCircuit: new OpenCircuit());

        await Assert.ThrowsAsync<AuthenticationException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));

        Assert.Null(transport.LastCompletionRequest);
    }

    [Fact]
    public async Task CompleteAsync_ParsesFinishReasonUsageAndMalformedSalvage()
    {
        var transport = new ScriptedTransport { Completion = new(200, "{\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}") };
        var provider = new TestProvider(transport);
        var response = await provider.CompleteAsync(new LlmRequest { Prompt = "hi" });
        Assert.Equal("hello", response.Content); Assert.Equal("stop", response.FinishReason); Assert.Equal(7, response.Usage!.TotalTokens);
        transport.Completion = new(200, "[{\"artist\":\"a\"");
        Assert.Equal("[{\"artist\":\"a\"", (await provider.CompleteAsync(new LlmRequest { Prompt = "hi" })).Content);
    }

    [Fact]
    public async Task CheckHealthAsync_UsesPinnedProbeAndCustomErrorMapping()
    {
        var transport = new ScriptedTransport { Completion = new(503, "busy") };
        var provider = new TestProvider(transport, errorMapper: static (_, _, _, _) => new ProviderException("test", LlmErrorCode.QuotaExceeded, "custom health"));
        var health = await provider.CheckHealthAsync();
        Assert.False(health.IsHealthy); Assert.Equal("503", health.ErrorCode);
        Assert.Equal("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"Reply with OK\"}],\"max_tokens\":5}", transport.LastCompletionRequest!.JsonBody);
    }

    [Fact]
    public async Task CompleteAsync_RecordsReturnedAuthenticationFailureOnceAndSuccessOnNextCall()
    {
        var circuit = new RecordingCircuit();
        var transport = new ScriptedTransport { Completion = new(401, "bad") };
        var provider = new TestProvider(transport, authCircuit: circuit);
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));
        Assert.Equal(1, circuit.Failures);
        transport.Completion = new(200, OkBody);
        await provider.CompleteAsync(new LlmRequest { Prompt = "hi" });
        Assert.Equal(1, circuit.Successes);
    }

    [Fact]
    public async Task CompleteAsync_PreservesExplicitRetryAfterForProviderMapping()
    {
        TimeSpan? observed = null;
        var provider = new TestProvider(new ScriptedTransport { Completion = new(429, "body", TimeSpan.Zero) }, errorMapper: (_, _, retryAfter, _) => { observed = retryAfter; return new RateLimitException("test", "limited", retryAfter); });
        await Assert.ThrowsAsync<RateLimitException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));
        Assert.Equal(TimeSpan.Zero, observed);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var transport = new CancellingTransport();
        var provider = new TestProvider(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }, cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCancelledCallerWinsOverAnOpenAuthCircuit()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, OkBody) }, authCircuit: new OpenCircuit());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }, cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_RejectsAnEmptyOverrideParseResult()
    {
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, OkBody) }, parseEmpty: true);
        await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));
    }

    [Fact]
    public async Task StreamAsync_RejectsDoneOnlyStreamWithoutMeaningfulContent()
    {
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, OkBody), StreamContent = "data: [DONE]\n\n" });
        var stream = provider.StreamAsync(new LlmRequest { Prompt = "hi" });

        await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in stream!) { }
        });
    }

    [Fact]
    public void StreamAsync_AlreadyCancelledCallerThrowsBeforeReturningEnumerable()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var provider = new TestProvider(new ScriptedTransport { Completion = new(200, OkBody) });
        Assert.ThrowsAny<OperationCanceledException>(() => provider.StreamAsync(new LlmRequest { Prompt = "hi" }, cts.Token));
    }

    [Fact]
    public async Task StreamAsync_PreservesMappedRateLimitMetadataFromOpenFailure()
    {
        var expected = new RateLimitException("test", "limited", TimeSpan.FromSeconds(7));
        var provider = new TestProvider(new MappedThrowingTransport(expected));

        var error = await Assert.ThrowsAsync<RateLimitException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new LlmRequest { Prompt = "hi" })!) { }
        });

        Assert.Equal(LlmErrorCode.RateLimited, error.ErrorCode);
        Assert.True(error.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.Same(expected, error);
    }

    [Theory]
    [InlineData(0.2f, "0.2")]
    [InlineData(0.33333334f, "0.33333334")]
    public async Task CompleteAsync_PreservesLegacyFloatWirePrecision(float temperature, string wireValue)
    {
        var transport = new ScriptedTransport { Completion = new(200, OkBody) };
        var provider = new TestProvider(transport);
        await provider.CompleteAsync(new LlmRequest { Prompt = "🎵 <tag>\n\u0001", Temperature = temperature });

        Assert.Contains($"\"temperature\":{wireValue},", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
        Assert.Contains("🎵 <tag>\\n\\u0001", transport.LastCompletionRequest.JsonBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\uD83C\\uDFB5", transport.LastCompletionRequest.JsonBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestProvider : OpenAiChatProviderBase
    {
        private readonly bool _sendsTemperature;
        private readonly bool _supportsJson;
        private readonly IReadOnlyDictionary<string, string>? _completionHeaders;
        private readonly Func<int, string?, TimeSpan?, Exception?, LlmProviderException>? _errorMapper;

        public TestProvider(IOpenAiChatTransport transport, bool sendsTemperature = true, bool supportsJson = true,
            IReadOnlyDictionary<string, string>? completionHeaders = null,
            Func<int, string?, TimeSpan?, Exception?, LlmProviderException>? errorMapper = null,
            IOpenAiChatAuthCircuit? authCircuit = null, bool parseEmpty = false, TimeSpan? completionTimeout = null, string apiKey = "test-key")
            : base(transport, apiKey, "test-model", "test", "test-model", completionTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), authCircuit)
        {
            _sendsTemperature = sendsTemperature;
            _supportsJson = supportsJson;
            _completionHeaders = completionHeaders;
            _errorMapper = errorMapper;
            _parseEmpty = parseEmpty;
        }

        private readonly bool _parseEmpty;

        public override string DisplayName => "Test";
        public override LlmProviderCapabilities Capabilities => new() { Flags = LlmCapabilityFlags.TextCompletion, UsesOpenAiCompatibleApi = true };
        protected override Uri ChatCompletionsEndpoint => new(Endpoint);
        protected override bool SendsTemperature => _sendsTemperature;
        protected override bool SupportsJsonResponseFormat => _supportsJson;

        protected override void AddCompletionRequestHeaders(IDictionary<string, string> headers)
        {
            if (_completionHeaders is null) return;
            foreach (var (name, value) in _completionHeaders) headers[name] = value;
        }

        protected override LlmProviderException MapHttpError(int statusCode, string? body, TimeSpan? retryAfter, Exception? inner)
            => _errorMapper?.Invoke(statusCode, body, retryAfter, inner) ?? base.MapHttpError(statusCode, body, retryAfter, inner);
        protected override LlmResponse ParseCompletion(string content) => _parseEmpty ? new LlmResponse { Content = string.Empty } : base.ParseCompletion(content);
    }

    private sealed class ScriptedTransport : IOpenAiChatTransport
    {
        public required OpenAiChatResponse Completion { get; set; }
        public string? StreamContent { get; init; }
        public OpenAiChatRequest? LastCompletionRequest { get; private set; }
        public OpenAiChatRequest? LastStreamRequest { get; private set; }

        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        {
            LastCompletionRequest = request;
            return ValueTask.FromResult(Completion);
        }

        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        {
            LastStreamRequest = request;
            return ValueTask.FromResult(new OpenAiChatStreamResponse(200, StreamContent is null ? Stream.Null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StreamContent))));
        }
    }

    private sealed class CancellingTransport : IOpenAiChatTransport
    {
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromCanceled<OpenAiChatResponse>(cancellationToken);
        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromCanceled<OpenAiChatStreamResponse>(cancellationToken);
    }

    private sealed class MappedThrowingTransport(LlmProviderException exception) : IOpenAiChatTransport
    {
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
            => ValueTask.FromException<OpenAiChatResponse>(exception);
        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
            => ValueTask.FromException<OpenAiChatStreamResponse>(exception);
    }

    private sealed class ThrowingTransport(string secret) : IOpenAiChatTransport
    {
        private readonly HttpRequestException _exception = new($"transport {secret}", new InvalidOperationException($"inner {secret}"));
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromException<OpenAiChatResponse>(_exception);
        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromException<OpenAiChatStreamResponse>(_exception);
    }

    private sealed class BlockingTransport : IOpenAiChatTransport
    {
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new OpenAiChatResponse(200, OkBody));
        public async ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class BlockingReadTransport : IOpenAiChatTransport
    {
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task ReadStarted => _readStarted.Task;
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new OpenAiChatResponse(200, OkBody));
        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OpenAiChatStreamResponse(200, new BlockingReadStream(_readStarted), dispose: () => { Disposed = true; return ValueTask.CompletedTask; }));
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _readStarted;
        public BlockingReadStream(TaskCompletionSource<bool> readStarted) => _readStarted = readStarted;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult(true);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => 0, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(true);
            return new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => 0, cancellationToken));
        }
        public override void Flush() { } public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask; public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingCircuit : IOpenAiChatAuthCircuit
    {
        public int Failures { get; private set; }
        public int Successes { get; private set; }
        public bool IsOpen(string providerId, string credential, out string? reason) { reason = null; return false; }
        public void RecordAuthFailure(string providerId, string credential, LlmProviderException error) => Failures++;
        public void RecordSuccess(string providerId, string credential) => Successes++;
    }

    private sealed class OpenCircuit : IOpenAiChatAuthCircuit
    {
        public bool IsOpen(string providerId, string credential, out string? reason)
        {
            reason = "known bad credential";
            return true;
        }

        public void RecordAuthFailure(string providerId, string credential, LlmProviderException error) { }
        public void RecordSuccess(string providerId, string credential) { }
    }
}
