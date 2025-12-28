using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Generic authentication token management service for streaming providers.
    /// Handles refresh, persistence, and status tracking for access tokens or sessions.
    /// </summary>
    public class StreamingTokenManager<TSession, TCredentials> : IDisposable
        where TSession : class
        where TCredentials : class
    {
        private readonly ILogger<StreamingTokenManager<TSession, TCredentials>> _logger;
        private readonly IStreamingTokenAuthenticationService<TSession, TCredentials> _authService;
        private readonly ITokenStore<TSession>? _tokenStore;
        private readonly StreamingTokenManagerOptions<TSession> _options;
        private readonly Timer _refreshTimer;
        private readonly SemaphoreSlim _refreshSemaphore;
        private readonly object _tokenLock = new();
        private readonly object _loadLock = new();

        private volatile TSession? _currentSession;
        private DateTime _sessionExpiryTime = DateTime.MinValue;
        private volatile bool _isRefreshing;
        private volatile int _refreshAttempts;
        private Task? _initialLoadTask;

        public event EventHandler<SessionRefreshEventArgs<TSession>>? SessionRefreshed;
        public event EventHandler<SessionRefreshFailedEventArgs>? SessionRefreshFailed;

        public StreamingTokenManager(
            IStreamingTokenAuthenticationService<TSession, TCredentials> authService,
            ILogger<StreamingTokenManager<TSession, TCredentials>> logger,
            ITokenStore<TSession>? tokenStore = null,
            StreamingTokenManagerOptions<TSession>? options = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tokenStore = tokenStore;
            _options = options ?? new StreamingTokenManagerOptions<TSession>();

            _refreshSemaphore = new SemaphoreSlim(1, 1);
            _refreshTimer = new Timer(CheckTokenExpiry, null, _options.RefreshCheckInterval, _options.RefreshCheckInterval);

            _logger.LogDebug("StreamingTokenManager initialized (buffer={Buffer} checkInterval={Interval})",
                _options.RefreshBuffer, _options.RefreshCheckInterval);
        }

        /// <summary>
        /// Gets the current valid session, refreshing with the provided credentials when necessary.
        /// </summary>
        public async Task<TSession> GetValidSessionAsync(TCredentials? fallbackCredentials = null)
        {
            await EnsurePersistedSessionAsync().ConfigureAwait(false);

            if (IsSessionValid())
            {
                return _currentSession!;
            }

            if (fallbackCredentials == null)
            {
                throw new InvalidOperationException("No valid session is available and no fallback credentials were provided.");
            }

            await RefreshSessionAsync(fallbackCredentials).ConfigureAwait(false);
            return _currentSession!;
        }

        /// <summary>
        /// Forces a session refresh using the supplied credentials.
        /// </summary>
        public async Task RefreshSessionAsync(TCredentials credentials)
        {
            if (credentials == null)
            {
                throw new ArgumentNullException(nameof(credentials));
            }

            await EnsurePersistedSessionAsync().ConfigureAwait(false);

            await _refreshSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                _isRefreshing = true;
                _refreshAttempts++;

                _logger.LogDebug("Attempting session refresh (attempt {Attempt}/{Max})", _refreshAttempts, _options.MaxRefreshAttempts);

                var newSession = await _authService.AuthenticateAsync(credentials).ConfigureAwait(false);
                var expiry = DetermineExpiry(newSession);
                TokenEnvelope<TSession>? envelope = null;

                lock (_tokenLock)
                {
                    _currentSession = newSession;
                    _sessionExpiryTime = expiry;
                    _refreshAttempts = 0;

                    if (_tokenStore != null)
                    {
                        envelope = new TokenEnvelope<TSession>(
                            newSession,
                            expiry,
                            _options.GetMetadata?.Invoke(newSession));
                    }
                }

                if (envelope != null)
                {
                    await PersistSafely(() => _tokenStore!.SaveAsync(envelope, CancellationToken.None)).ConfigureAwait(false);
                }

                try { Observability.Metrics.AuthRefreshes.Add(1); } catch { }

                SessionRefreshed?.Invoke(this, new SessionRefreshEventArgs<TSession>
                {
                    NewSession = newSession,
                    RefreshedAt = DateTime.UtcNow,
                    ExpiresAt = expiry
                });

                _logger.LogInformation("Session refreshed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session refresh failed (attempt {Attempt}/{Max})", _refreshAttempts, _options.MaxRefreshAttempts);

                SessionRefreshFailed?.Invoke(this, new SessionRefreshFailedEventArgs
                {
                    AttemptNumber = _refreshAttempts,
                    Exception = ex,
                    FailedAt = DateTime.UtcNow
                });

                if (_refreshAttempts >= _options.MaxRefreshAttempts)
                {
                    _logger.LogError("Maximum refresh attempts exceeded, clearing session");
                    await ClearSessionAsync().ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                _isRefreshing = false;
                _refreshSemaphore.Release();
            }
        }

        /// <summary>
        /// Clears the current session and any persisted state synchronously.
        /// </summary>
        public void ClearSession()
        {
            ClearSessionAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Clears the current session and persisted state.
        /// </summary>
        public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
        {
            lock (_tokenLock)
            {
                _currentSession = null;
                _sessionExpiryTime = DateTime.MinValue;
                _refreshAttempts = 0;
            }

            // Always clear any persisted session when a store is configured, even if no
            // in-memory session has been loaded yet. This ensures an explicit clear truly
            // wipes persisted state (e.g., logout at startup scenarios).
            if (_tokenStore != null)
            {
                await PersistSafely(() => _tokenStore!.ClearAsync(cancellationToken)).ConfigureAwait(false);
            }

            _logger.LogDebug("Session cleared");
        }

        /// <summary>
        /// Gets current session status information.
        /// </summary>
        public SessionStatus GetSessionStatus()
        {
            if (_tokenStore != null)
            {
                EnsurePersistedSessionAsync().GetAwaiter().GetResult();
            }

            lock (_tokenLock)
            {
                return new SessionStatus
                {
                    IsValid = IsSessionValidUnsafe(),
                    ExpiresAt = _sessionExpiryTime,
                    IsRefreshing = _isRefreshing,
                    RefreshAttempts = _refreshAttempts,
                    TimeUntilExpiry = _sessionExpiryTime == DateTime.MinValue
                        ? TimeSpan.Zero
                        : _sessionExpiryTime - DateTime.UtcNow
                };
            }
        }

        private bool IsSessionValid()
        {
            lock (_tokenLock)
            {
                return IsSessionValidUnsafe();
            }
        }

        private bool IsSessionValidUnsafe()
        {
            return _currentSession != null &&
                   DateTime.UtcNow < _sessionExpiryTime.Subtract(_options.RefreshBuffer);
        }

        private DateTime DetermineExpiry(TSession session)
        {
            var expiry = _options.GetSessionExpiry?.Invoke(session);
            return expiry ?? DateTime.UtcNow.Add(_options.DefaultSessionLifetime);
        }

        private async void CheckTokenExpiry(object? state)
        {
            try
            {
                // Check if session is within refresh buffer (needs refresh soon)
                bool needsRefresh;
                lock (_tokenLock)
                {
                    needsRefresh = _currentSession != null &&
                                   DateTime.UtcNow >= _sessionExpiryTime.Subtract(_options.RefreshBuffer) &&
                                   DateTime.UtcNow < _sessionExpiryTime;
                }

                if (!needsRefresh || _isRefreshing)
                {
                    return;
                }

                // Check if proactive refresh is enabled and credentials are available
                if (!_options.EnableProactiveRefresh || _options.ProactiveRefreshCredentialsProvider == null)
                {
                    _logger.LogDebug("Session approaching expiry, awaiting caller-provided refresh (proactive refresh not configured)");
                    return;
                }

                var credentialsObject = _options.ProactiveRefreshCredentialsProvider();
                if (credentialsObject is not TCredentials credentials)
                {
                    _logger.LogDebug("Session approaching expiry, but credentials provider did not return valid credentials");
                    return;
                }

                _logger.LogDebug("Proactive token refresh: session expires in {TimeToExpiry}, refreshing preemptively",
                    _sessionExpiryTime - DateTime.UtcNow);

                try
                {
                    await RefreshSessionAsync(credentials).ConfigureAwait(false);
                    _logger.LogDebug("Proactive token refresh completed successfully");
                }
                catch (Exception refreshEx)
                {
                    _logger.LogWarning(refreshEx, "Proactive token refresh failed, will retry on next check interval");
                    // Don't rethrow - this is background maintenance, let the timer retry
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking session expiry");
            }
        }

        private async Task EnsurePersistedSessionAsync()
        {
            if (_tokenStore == null)
            {
                return;
            }

            var loadTask = Volatile.Read(ref _initialLoadTask);
            if (loadTask == null)
            {
                lock (_loadLock)
                {
                    loadTask = _initialLoadTask;
                    if (loadTask == null)
                    {
                        loadTask = LoadPersistedSessionAsync();
                        _initialLoadTask = loadTask;
                    }
                }
            }

            await loadTask.ConfigureAwait(false);
        }

        private async Task LoadPersistedSessionAsync()
        {
            if (_tokenStore == null)
            {
                return;
            }

            try
            {
                var envelope = await _tokenStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (envelope == null)
                {
                    _logger.LogDebug("No persisted session found");
                    return;
                }

                if (envelope.ExpiresAt.HasValue && envelope.ExpiresAt.Value <= DateTime.UtcNow)
                {
                    _logger.LogInformation("Persisted session expired at {Expiry}, clearing store", envelope.ExpiresAt);
                    await _tokenStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                lock (_tokenLock)
                {
                    _currentSession = envelope.Session;
                    _sessionExpiryTime = envelope.ExpiresAt ?? DateTime.UtcNow.Add(_options.DefaultSessionLifetime);
                    _refreshAttempts = 0;
                }

                _logger.LogInformation("Loaded persisted session (expires {Expiry})", _sessionExpiryTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load persisted session");
            }
        }

        private async Task PersistSafely(Func<Task> persistence)
        {
            try
            {
                await persistence().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token persistence operation failed");
            }
        }

        public void Dispose()
        {
            try
            {
                _refreshTimer.Dispose();
                _refreshSemaphore.Dispose();
                ClearSession();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during StreamingTokenManager disposal");
            }
        }
    }

    /// <summary>
    /// Interface for generic streaming authentication services used by StreamingTokenManager.
    /// Note: This is separate from the base IStreamingAuthenticationService to avoid conflicts.
    /// </summary>
    public interface IStreamingTokenAuthenticationService<TSession, TCredentials>
        where TSession : class
        where TCredentials : class
    {
        Task<TSession> AuthenticateAsync(TCredentials credentials);
        Task<bool> ValidateSessionAsync(TSession session);
    }

    /// <summary>
    /// Event arguments for session refresh events.
    /// </summary>
    public class SessionRefreshEventArgs<TSession> : EventArgs
        where TSession : class
    {
        public TSession NewSession { get; set; } = default!;
        public DateTime RefreshedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Event arguments for session refresh failure events.
    /// </summary>
    public class SessionRefreshFailedEventArgs : EventArgs
    {
        public int AttemptNumber { get; set; }
        public Exception Exception { get; set; } = default!;
        public DateTime FailedAt { get; set; }
    }

    /// <summary>
    /// Represents current session status.
    /// </summary>
    public class SessionStatus
    {
        public bool IsValid { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsRefreshing { get; set; }
        public int RefreshAttempts { get; set; }
        public TimeSpan TimeUntilExpiry { get; set; }
    }
}
