using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Resilience;
using Lidarr.Plugin.Common.Services.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Http
{
    /// <summary>
    /// Behavior-pinning tests for <see cref="BackendHealthDelegatingHandler"/>:
    /// fast-fail short-circuit while a backend is known-down, MarkDown only on
    /// connection-class failures, MarkUp on success, per-baseUrl cache slots, and
    /// the fixed-provider convenience overload. Time is controlled via
    /// <see cref="FakeTimeProvider"/> — no wall-clock races.
    /// </summary>
    [Trait("Category", "Unit")]
    public class BackendHealthDelegatingHandlerTests
    {
        private const int GraceSeconds = 30;

        /// <summary>Stub inner handler: scripted responses/exceptions + request capture.</summary>
        private sealed class StubInnerHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public List<HttpRequestMessage> Requests { get; } = new();

            public StubInnerHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_responder(request));
            }
        }

        private static HttpRequestException ConnectionRefused() =>
            new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused));

        private static (HttpClient Client, StubInnerHandler Inner) CreateClient(
            BackendHealthCache cache,
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            Func<string, string>? classifyProvider = null)
        {
            var inner = new StubInnerHandler(responder);
            var handler = new BackendHealthDelegatingHandler(cache, classifyProvider) { InnerHandler = inner };
            return (new HttpClient(handler), inner);
        }

        [Fact]
        public void Constructor_NullCache_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new BackendHealthDelegatingHandler(null!));
            Assert.Throws<ArgumentNullException>(() => new BackendHealthDelegatingHandler(null!, "apple:music"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_FixedProviderOverload_RejectsBlankProvider(string? provider)
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            Assert.Throws<ArgumentException>(() => new BackendHealthDelegatingHandler(cache, provider!));
        }

        [Fact]
        public async Task HealthyBackend_RequestPassesThrough()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            var (client, inner) = CreateClient(cache, _ => new HttpResponseMessage(HttpStatusCode.OK));

            var response = await client.GetAsync("https://api.example.com/v1/albums");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(inner.Requests);
        }

        [Fact]
        public async Task ConnectionClassFailure_PropagatesAndMarksDown_NextCallShortCircuits()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            var failing = true;
            var (client, inner) = CreateClient(cache, _ => failing
                ? throw ConnectionRefused()
                : new HttpResponseMessage(HttpStatusCode.OK));

            // First call: the connection failure itself propagates unchanged.
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/v1/a"));
            Assert.Single(inner.Requests);

            // Second call inside the grace window: fast-fail WITHOUT touching the wire,
            // even though the backend would now respond.
            failing = false;
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/v1/b"));
            Assert.Single(inner.Requests); // inner handler NOT called again
            Assert.Contains("known-down", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task NonConnectionFailure_Propagates_ButDoesNotMarkDown()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            var failing = true;
            var (client, inner) = CreateClient(cache, _ => failing
                ? throw new HttpRequestException("HTTP 503 from a slow-but-alive backend")
                : new HttpResponseMessage(HttpStatusCode.OK));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/v1/a"));

            // A plain HttpRequestException (no SocketException) must not trip the gate:
            // the next request goes through to the backend.
            failing = false;
            var response = await client.GetAsync("https://api.example.com/v1/b");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, inner.Requests.Count);
        }

        [Fact]
        public async Task Cancellation_Propagates_AndDoesNotMarkDown()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            var cancel = true;
            var (client, inner) = CreateClient(cache, _ => cancel
                ? throw new OperationCanceledException()
                : new HttpResponseMessage(HttpStatusCode.OK));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://api.example.com/v1/a"));

            cancel = false;
            var response = await client.GetAsync("https://api.example.com/v1/b");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, inner.Requests.Count);
        }

        [Fact]
        public async Task GraceExpiry_ThenSuccess_ClearsDownState()
        {
            var time = new FakeTimeProvider();
            var cache = new BackendHealthCache(time, GraceSeconds);
            var failing = true;
            var (client, _) = CreateClient(cache, _ => failing
                ? throw ConnectionRefused()
                : new HttpResponseMessage(HttpStatusCode.OK));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/v1/a"));
            Assert.True(cache.IsKnownDown("api.example.com", "https://api.example.com", out _));

            // Advance virtual time past the grace window: the next request is a live probe.
            time.Advance(TimeSpan.FromSeconds(GraceSeconds + 1));
            failing = false;
            var response = await client.GetAsync("https://api.example.com/v1/a");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(cache.IsKnownDown("api.example.com", "https://api.example.com", out _));
        }

        [Fact]
        public async Task SameProvider_DifferentHosts_KeepIndependentCacheSlots()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            // Both hosts classify to one logical provider (the Tidal subdomain-mapping pattern),
            // but the cache slot is per baseUrl: host A down must not fast-fail host B.
            var (client, inner) = CreateClient(
                cache,
                req => req.RequestUri!.Host == "down.example.com"
                    ? throw ConnectionRefused()
                    : new HttpResponseMessage(HttpStatusCode.OK),
                classifyProvider: _ => "tidal");

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://down.example.com/v1/a"));

            var response = await client.GetAsync("https://up.example.com/v1/a");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, inner.Requests.Count);

            // And the down host still short-circuits.
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://down.example.com/v1/b"));
            Assert.Equal(2, inner.Requests.Count);
        }

        [Fact]
        public async Task FixedProviderOverload_UsesServiceNameAsLabel_AndProviderKey()
        {
            var cache = new BackendHealthCache(new FakeTimeProvider(), GraceSeconds);
            var inner = new StubInnerHandler(_ => throw ConnectionRefused());
            var handler = new BackendHealthDelegatingHandler(cache, "apple:music") { InnerHandler = inner };
            var client = new HttpClient(handler);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.music.apple.com/v1/a"));

            // Down-state is keyed by the fixed provider, and the short-circuit message
            // carries the fixed service label.
            Assert.True(cache.IsKnownDown("apple:music", "https://api.music.apple.com", out _));
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.music.apple.com/v1/b"));
            Assert.StartsWith("apple:music", ex.Message);
            Assert.Single(inner.Requests);
        }
    }
}
