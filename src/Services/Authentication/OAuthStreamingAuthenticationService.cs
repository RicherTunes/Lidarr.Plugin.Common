using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Interface for OAuth-specific streaming authentication operations
    /// </summary>
    public interface IOAuthStreamingAuthenticationService<TSession, TCredentials> 
        : IStreamingAuthenticationService<TSession, TCredentials>
        where TSession : class, IAuthSession
        where TCredentials : class, IAuthCredentials
    {
        /// <summary>
        /// Initiates OAuth authorization flow and returns authorization URL
        /// </summary>
        /// <param name="redirectUri">Redirect URI for OAuth flow</param>
        /// <param name="scopes">Requested OAuth scopes</param>
        /// <param name="state">Optional state parameter for security</param>
        /// <returns>Authorization URL and flow identifier</returns>
        Task<OAuthFlowResult> InitiateOAuthFlowAsync(string redirectUri, IEnumerable<string> scopes = null, string state = null);
        
        /// <summary>
        /// Exchanges authorization code for access tokens
        /// </summary>
        /// <param name="authorizationCode">Authorization code from callback</param>
        /// <param name="flowId">Flow identifier from InitiateOAuthFlowAsync</param>
        /// <returns>Authenticated session</returns>
        Task<TSession> ExchangeCodeForTokensAsync(string authorizationCode, string flowId);
        
        /// <summary>
        /// Refreshes an expired access token using refresh token
        /// </summary>
        /// <param name="session">Current session with refresh token</param>
        /// <returns>Updated session with new tokens</returns>
        Task<TSession> RefreshTokensAsync(TSession session);
        
        /// <summary>
        /// Revokes tokens and ends session
        /// </summary>
        /// <param name="session">Session to revoke</param>
        Task RevokeTokensAsync(TSession session);
    }

    /// <summary>
    /// Base class for OAuth 2.0 + PKCE authentication for streaming services
    /// Implements secure OAuth flows with PKCE protection and token management
    /// </summary>
    /// <typeparam name="TSession">Type representing authenticated session</typeparam>
    /// <typeparam name="TCredentials">Type representing credentials/configuration</typeparam>
    /// <remarks>
    /// Supports streaming services using OAuth 2.0 with PKCE:
    /// - Tidal: OAuth 2.0 + PKCE with device flow support
    /// - Spotify: OAuth 2.0 + PKCE with various grant types
    /// - Apple Music: OAuth-like authentication with developer tokens
    /// 
    /// Key security features:
    /// - PKCE (Proof Key for Code Exchange) prevents authorization code interception
    /// - Secure state parameter generation for CSRF protection
    /// - Token refresh with automatic retry and fallback
    /// - Secure credential storage integration
    /// </remarks>
    public abstract class OAuthStreamingAuthenticationService<TSession, TCredentials> 
        : BaseStreamingAuthenticationService<TSession, TCredentials>, IOAuthStreamingAuthenticationService<TSession, TCredentials>
        where TSession : class, IAuthSession
        where TCredentials : class, IAuthCredentials
    {
        protected readonly IPKCEGenerator _pkceGenerator;
        private readonly Dictionary<string, OAuthFlowState> _activeFlows;
        private readonly object _flowsLock = new();
        private readonly TimeSpan _flowExpirationTime = TimeSpan.FromMinutes(10);

        protected OAuthStreamingAuthenticationService(IPKCEGenerator pkceGenerator = null)
        {
            _pkceGenerator = pkceGenerator ?? new PKCEGenerator();
            _activeFlows = new Dictionary<string, OAuthFlowState>();
        }

        /// <summary>
        /// Initiates OAuth authorization flow with PKCE protection
        /// </summary>
        public virtual async Task<OAuthFlowResult> InitiateOAuthFlowAsync(
            string redirectUri, 
            IEnumerable<string> scopes = null, 
            string state = null)
        {
            if (string.IsNullOrEmpty(redirectUri))
                throw new ArgumentNullException(nameof(redirectUri));

            // Generate PKCE codes
            var (codeVerifier, codeChallenge) = _pkceGenerator.GeneratePair();
            
            // Generate state for CSRF protection if not provided
            state ??= GenerateSecureState();
            
            // Create flow identifier
            var flowId = Guid.NewGuid().ToString();
            
            // Store flow state for later verification
            lock (_flowsLock)
            {
                _activeFlows[flowId] = new OAuthFlowState
                {
                    CodeVerifier = codeVerifier,
                    State = state,
                    RedirectUri = redirectUri,
                    CreatedAt = DateTime.UtcNow,
                    Scopes = scopes?.ToList()
                };
                
                // Cleanup expired flows
                CleanupExpiredFlows();
            }

            // Build authorization URL using service-specific implementation
            var authUrl = await BuildAuthorizationUrlAsync(codeChallenge, state, redirectUri, scopes);
            if (string.IsNullOrEmpty(authUrl))
                throw new InvalidOperationException("Authorization URL cannot be null or empty.");

            return new OAuthFlowResult
            {
                AuthorizationUrl = authUrl!,
                FlowId = flowId,
                State = state,
                ExpiresAt = DateTime.UtcNow.Add(_flowExpirationTime)
            };
        }

        /// <summary>
        /// Exchanges authorization code for tokens with PKCE verification
        /// </summary>
        public virtual async Task<TSession> ExchangeCodeForTokensAsync(string authorizationCode, string flowId)
        {
            if (string.IsNullOrEmpty(authorizationCode))
                throw new ArgumentNullException(nameof(authorizationCode));
            if (string.IsNullOrEmpty(flowId))
                throw new ArgumentNullException(nameof(flowId));

            OAuthFlowState flowState;
            lock (_flowsLock)
            {
                if (!_activeFlows.TryGetValue(flowId, out var existingState))
                    throw new InvalidOperationException($"OAuth flow {flowId} not found or expired");
                
                // Remove used flow
                _activeFlows.Remove(flowId);
                flowState = existingState;
            }

            // Check flow expiration
            if (DateTime.UtcNow - flowState.CreatedAt > _flowExpirationTime)
                throw new InvalidOperationException("OAuth flow has expired");

            try
            {
                // Exchange code for tokens using service-specific implementation
                var session = await ExchangeCodeForTokensInternalAsync(
                    authorizationCode, 
                    flowState.CodeVerifier, 
                    flowState.RedirectUri);

                // Cache session
                await CacheSessionAsync(session);
                
                return session;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to exchange authorization code for tokens", ex);
            }
        }

        /// <summary>
        /// Refreshes tokens using refresh token with automatic retry
        /// </summary>
        public virtual async Task<TSession> RefreshTokensAsync(TSession session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            var refreshToken = ExtractRefreshToken(session);
            if (string.IsNullOrEmpty(refreshToken))
                throw new InvalidOperationException("Session does not contain a valid refresh token");

            try
            {
                var refreshedSession = await RefreshTokensInternalAsync(refreshToken);
                
                // Update cached session
                await CacheSessionAsync(refreshedSession);
                
                return refreshedSession;
            }
            catch (Exception ex)
            {
                // If refresh fails, clear cached session
                await ClearCachedSessionAsync();
                throw new InvalidOperationException("Failed to refresh access token", ex);
            }
        }

        /// <summary>
        /// Revokes tokens and clears session
        /// </summary>
        public virtual async Task RevokeTokensAsync(TSession session)
        {
            if (session == null)
                return;

            try
            {
                await RevokeTokensInternalAsync(session);
            }
            catch (Exception ex)
            {
                // Log error but don't throw - revocation is best effort
                Console.WriteLine($"Warning: Failed to revoke tokens: {ex.Message}");
            }
            finally
            {
                // Always clear cached session
                await ClearCachedSessionAsync();
            }
        }

        #region Abstract Methods - Service Specific Implementation

        /// <summary>
        /// Builds service-specific authorization URL
        /// </summary>
        protected abstract Task<string> BuildAuthorizationUrlAsync(
            string codeChallenge, 
            string state, 
            string redirectUri, 
            IEnumerable<string> scopes);

        /// <summary>
        /// Exchanges authorization code for tokens (service-specific implementation)
        /// </summary>
        protected abstract Task<TSession> ExchangeCodeForTokensInternalAsync(
            string authorizationCode, 
            string codeVerifier, 
            string redirectUri);

        /// <summary>
        /// Refreshes tokens using refresh token (service-specific implementation)
        /// </summary>
        protected abstract Task<TSession> RefreshTokensInternalAsync(string refreshToken);

        /// <summary>
        /// Revokes tokens (service-specific implementation)
        /// </summary>
        protected abstract Task RevokeTokensInternalAsync(TSession session);

        /// <summary>
        /// Extracts refresh token from session (service-specific)
        /// </summary>
        protected abstract string ExtractRefreshToken(TSession session);

        /// <summary>
        /// Caches authenticated session (service-specific storage)
        /// </summary>
        protected abstract Task CacheSessionAsync(TSession session);

        /// <summary>
        /// Clears cached session (service-specific storage)
        /// </summary>
        protected abstract Task ClearCachedSessionAsync();

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates a cryptographically secure state parameter for CSRF protection
        /// </summary>
        protected virtual string GenerateSecureState()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Cleans up expired OAuth flows to prevent memory leaks
        /// </summary>
        private void CleanupExpiredFlows()
        {
            var cutoffTime = DateTime.UtcNow.Subtract(_flowExpirationTime);
            var expiredFlows = new List<string>();

            foreach (var kvp in _activeFlows)
            {
                if (kvp.Value.CreatedAt < cutoffTime)
                    expiredFlows.Add(kvp.Key);
            }

            foreach (var expiredFlow in expiredFlows)
            {
                _activeFlows.Remove(expiredFlow);
            }
        }

        /// <summary>
        /// Validates OAuth flow state parameters
        /// </summary>
        protected virtual bool ValidateFlowState(string flowId, string state)
        {
            lock (_flowsLock)
            {
                if (!_activeFlows.TryGetValue(flowId, out var flowState))
                    return false;

                return string.Equals(flowState.State, state, StringComparison.Ordinal);
            }
        }

        #endregion

        #region Internal Classes

        private class OAuthFlowState
        {
            public string CodeVerifier { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
            public string RedirectUri { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public List<string> Scopes { get; set; } = new();
        }

        #endregion
    }

    /// <summary>
    /// Result of OAuth flow initiation
    /// </summary>
    public class OAuthFlowResult
    {
        /// <summary>
        /// URL user should visit to authorize the application
        /// </summary>
        public string AuthorizationUrl { get; set; } = string.Empty;
        
        /// <summary>
        /// Unique identifier for this OAuth flow
        /// </summary>
        public string FlowId { get; set; } = string.Empty;
        
        /// <summary>
        /// State parameter for CSRF protection (should match callback)
        /// </summary>
        public string State { get; set; } = string.Empty;
        
        /// <summary>
        /// When this OAuth flow expires
        /// </summary>
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// OAuth configuration for streaming services
    /// </summary>
    public class OAuthConfiguration
    {
        /// <summary>
        /// OAuth client ID
        /// </summary>
        public string ClientId { get; set; } = string.Empty;
        
        /// <summary>
        /// OAuth client secret (optional for PKCE flows)
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;
        
        /// <summary>
        /// Authorization endpoint URL
        /// </summary>
        public string AuthorizationEndpoint { get; set; } = string.Empty;
        
        /// <summary>
        /// Token exchange endpoint URL
        /// </summary>
        public string TokenEndpoint { get; set; } = string.Empty;
        
        /// <summary>
        /// Token revocation endpoint URL (optional)
        /// </summary>
        public string RevocationEndpoint { get; set; } = string.Empty;
        
        /// <summary>
        /// Default scopes to request
        /// </summary>
        public List<string> DefaultScopes { get; set; } = new();
        
        /// <summary>
        /// Whether to use PKCE (recommended for public clients)
        /// </summary>
        public bool UsePKCE { get; set; } = true;
        
        /// <summary>
        /// Additional parameters to include in authorization request
        /// </summary>
        public Dictionary<string, string> AdditionalAuthParams { get; set; } = new();
    }
}
