using System;
using System.Linq;
using System.Threading;
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
        public async Task Dispose_DoesNotClearPersistedSession()
        {
            var store = new MemoryTokenStore<TestSession>();
            await store.SaveAsync(new TokenEnvelope<TestSession>(new TestSession("persisted"), DateTime.UtcNow.AddMinutes(30)));

            var authService = new FakeAuthService();
            using (var manager = CreateManager(authService, store))
            {
                var session = await manager.GetValidSessionAsync();
                Assert.Equal("persisted", session.Id);
            }

            var envelope = await store.LoadAsync();
            Assert.NotNull(envelope);
            Assert.Equal("persisted", envelope!.Session.Id);
        }

        [Fact]
        public async Task GetValidSessionAsync_ConcurrentCallersWithInvalidSession_AuthenticateExactlyOnce()
        {
            // Token-stampede guard: one caller authenticates, queued callers reuse its result.
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService { AuthDelay = TimeSpan.FromMilliseconds(50) };
            using var manager = CreateManager(authService, store);
            var fallback = new TestCredentials("fallback");

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => manager.GetValidSessionAsync(fallback)))
                .ToArray();
            var sessions = await Task.WhenAll(tasks);

            Assert.Equal(1, authService.AuthenticateCalls);
            Assert.All(sessions, session => Assert.Equal(sessions[0].Id, session.Id));
        }

        [Fact]
        public async Task RefreshSessionAsync_ForcesNewSession_EvenWhenCurrentSessionStillValid()
        {
            // Force-refresh is used after server-side token rejection; validity-based
            // deduplication only applies to GetValidSessionAsync.
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService();
            using var manager = CreateManager(authService, store);

            await manager.RefreshSessionAsync(new TestCredentials("first"));
            await manager.RefreshSessionAsync(new TestCredentials("second"));

            Assert.Equal(2, authService.AuthenticateCalls);
            Assert.Equal(new TestCredentials("second"), authService.LastCredentials);
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
        public async Task ProactiveRefresh_RefreshesSession_WhenTimerMissesWindowAndSessionAlreadyExpired()
        {
            var fakeTime = new FakeTimeProvider();
            var store = new MemoryTokenStore<TestSession>();
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

            _ = await manager.GetValidSessionAsync();

            fakeTime.Advance(TimeSpan.FromSeconds(20));
            manager.TriggerRefreshCheck();

            bool refreshed = await WaitUntilAsync(() => authService.AuthenticateCalls >= 1);

            Assert.True(refreshed, "Expected proactive refresh to recover even after the original expiry was missed.");
            Assert.Equal(new TestCredentials("proactive"), authService.LastCredentials);
        }

        [Fact]
        public async Task ProactiveRefresh_ConcurrentChecks_AuthenticateExactlyOnce()
        {
            var fakeTime = new FakeTimeProvider();
            var store = new MemoryTokenStore<TestSession>();
            var sessionExpiry = fakeTime.GetUtcNow().UtcDateTime.AddSeconds(10);
            await store.SaveAsync(new TokenEnvelope<TestSession>(
                new TestSession("persisted"),
                sessionExpiry));

            var authService = new FakeAuthService { AuthDelay = TimeSpan.FromMilliseconds(50) };
            using var manager = CreateManager(
                authService,
                store,
                proactiveCredentialsProvider: () => new TestCredentials("proactive"),
                refreshBuffer: TimeSpan.FromSeconds(5),
                timeProvider: fakeTime);

            _ = await manager.GetValidSessionAsync();
            fakeTime.Advance(TimeSpan.FromSeconds(6));

            using ManualResetEventSlim start = new(false);
            Task[] triggers = Enumerable.Range(0, 50)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    manager.TriggerRefreshCheck();
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(triggers);
            bool refreshed = await WaitUntilAsync(() => authService.AuthenticateCalls >= 1);
            Assert.True(refreshed, "Expected at least one proactive refresh.");

            await Task.Delay(200);

            Assert.Equal(1, authService.AuthenticateCalls);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            // Generous deadline: TriggerRefreshCheck() schedules the refresh on the thread pool,
            // which is saturated under full-suite parallel load, so a 2s poll window flaked. The
            // work always completes; it just needs wall-clock under load (bounded by blame-hang).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return condition();
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
            private int authenticateCalls;

            public int AuthenticateCalls => Volatile.Read(ref this.authenticateCalls);

            public TestSession? LastIssuedSession { get; private set; }

            public TestCredentials? LastCredentials { get; private set; }

            public TimeSpan AuthDelay { get; init; } = TimeSpan.Zero;

            public async Task<TestSession> AuthenticateAsync(TestCredentials credentials)
            {
                int call = Interlocked.Increment(ref this.authenticateCalls);
                if (this.AuthDelay > TimeSpan.Zero)
                {
                    await Task.Delay(this.AuthDelay);
                }

                LastCredentials = credentials;
                LastIssuedSession = new TestSession($"session-{call}");
                return LastIssuedSession;
            }

            public Task<bool> ValidateSessionAsync(TestSession session) => Task.FromResult(true);
        }
    }
}
