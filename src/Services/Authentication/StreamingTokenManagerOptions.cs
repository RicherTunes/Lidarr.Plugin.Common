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
    }
}
