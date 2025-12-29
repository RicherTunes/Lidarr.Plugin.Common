using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Configuration options for <see cref="StreamingTokenManager{TSession,TCredentials}"/>.
    /// </summary>
    /// <typeparam name="TSession">Session representation type.</typeparam>
    public class StreamingTokenManagerOptions<TSession>
        where TSession : class
    {
        /// <summary>
        /// Gets or sets the grace period subtracted from expiry when deciding to refresh.
        /// Defaults to five minutes.
        /// </summary>
        public TimeSpan RefreshBuffer { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets how frequently the background expiry monitor should run.
        /// Defaults to one minute.
        /// </summary>
        public TimeSpan RefreshCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the maximum number of refresh attempts before giving up.
        /// Defaults to three attempts.
        /// </summary>
        public int MaxRefreshAttempts { get; set; } = 3;

        /// <summary>
        /// Gets or sets the delay between refresh retries when they fail.
        /// Defaults to thirty seconds.
        /// </summary>
        public TimeSpan RefreshRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the default session lifetime when no explicit expiry is provided by the service.
        /// Defaults to twenty-four hours.
        /// </summary>
        public TimeSpan DefaultSessionLifetime { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Optional callback returning the absolute expiry for a newly authenticated session.
        /// </summary>
        public Func<TSession, DateTime?>? GetSessionExpiry { get; set; }

        /// <summary>
        /// Optional callback producing metadata to persist alongside the session.
        /// </summary>
        public Func<TSession, IReadOnlyDictionary<string, string>?>? GetMetadata { get; set; }

        /// <summary>
        /// Optional callback to retrieve credentials for proactive (background) token refresh.
        /// When set and <see cref="EnableProactiveRefresh"/> is enabled, the background timer will
        /// automatically refresh tokens before they expire.
        /// When null, refresh only happens on-demand via <see cref="StreamingTokenManager{TSession,TCredentials}.GetValidSessionAsync"/>.
        /// </summary>
        /// <remarks>
        /// This returns <see cref="object"/> to keep options independent from any specific credentials type.
        /// The token manager will attempt to cast the returned value to its configured <c>TCredentials</c>.
        /// </remarks>
        public Func<object?>? ProactiveRefreshCredentialsProvider { get; set; }

        /// <summary>
        /// Whether to enable proactive (background) token refresh.
        /// Requires <see cref="ProactiveRefreshCredentialsProvider"/> to be set.
        /// Defaults to true when a credentials provider is available.
        /// </summary>
        public bool EnableProactiveRefresh { get; set; } = true;
    }
}
