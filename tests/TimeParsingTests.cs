using System;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// R2-08: external time values (epoch seconds/ms from untrusted JSON, ISO dates) must parse without throwing
    /// on hostile/malformed input. DateTimeOffset.FromUnixTime* throws ArgumentOutOfRangeException past its
    /// representable range, and DateTime(Offset).Parse without InvariantCulture can shift the instant on a
    /// non-Gregorian/locale culture. These Try* helpers fail closed instead.
    /// </summary>
    public sealed class TimeParsingTests
    {
        [Fact]
        public void TryFromUnixTimeSeconds_Valid_ReturnsTrue()
        {
            Assert.True(TimeParsing.TryFromUnixTimeSeconds(0, out var epoch));
            Assert.Equal(DateTimeOffset.UnixEpoch, epoch);

            Assert.True(TimeParsing.TryFromUnixTimeSeconds(1_700_000_000, out var recent));
            Assert.Equal(2023, recent.UtcDateTime.Year);
        }

        [Theory]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void TryFromUnixTimeSeconds_OutOfRange_ReturnsFalse_DoesNotThrow(long seconds)
        {
            Assert.False(TimeParsing.TryFromUnixTimeSeconds(seconds, out var value));
            Assert.Equal(default, value);
        }

        [Fact]
        public void TryFromUnixTimeMilliseconds_Valid_ReturnsTrue()
        {
            Assert.True(TimeParsing.TryFromUnixTimeMilliseconds(1_700_000_000_000, out var recent));
            Assert.Equal(2023, recent.UtcDateTime.Year);
        }

        [Theory]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void TryFromUnixTimeMilliseconds_OutOfRange_ReturnsFalse_DoesNotThrow(long ms)
        {
            Assert.False(TimeParsing.TryFromUnixTimeMilliseconds(ms, out var value));
            Assert.Equal(default, value);
        }

        [Fact]
        public void TryParseIsoDateInvariant_Valid_ParsesAsUtc()
        {
            Assert.True(TimeParsing.TryParseIsoDateInvariant("2024-03-15T08:30:00Z", out var value));
            Assert.Equal(TimeSpan.Zero, value.Offset); // adjusted to UTC
            Assert.Equal(new DateTime(2024, 3, 15, 8, 30, 0, DateTimeKind.Utc), value.UtcDateTime);
        }

        [Fact]
        public void TryParseIsoDateInvariant_NoZone_AssumedUtc()
        {
            // No offset in the string → AssumeUniversal treats it as UTC rather than local.
            Assert.True(TimeParsing.TryParseIsoDateInvariant("2024-03-15T08:30:00", out var value));
            Assert.Equal(TimeSpan.Zero, value.Offset);
            Assert.Equal(8, value.UtcDateTime.Hour);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a date")]
        [InlineData("13/45/2024")]
        public void TryParseIsoDateInvariant_Invalid_ReturnsFalse(string? text)
        {
            Assert.False(TimeParsing.TryParseIsoDateInvariant(text, out var value));
            Assert.Equal(default, value);
        }
    }
}
