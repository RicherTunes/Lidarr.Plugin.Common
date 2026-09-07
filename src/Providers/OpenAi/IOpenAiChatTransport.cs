using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Providers.OpenAi;

/// <summary>Transport seam for OpenAI Chat Completions providers.</summary>
public interface IOpenAiChatTransport
{
    ValueTask<OpenAiChatResponse> SendAsync(OpenAiChatRequest request, CancellationToken cancellationToken);

    ValueTask<OpenAiChatStreamResponse> OpenStreamAsync(OpenAiChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Immutable outbound OpenAI Chat Completions request.</summary>
public sealed record OpenAiChatRequest(
    Uri Endpoint,
    string JsonBody,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout);

/// <summary>Buffered response returned by an OpenAI Chat Completions transport.</summary>
public sealed record OpenAiChatResponse(int StatusCode, string? Body, TimeSpan? RetryAfter = null);

/// <summary>Streaming response whose owner is disposed after decoding completes or is cancelled.</summary>
public sealed class OpenAiChatStreamResponse : IAsyncDisposable
{
    private readonly Func<ValueTask>? _dispose;

    public OpenAiChatStreamResponse(int statusCode, Stream content, string? errorBody = null, TimeSpan? retryAfter = null, Func<ValueTask>? dispose = null)
    {
        StatusCode = statusCode;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        ErrorBody = errorBody;
        RetryAfter = retryAfter;
        _dispose = dispose;
    }

    public int StatusCode { get; }
    public Stream Content { get; }
    public string? ErrorBody { get; }
    public TimeSpan? RetryAfter { get; }

    public ValueTask DisposeAsync() => _dispose is null ? Content.DisposeAsync() : _dispose();
}
