using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http
{
    /// <summary>
    /// Tests for <see cref="CachingHttpExecutor"/>. Each test focuses on one <see cref="CacheHitKind"/>
    /// outcome: Hit, SoftRevalidate, NotModifiedFold, StaleIfError, EvictOnTerminal, Miss, Passthrough.
    /// </summary>
    [Trait("Category", "Unit")]
    public class CachingHttpExecutorTests
    {
        private const string BaseUrl = "https://api.example.test/v1";
        private const string Endpoint = "/v1/catalog/us/albums/12345";

        // ---- Test doubles ----

        private sealed class ScriptedHandler : DelegatingHandler
        {
            private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _factory;
            public int Calls;
            public List<HttpRequestMessage> Requests { get; } = new();

            public ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> factory)
            {
                _factory = factory;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                // Snapshot headers before we let the caller dispose the request.
                var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var h in request.Headers) snapshot.Headers.TryAddWithoutValidation(h.Key, h.Value);
                Requests.Add(snapshot);
                return Task.FromResult(_factory(Calls, request));
            }
        }

        private sealed class TestCache : StreamingResponseCache
        {
            private readonly FakeTimeProvider _tp;
            public TestCache(FakeTimeProvider tp, ICachePolicyProvider provider)
                : base(tp, NullLogger<StreamingResponseCache>.Instance, provider)
            {
                _tp = tp;
            }
            protected override string GetServiceName() => "TestService";
        }

        private sealed class StaticPolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy _policy;
            public StaticPolicyProvider(CachePolicy policy) { _policy = policy; }
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => _policy;
        }

        private sealed class InMemoryConditionalState : IConditionalRequestState
        {
            private readonly ConcurrentDictionary<string, (string? etag, DateTimeOffset? lm)> _map = new();
            public ValueTask<(string? ETag, DateTimeOffset? LastModified)?> TryGetValidatorsAsync(string cacheKey, CancellationToken ct = default)
                => new(_map.TryGetValue(cacheKey, out var v) ? ((string?, DateTimeOffset?)?)(v.etag, v.lm) : null);
            public ValueTask SetValidatorsAsync(string cacheKey, string? eTag, DateTimeOffset? lastModified, CancellationToken ct = default)
            {
                if (eTag == null && !lastModified.HasValue) _map.TryRemove(cacheKey, out _);
                else _map[cacheKey] = (eTag, lastModified);
                return ValueTask.CompletedTask;
            }
            public int Count => _map.Count;
        }

        // ---- Helpers ----

        private static StreamingApiRequestBuilder NewBuilder()
        {
            var b = new StreamingApiRequestBuilder(BaseUrl).Endpoint("catalog/us/albums/12345").Get();
            return b;
        }

        private static CacheKey NewKey() => new CacheKey(Endpoint, new Dictionary<string, string> { ["q"] = "k" });

        private static HttpResponseMessage NewOk(string body = "{\"ok\":true}", string? etag = null, DateTimeOffset? lastModified = null)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            };
            resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
            if (!string.IsNullOrEmpty(etag)) resp.Headers.ETag = new EntityTagHeaderValue(etag);
            if (lastModified.HasValue) resp.Content.Headers.LastModified = lastModified;
            return resp;
        }

        private static (CachingHttpExecutor exec, ScriptedHandler handler, TestCache cache, FakeTimeProvider tp) Build(
            CachePolicy policy,
            Func<int, HttpRequestMessage, HttpResponseMessage> handlerFactory,
            IConditionalRequestState? conditional = null)
        {
            var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var provider = new StaticPolicyProvider(policy);
            var cache = new TestCache(tp, provider);
            var handler = new ScriptedHandler(handlerFactory);
            var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
            // Use a small resilience policy: maxRetries=2 (1 retry), tight backoff so retried tests stay fast.
            var resilience = ResiliencePolicy.Default.With(
                maxRetries: 2,
                retryBudget: TimeSpan.FromSeconds(5),
                initialBackoff: TimeSpan.FromMilliseconds(1),
                maxBackoff: TimeSpan.FromMilliseconds(2),
                jitterMin: TimeSpan.Zero,
                jitterMax: TimeSpan.Zero,
                perRequestTimeout: TimeSpan.FromSeconds(5));
            var exec = new CachingHttpExecutor(client, cache, resilience, provider, conditional, tp);
            return (exec, handler, cache, tp);
        }

        // ---- Tests ----

        [Fact]
        public async Task Miss_PopulatesCacheAndReturnsParsedPayload()
        {
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(10));
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk("{\"id\":\"a\"}"));

            var hooks = new CachingHttpHooks<string>(
                ParseAsync: async (resp, ct) => await resp.Content.ReadAsStringAsync(ct));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy, hooks);

            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal("{\"id\":\"a\"}", Encoding.UTF8.GetString(result.Body));
            Assert.Equal("{\"id\":\"a\"}", result.Payload);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task Hit_ReturnsCachedBodyWithoutContactingOrigin()
        {
            // Soft-revalidate disabled but a fresh cached entry already exists; the next call should not contact origin.
            // We seed by issuing a Miss first, then a follow-up should serve from cache via the cache provider's
            // ShouldCache + Get path inside StreamingResponseCache. CachingHttpExecutor itself doesn't have a
            // "fast-path Hit" branch for in-window cache (that's handled by IStreamingResponseCache); but
            // soft-revalidate is the executor's Hit-equivalent path. We test both: here, a long soft window.
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(30))
                .WithExecutor(softRevalidateWindow: TimeSpan.FromMinutes(30));

            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk());
            var key = NewKey();

            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.Equal(1, handler.Calls);

            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.SoftRevalidate, second.HitKind); // first hit-equivalent
            Assert.Equal(1, handler.Calls); // origin not contacted again
        }

        [Fact]
        public async Task SoftRevalidate_ReturnsCachedWithoutOriginInsideWindow()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5))
                .WithExecutor(softRevalidateWindow: TimeSpan.FromMinutes(2));

            var (exec, handler, _, tp) = Build(policy, (_, _) => NewOk("{\"v\":1}"));
            var key = NewKey();

            // 1) populate
            var miss = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, miss.HitKind);

            // 2) inside soft window -> SoftRevalidate, no origin call
            tp.Advance(TimeSpan.FromMinutes(1));
            var soft = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.SoftRevalidate, soft.HitKind);
            Assert.Equal(1, handler.Calls);
            Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(soft.Body));
        }

        [Fact]
        public async Task NotModifiedFold_Returns200FromCacheAndAttachesIfNoneMatchHeader()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(10), enableConditionalRevalidation: true);

            // Origin returns ETag=v1 first, then 304 on revalidation.
            var (exec, handler, _, tp) = Build(policy, (call, req) =>
            {
                if (call == 1) return NewOk("body-v1", etag: "\"v1\"");
                Assert.True(req.Headers.IfNoneMatch.Count > 0, "If-None-Match should be attached on revalidation");
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });

            var key = NewKey();
            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);

            // Skip soft-revalidate: window is unset on this policy.
            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.NotModifiedFold, second.HitKind);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode); // synthesized 200
            Assert.Equal("body-v1", Encoding.UTF8.GetString(second.Body));
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task NotModifiedFold_UsesExternalConditionalState_WhenProvided()
        {
            // ConditionalRevalidation = false; rely entirely on the IConditionalRequestState.
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(10));
            var conditional = new InMemoryConditionalState();

            var (exec, handler, _, _) = Build(policy, (call, req) =>
            {
                if (call == 1) return NewOk("body", etag: "\"abc\"");
                Assert.True(req.Headers.IfNoneMatch.Count > 0);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }, conditional);

            var key = NewKey();
            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.Equal(1, conditional.Count); // validator persisted

            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.NotModifiedFold, second.HitKind);
        }

        [Fact]
        public async Task StaleIfError_Returns5xxFallbackFromCacheWithinWindow()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5))
                .WithExecutor(staleIfErrorTtl: TimeSpan.FromHours(2));

            // First call OK (populates cache); second call returns 503.
            var (exec, handler, _, tp) = Build(policy, (call, _) =>
                call == 1 ? NewOk("warm-body") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var key = NewKey();
            await exec.SendAsync(NewBuilder(), key, policy);

            // Advance past TTL but inside stale-if-error window.
            tp.Advance(TimeSpan.FromMinutes(30));

            var stale = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.StaleIfError, stale.HitKind);
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            Assert.Equal("warm-body", Encoding.UTF8.GetString(stale.Body));
        }

        [Fact]
        public async Task StaleIfError_PassesThrough5xxWhenWindowExpired()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5))
                .WithExecutor(staleIfErrorTtl: TimeSpan.FromMinutes(10));

            var (exec, handler, _, tp) = Build(policy, (call, _) =>
                call == 1 ? NewOk() : new HttpResponseMessage(HttpStatusCode.BadGateway));

            var key = NewKey();
            await exec.SendAsync(NewBuilder(), key, policy);
            tp.Advance(TimeSpan.FromHours(1)); // outside stale window

            var result = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Passthrough, result.HitKind);
            Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        }

        [Fact]
        public async Task EvictOnTerminal_ClearsCacheAndValidatorsOn404()
        {
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(10));
            var conditional = new InMemoryConditionalState();

            var (exec, handler, cache, _) = Build(policy, (call, _) =>
                call == 1 ? NewOk("warm", etag: "\"e1\"") : new HttpResponseMessage(HttpStatusCode.NotFound), conditional);

            var key = NewKey();
            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.NotNull(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));
            Assert.Equal(1, conditional.Count);

            var evictKindRecorded = (CacheHitKind?)null;
            HttpStatusCode? evictedStatus = null;
            var hooks = new CachingHttpHooks<string>(
                OnHit: (kind, _) => evictKindRecorded = kind,
                OnEvict: (status, _) => evictedStatus = status);

            var terminal = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.EvictOnTerminal, terminal.HitKind);
            Assert.Equal(HttpStatusCode.NotFound, terminal.StatusCode);
            Assert.Null(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));
            Assert.Equal(0, conditional.Count);
            Assert.Equal(CacheHitKind.EvictOnTerminal, evictKindRecorded);
            Assert.Equal(HttpStatusCode.NotFound, evictedStatus);
        }

        [Fact]
        public async Task EvictOnTerminal_RespectsPolicyToggle()
        {
            // Disable terminal eviction; 410 should pass through and leave the cache intact.
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(10))
                .WithExecutor(evictOnTerminalStatus: false);

            var (exec, handler, cache, _) = Build(policy, (call, _) =>
                call == 1 ? NewOk() : new HttpResponseMessage(HttpStatusCode.Gone));

            var key = NewKey();
            await exec.SendAsync(NewBuilder(), key, policy);
            Assert.NotNull(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));

            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Passthrough, second.HitKind);
            Assert.NotNull(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));
        }

        [Fact]
        public async Task Passthrough_ReturnsNonSuccessUnchanged()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy);
            Assert.Equal(CacheHitKind.Passthrough, result.HitKind);
            Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        }

        [Fact]
        public async Task Passthrough_When304WithNoCachedBody()
        {
            // Response is a synthetic 304 with no prior cached content — surface as Passthrough rather than crash.
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(5));
            var (exec, _, _, _) = Build(policy, (_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy);
            Assert.Equal(CacheHitKind.Passthrough, result.HitKind);
            Assert.Equal(HttpStatusCode.NotModified, result.StatusCode);
        }

        [Fact]
        public async Task ParseHook_ReturnsParsedPayloadOnEveryHitKind()
        {
            // Verify ParseAsync is called on Miss, SoftRevalidate, NotModifiedFold, and StaleIfError paths.
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5), enableConditionalRevalidation: true)
                .WithExecutor(softRevalidateWindow: TimeSpan.FromMinutes(2), staleIfErrorTtl: TimeSpan.FromHours(2));

            var calls = 0;
            var (exec, _, _, tp) = Build(policy, (call, _) =>
            {
                calls = call;
                if (call == 1) return NewOk("payload-v1", etag: "\"e1\"");
                if (call == 2) return new HttpResponseMessage(HttpStatusCode.NotModified);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });

            var hooks = new CachingHttpHooks<string>(ParseAsync: async (resp, ct) =>
            {
                var s = await resp.Content.ReadAsStringAsync(ct);
                return "[parsed]" + s;
            });

            var key = NewKey();

            // Miss
            var miss = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.Miss, miss.HitKind);
            Assert.Equal("[parsed]payload-v1", miss.Payload);

            // Soft-revalidate (still inside soft window)
            tp.Advance(TimeSpan.FromSeconds(30));
            var soft = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.SoftRevalidate, soft.HitKind);
            Assert.Equal("[parsed]payload-v1", soft.Payload);

            // Skip soft window -> 304 fold
            tp.Advance(TimeSpan.FromMinutes(3));
            var fold = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.NotModifiedFold, fold.HitKind);
            Assert.Equal("[parsed]payload-v1", fold.Payload);

            // Past TTL but inside stale window -> 5xx fallback
            tp.Advance(TimeSpan.FromMinutes(10));
            var stale = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.StaleIfError, stale.HitKind);
            Assert.Equal("[parsed]payload-v1", stale.Payload);
        }

        [Fact]
        public async Task MutateRequest_HookCanAddCustomHeaders()
        {
            var policy = CachePolicy.Default;
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk());

            var hooks = new CachingHttpHooks<string>(
                MutateRequest: req => req.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc-123"));

            await exec.SendAsync(NewBuilder(), NewKey(), policy, hooks);

            Assert.Single(handler.Requests);
            Assert.True(handler.Requests[0].Headers.Contains("X-Correlation-Id"));
            Assert.Contains("abc-123", handler.Requests[0].Headers.GetValues("X-Correlation-Id"));
        }

        [Fact]
        public async Task OnHit_HookFiresWithCacheHitKind()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5))
                .WithExecutor(softRevalidateWindow: TimeSpan.FromMinutes(2));
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk());

            var hits = new List<CacheHitKind>();
            var hooks = new CachingHttpHooks<string>(OnHit: (kind, _) => hits.Add(kind));
            var key = NewKey();

            await exec.SendAsync(NewBuilder(), key, policy, hooks);
            await exec.SendAsync(NewBuilder(), key, policy, hooks);

            Assert.Equal(2, hits.Count);
            Assert.Equal(CacheHitKind.Miss, hits[0]);
            Assert.Equal(CacheHitKind.SoftRevalidate, hits[1]);
        }

        [Fact]
        public async Task DisabledPolicy_AlwaysHitsOriginAndDoesNotCache()
        {
            var policy = CachePolicy.Disabled;
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk());
            var key = NewKey();

            var first = await exec.SendAsync(NewBuilder(), key, policy);
            var second = await exec.SendAsync(NewBuilder(), key, policy);

            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.Equal(CacheHitKind.Miss, second.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task PolicyProviderOverload_ResolvesPolicyWhenOmittedAtCallSite()
        {
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(5));
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk("p"));

            // Use the convenience overload that omits the per-call policy.
            var result = await exec.SendAsync<string>(NewBuilder(), NewKey());

            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task RetryAfter_OnFirst429_TransparentlyRetriesAndReturnsCachedMiss()
        {
            // 429 should be retried by GenericResilienceExecutor; the second attempt succeeds and the cached
            // path treats it as Miss. This exercises the integration with the resilience executor.
            var policy = CachePolicy.Default;
            var (exec, handler, _, _) = Build(policy, (call, _) =>
            {
                if (call == 1)
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429);
                    r.Headers.Add("Retry-After", "0");
                    return r;
                }
                return NewOk();
            });

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy);
            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task NoStorePrivateResponse_IsNotCached()
        {
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(5));
            var (exec, handler, cache, _) = Build(policy, (_, _) =>
            {
                var r = NewOk();
                r.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return r;
            });

            var key = NewKey();
            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            // No-store: should not be cached
            Assert.Null(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));

            // Subsequent call must contact origin again
            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, second.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task ConditionalHeaders_UseLastModifiedWhenETagAbsent()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5), enableConditionalRevalidation: true);

            var lastMod = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var (exec, handler, _, _) = Build(policy, (call, req) =>
            {
                if (call == 1) return NewOk("payload", etag: null, lastModified: lastMod);
                Assert.True(req.Headers.Contains("If-Modified-Since"), "If-Modified-Since should be attached");
                Assert.False(req.Headers.IfNoneMatch.Count > 0, "If-None-Match should be absent without ETag");
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });

            var key = NewKey();
            await exec.SendAsync(NewBuilder(), key, policy);
            var fold = await exec.SendAsync(NewBuilder(), key, policy);

            Assert.Equal(CacheHitKind.NotModifiedFold, fold.HitKind);
        }

        // ---- Phase 5e refinements ----

        [Fact]
        public async Task HotHit_FastPath_ReturnsCachedHitWithoutOriginAfterMiss()
        {
            // EnabledForFreshEntries: cache populated on first call, second call returns Hit without contacting origin.
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(10))
                .WithExecutor(hotHitMode: HotCacheHitMode.EnabledForFreshEntries);

            var (exec, handler, _, tp) = Build(policy, (_, _) => NewOk("hot-body"));
            var key = NewKey();

            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.Equal(1, handler.Calls);

            // Inside Duration window — fast path returns Hit without origin.
            tp.Advance(TimeSpan.FromMinutes(2));
            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Hit, second.HitKind);
            Assert.Equal(1, handler.Calls);
            Assert.Equal("hot-body", System.Text.Encoding.UTF8.GetString(second.Body));
        }

        [Fact]
        public async Task HotHit_FastPath_ContactsOriginOnceWindowExpires()
        {
            var policy = CachePolicy.Default
                .With(duration: TimeSpan.FromMinutes(5))
                .WithExecutor(hotHitMode: HotCacheHitMode.EnabledForFreshEntries);

            var (exec, handler, _, tp) = Build(policy, (_, _) => NewOk("body"));
            var key = NewKey();

            await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(1, handler.Calls);

            // Past Duration — fast path no longer applies, origin is contacted.
            tp.Advance(TimeSpan.FromMinutes(10));
            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, second.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task HotHit_Disabled_DoesNotShortCircuit()
        {
            // Default mode (Disabled) should never produce a Hit kind without one of the existing windows.
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(10));
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk());
            var key = NewKey();

            await exec.SendAsync(NewBuilder(), key, policy);
            var second = await exec.SendAsync(NewBuilder(), key, policy);

            // Disabled means no fast path, so the second call must contact the origin again.
            Assert.NotEqual(CacheHitKind.Hit, second.HitKind);
            Assert.Equal(CacheHitKind.Miss, second.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task PropagateParseExceptions_DefaultFalse_AbsorbsAndReturnsDefault()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk("{\"id\":\"a\"}"));

            // Hook deliberately throws.
            var hooks = new CachingHttpHooks<string>(
                ParseAsync: (_, _) => throw new InvalidOperationException("parse boom"));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy, hooks);
            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Null(result.Payload); // exception absorbed -> default
        }

        [Fact]
        public async Task PropagateParseExceptions_True_SurfacesParseException()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk("{}"));

            var hooks = new CachingHttpHooks<string>(
                ParseAsync: (_, _) => throw new InvalidOperationException("parse boom"))
            {
                PropagateParseExceptions = true
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.SendAsync(NewBuilder(), NewKey(), policy, hooks));
        }

        // ---- Wave 7 gap-fill: branch coverage for hook-exception swallowing and provider-null
        // overload guard (pre-wave-7 line-rate 85.10% / branch-rate 57.89%). ----

        [Fact]
        public async Task PolicyProviderOverload_WithoutProvider_Throws()
        {
            // Build an executor without an ICachePolicyProvider, then call the convenience
            // overload that omits per-call policy. The guard at the top of SendAsync must throw.
            var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var provider = new StaticPolicyProvider(CachePolicy.Default);
            var cache = new TestCache(tp, provider);
            var handler = new ScriptedHandler((_, _) => NewOk());
            var client = new HttpClient(handler);

            var exec = new CachingHttpExecutor(
                client, cache, resiliencePolicy: null,
                policyProvider: null,                    // <- explicitly absent
                conditionalState: null, timeProvider: tp);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.SendAsync<string>(NewBuilder(), NewKey()));
        }

        [Fact]
        public async Task MutateRequest_HookThrowing_IsSwallowed_AndRequestStillSends()
        {
            // The MutateRequest hook is wrapped in try/catch (logged at Debug). A throwing hook
            // must not propagate and must not block the request.
            var policy = CachePolicy.Default;
            var (exec, handler, _, _) = Build(policy, (_, _) => NewOk("body"));

            var hooks = new CachingHttpHooks<string>(
                MutateRequest: _ => throw new InvalidOperationException("mutate boom"));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy, hooks);

            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task OnHit_HookThrowing_IsSwallowed_AndResultIsReturned()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk());

            var hooks = new CachingHttpHooks<string>(
                OnHit: (_, _) => throw new InvalidOperationException("onhit boom"));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy, hooks);
            Assert.Equal(CacheHitKind.Miss, result.HitKind);
        }

        [Fact]
        public async Task OnEvict_HookThrowing_IsSwallowed_AndEvictionStillCompletes()
        {
            // 404 path: OnEvict hook throws, executor must swallow and still return EvictOnTerminal.
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(10));
            var (exec, _, cache, _) = Build(policy, (call, _) =>
                call == 1 ? NewOk("warm") : new HttpResponseMessage(HttpStatusCode.NotFound));

            var key = NewKey();
            await exec.SendAsync(NewBuilder(), key, policy);
            Assert.NotNull(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));

            var hooks = new CachingHttpHooks<string>(
                OnEvict: (_, _) => throw new InvalidOperationException("evict boom"));

            var terminal = await exec.SendAsync(NewBuilder(), key, policy, hooks);
            Assert.Equal(CacheHitKind.EvictOnTerminal, terminal.HitKind);
            // Eviction still happened despite the hook failure.
            Assert.Null(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));
        }

        [Fact]
        public async Task NotModifiedFold_With304AndNoCachedBody_FallsBackToPassthrough()
        {
            // 304 with shouldCache=false means cache lookup is skipped → passthrough branch (line 215-217).
            // Use Disabled policy so shouldCache is false but the request still happens.
            var policy = CachePolicy.Disabled;
            var (exec, _, _, _) = Build(policy, (_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy);
            Assert.Equal(CacheHitKind.Passthrough, result.HitKind);
            Assert.Equal(HttpStatusCode.NotModified, result.StatusCode);
        }

        [Fact]
        public async Task PrivateCacheControl_IsNotCached()
        {
            // Distinguishes Private from NoStore — both must skip the cache write (IsResponseCacheable false branch).
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromMinutes(5));
            var (exec, handler, cache, _) = Build(policy, (_, _) =>
            {
                var r = NewOk();
                r.Headers.CacheControl = new CacheControlHeaderValue { Private = true };
                return r;
            });

            var key = NewKey();
            var first = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, first.HitKind);
            Assert.Null(cache.Get<CachedHttpResponse>(Endpoint, key.Parameters));

            var second = await exec.SendAsync(NewBuilder(), key, policy);
            Assert.Equal(CacheHitKind.Miss, second.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task RetryAfter_AbsoluteDate_HonoredForBackoff()
        {
            // Exercises the GetRetryAfterDelay branch where retryAfter.Date.HasValue is true (not Delta).
            // Use a date in the past so the helper computes a non-positive delta and clamps to zero;
            // the request still retries (resilience executor) and the second attempt succeeds.
            var policy = CachePolicy.Default;
            var (exec, handler, _, _) = Build(policy, (call, _) =>
            {
                if (call == 1)
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429);
                    r.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1));
                    return r;
                }
                return NewOk();
            });

            var result = await exec.SendAsync(NewBuilder(), NewKey(), policy);
            Assert.Equal(CacheHitKind.Miss, result.HitKind);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task Builder_Null_Throws()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => exec.SendAsync(builder: null!, NewKey(), policy));
        }

        [Fact]
        public async Task Key_Null_Throws()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => exec.SendAsync(NewBuilder(), key: null!, policy));
        }

        [Fact]
        public async Task Policy_Null_Throws()
        {
            var policy = CachePolicy.Default;
            var (exec, _, _, _) = Build(policy, (_, _) => NewOk());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => exec.SendAsync(NewBuilder(), NewKey(), policy: null!));
        }
    }
}
