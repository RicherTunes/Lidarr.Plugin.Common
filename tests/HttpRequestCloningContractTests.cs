using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HttpRequestCloningContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveRequestEnvelopeAndTypedOptions_WithoutSharingOwnedContent(bool retry)
    {
        using var source = new HttpRequestMessage(HttpMethod.Patch, "relative/resource")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent("owned by the caller", Encoding.UTF8, "text/plain")
        };
        source.Headers.TryAddWithoutValidation("X-Multiple", new[] { "first", "second" });
        source.Content.Headers.TryAddWithoutValidation("X-Content", "preserve");
        var marker = new Marker();
        Func<int> callback = () => 42;
        source.Options.Set(new HttpRequestOptionsKey<IMarker>("interface"), marker);
        source.Options.Set(new HttpRequestOptionsKey<int>("integer"), 17);
        source.Options.Set(new HttpRequestOptionsKey<int?>("nullable"), 23);
        source.Options.Set(new HttpRequestOptionsKey<DayOfWeek>("enum"), DayOfWeek.Friday);
        source.Options.Set(new HttpRequestOptionsKey<Func<int>>("callback"), callback);
        source.Options.Set(new HttpRequestOptionsKey<object?>("explicit-null"), null);
        source.Options.Set(new HttpRequestOptionsKey<int?>("null-nullable"), null);
        source.Options.Set(new HttpRequestOptionsKey<string>("text"), "original");

        using (var clone = await CloneAsync(source, retry))
        {
            Assert.NotSame(source, clone);
            Assert.Equal(source.Method, clone.Method);
            Assert.Equal(source.RequestUri, clone.RequestUri);
            Assert.Equal(source.Version, clone.Version);
            Assert.Equal(source.VersionPolicy, clone.VersionPolicy);
            Assert.Equal(new[] { "first", "second" }, clone.Headers.GetValues("X-Multiple"));
            Assert.Equal(new[] { "preserve" }, clone.Content!.Headers.GetValues("X-Content"));
            Assert.Equal(source.Content.Headers.ContentType, clone.Content.Headers.ContentType);
            Assert.NotSame(source.Content, clone.Content);
            Assert.Equal("owned by the caller", await clone.Content.ReadAsStringAsync());
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<IMarker>("interface"), out var copiedMarker));
            Assert.Same(marker, copiedMarker);
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<int>("integer"), out var copiedInteger));
            Assert.Equal(17, copiedInteger);
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<int?>("nullable"), out var copiedNullable));
            Assert.Equal(23, copiedNullable);
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<DayOfWeek>("enum"), out var copiedEnum));
            Assert.Equal(DayOfWeek.Friday, copiedEnum);
            Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<Func<int>>("callback"), out var copiedCallback));
            Assert.Same(callback, copiedCallback);
            var dictionary = (IReadOnlyDictionary<string, object?>)clone.Options;
            Assert.True(dictionary.ContainsKey("explicit-null"));
            Assert.Null(dictionary["explicit-null"]);
            Assert.Equal(source.Options.TryGetValue(new HttpRequestOptionsKey<object?>("explicit-null"), out var sourceNull),
                clone.Options.TryGetValue(new HttpRequestOptionsKey<object?>("explicit-null"), out var cloneNull));
            Assert.Equal(sourceNull, cloneNull);
            Assert.True(dictionary.ContainsKey("null-nullable"));
            Assert.Equal(source.Options.TryGetValue(new HttpRequestOptionsKey<int?>("null-nullable"), out var sourceNullableNull),
                clone.Options.TryGetValue(new HttpRequestOptionsKey<int?>("null-nullable"), out var cloneNullableNull));
            Assert.Equal(sourceNullableNull, cloneNullableNull);
            clone.Options.Set(new HttpRequestOptionsKey<string>("text"), "clone only");
            Assert.True(source.Options.TryGetValue(new HttpRequestOptionsKey<string>("text"), out var originalText));
            Assert.Equal("original", originalText);
        }

        Assert.Equal("owned by the caller", await source.Content.ReadAsStringAsync());
        using var secondClone = await CloneAsync(source, retry);
        Assert.Equal("owned by the caller", await secondClone.Content!.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveTheDistinctionBetweenOneOffCloningAndRetryCaching(bool retry)
    {
        using var source = new HttpRequestMessage(HttpMethod.Post, "https://clone-contract.test/item")
        {
            Content = new StringContent("live content")
        };
        var cached = Encoding.UTF8.GetBytes("cached retry content");
        source.Options.Set(PluginHttpOptions.BufferedBodyKey, cached);

        using var clone = await CloneAsync(source, retry);

        Assert.Equal(retry ? "cached retry content" : "live content", await clone.Content!.ReadAsStringAsync());
        Assert.True(source.Options.TryGetValue(PluginHttpOptions.BufferedBodyKey, out var remainingCache));
        Assert.Same(cached, remainingCache);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Should_PropagateBodyFailureOnce_WithoutRetryingSerialization(bool retry, bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException("synthetic cancellation") : new IOException("synthetic unreadable content");
        using var content = new FailingContent(failure);
        using var source = new HttpRequestMessage(HttpMethod.Post, "https://clone-failure.test/item") { Content = content };

        var error = await Record.ExceptionAsync(() => CloneAsync(source, retry));

        Assert.NotNull(error);
        Assert.Same(failure, error is HttpRequestException wrapped ? wrapped.InnerException : error);
        Assert.Equal(1, content.SerializeCount);
        Assert.False(content.IsDisposed);
        Assert.False(source.Options.TryGetValue(PluginHttpOptions.BufferedBodyKey, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveNoContentAndEmptyRequestOptions(bool retry)
    {
        using var source = new HttpRequestMessage(HttpMethod.Get, "https://clone-empty.test/item");
        using var clone = await CloneAsync(source, retry);
        Assert.Null(clone.Content);
        Assert.Empty(clone.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RejectMissingSourceWithAnArgumentException(bool retry)
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => CloneAsync(null!, retry));
        Assert.Equal("request", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PopulateAndReuseBodyCache_OnlyForRetryCloning(bool retry)
    {
        using var source = new HttpRequestMessage(HttpMethod.Post, "https://clone-cache.test/item")
        {
            Content = new StringContent("cache once")
        };
        using var first = await CloneAsync(source, retry);
        Assert.Equal(retry, source.Options.TryGetValue(PluginHttpOptions.BufferedBodyKey, out var initialCache));
        if (retry) Assert.Equal("cache once", Encoding.UTF8.GetString(initialCache!));
        using var second = await CloneAsync(source, retry);
        Assert.Equal(retry, source.Options.TryGetValue(PluginHttpOptions.BufferedBodyKey, out var remainingCache));
        Assert.Same(initialCache, remainingCache);
        Assert.NotSame(first.Content, second.Content);
        first.Dispose();
        Assert.Equal("cache once", await second.Content!.ReadAsStringAsync());
        Assert.Equal("cache once", await source.Content.ReadAsStringAsync());
    }

    private static Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, bool retry)
        => retry ? HttpClientExtensions.CloneForRetryAsync(source) : HttpClientExtensions.CloneHttpRequestMessageAsync(source);

    private interface IMarker { }
    private sealed class Marker : IMarker { }

    private sealed class FailingContent : HttpContent
    {
        private readonly Exception _failure;
        public FailingContent(Exception failure) => _failure = failure;
        public int SerializeCount { get; private set; }
        public bool IsDisposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializeCount++;
            return Task.FromException(_failure);
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
