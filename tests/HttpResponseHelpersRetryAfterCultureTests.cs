using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// HTTP-date Retry-After values are RFC 7231 IMF-fixdate ("Wed, 21 Oct 2099 07:28:00 GMT") — always
    /// English day/month abbreviations on a Gregorian calendar. ParseRetryAfter must parse them with
    /// CultureInfo.InvariantCulture, not the host's CurrentCulture: on a non-English / non-Gregorian host
    /// (e.g. Thai Buddhist, +543 years) the current-culture parser does not recognize "Oct"/"Wed", silently
    /// fails, and the Retry-After header is dropped — degrading rate-limit backoff.
    /// </summary>
    public sealed class HttpResponseHelpersRetryAfterCultureTests
    {
        [Theory]
        [InlineData("th-TH")] // Thai Buddhist calendar (+543 years)
        [InlineData("fa-IR")] // Persian calendar
        [InlineData("ar-SA")] // Umm al-Qura calendar
        public void ParseRetryAfter_HttpDate_UnderNonGregorianCulture_StillParses(string cultureName)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

                // A fixed far-future IMF-fixdate so the delta is unambiguously large + positive.
                var headers = new[] { new KeyValuePair<string, string>("Retry-After", "Wed, 21 Oct 2099 07:28:00 GMT") };

                var result = HttpResponseHelpers.ParseRetryAfter(headers);

                Assert.NotNull(result); // current-culture parse drops the header → null (the bug)
                Assert.True(result!.Value > TimeSpan.FromDays(365 * 50),
                    $"a 2099 HTTP-date should be decades out, got {result}");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void ParseRetryAfter_NumericSeconds_Unaffected()
        {
            var headers = new[] { new KeyValuePair<string, string>("Retry-After", "120") };
            Assert.Equal(TimeSpan.FromSeconds(120), HttpResponseHelpers.ParseRetryAfter(headers));
        }
    }
}
