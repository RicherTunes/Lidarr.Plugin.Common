using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
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
            var manager = CreateManager(authService, store);

            var session = await manager.GetValidSessionAsync();

            Assert.Equal("persisted", session.Id);
            Assert.Equal(0, authService.AuthenticateCalls);
        }

        [Fact]
        public async Task RefreshSessionAsync_PersistsEnvelope()
        {
            var store = new MemoryTokenStore<TestSession>();
            var authService = new FakeAuthService();
            var manager = CreateManager(authService, store);

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
            var manager = CreateManager(authService, store);

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
            var manager = CreateManager(authService, store);
            var fallback = new TestCredentials("fallback");

            var session = await manager.GetValidSessionAsync(fallback);

            Assert.Equal("session-1", session.Id);
            Assert.Equal(1, authService.AuthenticateCalls);
            Assert.Equal(fallback, authService.LastCredentials);

            var persisted = await store.LoadAsync();
            Assert.NotNull(persisted);
            Assert.Equal(session.Id, persisted!.Session.Id);
        }

        private static StreamingTokenManager<TestSession, TestCredentials> CreateManager(
            FakeAuthService authService,
            ITokenStore<TestSession> store)
        {
            var options = new StreamingTokenManagerOptions<TestSession>
            {
                DefaultSessionLifetime = TimeSpan.FromMinutes(15),
                RefreshBuffer = TimeSpan.FromMinutes(1)
            };

            return new StreamingTokenManager<TestSession, TestCredentials>(
                authService,
                NullLogger<StreamingTokenManager<TestSession, TestCredentials>>.Instance,
                store,
                options);
        }

        private sealed record TestSession(string Id);

        private sealed record TestCredentials(string Value);

        private sealed class FakeAuthService : IStreamingTokenAuthenticationService<TestSession, TestCredentials>
        {
            public int AuthenticateCalls { get; private set; }

            public TestSession? LastIssuedSession { get; private set; }

            public TestCredentials? LastCredentials { get; private set; }

            public Task<TestSession> AuthenticateAsync(TestCredentials credentials)
            {
                AuthenticateCalls++;
                LastCredentials = credentials;
                LastIssuedSession = new TestSession($"session-{AuthenticateCalls}");
                return Task.FromResult(LastIssuedSession);
            }

            public Task<bool> ValidateSessionAsync(TestSession session) => Task.FromResult(true);
        }
    }
}
