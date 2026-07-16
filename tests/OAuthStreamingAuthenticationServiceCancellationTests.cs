using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Pins the cancellation/timeout contract on <see cref="OAuthStreamingAuthenticationService{TSession, TCredentials}"/>:
    /// a hung provider refresh must not wedge every token consumer for the process lifetime
    /// (audit C-4/C-5), caller cancellation must abandon the wait without killing the shared
    /// single-flight refresh (refresh tokens are single-use on rotating providers), and a
    /// timeout must not clear the cached session (a slow auth server is not a revoked token).
    /// </summary>
    [Trait("Category", "Unit")]
    public class OAuthStreamingAuthenticationServiceCancellationTests
    {
        private class TestAuthSession : IAuthSession
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public DateTime? ExpiresAt { get; set; }
            public bool IsExpired => ExpiresAt == null || ExpiresAt.Value < DateTime.UtcNow;
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        private class TestCredentials : IAuthCredentials
        {
            public string ClientId { get; set; } = "test-client";
            public AuthenticationType Type => AuthenticationType.OAuth2;

            public bool IsValid(out string errorMessage)
            {
                errorMessage = string.Empty;
                return true;
            }
        }

        /// <summary>
        /// Refresh behavior is driven per-test through <see cref="RefreshImplementation"/>.
        /// Uses the legacy 1-arg <c>RefreshTokensInternalAsync</c> override unless
        /// <see cref="OverrideCancellableRefresh"/> is set, in which case the CT overload is
        /// overridden instead (to prove the token is forwarded).
        /// </summary>
        private class TestOAuthService : OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>
        {
            public Func<string, Task<TestAuthSession>> RefreshImplementation { get; set; } =
                token => Task.FromResult(new TestAuthSession
                {
                    AccessToken = "refreshed-access",
                    RefreshToken = "rotated-" + token,
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });

            public Func<string, CancellationToken, Task<TestAuthSession>>? OverrideCancellableRefresh { get; set; }

            public int ClearCachedSessionCalls;
            public int CacheSessionCalls;
            public TimeSpan RefreshTimeoutOverride { get; set; } = TimeSpan.FromSeconds(100);

            protected override TimeSpan RefreshTimeout => RefreshTimeoutOverride;

            protected override Task<TestAuthSession> PerformAuthenticationAsync(TestCredentials credentials)
                => Task.FromResult(new TestAuthSession { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTime.UtcNow.AddHours(1) });

            protected override Task<string> BuildAuthorizationUrlAsync(string codeChallenge, string state, string redirectUri, IEnumerable<string> scopes)
                => Task.FromResult("https://example.test/authorize");

            protected override Task<TestAuthSession> ExchangeCodeForTokensInternalAsync(string authorizationCode, string codeVerifier, string redirectUri)
                => Task.FromResult(new TestAuthSession());

            protected override Task<TestAuthSession> RefreshTokensInternalAsync(string refreshToken)
                => RefreshImplementation(refreshToken);

            protected override Task<TestAuthSession> RefreshTokensInternalAsync(string refreshToken, CancellationToken cancellationToken)
                => OverrideCancellableRefresh != null
                    ? OverrideCancellableRefresh(refreshToken, cancellationToken)
                    : base.RefreshTokensInternalAsync(refreshToken, cancellationToken);

            protected override Task RevokeTokensInternalAsync(TestAuthSession session) => Task.CompletedTask;

            protected override string ExtractRefreshToken(TestAuthSession session) => session.RefreshToken;

            protected override Task CacheSessionAsync(TestAuthSession session)
            {
                Interlocked.Increment(ref CacheSessionCalls);
                return Task.CompletedTask;
            }

            protected override Task ClearCachedSessionAsync()
            {
                Interlocked.Increment(ref ClearCachedSessionCalls);
                return Task.CompletedTask;
            }
        }

        private static TestAuthSession ExpiredSession(string refreshToken = "refresh-1") => new()
        {
            AccessToken = "expired-access",
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        };

        [Fact]
        public async Task RefreshTokensAsync_HungRefresh_TimesOut_InsteadOfWedging()
        {
            var service = new TestOAuthService
            {
                RefreshTimeoutOverride = TimeSpan.FromMilliseconds(150),
                // Legacy 1-arg override that ignores cancellation and never completes —
                // the exact shape every current plugin implements.
                RefreshImplementation = _ => new TaskCompletionSource<TestAuthSession>().Task
            };

            await Assert.ThrowsAsync<TimeoutException>(
                () => service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None));
        }

        [Fact]
        public async Task RefreshTokensAsync_AfterTimeout_SubsequentCallersAreNotWedged()
        {
            var firstCall = true;
            var service = new TestOAuthService
            {
                RefreshTimeoutOverride = TimeSpan.FromMilliseconds(150)
            };
            service.RefreshImplementation = token =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return new TaskCompletionSource<TestAuthSession>().Task; // hangs
                }

                return Task.FromResult(new TestAuthSession
                {
                    AccessToken = "second-refresh-ok",
                    RefreshToken = "rotated",
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            };

            await Assert.ThrowsAsync<TimeoutException>(
                () => service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None));

            var recovered = await service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None);
            Assert.Equal("second-refresh-ok", recovered.AccessToken);
        }

        [Fact]
        public async Task RefreshTokensAsync_Timeout_DoesNotClearCachedSession()
        {
            var service = new TestOAuthService
            {
                RefreshTimeoutOverride = TimeSpan.FromMilliseconds(150),
                RefreshImplementation = _ => new TaskCompletionSource<TestAuthSession>().Task
            };

            await Assert.ThrowsAsync<TimeoutException>(
                () => service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None));

            // A slow auth server is a transient condition; the persisted session may still be
            // perfectly valid. Clearing it here is the "daily re-login" bug class (T-2).
            Assert.Equal(0, service.ClearCachedSessionCalls);
        }

        [Fact]
        public async Task RefreshTokensAsync_ProviderFailure_StillClearsCachedSession()
        {
            var service = new TestOAuthService
            {
                RefreshImplementation = _ => Task.FromException<TestAuthSession>(new InvalidOperationException("invalid_grant"))
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None));

            Assert.Equal(1, service.ClearCachedSessionCalls);
        }

        [Fact]
        public async Task RefreshTokensAsync_PreCancelledToken_ThrowsWithoutStartingRefresh()
        {
            var refreshStarted = 0;
            var service = new TestOAuthService();
            service.RefreshImplementation = _ =>
            {
                Interlocked.Increment(ref refreshStarted);
                return Task.FromResult(new TestAuthSession { ExpiresAt = DateTime.UtcNow.AddHours(1) });
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.RefreshTokensAsync(ExpiredSession(), cts.Token));

            Assert.Equal(0, refreshStarted);
        }

        [Fact]
        public async Task RefreshTokensAsync_CallerCancellation_AbandonsWait_WithoutKillingSharedRefresh()
        {
            var leaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeRefresh = new TaskCompletionSource<TestAuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshCalls = 0;

            var service = new TestOAuthService();
            service.RefreshImplementation = _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                leaderStarted.TrySetResult();
                return completeRefresh.Task;
            };

            using var cts = new CancellationTokenSource();
            var leaderWait = service.RefreshTokensAsync(ExpiredSession(), cts.Token);
            await leaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaderWait);

            // The shared refresh must still be able to finish and publish its result —
            // the refresh token was already consumed at the provider, so killing the
            // in-flight work would strand the rotated token.
            completeRefresh.TrySetResult(new TestAuthSession
            {
                AccessToken = "shared-refresh-survived",
                RefreshToken = "rotated",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });

            var joined = await service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None);
            Assert.Equal("shared-refresh-survived", joined.AccessToken);
            Assert.Equal(1, refreshCalls); // second call was served by single-flight, not a new refresh
        }

        [Fact]
        public async Task RefreshTokensAsync_ForwardsCancellableToken_ToCancellableInternalOverload()
        {
            var observedCanBeCanceled = false;
            var service = new TestOAuthService
            {
                OverrideCancellableRefresh = (token, ct) =>
                {
                    observedCanBeCanceled = ct.CanBeCanceled;
                    return Task.FromResult(new TestAuthSession
                    {
                        AccessToken = "ct-overload",
                        RefreshToken = "rotated",
                        ExpiresAt = DateTime.UtcNow.AddHours(1)
                    });
                }
            };

            var result = await service.RefreshTokensAsync(ExpiredSession(), CancellationToken.None);

            Assert.Equal("ct-overload", result.AccessToken);
            // The internal overload must observe the timeout token so a well-behaved
            // provider can abort its HTTP call when the refresh window expires.
            Assert.True(observedCanBeCanceled);
        }

        [Fact]
        public async Task RefreshTokensAsync_LegacySingleArgCallPath_StillWorks()
        {
            var service = new TestOAuthService();

            var result = await service.RefreshTokensAsync(ExpiredSession("legacy-token"));

            Assert.Equal("rotated-legacy-token", result.RefreshToken);
            Assert.Equal(1, service.CacheSessionCalls);
        }
    }
}
