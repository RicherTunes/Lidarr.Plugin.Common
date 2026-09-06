using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HttpJsonOwnershipTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "http-error")]
    [InlineData(true, "http-error")]
    [InlineData(false, "invalid-json")]
    [InlineData(true, "invalid-json")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "whitespace")]
    [InlineData(true, "whitespace")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "non-json-media")]
    [InlineData(true, "non-json-media")]
    public async Task Should_DisposeOwnedResponse_WhilePreservingJsonContracts(bool post, string scenario)
    {
        var body = scenario switch
        {
            "invalid-json" => "{",
            "null" => "null",
            "whitespace" => " \t ",
            "empty" => string.Empty,
            _ => "{\"Value\":\"retained\"}"
        };
        using var content = new TrackedContent(body, scenario == "non-json-media" ? "text/plain" : "application/json");
        using var response = new HttpResponseMessage(scenario == "http-error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
        {
            Content = content
        };
        HttpContent? requestBody = null;
        using var handler = new Handler((request, _) =>
        {
            requestBody = request.Content;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        Payload? result = null;

        var error = await Record.ExceptionAsync(async () =>
            result = await ExecuteAsync(client, post));

        if (scenario == "http-error") Assert.IsType<HttpRequestException>(error);
        else if (scenario == "invalid-json" || (post && scenario is "whitespace" or "empty")) Assert.IsType<JsonException>(error);
        else if (!post && (scenario == "null" || scenario == "non-json-media")) Assert.IsType<InvalidOperationException>(error);
        else
        {
            Assert.Null(error);
            if (scenario is "null" or "whitespace" or "empty") Assert.Null(result);
            else Assert.Equal("retained", result!.Value);
        }

        Assert.Equal(1, content.DisposeCount);
        Assert.False(handler.IsDisposed); // The supplied client and handler remain caller-owned.
        Assert.Equal(1, handler.SendCount);
        if (post)
        {
            Assert.NotNull(requestBody);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => requestBody!.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveCaseSensitivityAndExplicitSerializerOptions(bool post)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":\"lowercase\"}", Encoding.UTF8, "application/json")
        }));
        using var client = new HttpClient(handler);
        var defaultResult = await ExecuteAsync(client, post);
        var explicitResult = await ExecuteAsync(client, post, options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(post ? null : "lowercase", defaultResult!.Value);
        Assert.Equal("lowercase", explicitResult!.Value);
        Assert.Equal(2, handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_DisposePostBody_WhenSendThrows(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        HttpContent? requestBody = null;
        string? serialized = null;
        using var handler = new Handler(async (request, _) =>
        {
            requestBody = request.Content;
            serialized = await request.Content!.ReadAsStringAsync();
            if (cancelled)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new HttpRequestException("synthetic transport failure");
        });
        using var client = new HttpClient(handler);

        var error = await Record.ExceptionAsync(() => ExecuteAsync(client, true, cancellation.Token));

        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<HttpRequestException>(error);
        Assert.Equal("{\"Value\":\"outbound\"}", serialized);
        Assert.NotNull(requestBody);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => requestBody!.ReadAsStringAsync());
        Assert.False(handler.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ObserveCallerCancellation_AfterResponseBuffering(bool post)
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new CancelAfterBufferingContent(cancellation);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new Handler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(client, post, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, content.SerializeCount);
        Assert.Equal(1, content.DisposeCount);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task Should_PreservePostSerializationOptionsAndJsonContentHeaders()
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
            Assert.Equal("{\"value\":\"outbound\"}", await request.Content.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"custom options\"}", Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var result = await ExecuteAsync(client, true, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("custom options", result.Value);
    }

    private static Task<Payload> ExecuteAsync(HttpClient client, bool post, CancellationToken cancellation = default, JsonSerializerOptions? options = null)
        => post
            ? HttpClientExtensions.PostJsonAsync<Payload, Payload>(client, "https://json-ownership.test/item", new Payload { Value = "outbound" }, options!, cancellation)
            : HttpClientExtensions.GetJsonAsync<Payload>(client, "https://json-ownership.test/item", options!, cancellation);

    public sealed class Payload
    {
        public string? Value { get; set; }
    }

    private sealed class TrackedContent : ByteArrayContent
    {
        public TrackedContent(string value, string mediaType) : base(Encoding.UTF8.GetBytes(value))
        {
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterBufferingContent : HttpContent
    {
        private readonly CancellationTokenSource _caller;
        public CancelAfterBufferingContent(CancellationTokenSource caller)
        {
            _caller = caller;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public int DisposeCount { get; private set; }
        public int SerializeCount { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializeCount++;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"Value\":\"too late\"}"));
            _caller.Cancel();
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        public int SendCount { get; private set; }
        public bool IsDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return _send(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
