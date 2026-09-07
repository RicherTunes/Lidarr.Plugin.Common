using System;
using System.Collections.Generic;
using System.IO;
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
            "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":1,\"max_tokens\":2000,\"stream\":false}",
            transport.LastCompletionRequest!.JsonBody);
        Assert.Equal("provider-authorized", transport.LastCompletionRequest.Headers["Authorization"]);
        Assert.Equal("visible-hook", transport.LastCompletionRequest.Headers["X-Provider"]);
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
        Assert.False(health.IsHealthy); Assert.Equal("QuotaExceeded", health.ErrorCode);
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

    private sealed class TestProvider : OpenAiChatProviderBase
    {
        private readonly bool _sendsTemperature;
        private readonly bool _supportsJson;
        private readonly IReadOnlyDictionary<string, string>? _completionHeaders;
        private readonly Func<int, string?, TimeSpan?, Exception?, LlmProviderException>? _errorMapper;

        public TestProvider(IOpenAiChatTransport transport, bool sendsTemperature = true, bool supportsJson = true,
            IReadOnlyDictionary<string, string>? completionHeaders = null,
            Func<int, string?, TimeSpan?, Exception?, LlmProviderException>? errorMapper = null,
            IOpenAiChatAuthCircuit? authCircuit = null, bool parseEmpty = false)
            : base(transport, "test-key", "test-model", "test", "test-model", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), authCircuit)
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

        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        {
            LastCompletionRequest = request;
            return ValueTask.FromResult(Completion);
        }

        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OpenAiChatStreamResponse(200, StreamContent is null ? Stream.Null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StreamContent))));
    }

    private sealed class CancellingTransport : IOpenAiChatTransport
    {
        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromCanceled<OpenAiChatResponse>(cancellationToken);
        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken) => ValueTask.FromCanceled<OpenAiChatStreamResponse>(cancellationToken);
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
