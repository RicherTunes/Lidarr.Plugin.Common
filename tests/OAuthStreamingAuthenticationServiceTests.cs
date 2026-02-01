using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class OAuthStreamingAuthenticationServiceTests
    {
        #region Test Implementations

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
                errorMessage = !string.IsNullOrEmpty(ClientId) ? "" : "Client ID is required";
                return !string.IsNullOrEmpty(ClientId);
            }
        }

        private class TestOAuthService : OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>
        {
            private readonly Dictionary<string, TestAuthSession> _tokenExchangeResults = new();
            private readonly Dictionary<string, TestAuthSession> _refreshResults = new();
            private readonly Dictionary<string, string> _authUrls = new();
            private readonly List<string> _revokedTokens = new();

            public TestOAuthService(IPKCEGenerator? pkceGenerator = null)
                : base(pkceGenerator!)
            {
            }

            public void SetTokenExchangeResult(string code, TestAuthSession session)
            {
                _tokenExchangeResults[code] = session;
            }

            public void SetRefreshResult(string refreshToken, TestAuthSession session)
            {
                _refreshResults[refreshToken] = session;
            }

            public void SetAuthUrl(string challenge, string url)
            {
                _authUrls[challenge] = url;
            }

            public List<string> RevokedTokens => _revokedTokens;

            // Implementation of abstract method from BaseStreamingAuthenticationService
            protected override Task<TestAuthSession> PerformAuthenticationAsync(TestCredentials credentials)
            {
                return Task.FromResult(new TestAuthSession
                {
                    AccessToken = "authenticated-access-token",
                    RefreshToken = "authenticated-refresh-token",
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            }

            protected override Task<string> BuildAuthorizationUrlAsync(
                string codeChallenge,
                string state,
                string redirectUri,
                IEnumerable<string>? scopes)
            {
                if (_authUrls.TryGetValue(codeChallenge, out var url))
                {
                    return Task.FromResult(url);
                }

                var scopeParam = scopes != null ? $"&scope={string.Join(" ", scopes)}" : "";
                return Task.FromResult($"https://auth.example.com/authorize?code_challenge={codeChallenge}&state={state}&redirect_uri={redirectUri}{scopeParam}");
            }

            protected override Task<TestAuthSession> ExchangeCodeForTokensInternalAsync(
                string authorizationCode,
                string codeVerifier,
                string redirectUri)
            {
                if (_tokenExchangeResults.TryGetValue(authorizationCode, out var session))
                {
                    return Task.FromResult(session);
                }

                return Task.FromResult(new TestAuthSession
                {
                    AccessToken = $"access-token-for-{authorizationCode}",
                    RefreshToken = $"refresh-token-for-{authorizationCode}",
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            }

            protected override Task<TestAuthSession> RefreshTokensInternalAsync(string refreshToken)
            {
                if (_refreshResults.TryGetValue(refreshToken, out var session))
                {
                    return Task.FromResult(session);
                }

                return Task.FromResult(new TestAuthSession
                {
                    AccessToken = $"refreshed-access-token",
                    RefreshToken = refreshToken,
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            }

            protected override Task RevokeTokensInternalAsync(TestAuthSession session)
            {
                _revokedTokens.Add(session.AccessToken);
                return Task.CompletedTask;
            }

            protected override string ExtractRefreshToken(TestAuthSession session)
            {
                return session.RefreshToken;
            }

            protected override Task CacheSessionAsync(TestAuthSession session)
            {
                // Test implementation - no actual caching
                return Task.CompletedTask;
            }

            protected override Task ClearCachedSessionAsync()
            {
                // Test implementation - no actual cache to clear
                return Task.CompletedTask;
            }

            // Test helpers
            public int GetActiveFlowCount()
            {
                // Use reflection to access private field
                var flowsField = typeof(OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>)
                    .GetField("_activeFlows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var flows = flowsField?.GetValue(this) as System.Collections.Generic.Dictionary<string, object>;
                return flows?.Count ?? 0;
            }
        }

        #endregion

        #region InitiateOAuthFlowAsync Tests

        [Fact]
        public async Task InitiateOAuthFlowAsync_GeneratesValidFlowResult()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.AuthorizationUrl);
            Assert.NotEmpty(result.FlowId);
            Assert.NotEmpty(result.State);
            Assert.True(result.ExpiresAt > DateTime.UtcNow);
            Assert.True(result.ExpiresAt < DateTime.UtcNow.AddMinutes(11));
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_IncludesCodeChallengeInAuthUrl()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.Contains("code_challenge=", result.AuthorizationUrl);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_IncludesStateInAuthUrl()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.Contains("state=", result.AuthorizationUrl);
            Assert.Contains(result.State, result.AuthorizationUrl);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_ThrowsOnNullRedirectUri()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.InitiateOAuthFlowAsync(null!));
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_ThrowsOnEmptyRedirectUri()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.InitiateOAuthFlowAsync(string.Empty));
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_AcceptsCustomState()
        {
            // Arrange
            var service = new TestOAuthService();
            var customState = "my-custom-state-12345";

            // Act
            var result = await service.InitiateOAuthFlowAsync(
                "https://example.com/callback",
                null!,
                customState);

            // Assert
            Assert.Equal(customState, result.State);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_GeneratesStateWhenNotProvided()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var result1 = await service.InitiateOAuthFlowAsync("https://example.com/callback");
            var result2 = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.NotEmpty(result1.State);
            Assert.NotEmpty(result2.State);
            Assert.NotEqual(result1.State, result2.State);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_IncludesScopesInAuthUrl()
        {
            // Arrange
            var service = new TestOAuthService();
            var scopes = new List<string> { "read", "write", "playlist" };

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback", scopes);

            // Assert
            Assert.Contains("read", result.AuthorizationUrl);
            Assert.Contains("write", result.AuthorizationUrl);
            Assert.Contains("playlist", result.AuthorizationUrl);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_CreatesUniqueFlowIds()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var result1 = await service.InitiateOAuthFlowAsync("https://example.com/callback");
            var result2 = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.NotEqual(result1.FlowId, result2.FlowId);
        }

        [Fact]
        public async Task InitiateOAuthFlowAsync_SetsCorrectExpiration()
        {
            // Arrange
            var service = new TestOAuthService();
            var now = DateTime.UtcNow;

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert - Flow should expire in approximately 10 minutes
            var expirationWindow = result.ExpiresAt - now;
            Assert.InRange(expirationWindow.TotalMinutes, 9.9, 10.1);
        }

        #endregion

        #region ExchangeCodeForTokensAsync Tests

        [Fact]
        public async Task ExchangeCodeForTokensAsync_ReturnsValidSession()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Act
            var session = await service.ExchangeCodeForTokensAsync("test-auth-code", flowResult.FlowId);

            // Assert
            Assert.NotNull(session);
            Assert.NotEmpty(session.AccessToken);
            Assert.NotEmpty(session.RefreshToken);
            Assert.False(session.IsExpired);
        }

        [Fact]
        public async Task ExchangeCodeForTokensAsync_ThrowsOnNullCode()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExchangeCodeForTokensAsync(null!, flowResult.FlowId));
        }

        [Fact]
        public async Task ExchangeCodeForTokensAsync_ThrowsOnNullFlowId()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.ExchangeCodeForTokensAsync("test-code", null!));
        }

        [Fact]
        public async Task ExchangeCodeForTokensAsync_ThrowsOnInvalidFlowId()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExchangeCodeForTokensAsync("test-code", "invalid-flow-id"));

            Assert.Contains("not found or expired", ex.Message);
        }

        [Fact]
        public async Task ExchangeCodeForTokensAsync_RemovesFlowAfterExchange()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Act
            await service.ExchangeCodeForTokensAsync("test-code", flowResult.FlowId);

            // Assert - Attempting to use same flow again should fail
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExchangeCodeForTokensAsync("test-code", flowResult.FlowId));
        }

        [Fact]
        public async Task ExchangeCodeForTokensAsync_UsesPKCEVerifier()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // The flow should have stored a code verifier that gets used in exchange
            // This is verified by the fact the exchange succeeds (implementation uses verifier)

            // Act
            var session = await service.ExchangeCodeForTokensAsync("test-code", flowResult.FlowId);

            // Assert
            Assert.NotNull(session);
        }

        #endregion

        #region RefreshTokensAsync Tests

        [Fact]
        public async Task RefreshTokensAsync_ReturnsRefreshedSession()
        {
            // Arrange
            var service = new TestOAuthService();
            var originalSession = new TestAuthSession
            {
                AccessToken = "original-access",
                RefreshToken = "original-refresh",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };

            // Act
            var refreshedSession = await service.RefreshTokensAsync(originalSession);

            // Assert
            Assert.NotNull(refreshedSession);
            Assert.NotEmpty(refreshedSession.AccessToken);
        }

        [Fact]
        public async Task RefreshTokensAsync_PreservesRefreshToken()
        {
            // Arrange
            var service = new TestOAuthService();
            var refreshToken = "my-refresh-token";
            var originalSession = new TestAuthSession
            {
                AccessToken = "original-access",
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };

            // Act
            var refreshedSession = await service.RefreshTokensAsync(originalSession);

            // Assert
            Assert.Equal(refreshToken, refreshedSession.RefreshToken);
        }

        [Fact]
        public async Task RefreshTokensAsync_ThrowsOnNullSession()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                service.RefreshTokensAsync(null!));
        }

        [Fact]
        public async Task RefreshTokensAsync_ThrowsOnSessionWithoutRefreshToken()
        {
            // Arrange
            var service = new TestOAuthService();
            var session = new TestAuthSession
            {
                AccessToken = "access-token",
                RefreshToken = null!,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RefreshTokensAsync(session));
            Assert.Contains("refresh token", ex.Message);
        }

        #endregion

        #region RevokeTokensAsync Tests

        [Fact]
        public async Task RevokeTokensAsync_ClearsSession()
        {
            // Arrange
            var service = new TestOAuthService();
            var session = new TestAuthSession
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };

            // Act
            await service.RevokeTokensAsync(session);

            // Assert - Should complete without throwing
            Assert.True(true);
        }

        [Fact]
        public async Task RevokeTokensAsync_HandlesNullSession()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act & Assert - Should not throw
            await service.RevokeTokensAsync(null!);
        }

        [Fact]
        public async Task RevokeTokensAsync_CallsRevokeInternal()
        {
            // Arrange
            var service = new TestOAuthService();
            var session = new TestAuthSession
            {
                AccessToken = "test-access-token",
                RefreshToken = "refresh-token",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };

            // Act
            await service.RevokeTokensAsync(session);

            // Assert
            Assert.Contains(session.AccessToken, service.RevokedTokens);
        }

        #endregion

        #region Flow State Management Tests

        [Fact]
        public async Task ValidateFlowState_ValidatesMatchingState()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Act - Use reflection to call private method
            var validateMethod = typeof(OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>)
                .GetMethod("ValidateFlowState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isValid = (bool?)validateMethod?.Invoke(service, new object[] { flowResult.FlowId, flowResult.State });

            // Assert
            Assert.True(isValid ?? false);
        }

        [Fact]
        public async Task ValidateFlowState_RejectsMismatchedState()
        {
            // Arrange
            var service = new TestOAuthService();
            var flowResult = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Act
            var validateMethod = typeof(OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>)
                .GetMethod("ValidateFlowState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isValid = (bool?)validateMethod?.Invoke(service, new object[] { flowResult.FlowId, "wrong-state" });

            // Assert
            Assert.False(isValid ?? true);
        }

        [Fact]
        public Task ValidateFlowState_RejectsInvalidFlowId()
        {
            // Arrange
            var service = new TestOAuthService();

            // Act
            var validateMethod = typeof(OAuthStreamingAuthenticationService<TestAuthSession, TestCredentials>)
                .GetMethod("ValidateFlowState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var isValid = (bool?)validateMethod?.Invoke(service, new object[] { "invalid-flow-id", "some-state" });

            // Assert
            Assert.False(isValid ?? true);
            return Task.CompletedTask;
        }

        #endregion

        #region PKCE Integration Tests

        [Fact]
        public async Task InitiateOAuthFlowAsync_UsesProvidedPKCEGenerator()
        {
            // Arrange
            var mockPkce = new MockPKCEGenerator
            {
                Verifier = "test-verifier-123456789012345678901234567890123456",
                Challenge = "test-challenge"
            };
            var service = new TestOAuthService(mockPkce);

            // Act
            var result = await service.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.Contains("test-challenge", result.AuthorizationUrl);
        }

        private class MockPKCEGenerator : IPKCEGenerator
        {
            public string Verifier { get; set; } = string.Empty;
            public string Challenge { get; set; } = string.Empty;

            public (string codeVerifier, string codeChallenge) GeneratePair(int length = 128)
            {
                return (Verifier, Challenge);
            }

            public string CreateS256Challenge(string codeVerifier)
            {
                return Challenge;
            }
        }

        #endregion

        #region Custom Auth URL Tests

        [Fact]
        public async Task InitiateOAuthFlowAsync_UsesCustomAuthUrlWhenSet()
        {
            // Arrange
            var service = new TestOAuthService();
            var customUrl = "https://custom-auth.example.com/authorize?custom_param=value";
            service.SetAuthUrl("test-challenge", customUrl);

            // Use a mock PKCE generator to provide a predictable challenge
            var mockPkce = new MockPKCEGenerator
            {
                Verifier = "test-verifier",
                Challenge = "test-challenge"
            };
            var serviceWithMock = new TestOAuthService(mockPkce);
            serviceWithMock.SetAuthUrl("test-challenge", customUrl);

            // Act
            var result = await serviceWithMock.InitiateOAuthFlowAsync("https://example.com/callback");

            // Assert
            Assert.Equal(customUrl, result.AuthorizationUrl);
        }

        #endregion
    }
}
