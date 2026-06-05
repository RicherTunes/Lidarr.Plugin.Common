using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Shared helpers for extracting well-known HTTP response header values. Accepts the header
    /// dictionary as <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>
    /// so the helpers are usable from both <see cref="System.Net.Http.HttpResponseMessage"/>-based
    /// and <c>NzbDrone.Common.Http.HttpResponse</c>-based call sites without a compile-time
    /// dependency on the Lidarr host assemblies.
    /// </summary>
    /// <remarks>
    /// <c>NzbDrone.Common.Http.HttpHeader</c> inherits from <c>NameValueCollection</c> and
    /// additionally implements <c>IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;</c>,
    /// so <c>response.Headers</c> satisfies the parameter type directly.
    /// </remarks>
    public static class HttpResponseHelpers
    {
        /// <summary>
        /// Iterates <paramref name="headers"/> for a case-insensitive <c>Retry-After</c> entry,
        /// tries integer seconds first (clamped to non-negative via <see cref="Math.Max(int,int)"/>),
        /// then an HTTP-date (<see cref="DateTimeOffset"/>), and returns the resulting
        /// <see cref="TimeSpan"/> or <see langword="null"/> when the header is absent or unparseable.
        /// </summary>
        /// <param name="headers">
        /// The response header collection. A <see langword="null"/> argument returns
        /// <see langword="null"/> without throwing.
        /// </param>
        /// <returns>
        /// A non-negative <see cref="TimeSpan"/> derived from the <c>Retry-After</c> header value,
        /// or <see langword="null"/> if the header is absent, empty, or cannot be interpreted as
        /// either an integer delta or an HTTP-date.
        /// </returns>
        public static TimeSpan? ParseRetryAfter(IEnumerable<KeyValuePair<string, string>>? headers)
        {
            try
            {
                if (headers == null) return null;

                foreach (var header in headers)
                {
                    if (!header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var raw = (header.Value ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(raw)) continue;

                    // Numeric delta (seconds) — most common wire format (e.g. "120").
                    if (int.TryParse(raw, out var seconds))
                    {
                        return TimeSpan.FromSeconds(Math.Max(0, seconds));
                    }

                    // HTTP-date — RFC 7231 IMF-fixdate (e.g. "Wed, 21 Oct 2025 07:28:00 GMT"). Always English
                    // day/month names on a Gregorian calendar, so parse with InvariantCulture: the host's
                    // CurrentCulture (esp. non-Gregorian, e.g. Thai Buddhist) fails to recognize "Oct"/"Wed"
                    // and would silently drop the Retry-After header.
                    if (DateTimeOffset.TryParse(
                            raw,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var when))
                    {
                        var delta = when - DateTimeOffset.UtcNow;
                        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
                    }

                    // First matching header consumed; stop iterating.
                    break;
                }
            }
            catch
            {
                // Best-effort. Retry-After is advisory; absence must not break the error path.
            }

            return null;
        }
    }
}
