using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Providers.OpenAi;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.OpenAi;

/// <summary>
/// Ports only the Brainarr base-contract cases that were not already covered by the
/// Common provider fixture. The accompanying audit maps all fifteen original cases.
/// </summary>
public sealed class OpenAiOriginalContractParityTests
{
    private const string Endpoint = "https://chat.example.test/v1/chat/completions";
    private const string OkBody = "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task JsonMode_EmitsResponseFormat_WhenSupported()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", JsonMode = true });

        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Temperature_SentByDefault_UsingProviderDefault()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport, defaultTemperature: 0.8);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi" });

        Assert.Contains("\"temperature\":0.8", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Temperature_RequestValueWinsOverDefault()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport, defaultTemperature: 0.8);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", Temperature = 0.5f });

        Assert.Contains("\"temperature\":0.5", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemPrompt_EmittedAsFirstMessage()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport);

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi", SystemPrompt = "sys" });

        Assert.Contains("\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"user\",\"content\":\"hi\"}]", transport.LastCompletionRequest!.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_UsesBearerAuth_AndExtraHeaders()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport, apiKey: "sk-contract", completionHeaders: new Dictionary<string, string> { ["X-Extra"] = "extra-value" });

        await provider.CompleteAsync(new LlmRequest { Prompt = "hi" });

        var request = transport.LastCompletionRequest!;
        Assert.Equal(Endpoint, request.Endpoint.ToString());
        Assert.Equal("Bearer sk-contract", request.Headers["Authorization"]);
        Assert.Equal("extra-value", request.Headers["X-Extra"]);
    }

    [Fact]
    public async Task AuthFailure_RecordsToCircuit_AndPreflightRejects()
    {
        var transport = new RecordingTransport { Completion = new OpenAiChatResponse(401, "{}") };
        var circuit = new ThresholdCircuit();
        var provider = new ParityProvider(transport, authCircuit: circuit);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));
        }

        var sendsBeforePreflight = transport.CompletionSendCount;
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => provider.CompleteAsync(new LlmRequest { Prompt = "hi" }));

        Assert.Contains("Auth circuit open", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, circuit.Failures);
        Assert.Equal(sendsBeforePreflight, transport.CompletionSendCount);
    }

    [Fact]
    public async Task HealthProbe_DefaultBody_UsesReplyWithOkAndFiveTokens()
    {
        var transport = new RecordingTransport();
        var provider = new ParityProvider(transport);

        var health = await provider.CheckHealthAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal("testchat", health.Provider);
        Assert.Equal(
            "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"Reply with OK\"}],\"max_tokens\":5}",
            transport.LastCompletionRequest!.JsonBody);
    }

    [Fact]
    public void UpdateModel_MapsThroughNormalizeModel()
    {
        var provider = new ParityProvider(new RecordingTransport(), mapModels: true);

        provider.UpdateModel("  ");
        Assert.Equal("test-model", provider.ExposedCurrentModel);
        provider.UpdateModel(" some-model ");

        Assert.Equal("mapped:some-model", provider.ExposedCurrentModel);
    }

    [Fact]
    public void Constructor_EmptyApiKey_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ParityProvider(new RecordingTransport(), apiKey: string.Empty));

        Assert.Equal("apiKey", exception.ParamName);
    }

    private sealed class ParityProvider : OpenAiChatProviderBase
    {
        private readonly IReadOnlyDictionary<string, string>? _completionHeaders;

        public ParityProvider(
            IOpenAiChatTransport transport,
            string apiKey = "test-key",
            IReadOnlyDictionary<string, string>? completionHeaders = null,
            IOpenAiChatAuthCircuit? authCircuit = null,
            double defaultTemperature = 0.7,
            bool mapModels = false)
            : base(transport, apiKey, "test-model", "testchat", "test-model", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), authCircuit)
        {
            _completionHeaders = completionHeaders;
            _defaultTemperature = defaultTemperature;
            _mapModels = mapModels;
        }

        private readonly double _defaultTemperature;
        private readonly bool _mapModels;

        public override string DisplayName => "Test Chat";
        public override LlmProviderCapabilities Capabilities => new() { Flags = LlmCapabilityFlags.TextCompletion | LlmCapabilityFlags.SystemPrompt, UsesOpenAiCompatibleApi = true };
        public string ExposedCurrentModel => CurrentModel;
        protected override Uri ChatCompletionsEndpoint => new(Endpoint);
        protected override double DefaultTemperature => _defaultTemperature;

        protected override void AddCompletionRequestHeaders(IDictionary<string, string> headers)
        {
            if (_completionHeaders is null) return;
            foreach (var (name, value) in _completionHeaders) headers[name] = value;
        }

        protected override string NormalizeModel(string model) => _mapModels ? "mapped:" + model.Trim() : model;
    }

    private sealed class RecordingTransport : IOpenAiChatTransport
    {
        public OpenAiChatResponse Completion { get; set; } = new(200, OkBody);
        public OpenAiChatRequest? LastCompletionRequest { get; private set; }
        public int CompletionSendCount { get; private set; }

        public ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
        {
            CompletionSendCount++;
            LastCompletionRequest = request;
            return ValueTask.FromResult(Completion);
        }

        public ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ThresholdCircuit : IOpenAiChatAuthCircuit
    {
        public int Failures { get; private set; }

        public bool IsOpen(string providerId, string credential, out string? reason)
        {
            reason = Failures >= 3 ? "threshold reached" : null;
            return Failures >= 3;
        }

        public void RecordAuthFailure(string providerId, string credential, LlmProviderException error) => Failures++;
        public void RecordSuccess(string providerId, string credential) { }
    }
}
