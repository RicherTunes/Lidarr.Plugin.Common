using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Performance
{
    /// <summary>
    /// Fluent options object for <see cref="UniversalAdaptiveRateLimiter"/>.
    ///
    /// Lets plugins override the built-in per-service rate caps with a value
    /// drawn from user-facing settings (e.g. applemusicarr's "Requests per
    /// second" field). Before this type existed, the limiter only had a
    /// parameterless ctor with hardcoded defaults — plugin settings that
    /// nominally controlled rate were stored but ignored at runtime.
    ///
    /// Usage:
    /// <code>
    /// var options = new UniversalAdaptiveRateLimiterOptions()
    ///     .WithServiceLimit("AppleMusic", requestsPerMinute: 60);
    /// services.AddSingleton&lt;IUniversalAdaptiveRateLimiter&gt;(
    ///     _ =&gt; new UniversalAdaptiveRateLimiter(options));
    /// </code>
    /// </summary>
    public sealed class UniversalAdaptiveRateLimiterOptions
    {
        private readonly Dictionary<string, int> _serviceLimits =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Set the starting requests-per-minute rate for the named service. The
        /// limiter uses this as the initial cap and lets adaptive backoff vary
        /// within bounds derived around it (min = 30% of the value clamped to 1,
        /// max = 130% of the value). Returns the same options instance for fluent
        /// chaining.
        /// </summary>
        /// <param name="service">Service name; matched case-insensitively (e.g. "AppleMusic", "Tidal").</param>
        /// <param name="requestsPerMinute">Starting rate cap. Must be &gt; 0.</param>
        public UniversalAdaptiveRateLimiterOptions WithServiceLimit(string service, int requestsPerMinute)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                throw new ArgumentException("Service name is required.", nameof(service));
            }
            if (requestsPerMinute <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestsPerMinute),
                    requestsPerMinute,
                    "Requests-per-minute must be > 0.");
            }
            _serviceLimits[service] = requestsPerMinute;
            return this;
        }

        /// <summary>
        /// Look up the user-configured rate for the named service, if any.
        /// </summary>
        internal bool TryGetServiceLimit(string service, out int requestsPerMinute)
        {
            return _serviceLimits.TryGetValue(service ?? string.Empty, out requestsPerMinute);
        }
    }
}
