using System;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
using Lidarr.Plugin.Common.TestKit.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingTokenManagerTests
    {
        [Fact]
        public async Task GetValidSessionAsync_UsesPersistedSession_WhenNotExpired()
        {
            var store = new MemoryTokenStore<TestSession>();
            await store.SaveAsync(new TokenEnvelope<TestSession>(new TestSession("persisted"), DateTime.UtcNow.AddMinutes(30)));

            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);

            var session = await manager.GetValidSessionAsync();

            Assert.Equal("persisted", session.Id);
            Assert.Equal(0, authService.AuthenticateCalls);
        }

        [Fact]
        public async Task RefreshSessionAsync_PersistsEnvelope()
        {
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);

            await manager.RefreshSessionAsync(new TestCredentials("primary"));

            var envelope = await store.LoadAsync();
            Assert.NotNull(envelope);
            Assert.Equal(authService.LastIssuedSession?.Id, envelope!.Session.Id);

            await manager.ClearSessionAsync();
            Assert.Null(await store.LoadAsync());
        }

        [Fact]
        public async Task ClearSessionAsync_ClearsPersisted_WhenNoInMemorySessionLoaded()
        {
            var store = new MemoryTokenStore<TestSession>();
            await store.SaveAsync(new TokenEnvelope<TestSession>(new TestSession("persisted"), DateTime.UtcNow.AddMinutes(30)));

            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);

            // Explicitly clear before any call that would load the persisted session into memory
            await manager.ClearSessionAsync();

            Assert.Null(await store.LoadAsync());
        }

        [Fact]
        public async Task GetValidSessionAsync_RefreshesExpiredPersistedSession()
        {
            var store = new MemoryTokenStore<TestSession>();
            await store.SaveAsync(new TokenEnvelope<TestSession>(new TestSession("stale"), DateTime.UtcNow.AddMinutes(-5)));

            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);
            var fallback = new TestCredentials("fallback");

            var session = await manager.GetValidSessionAsync(fallback);

            Assert.Equal("session-1", session.Id);
            Assert.Equal(1, authService.AuthenticateCalls);
            Assert.Equal(fallback, authService.LastCredentials);

            var persisted = await store.LoadAsync();
            Assert.NotNull(persisted);
            Assert.Equal(session.Id, persisted!.Session.Id);
        }

        [Fact]
        public async Task ProactiveRefresh_RefreshesSession_WhenApproachingExpiryAndCredentialsAvailable()
        {
            // Use FakeTimeProvider for deterministic time control (no flaky timer/threadpool variance)
            var fakeTime = new FakeTimeProvider();
            var store = new MemoryTokenStore<TestSession>();

            // Create a session that expires 10 seconds from "now" (fake time)
            var sessionExpiry = fakeTime.GetUtcNow().UtcDateTime.AddSeconds(10);
            await store.SaveAsync(new TokenEnvelope<TestSession>(
                new TestSession("persisted"),
                sessionExpiry));

            var authService = new FakeAuthService();
            using var manager = CreateManager(
                authService,
                store,
                proactiveCredentialsProvider: () => new TestCredentials("proactive"),
                refreshBuffer: TimeSpan.FromSeconds(5),
                timeProvider: fakeTime);

            // Ensure the persisted session is loaded into memory
            _ = await manager.GetValidSessionAsync();
            Assert.Equal(0, authService.AuthenticateCalls); // Should not have refreshed yet

            // Advance time to enter the refresh buffer (10s expiry - 5s buffer = triggers at 5s)
            fakeTime.Advance(TimeSpan.FromSeconds(6));

            // Manually trigger the refresh check (deterministic, no timer variance)
            manager.TriggerRefreshCheck();

            // Give async refresh a moment to complete
            await Task.Delay(50);

            Assert.True(authService.AuthenticateCalls >= 1, "Expected proactive refresh to authenticate at least once.");
            Assert.Equal(new TestCredentials("proactive"), authService.LastCredentials);

            var envelope = await store.LoadAsync();
            Assert.NotNull(envelope);
            Assert.Equal(authService.LastIssuedSession?.Id, envelope!.Session.Id);
        }

        [Fact]
        public async Task GetValidSessionAsync_ConcurrentCallersWithInvalidSession_AuthenticateExactlyOnce()
        {
            // Token-stampede guard: when N callers race GetValidSessionAsync with no valid
            // session, the first performs the refresh and the rest must reuse its result —
            // not serially re-authenticate N times against the upstream provider.
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService { AuthDelay = TimeSpan.FromMilliseconds(50) };
            using var manager = CreateManager(authService, store);
            var fallback = new TestCredentials("fallback");

            var tasks = System.Linq.Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => manager.GetValidSessionAsync(fallback)))
                .ToArray();
            var sessions = await Task.WhenAll(tasks);

            Assert.Equal(1, authService.AuthenticateCalls);
            Assert.All(sessions, s => Assert.Equal(sessions[0].Id, s.Id));
        }

        [Fact]
        public async Task RefreshSessionAsync_ForcesNewSession_EvenWhenCurrentSessionStillValid()
        {
            // Pins the public force-refresh contract: callers invoke RefreshSessionAsync after
            // server-side rejections of clock-valid tokens, so it must always re-authenticate
            // (the stampede dedup applies only to the validity-driven GetValidSessionAsync path).
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);

            await manager.RefreshSessionAsync(new TestCredentials("first"));
            await manager.RefreshSessionAsync(new TestCredentials("second"));

            Assert.Equal(2, authService.AuthenticateCalls);
        }

        private static StreamingTokenManager<TestSession, TestCredentials> CreateManager(
            FakeAuthService authService,
            ITokenStore<TestSession> store,
            Func<TestCredentials?>? proactiveCredentialsProvider = null,
            TimeSpan? refreshBuffer = null,
            TimeSpan? refreshCheckInterval = null,
            TimeProvider? timeProvider = null)
        {
            var options = new StreamingTokenManagerOptions<TestSession>
            {
                DefaultSessionLifetime = TimeSpan.FromMinutes(15),
                RefreshBuffer = refreshBuffer ?? TimeSpan.FromMinutes(1),
                RefreshCheckInterval = refreshCheckInterval ?? TimeSpan.FromMilliseconds(25),
                ProactiveRefreshCredentialsProvider = proactiveCredentialsProvider == null
                    ? null
                    : () => proactiveCredentialsProvider()
            };

            return new StreamingTokenManager<TestSession, TestCredentials>(
                authService,
                NullLogger<StreamingTokenManager<TestSession, TestCredentials>>.Instance,
                store,
                options,
                timeProvider);
        }

        private sealed record TestSession(string Id);

        private sealed record TestCredentials(string Value);

        private sealed class FakeAuthService : IStreamingTokenAuthenticationService<TestSession, TestCredentials>
        {
            private int _authenticateCalls;

            public int AuthenticateCalls => System.Threading.Volatile.Read(ref _authenticateCalls);

            public TestSession? LastIssuedSession { get; private set; }

            public TestCredentials? LastCredentials { get; private set; }

            public TimeSpan AuthDelay { get; init; } = TimeSpan.Zero;

            public async Task<TestSession> AuthenticateAsync(TestCredentials credentials)
            {
                var call = System.Threading.Interlocked.Increment(ref _authenticateCalls);
                if (AuthDelay > TimeSpan.Zero)
                {
                    await Task.Delay(AuthDelay);
                }

                LastCredentials = credentials;
                LastIssuedSession = new TestSession($"session-{call}");
                return LastIssuedSession;
            }

            public Task<bool> ValidateSessionAsync(TestSession session) => Task.FromResult(true);
        }
    }
}
