using System;
using System.Globalization;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Culture- and overflow-safe parsing of external time values (R2-08). Streaming/credential APIs return epoch
    /// seconds/milliseconds and ISO-8601 dates from untrusted bodies; a hostile or malformed value (e.g.
    /// <see cref="long.MaxValue"/>) makes <see cref="DateTimeOffset.FromUnixTimeSeconds"/> /
    /// <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/> throw <see cref="ArgumentOutOfRangeException"/>, and
    /// parsing under a non-invariant culture can shift the instant. These <c>Try*</c> helpers fail closed
    /// (return <c>false</c>) instead of throwing, so callers can skip a bad value rather than crash the pipeline.
    /// </summary>
    public static class TimeParsing
    {
        // DateTimeOffset's representable Unix range, precomputed so an out-of-range epoch is rejected by a
        // comparison rather than by catching the exception FromUnixTime* would throw.
        private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
        private static readonly long MinUnixMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
        private static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

        /// <summary>Converts Unix epoch <paramref name="seconds"/> to a <see cref="DateTimeOffset"/>, returning
        /// <c>false</c> (instead of throwing) when the value is outside the representable range.</summary>
        public static bool TryFromUnixTimeSeconds(long seconds, out DateTimeOffset value)
        {
            if (seconds < MinUnixSeconds || seconds > MaxUnixSeconds)
            {
                value = default;
                return false;
            }

            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        /// <summary>Converts Unix epoch <paramref name="milliseconds"/> to a <see cref="DateTimeOffset"/>,
        /// returning <c>false</c> (instead of throwing) when the value is outside the representable range.</summary>
        public static bool TryFromUnixTimeMilliseconds(long milliseconds, out DateTimeOffset value)
        {
            if (milliseconds < MinUnixMilliseconds || milliseconds > MaxUnixMilliseconds)
            {
                value = default;
                return false;
            }

            value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }

        /// <summary>Parses an ISO-8601 / RFC-3339 date-time using <see cref="CultureInfo.InvariantCulture"/> and
        /// normalizing to UTC (<see cref="DateTimeStyles.AssumeUniversal"/> | <see cref="DateTimeStyles.AdjustToUniversal"/>),
        /// so a zone-less string is treated as UTC and a non-Gregorian/locale culture can't shift the instant.</summary>
        public static bool TryParseIsoDateInvariant(string? text, out DateTimeOffset value)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                value = default;
                return false;
            }

            return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
        }
    }
}
